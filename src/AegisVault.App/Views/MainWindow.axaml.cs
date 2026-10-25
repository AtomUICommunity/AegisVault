using System.Collections.Specialized;
using System.Linq;
using AegisVault.App.Localization;
using AegisVault.App.Services;
using AegisVault.App.ViewModels;
using AegisVault.Core.Models;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Controls.Templates;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using AtomUIMenuItem = AtomUI.Desktop.Controls.MenuItem;
using AtomUIButton = AtomUI.Desktop.Controls.Button;
using MenuFlyout = AtomUI.Desktop.Controls.MenuFlyout;
using MenuSeparator = AtomUI.Desktop.Controls.MenuSeparator;

namespace AegisVault.App.Views;

public partial class MainWindow : Window
{
    private ClipboardService? _clipboard;
    private AutoLockService? _autoLock;
    private Action? _openSettings;
    private Action<Window>? _applyScreenGuard;
    private Func<PasswordGeneratorOptions?>? _generatorOptions;
    private bool _handlersAttached;
    private bool _clipboardCleanupAttached;

    /// <summary>Raised when the user asks to unlock from the lock overlay.</summary>
    public event Action? UnlockRequested;

    public MainWindow()
    {
        InitializeComponent();

        // The sidebar drag gesture is tracked at window level with tunnelled
        // handlers: AtomUI's menu interaction re-captures the pointer to its own
        // container after a row press, so row-scoped handlers stop seeing moves on
        // a real desktop even though they work in the headless tests.
        AddHandler(PointerMovedEvent, OnCategoryDragMoved, RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(PointerReleasedEvent, OnCategoryDragReleased, RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(PointerCaptureLostEvent, OnCategoryDragCaptureLost, RoutingStrategies.Tunnel, handledEventsToo: true);
    }

    private CategoryDrag? _drag;

    private void OnCategoryDragMoved(object? sender, PointerEventArgs e)
    {
        if (_drag is not { } drag)
        {
            return;
        }

        if (drag.Track(e))
        {
            e.Handled = true;
        }
    }

    private void OnCategoryDragReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_drag is not { } drag)
        {
            return;
        }

        var started = drag.IsStarted;
        _drag = null;
        drag.Release(e);

        if (started)
        {
            e.Handled = true;
        }
    }

    private void OnCategoryDragCaptureLost(object? sender, PointerCaptureLostEventArgs e) => CancelCategoryDrag();

    /// <summary>
    /// Drops any in-flight drag. Called when the tree is rebuilt (every row is
    /// replaced) and when the window closes, so no marker is left behind.
    /// </summary>
    private void CancelCategoryDrag()
    {
        _drag?.Cancel();
        _drag = null;
    }

    /// <summary>Row <c>Border</c>s of the sidebar tree, in visual order.</summary>
    internal IEnumerable<Border> GetCategoryRows()
        => CategoryTree.GetVisualDescendants()
            .OfType<Border>()
            .Where(candidate => candidate.Classes.Contains("categoryRowLabel"));


    private MainViewModel? _categoryNodesViewModel;

    /// <summary>
    /// Assigns the row template to every category node. AtomUI only uses a node's
    /// own template, and falls back to a TreeDataTemplate that would render the
    /// node object itself, so the view owns this wiring (and re-applies it when
    /// the tree is rebuilt).
    /// </summary>
    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);

        if (_categoryNodesViewModel is not null)
        {
            _categoryNodesViewModel.CategoryNodes.CollectionChanged -= OnCategoryNodesChanged;
            _categoryNodesViewModel = null;
        }

        if (DataContext is MainViewModel viewModel)
        {
            _categoryNodesViewModel = viewModel;
            viewModel.CategoryNodes.CollectionChanged += OnCategoryNodesChanged;
            ApplyCategoryRowTemplates(viewModel);
        }
    }

    private void OnCategoryNodesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        // A rebuild replaces every row, so any drag markers point at controls that
        // are no longer in the tree; drop the gesture instead of leaking them.
        CancelCategoryDrag();
        ApplyCategoryRowTemplates(_categoryNodesViewModel);
    }

    private void ApplyCategoryRowTemplates(MainViewModel? viewModel)
    {
        if (viewModel is null || Resources["CategoryRowTemplate"] is not IDataTemplate template)
        {
            return;
        }

        Apply(viewModel.CategoryNodes);

        void Apply(IEnumerable<CategoryNavNode> nodes)
        {
            foreach (var node in nodes)
            {
                node.HeaderTemplate = template;
                Apply(node.Entries.OfType<CategoryNavNode>());
            }
        }
    }

    public void ShowLockOverlay()
    {
        ClipboardToast.IsVisible = false;
        LockOverlay.IsVisible = true;
        UnlockButton.Focus();
    }

    public void HideLockOverlay() => LockOverlay.IsVisible = false;

    private void OnUnlockClicked(object? sender, RoutedEventArgs e) => UnlockRequested?.Invoke();

    public void Attach(
        MainViewModel viewModel,
        ClipboardService clipboard,
        AutoLockService autoLock,
        Action? openSettings = null,
        Action<Window>? applyScreenGuard = null,
        Func<PasswordGeneratorOptions?>? generatorOptions = null)
    {
        DataContext = viewModel;
        _clipboard = clipboard;
        _autoLock = autoLock;
        _openSettings = openSettings;
        _applyScreenGuard = applyScreenGuard;
        _generatorOptions = generatorOptions;
        HookClipboardCleanup(clipboard);
        HideLockOverlay();

        if (_handlersAttached)
        {
            // Unlock after a lock re-attaches a fresh session; handlers must
            // not accumulate across lock/unlock cycles.
            return;
        }

        _handlersAttached = true;

        AddHandler(PointerMovedEvent, (_, _) => _autoLock.ReportActivity(), RoutingStrategies.Tunnel);
        AddHandler(KeyDownEvent, (_, _) => _autoLock.ReportActivity(), RoutingStrategies.Tunnel);

        PropertyChanged += (_, args) =>
        {
            if (args.Property == WindowStateProperty && WindowState == WindowState.Minimized)
            {
                Hide();
                _autoLock.SetMinimized(true);
            }
        };

        Activated += (_, _) => _autoLock.ReportActivity();
    }

    /// <summary>
    /// Clears a pending secret while the window (and its clipboard) is still
    /// alive. <c>Closed</c> is too late: the TopLevel is torn down by then and
    /// the cleanup silently fails, leaving the secret behind on exit.
    /// </summary>
    internal void HookClipboardCleanup(ClipboardService clipboard)
    {
        _clipboard = clipboard;
        if (_clipboardCleanupAttached)
        {
            return;
        }

        _clipboardCleanupAttached = true;
        Closing += (_, args) =>
        {
            // A cancelled close (the user chose "minimize to tray") keeps the
            // session alive, so the clipboard service must survive with it.
            if (!args.Cancel)
            {
                _clipboard?.Dispose();
            }
        };
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (LockOverlay.IsVisible)
        {
            // While locked in place the window must not react to any shortcut.
            if (e.Key is Key.Enter or Key.Space && e.KeyModifiers == KeyModifiers.None)
            {
                UnlockRequested?.Invoke();
                e.Handled = true;
                return;
            }

            e.Handled = true;
            base.OnKeyDown(e);
            return;
        }

        if (DataContext is not MainViewModel viewModel)
        {
            base.OnKeyDown(e);
            return;
        }

        var ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control);
        var shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        var alt = e.KeyModifiers.HasFlag(KeyModifiers.Alt);
        var focusedIsTextBox = FocusManager?.GetFocusedElement() is TextBox;

        if (ctrl && !shift && !alt && e.Key == Key.F)
        {
            SearchBox.Focus();
            e.Handled = true;
        }
        else if (ctrl && !shift && !alt && e.Key == Key.N)
        {
            viewModel.AddEntryCommand.Execute(null);
            e.Handled = true;
        }
        else if (ctrl && !shift && !alt && e.Key == Key.E)
        {
            viewModel.BeginEditCommand.Execute(null);
            e.Handled = true;
        }
        else if (ctrl && !shift && !alt && e.Key == Key.S)
        {
            viewModel.SaveEntryCommand.Execute(null);
            e.Handled = true;
        }
        else if (ctrl && !shift && !alt && e.Key == Key.G)
        {
            OnGeneratePasswordClicked(this, new RoutedEventArgs());
            e.Handled = true;
        }
        else if (ctrl && shift && !alt && e.Key == Key.C)
        {
            viewModel.CopyPasswordCommand.Execute(null);
            e.Handled = true;
        }
        else if (ctrl && alt && !shift && e.Key == Key.C)
        {
            viewModel.CopyTotpCommand.Execute(null);
            e.Handled = true;
        }
        else if (ctrl && shift && !alt && e.Key == Key.L)
        {
            viewModel.LockCommand.Execute(null);
            e.Handled = true;
        }
        else if (ctrl && !shift && !alt && e.Key is Key.D1 or Key.D2 or Key.D3 or Key.D4)
        {
            viewModel.ShowSmartViewCommand.Execute(e.Key switch
            {
                Key.D1 => 1,
                Key.D2 => 2,
                Key.D3 => 3,
                _ => 4,
            });
            e.Handled = true;
        }
        else if (!ctrl && e.Key == Key.Delete && !focusedIsTextBox)
        {
            // Soft delete: recoverable from the recycle bin, so no confirmation
            // (only the irreversible purge/empty actions ask).
            viewModel.DeleteEntryCommand.Execute(null);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape && viewModel.IsEditing)
        {
            viewModel.CancelEditCommand.Execute(null);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape && !string.IsNullOrEmpty(viewModel.SearchText))
        {
            viewModel.ClearSearchCommand.Execute(null);
            e.Handled = true;
        }
        else
        {
            base.OnKeyDown(e);
        }
    }

    private void OnRowCopyUsernameClicked(object? sender, RoutedEventArgs e)
    {
        CopyFeedback.Flash(sender as AtomUIButton);
        InvokeForRow(sender, viewModel => viewModel.CopyUsernameCommand.Execute(null));
    }

    private void OnOpenUrlClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: string url } && DataContext is MainViewModel viewModel)
        {
            viewModel.OpenUrlCommand.Execute(url);
        }
    }

    private void OnDeleteEntryClicked(object? sender, RoutedEventArgs e)
        => (DataContext as MainViewModel)?.DeleteEntryCommand.Execute(null);

    /// <summary>Permanent deletion from the recycle bin — the one that confirms.</summary>
    private async void OnPurgeEntryClicked(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel viewModel || viewModel.SelectedEntry is not { } entry)
        {
            return;
        }

        try
        {
            var dialog = new ConfirmWindow(
                Loc.T("Main_PurgeEntryTitle"),
                Loc.Format("Main_PurgeEntryMessage", entry.Title));
            if (await dialog.ShowDialog<bool>(this))
            {
                viewModel.PurgeSelectedEntry();
            }
        }
        catch (Exception)
        {
        }
    }

    private async void OnEmptyRecycleBinClicked(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel viewModel)
        {
            return;
        }

        try
        {
            var dialog = new ConfirmWindow(
                Loc.T("Main_EmptyRecycleBinTitle"),
                Loc.Format("Main_EmptyRecycleBinMessage", viewModel.DeletedCount));
            if (await dialog.ShowDialog<bool>(this))
            {
                viewModel.EmptyRecycleBin();
            }
        }
        catch (Exception)
        {
        }
    }

    private void OnSettingsClicked(object? sender, RoutedEventArgs e) => _openSettings?.Invoke();

    /// <summary>Hero shortcut: pick a CSV export and import it (shared flow).</summary>
    private async void OnImportDataClicked(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel viewModel)
        {
            return;
        }

        try
        {
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = Loc.T("Settings_ImportPickTitle"),
                AllowMultiple = false,
                FileTypeFilter = [new FilePickerFileType(Loc.T("Settings_CsvFileType")) { Patterns = ["*.csv"] }],
            });

            if (files.Count > 0)
            {
                await viewModel.ImportCsvFromAsync(files[0].Path.LocalPath);
            }
        }
        catch (Exception)
        {
            // The picker is best effort; the settings page stays available.
        }
    }

    private void OnRowCopyPasswordClicked(object? sender, RoutedEventArgs e)
    {
        CopyFeedback.Flash(sender as AtomUIButton);
        InvokeForRow(sender, viewModel => viewModel.CopyPasswordCommand.Execute(null));
    }

    private void OnRowCopyTotpClicked(object? sender, RoutedEventArgs e)
    {
        CopyFeedback.Flash(sender as AtomUIButton);
        InvokeForRow(sender, viewModel => viewModel.CopyTotpCommand.Execute(null));
    }

    /// <summary>
    /// Copy buttons in the detail panel keep their command (the view-model owns the
    /// clipboard and the status message) and only add the transient feedback here.
    /// </summary>
    private void OnCopyFeedbackClicked(object? sender, RoutedEventArgs e)
        => CopyFeedback.Flash(sender as AtomUIButton);

    private async void OnNewCategoryClicked(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel viewModel)
        {
            return;
        }

        try
        {
            var dialog = new TextPromptWindow(
                Loc.T("Main_NewCategoryTitle"),
                Loc.T("Main_CategoryNameLabel"),
                string.Empty,
                name => viewModel.ValidateCategoryName(name),
                initialColor: null,
                showColorPicker: true);
            var result = await dialog.ShowDialog<string?>(this);
            if (!string.IsNullOrEmpty(result))
            {
                viewModel.TryCreateCategory(result, out _, CategoryPalette.ToStorage(dialog.SelectedColor));
            }
        }
        catch (Exception)
        {
        }
    }

    /// <summary>
    /// Category rows select from their label area (parents included — AtomUI treats a
    /// row with children as a submenu header and would only toggle it). Marking the
    /// press handled keeps the NavMenu's row activation out of the way, while the
    /// arrow column and the row padding still reach the control and toggle.
    /// <para>
    /// The same press may start a drag (see <see cref="CategoryDrag"/>): the gesture
    /// only arms here, and the first move past the threshold turns it into a drag, so
    /// a plain click behaves exactly as before.
    /// </para>
    /// </summary>
    private void OnCategoryRowLabelPress(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }

        e.Handled = true;
        SelectCategoryRow(sender);

        if (sender is Border { DataContext: CategoryNavNode node } row && node.IsUserCategory)
        {
            _drag = CategoryDrag.Arm(this, node, row, e);
            return;
        }

        CancelCategoryDrag();
    }

    private void OnCategoryRowLabelKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key is not (Key.Enter or Key.Space))
        {
            return;
        }

        e.Handled = true;
        SelectCategoryRow(sender);
    }

    private void SelectCategoryRow(object? sender)
    {
        if (sender is Control { DataContext: CategoryNavNode node } && DataContext is MainViewModel viewModel)
        {
            viewModel.SelectedCategoryNode = node;
        }
    }

    /// <summary>
    /// Per-row "…" menu. The row itself expands or selects the node, so every
    /// category action lives in this flyout instead of inline buttons.
    /// </summary>
    private void OnCategoryActionsClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: CategoryNavNode node } button ||
            DataContext is not MainViewModel viewModel ||
            node.Item.CategoryId is not { } id)
        {
            return;
        }

        var flyout = new MenuFlyout();

        var newChild = new AtomUIMenuItem { Header = Loc.T("Main_CategoryNewChild") };
        newChild.Click += async (_, _) => await CreateChildCategoryAsync(id, node.Item);
        flyout.Items.Add(newChild);

        var rename = new AtomUIMenuItem { Header = Loc.T("Main_RenameCategoryTooltip") };
        rename.Click += async (_, _) => await RenameCategoryAsync(id, node.Item);
        flyout.Items.Add(rename);

        var move = new AtomUIMenuItem { Header = Loc.T("Main_CategoryMoveTo") };
        move.Click += async (_, _) => await MoveCategoryAsync(id, node.Item);
        flyout.Items.Add(move);

        if (viewModel.CategoryNodes.Count > 1)
        {
            var merge = new AtomUIMenuItem { Header = Loc.T("Main_MergeCategory") };
            merge.Click += async (_, _) => await MergeCategoryAsync(id, node.Item);
            flyout.Items.Add(merge);
        }

        flyout.Items.Add(new MenuSeparator());

        var delete = new AtomUIMenuItem { Header = Loc.T("Main_DeleteCategoryTooltip") };
        delete.Click += async (_, _) => await DeleteCategoryAsync(id, node.Item);
        flyout.Items.Add(delete);

        flyout.ShowAt(button);
    }

    private async Task CreateChildCategoryAsync(Guid parentId, CategoryItem parent)
    {
        if (DataContext is not MainViewModel viewModel)
        {
            return;
        }

        try
        {
            var dialog = new TextPromptWindow(
                Loc.Format("Main_CategoryNewChildTitle", parent.DisplayName),
                Loc.T("Main_CategoryNameLabel"),
                string.Empty,
                name => viewModel.ValidateCategoryName(name, excludeId: null, parentId),
                initialColor: null,
                showColorPicker: true);
            var result = await dialog.ShowDialog<string?>(this);
            if (!string.IsNullOrEmpty(result))
            {
                if (!viewModel.TryCreateCategory(result, out var error, CategoryPalette.ToStorage(dialog.SelectedColor), parentId) &&
                    error is not null)
                {
                    viewModel.StatusMessage = error;
                }
            }
        }
        catch (Exception)
        {
        }
    }

    private async Task RenameCategoryAsync(Guid id, CategoryItem item)
    {
        if (DataContext is not MainViewModel viewModel)
        {
            return;
        }

        try
        {
            var dialog = new TextPromptWindow(
                Loc.T("Main_RenameCategoryTitle"),
                Loc.T("Main_CategoryNameLabel"),
                item.DisplayName,
                name => viewModel.ValidateCategoryName(name, id),
                initialColor: item.Color,
                showColorPicker: true);
            var result = await dialog.ShowDialog<string?>(this);
            if (!string.IsNullOrEmpty(result))
            {
                viewModel.TryRenameCategory(id, result, out _);
                viewModel.SetCategoryColor(id, CategoryPalette.ToStorage(dialog.SelectedColor));
            }
        }
        catch (Exception)
        {
        }
    }

    private async Task MoveCategoryAsync(Guid id, CategoryItem item)
    {
        if (DataContext is not MainViewModel viewModel)
        {
            return;
        }

        try
        {
            var message = Loc.Format("Main_CategoryMoveMessage", item.DisplayName);
            while (true)
            {
                var dialog = new CategoryParentWindow(
                    Loc.T("Main_CategoryMoveTo"),
                    message,
                    viewModel.BuildParentChoices(id),
                    item.ParentId,
                    Loc.T("Main_CategoryMoveAction"),
                    Loc.T("Main_Cancel"));
                if (!await dialog.ShowDialog<bool>(this))
                {
                    return;
                }

                if (viewModel.TryMoveCategory(id, dialog.SelectedParentId, out var error))
                {
                    return;
                }

                // Keep the picker open so the reason (duplicate name, depth cap)
                // stays visible and the user can pick another target right away.
                message = Loc.Format("Main_CategoryMoveMessage", item.DisplayName) +
                          Environment.NewLine + Environment.NewLine + error;
            }
        }
        catch (Exception)
        {
        }
    }

    private async Task MergeCategoryAsync(Guid id, CategoryItem item)
    {
        if (DataContext is not MainViewModel viewModel)
        {
            return;
        }

        try
        {
            var choices = viewModel.Categories
                .Where(category => category.CategoryId is { } candidate &&
                                   candidate != id &&
                                   !viewModel.GetCategorySubtreeIds(id).Contains(candidate))
                .Select(category => new CategoryChoice(category.CategoryId, category.DisplayName))
                .ToList();
            if (choices.Count == 0)
            {
                return;
            }

            var message = Loc.Format("Main_CategoryMergeMessage", item.DisplayName);
            while (true)
            {
                var dialog = new CategoryParentWindow(
                    Loc.T("Main_MergeCategory"),
                    message,
                    choices,
                    null,
                    Loc.T("Main_CategoryMergeAction"),
                    Loc.T("Main_Cancel"));
                if (!await dialog.ShowDialog<bool>(this) || dialog.SelectedParentId is not { } targetId)
                {
                    return;
                }

                if (viewModel.TryMergeCategory(id, targetId, out var error))
                {
                    return;
                }

                message = Loc.Format("Main_CategoryMergeMessage", item.DisplayName) +
                          Environment.NewLine + Environment.NewLine + error;
            }
        }
        catch (Exception)
        {
        }
    }

    private async Task DeleteCategoryAsync(Guid id, CategoryItem item)
    {
        if (DataContext is not MainViewModel viewModel)
        {
            return;
        }

        try
        {
            var (children, entries) = viewModel.DescribeCategory(id);
            var message = children > 0
                ? Loc.Format("Main_DeleteCategoryWithChildren", item.DisplayName, children)
                : Loc.Format("Main_DeleteCategoryMessage", item.DisplayName);
            if (entries > 0 && children > 0)
            {
                message += Environment.NewLine + Loc.Format("Main_DeleteCategoryEntries", entries);
            }

            var dialog = new ConfirmWindow(Loc.T("Main_DeleteCategoryTitle"), message);
            if (await dialog.ShowDialog<bool>(this))
            {
                viewModel.DeleteCategory(id, CategoryDeleteMode.PromoteChildren);
            }
        }
        catch (Exception)
        {
        }
    }

    private void OnSortButtonClicked(object? sender, RoutedEventArgs e)
        => OpenFlyoutOnKeyboard(SortButton);

    private void OnSortFlyoutOpened(object? sender, EventArgs e)
    {
        if (DataContext is not MainViewModel viewModel)
        {
            return;
        }

        // The flyout lives outside the normal binding scope, so the radio
        // state is synced explicitly whenever the menu opens.
        SortByNameItem.IsChecked = viewModel.IsSortByName;
        SortByRecentItem.IsChecked = viewModel.IsSortByRecent;
    }

    private void OnSortByNameClicked(object? sender, RoutedEventArgs e)
        => (DataContext as MainViewModel)?.SortByNameCommand.Execute(null);

    private void OnSortByRecentClicked(object? sender, RoutedEventArgs e)
        => (DataContext as MainViewModel)?.SortByRecentCommand.Execute(null);

    private void OnThemeButtonClicked(object? sender, RoutedEventArgs e)
        => OpenFlyoutOnKeyboard(ThemeButton);

    private void OnThemeFlyoutOpened(object? sender, EventArgs e)
    {
        if (DataContext is not MainViewModel viewModel)
        {
            return;
        }

        // The flyout lives outside the normal binding scope, so the radio
        // state is synced explicitly whenever the menu opens.
        ThemeSystemItem.IsChecked = viewModel.ThemePreference == "system";
        ThemeLightItem.IsChecked = viewModel.ThemePreference == "light";
        ThemeDarkItem.IsChecked = viewModel.ThemePreference == "dark";
    }

    private void OnThemeSystemClicked(object? sender, RoutedEventArgs e) => SetTheme("system");

    private void OnThemeLightClicked(object? sender, RoutedEventArgs e) => SetTheme("light");

    private void OnThemeDarkClicked(object? sender, RoutedEventArgs e) => SetTheme("dark");

    private void SetTheme(string theme)
        => (DataContext as MainViewModel)?.SetThemeCommand.Execute(theme);

    private void OnLanguageButtonClicked(object? sender, RoutedEventArgs e)
        => OpenFlyoutOnKeyboard(LanguageButton);

    private void OnLanguageFlyoutOpened(object? sender, EventArgs e)
    {
        if (DataContext is not MainViewModel viewModel)
        {
            return;
        }

        LanguageSystemItem.IsChecked = viewModel.LanguagePreference == Loc.System;
        LanguageZhItem.IsChecked = viewModel.LanguagePreference == Loc.Chinese;
        LanguageEnItem.IsChecked = viewModel.LanguagePreference == Loc.English;
    }

    private void OnLanguageSystemClicked(object? sender, RoutedEventArgs e) => SetLanguage(Loc.System);

    private void OnLanguageZhClicked(object? sender, RoutedEventArgs e) => SetLanguage(Loc.Chinese);

    private void OnLanguageEnClicked(object? sender, RoutedEventArgs e) => SetLanguage(Loc.English);

    private void SetLanguage(string language)
        => (DataContext as MainViewModel)?.SetLanguageCommand.Execute(language);

    /// <summary>
    /// Pointer presses open a dropdown flyout through FlyoutStateHelper; the
    /// Click event only fires for keyboard activation (Space/Enter), so the
    /// menu stays reachable without a mouse.
    /// </summary>
    private static void OpenFlyoutOnKeyboard(AtomUI.Desktop.Controls.DropdownButton button)
    {
        var flyout = button.DropdownFlyout;
        if (flyout is not null && !flyout.IsOpen)
        {
            flyout.ShowAt(button);
        }
    }

    private void InvokeForRow(object? sender, Action<MainViewModel> action)
    {
        if (sender is Button { DataContext: PasswordEntry entry } && DataContext is MainViewModel viewModel)
        {
            viewModel.SelectedEntry = entry;
            action(viewModel);
        }
    }

    private async void OnGeneratePasswordClicked(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (DataContext is not MainViewModel viewModel)
            {
                return;
            }

            var dialog = new GeneratorWindow
            {
                DataContext = new GeneratorViewModel(_clipboard, _generatorOptions?.Invoke()),
            };

            // The native window handle only exists once the dialog is shown;
            // applying the capture guard earlier would silently do nothing.
            dialog.Opened += (_, _) => _applyScreenGuard?.Invoke(dialog);

            var result = await dialog.ShowDialog<string?>(this);
            if (!string.IsNullOrEmpty(result))
            {
                viewModel.EditPassword = result;
            }
        }
        catch (Exception)
        {
        }
    }
}
