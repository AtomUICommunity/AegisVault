using System.Linq;
using AegisVault.App.Views;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Xunit;
using AtomUIMenuItem = AtomUI.Desktop.Controls.MenuItem;

namespace AegisVault.App.Tests;

public sealed class MainWindowTests
{
    [Fact]
    public Task LockOverlayUnlockButtonRaisesUnlockRequested() => Headless.Run(() =>
    {
        var window = new MainWindow();
        var raised = 0;
        window.UnlockRequested += () => raised++;

        window.ShowLockOverlay();

        var button = window.FindControl<Button>("UnlockButton");
        Assert.NotNull(button);
        Assert.True(button!.IsVisible);

        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

        Assert.Equal(1, raised);
    });

    [Fact]
    public Task UnlockRequestedSurvivesLockUnlockCycle() => Headless.Run(() =>
    {
        var window = new MainWindow();
        var raised = 0;
        window.UnlockRequested += () => raised++;

        // Lock, then hide the overlay as an unlock would, then lock again:
        // the overlay button must keep working after every cycle.
        window.ShowLockOverlay();
        window.HideLockOverlay();
        window.ShowLockOverlay();

        var button = window.FindControl<Button>("UnlockButton")!;
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

        Assert.Equal(2, raised);
    });

    [Fact]
    public Task SearchBoxShowsMagnifierPrefixIcon() => Headless.Run(() =>
    {
        var window = new MainWindow();

        var search = window.FindControl<AtomUI.Desktop.Controls.LineEdit>("SearchBox");

        Assert.NotNull(search);
        Assert.NotNull(search!.InnerLeftContent);
    });

    [Fact]
    public Task SortButtonExposesRadioMenuWithBothModes() => Headless.Run(() =>
    {
        var window = new MainWindow();

        var sort = window.FindControl<AtomUI.Desktop.Controls.DropdownButton>("SortButton");
        Assert.NotNull(sort);
        Assert.False(sort!.IsArrowVisible);

        var items = sort.DropdownFlyout?.Items.OfType<AtomUIMenuItem>().ToList();
        Assert.NotNull(items);
        Assert.Equal(2, items!.Count);
        Assert.All(items, item => Assert.Equal(MenuItemToggleType.Radio, item.ToggleType));
        Assert.All(items, item => Assert.Equal("SortMode", item.GroupName));
    });

    [Fact]
    public Task ThemeButtonExposesThreeModes() => Headless.Run(() =>
    {
        var window = new MainWindow();

        var theme = window.FindControl<AtomUI.Desktop.Controls.DropdownButton>("ThemeButton");
        Assert.NotNull(theme);
        Assert.False(theme!.IsArrowVisible);

        var items = ReadRadioItems(theme, "ThemeMode");
        Assert.Equal(3, items.Count);
    });

    [Fact]
    public Task LanguageButtonExposesThreeModes() => Headless.Run(() =>
    {
        var window = new MainWindow();

        var language = window.FindControl<AtomUI.Desktop.Controls.DropdownButton>("LanguageButton");
        Assert.NotNull(language);
        Assert.False(language!.IsArrowVisible);

        var items = ReadRadioItems(language, "LanguageMode");
        Assert.Equal(3, items.Count);
    });

    [Fact]
    public Task FilteringKeepsTheSelectionWhenTheEntryStillMatches() => Headless.Run(() =>
    {
        var directory = Path.Combine(Path.GetTempPath(), "aegis-ui-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            using var vault = Core.Services.VaultService.CreateNew(
                Path.Combine(directory, "vault.aegis"),
                "master password"u8.ToArray(),
                new Core.Services.VaultOptions
                {
                    Kdf = new Core.Models.KdfParameters
                    {
                        Algorithm = Core.Models.KdfParameters.AlgorithmArgon2id,
                        Iterations = 3,
                        MemoryBytes = 8L * 1024 * 1024,
                    },
                });
            vault.AddEntry(new Core.Models.PasswordEntry { Title = "GitHub", Username = "octocat" });
            vault.AddEntry(new Core.Models.PasswordEntry { Title = "Mail", Username = "me@example.com" });

            using var viewModel = new ViewModels.MainViewModel(vault);
            var window = new MainWindow { DataContext = viewModel };
            window.Show();

            var list = window.GetVisualDescendants().OfType<ListBox>().First(candidate =>
                (candidate.GetValue(AutomationProperties.AutomationIdProperty) as string) == "EntriesList");
            list.SelectedIndex = 0;
            var selected = viewModel.SelectedEntry;
            Assert.NotNull(selected);

            // The filter repopulates the bound collection; the selection must
            // survive as long as the entry still matches.
            viewModel.SearchText = selected!.Title;

            Assert.Same(selected, viewModel.SelectedEntry);
            Assert.Equal(0, list.SelectedIndex);

            window.Close();
        }
        finally
        {
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    });

    [Fact]
    public Task DetailActionBarIsPinnedOutsideTheScrollingRegion() => Headless.Run(() =>
    {
        // UX-3: the detail action bar (Cancel/Save, Edit/Delete) sits in its own
        // grid row below the ScrollViewer so it stays reachable without scrolling.
        var window = new MainWindow();

        var bar = window.FindControl<Border>("DetailActionBar");

        Assert.NotNull(bar);
        Assert.DoesNotContain(bar!.GetVisualAncestors(), ancestor => ancestor is ScrollViewer);
    });

    [Fact]
    public Task TotpSeedEditorIsMaskedUntilRevealed() => Headless.Run(() =>
    {
        var window = new MainWindow();

        var box = window.FindControl<AtomUI.Desktop.Controls.LineEdit>("TotpSecretBox");

        Assert.NotNull(box);
        Assert.NotEqual('\0', box!.PasswordChar);
        Assert.False(box.RevealPassword);
    });

    [Fact]
    public Task ClosingClearsACopiedSecret() => Headless.RunAsync<object?>(async () =>
    {
        var fake = new FakeClipboardAccess();
        using var clipboard = new Services.ClipboardService(fake, () => new Core.Models.UserConfig());
        var window = new MainWindow();
        window.HookClipboardCleanup(clipboard);
        window.Show();

        await clipboard.CopyAsync("s3cret");
        Assert.Equal("s3cret", fake.Text);

        // Closing fires while the window is still alive, so the clipboard is
        // still reachable and the secret is cleared before exit.
        window.Close();

        Assert.Null(fake.Text);
        return null;
    });

    [Fact]
    public Task CategoryTreeRendersRowsWithAnActionMenuAndExpands() => Headless.Run(() =>
    {
        var directory = Path.Combine(Path.GetTempPath(), "aegis-ui-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            using var vault = Core.Services.VaultService.CreateNew(
                Path.Combine(directory, "vault.aegis"),
                "master password"u8.ToArray(),
                new Core.Services.VaultOptions
                {
                    Kdf = new Core.Models.KdfParameters
                    {
                        Algorithm = Core.Models.KdfParameters.AlgorithmArgon2id,
                        Iterations = 3,
                        MemoryBytes = 8L * 1024 * 1024,
                    },
                });

            var root = vault.AddCategory("Work");
            var child = vault.AddCategory("Servers", parentId: root.Id);
            vault.AddEntry(new Core.Models.PasswordEntry { Title = "Host", CategoryId = child.Id });

            using var viewModel = new ViewModels.MainViewModel(vault);
            var window = new MainWindow { DataContext = viewModel };
            window.Show();

            var tree = CategoryTree(window);
            Assert.NotNull(tree);

            // Every branch starts expanded, so both rows are present from the start.
            var actionButtons = window.GetVisualDescendants().OfType<Button>()
                .Where(button => (button.GetValue(AutomationProperties.AutomationIdProperty) as string) == "CategoryActionsButton")
                .ToList();
            Assert.Equal(2, actionButtons.Count);
            Assert.Contains(actionButtons, button => ((ViewModels.CategoryTreeRow)button.DataContext!).Item.DisplayName == "Work");
            Assert.Contains(actionButtons, button => ((ViewModels.CategoryTreeRow)button.DataContext!).Item.DisplayName == "Servers");

            // Collapsing the branch through its chevron removes the nested row. The
            // chevron sits at the left edge of the row, which is also the click target.
            ClickAt(window, CategoryLabel(window, "Work")!, 6);
            Assert.Null(CategoryLabel(window, "Servers"));

            // Expanding it again brings the nested row back.
            ClickAt(window, CategoryLabel(window, "Work")!, 6);
            Assert.NotNull(CategoryLabel(window, "Servers"));

            var nested = window.GetVisualDescendants().OfType<Button>()
                .Where(button => (button.GetValue(AutomationProperties.AutomationIdProperty) as string) == "CategoryActionsButton")
                .Select(button => ((ViewModels.CategoryTreeRow)button.DataContext!).Item.DisplayName)
                .ToList();
            Assert.Contains("Servers", nested);

            window.Close();
        }
        finally
        {
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    });

    [Fact]
    public Task CategoryRowsSpanTheSidebarAndKeepCountsAligned() => Headless.Run(() =>
    {
        var directory = Path.Combine(Path.GetTempPath(), "aegis-ui-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            using var vault = Core.Services.VaultService.CreateNew(
                Path.Combine(directory, "vault.aegis"),
                "master password"u8.ToArray(),
                new Core.Services.VaultOptions
                {
                    Kdf = new Core.Models.KdfParameters
                    {
                        Algorithm = Core.Models.KdfParameters.AlgorithmArgon2id,
                        Iterations = 3,
                        MemoryBytes = 8L * 1024 * 1024,
                    },
                });

            // A parent (with a child) next to a leaf, plus an uncategorized entry so
            // the pseudo row is present as well.
            var root = vault.AddCategory("Work");
            vault.AddCategory("Servers", parentId: root.Id);
            vault.AddCategory("Personal");
            vault.AddEntry(new Core.Models.PasswordEntry { Title = "Loose" });

            using var viewModel = new ViewModels.MainViewModel(vault);
            var window = new MainWindow { DataContext = viewModel };
            window.Show();
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();

            var tree = CategoryTree(window);

            // Every row fills the sidebar width, so a click anywhere on it selects.
            var rows = CategoryLabelAll(window);
            Assert.Equal(4, rows.Count);
            Assert.All(rows, row => Assert.Equal(tree.Bounds.Width, row.Bounds.Width, 0.5));

            // The count sits at the same right edge on a parent row, a leaf row and
            // the uncategorized row: the reserved chevron column must not shift it.
            var countRightEdges = tree.GetVisualDescendants().OfType<Control>()
                .Where(candidate => (candidate.GetValue(AutomationProperties.AutomationIdProperty) as string) == "CategoryCountText")
                .Select(count => Math.Round(count.TranslatePoint(new Point(count.Bounds.Width, 0), tree)!.Value.X, 1))
                .Distinct()
                .ToList();
            Assert.Single(countRightEdges);

            // Nesting indents the row content by one step per level (AtomUI's Level
            // is zero based, so the top level stays flush with the other lists).
            double NameLeft(string displayName)
            {
                var name = tree.GetVisualDescendants().OfType<Control>()
                    .First(candidate =>
                        (candidate.GetValue(AutomationProperties.AutomationIdProperty) as string) == "CategoryNameText" &&
                        candidate.DataContext is ViewModels.CategoryTreeRow node &&
                        node.Item.DisplayName == displayName);
                return Math.Round(name.TranslatePoint(new Point(0, 0), tree)!.Value.X, 1);
            }

            var topLevel = NameLeft("Work");
            Assert.Equal(topLevel + 16, NameLeft("Servers"));
            Assert.Equal(topLevel, NameLeft("Personal"));

            window.Close();
        }
        finally
        {
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    });

    [Fact]
    public Task CategoryRowLabelClickSelectsParentsAndFiltersTheBranch() => Headless.Run(() =>
    {
        var directory = Path.Combine(Path.GetTempPath(), "aegis-ui-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            using var vault = Core.Services.VaultService.CreateNew(
                Path.Combine(directory, "vault.aegis"),
                "master password"u8.ToArray(),
                new Core.Services.VaultOptions
                {
                    Kdf = new Core.Models.KdfParameters
                    {
                        Algorithm = Core.Models.KdfParameters.AlgorithmArgon2id,
                        Iterations = 3,
                        MemoryBytes = 8L * 1024 * 1024,
                    },
                });

            var work = vault.AddCategory("Work");
            var servers = vault.AddCategory("Servers", parentId: work.Id);
            vault.AddEntry(new Core.Models.PasswordEntry { Title = "Direct", CategoryId = work.Id });
            vault.AddEntry(new Core.Models.PasswordEntry { Title = "Nested", CategoryId = servers.Id });

            using var viewModel = new ViewModels.MainViewModel(vault);
            var window = new MainWindow { DataContext = viewModel };
            window.Show();
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();

            var tree = CategoryTree(window);

            // A parent with children used to be unselectable (AtomUI treats it as a
            // submenu header and only toggles it), so its own entries could never be
            // listed. The label area now selects it and filters the whole branch.
            // (Click past the chevron column, which toggles instead of selecting.)
            ClickAt(window, CategoryLabel(tree, "Work")!, 80);

            Assert.Equal("Work", viewModel.SelectedCategory?.DisplayName);
            Assert.Equal(
                ["Direct", "Nested"],
                viewModel.FilteredEntries.Select(entry => entry.Title).OrderBy(title => title));

            // Leaves keep selecting exactly as before.
            ClickAt(window, CategoryLabel(tree, "Servers")!, 80);

            Assert.Equal("Servers", viewModel.SelectedCategory?.DisplayName);
            Assert.Equal(["Nested"], viewModel.FilteredEntries.Select(entry => entry.Title));

            window.Close();
        }
        finally
        {
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    });

    [Fact]
    public Task CategoryRowArrowKeepsTogglingWithoutSelecting() => Headless.Run(() =>
    {
        var directory = Path.Combine(Path.GetTempPath(), "aegis-ui-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            using var vault = Core.Services.VaultService.CreateNew(
                Path.Combine(directory, "vault.aegis"),
                "master password"u8.ToArray(),
                new Core.Services.VaultOptions
                {
                    Kdf = new Core.Models.KdfParameters
                    {
                        Algorithm = Core.Models.KdfParameters.AlgorithmArgon2id,
                        Iterations = 3,
                        MemoryBytes = 8L * 1024 * 1024,
                    },
                });

            var work = vault.AddCategory("Work");
            vault.AddCategory("Servers", parentId: work.Id);

            using var viewModel = new ViewModels.MainViewModel(vault);
            var window = new MainWindow { DataContext = viewModel };
            window.Show();
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();

            var tree = CategoryTree(window);
            ClickAt(window, CategoryLabel(tree, "Work")!, 80);
            Assert.Equal("Work", viewModel.SelectedCategory?.DisplayName);

            // The chevron collapses the branch without changing the selection, and the
            // nested row leaves the flat list.
            Assert.NotNull(CategoryLabel(window, "Servers"));
            ClickAt(window, CategoryLabel(tree, "Work")!, 6);

            Assert.Null(CategoryLabel(window, "Servers"));
            Assert.Equal("Work", viewModel.SelectedCategory?.DisplayName);

            window.Close();
        }
        finally
        {
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    });

    [Fact]
    public Task EscapeClearsTheSearchAndControlDigitsSwitchSmartViews() => Headless.Run(() =>
    {
        var directory = Path.Combine(Path.GetTempPath(), "aegis-ui-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            using var vault = Core.Services.VaultService.CreateNew(
                Path.Combine(directory, "vault.aegis"),
                "master password"u8.ToArray(),
                new Core.Services.VaultOptions
                {
                    Kdf = new Core.Models.KdfParameters
                    {
                        Algorithm = Core.Models.KdfParameters.AlgorithmArgon2id,
                        Iterations = 3,
                        MemoryBytes = 8L * 1024 * 1024,
                    },
                });
            vault.AddEntry(new Core.Models.PasswordEntry { Title = "One" });

            using var viewModel = new ViewModels.MainViewModel(vault);
            var window = new MainWindow { DataContext = viewModel };
            window.Show();
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();

            // Escape clears an active search (cancelling an edit keeps priority).
            viewModel.SearchText = "zzz";
            window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            Assert.Equal(string.Empty, viewModel.SearchText);

            // Ctrl+1/2 jump to the all/favorites smart views.
            window.KeyPress(Key.D2, RawInputModifiers.Control, PhysicalKey.Digit2, null);
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            Assert.Equal("favorites", viewModel.SelectedCategory?.Key);

            window.KeyPress(Key.D1, RawInputModifiers.Control, PhysicalKey.Digit1, null);
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            Assert.Equal("all", viewModel.SelectedCategory?.Key);

            window.Close();
        }
        finally
        {
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    });

    private static ListBox CategoryTree(Window window)
        => window.GetVisualDescendants().OfType<ListBox>()
            .First(candidate => (candidate.GetValue(AutomationProperties.AutomationIdProperty) as string) == "CategoryTree");

    private static Control? CategoryLabel(Visual root, string displayName)
        => root.GetVisualDescendants().OfType<Border>()
            .FirstOrDefault(border => border.Classes.Contains("categoryRowLabel") &&
                                      border.DataContext is ViewModels.CategoryTreeRow node &&
                                      node.Item.DisplayName == displayName);

    /// <summary>Every realised category row, in display order.</summary>
    private static List<Border> CategoryLabelAll(Visual root)
        => root.GetVisualDescendants().OfType<Border>()
            .Where(border => border.Classes.Contains("categoryRowLabel"))
            .ToList();

    /// <summary>The chevron that expands/collapses a parent row.</summary>
    private static Control CategoryArrow(Visual root, string displayName)
        => CategoryLabel(root, displayName)!
            .GetVisualDescendants().OfType<Control>()
            .First(candidate => candidate.Classes.Contains("categoryChevron"));

    private static void ClickAt(Window window, Control target, double? x = null)
    {
        var point = target.TranslatePoint(
            new Point(x ?? target.Bounds.Width / 2, target.Bounds.Height / 2),
            window) ?? throw new InvalidOperationException("The target is not connected to the window.");
        window.MouseDown(point, MouseButton.Left);
        window.MouseUp(point, MouseButton.Left);
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
    }

    private static List<AtomUIMenuItem> ReadRadioItems(
        AtomUI.Desktop.Controls.DropdownButton button,
        string groupName)
    {
        var items = button.DropdownFlyout?.Items.OfType<AtomUIMenuItem>().ToList();
        Assert.NotNull(items);
        Assert.All(items!, item => Assert.Equal(MenuItemToggleType.Radio, item.ToggleType));
        Assert.All(items!, item => Assert.Equal(groupName, item.GroupName));
        return items!;
    }
}
