using System.Collections.Specialized;
using System.Linq;
using AegisVault.App.Localization;
using AegisVault.App.Services;
using AegisVault.App.Theme;
using AegisVault.App.ViewModels;
using AegisVault.Core.Models;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Controls.Templates;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Transformation;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
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

    /// <summary>The row <c>Border</c> that renders a category, or null when not realised.</summary>
    internal Border? FindCategoryRow(Guid categoryId)
        => GetCategoryRows()
            .FirstOrDefault(candidate => candidate.DataContext is CategoryTreeRow node &&
                                         node.Item.CategoryId == categoryId);

    /// <summary>
    /// Y position of every realised category row, keyed by category. Sampled before a
    /// drop so the rows that change place can slide from their old spot to the new
    /// one (a FLIP animation).
    /// </summary>
    internal Dictionary<Guid, double> CaptureCategoryRowPositions()
    {
        var positions = new Dictionary<Guid, double>();
        foreach (var row in GetCategoryRows())
        {
            if (row.DataContext is CategoryTreeRow { Item.CategoryId: { } id } &&
                row.TranslatePoint(new Point(0, 0), CategoryTree) is { } top)
            {
                positions[id] = top.Y;
            }
        }

        return positions;
    }

    /// <summary>
    /// Slides the rows that changed place into their new position. Runs after the
    /// model update, so each row's final layout is already correct; the animation only
    /// offsets it by the distance it travelled and eases that offset back to zero.
    /// <para>
    /// This is the piece that makes a swap read as motion: a reorder recreates the
    /// moved row's container and its neighbours shift by one row height, which
    /// without an offset animation is an instantaneous jump.
    /// </para>
    /// </summary>
    internal void AnimateCategoryRowSwap(IReadOnlyDictionary<Guid, double> before)
    {
        // Wait for the layout pass that follows the collection change: the rows only
        // know their new positions afterwards, and measuring before that would compare
        // against stale bounds.
        Avalonia.Threading.Dispatcher.UIThread.Post(() => Animate(before), Avalonia.Threading.DispatcherPriority.Loaded);

        void Animate(IReadOnlyDictionary<Guid, double> previous)
        {
            if (AppTheme.ReduceMotion)
            {
                return;
            }

            var offsets = new List<(Border Row, double Delta)>();
            foreach (var row in GetCategoryRows())
            {
                if (row.DataContext is not CategoryTreeRow { Item.CategoryId: { } id } ||
                    !previous.TryGetValue(id, out var oldTop) ||
                    row.TranslatePoint(new Point(0, 0), CategoryTree) is not { } current)
                {
                    continue;
                }

                var delta = oldTop - current.Y;
                if (Math.Abs(delta) < 0.5)
                {
                    continue;
                }

                offsets.Add((row, delta));
            }

            if (offsets.Count == 0)
            {
                return;
            }

            // Drive the slide frame by frame. A Transitions-based version depends on
            // the style pass having installed the transition before the offset is
            // cleared, which is easy to get wrong and silently does nothing; stepping
            // RenderTransform directly is deterministic and never touches layout.
            RunSlide(offsets);
        }
    }

    /// <summary>Duration of the post-drop slide.</summary>
    private static readonly TimeSpan SlideDuration = TimeSpan.FromMilliseconds(180);

    /// <summary>
    /// Slides each row from an offset back to zero. <see cref="Visual.RenderTransform"/>
    /// is composited, so this never triggers a layout or paint pass of the sidebar.
    /// </summary>
    private void RunSlide(List<(Border Row, double Delta)> offsets)
    {
        var started = System.Diagnostics.Stopwatch.StartNew();
        var timer = new Avalonia.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(15),
        };

        void Apply(double progress)
        {
            // Ease-out cubic: quick departure, soft landing.
            var eased = 1 - Math.Pow(1 - progress, 3);

            foreach (var (row, delta) in offsets)
            {
                if (!row.IsAttachedToVisualTree())
                {
                    continue;
                }

                var remaining = delta * (1 - eased);
                row.RenderTransform = Math.Abs(remaining) < 0.2
                    ? null
                    : TransformOperations.Parse(
                        $"translate(0px, {remaining.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)}px)");
            }
        }

        timer.Tick += (_, _) =>
        {
            var progress = started.Elapsed.TotalMilliseconds / SlideDuration.TotalMilliseconds;
            if (progress >= 1)
            {
                timer.Stop();
                foreach (var (row, _) in offsets)
                {
                    row.RenderTransform = null;
                }

                return;
            }

            Apply(progress);
        };

        Apply(0);
        timer.Start();
    }

    /// <summary>
    /// Moves the floating "what am I dragging" chip. Position is a plain
    /// <see cref="Canvas"/> coordinate, so following the pointer never invalidates
    /// the sidebar's layout — only this one small element re-renders.
    /// </summary>
    internal void ShowCategoryDragGhost(string label, Point position)
    {
        if (CategoryDragGhostLabel.Text != label)
        {
            CategoryDragGhostLabel.Text = label;
        }

        if (!CategoryDragGhost.IsVisible)
        {
            CategoryDragGhost.IsVisible = true;
        }

        // Sit just below-right of the cursor, clamped inside the window.
        var x = Math.Min(position.X + 14, Bounds.Width - CategoryDragGhost.Bounds.Width - 8);
        var y = Math.Min(position.Y + 10, Bounds.Height - CategoryDragGhost.Bounds.Height - 8);
        Canvas.SetLeft(CategoryDragGhost, Math.Max(0, x));
        Canvas.SetTop(CategoryDragGhost, Math.Max(0, y));
    }

    internal void HideCategoryDragGhost() => CategoryDragGhost.IsVisible = false;


    private MainViewModel? _categoryRowsViewModel;

    /// <summary>
    /// Watches the flat row list so an in-flight drag is dropped when rows are rebuilt
    /// (the markers would otherwise point at controls that left the tree).
    /// </summary>
    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);

        if (_categoryRowsViewModel is not null)
        {
            _categoryRowsViewModel.CategoryRows.CollectionChanged -= OnCategoryRowsChanged;
            _categoryRowsViewModel = null;
        }

        if (DataContext is MainViewModel viewModel)
        {
            _categoryRowsViewModel = viewModel;
            viewModel.CategoryRows.CollectionChanged += OnCategoryRowsChanged;
        }
    }

    private void OnCategoryRowsChanged(object? sender, NotifyCollectionChangedEventArgs e)
        => CancelCategoryDrag();

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
    /// A category row press selects it, toggles the branch when the chevron is hit, and
    /// arms the drag gesture. The press is marked handled so the ListBoxItem does not
    /// also process it; selection is applied explicitly here.
    /// <para>
    /// The same press may start a drag (see <see cref="CategoryDrag"/>): the gesture only
    /// arms here, and the first move past the threshold turns it into a drag, so a plain
    /// click behaves exactly like a click.
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

        if (sender is not Border { DataContext: CategoryTreeRow row } border)
        {
            CancelCategoryDrag();
            return;
        }

        // Hitting the chevron column expands/collapses instead of starting a drag. The
        // chevron is the leading column of the indented grid, so its hit band starts at
        // the row's indent and spans the chevron width.
        var position = e.GetPosition(border);
        var indent = Math.Max(0, row.Depth - 1) * AegisVault.App.Converters.CategoryIndentConverter.IndentPerLevel;
        if (row.HasChildren &&
            position.X >= indent &&
            position.X <= indent + ChevronHitWidth)
        {
            (DataContext as MainViewModel)?.ToggleCategoryExpansion(row);
            CancelCategoryDrag();
            return;
        }

        if (row.IsUserCategory)
        {
            _drag = CategoryDrag.Arm(this, row, border, e);
            return;
        }

        CancelCategoryDrag();
    }

    /// <summary>Width of the leading chevron column that toggles a branch.</summary>
    private const double ChevronHitWidth = 18;

    /// <summary>
    /// Keyboard support for the sidebar tree: Left/Right collapse/expand the focused
    /// branch. Up/Down and Home/End already come from the ListBox.
    /// </summary>
    private void OnCategoryRowLabelKeyDown(object? sender, KeyEventArgs e)
    {
        if (sender is not Border { DataContext: CategoryTreeRow row } ||
            DataContext is not MainViewModel viewModel)
        {
            return;
        }

        switch (e.Key)
        {
            case Key.Right when row.HasChildren && !row.IsExpanded:
                e.Handled = true;
                viewModel.ToggleCategoryExpansion(row);
                break;
            case Key.Left when row.HasChildren && row.IsExpanded:
                e.Handled = true;
                viewModel.ToggleCategoryExpansion(row);
                break;
            case Key.Enter or Key.Space:
                e.Handled = true;
                SelectCategoryRow(sender);
                break;
        }
    }

    private void SelectCategoryRow(object? sender)
    {
        if (sender is Control { DataContext: CategoryTreeRow row } && DataContext is MainViewModel viewModel)
        {
            viewModel.SelectedCategoryRow = row;
        }
    }

    /// <summary>
    /// Per-row "…" menu. The row itself expands or selects the node, so every
    /// category action lives in this flyout instead of inline buttons.
    /// </summary>
    private void OnCategoryActionsClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: CategoryTreeRow node } button ||
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

        if (viewModel.CategoryRows.Count > 1)
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
