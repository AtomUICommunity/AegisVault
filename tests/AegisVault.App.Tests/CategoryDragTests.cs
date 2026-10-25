using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.VisualTree;
using AegisVault.App.ViewModels;
using AegisVault.App.Views;
using Xunit;

namespace AegisVault.App.Tests;

/// <summary>
/// Contract for the sidebar drag gesture (feat/category-drag-sort).
/// <para>
/// AtomUI's NavMenu has no drag API at all (verified by reflection: no
/// Drag*/Drop*/Reorder* member on NavMenu, NavMenuItem or the row header), so the
/// app draws the gesture itself on the row <c>Border</c> it already owns. These
/// tests pin the three mechanics the implementation depends on:
/// 1. the full pointer gesture (press → move → release) reaches the row even
///    though the row marks PointerPressed as handled;
/// 2. the row keeps the pointer captured, so moves outside the row still arrive;
/// 3. rows are laid out with a measurable band, so a drop can be resolved to
///    "above this row" vs "into this row".
/// </para>
/// </summary>
public sealed class CategoryDragTests
{
    private const string Password = "master password";

    private static (Window Window, Core.Services.VaultService Vault, string Directory) Show(
        params string[] topLevel)
        => Show(null, topLevel);

    private static (Window Window, Core.Services.VaultService Vault, string Directory) Show(
        Action<Core.Services.VaultService>? setup,
        params string[] topLevel)
    {
        var directory = Path.Combine(Path.GetTempPath(), "aegis-drag-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);

        var vault = Core.Services.VaultService.CreateNew(
            Path.Combine(directory, "vault.aegis"),
            System.Text.Encoding.UTF8.GetBytes(Password),
            new Core.Services.VaultOptions
            {
                Kdf = new Core.Models.KdfParameters
                {
                    Algorithm = Core.Models.KdfParameters.AlgorithmArgon2id,
                    Iterations = 3,
                    MemoryBytes = 8L * 1024 * 1024,
                },
            });

        foreach (var name in topLevel)
        {
            vault.AddCategory(name);
        }

        setup?.Invoke(vault);

        var viewModel = new MainViewModel(vault);
        var window = new MainWindow { DataContext = viewModel };
        window.Show();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        return (window, vault, directory);
    }

    private static Border Row(Visual root, string displayName)
        => root.GetVisualDescendants()
            .OfType<Border>()
            .First(candidate => candidate.Classes.Contains("categoryRowLabel")
                                && candidate.DataContext is CategoryNavNode node
                                && node.Item.DisplayName == displayName);

    private static Point Centre(Border row, Window window)
        => row.TranslatePoint(new Point(row.Bounds.Width / 2, row.Bounds.Height / 2), window)!.Value;

    private static Point TopEdge(Border row, Window window)
        => row.TranslatePoint(new Point(row.Bounds.Width / 2, 2), window)!.Value;

    private static void Drag(Window window, Border from, Point to)
    {
        var start = Centre(from, window);
        window.MouseDown(start, MouseButton.Left);
        window.MouseMove(new Point(start.X + 8, start.Y + 8));
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        window.MouseMove(to);
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        window.MouseUp(to, MouseButton.Left);
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
    }

    private static void Cleanup(Window window, string directory)
    {
        window.Close();
        try
        {
            Directory.Delete(directory, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    /// <summary>
    /// Category names in the order the sidebar shows them (dense sibling order,
    /// not the flat alphabetical list Core keeps).
    /// </summary>
    private static List<string> SidebarOrder(Core.Services.VaultService vault)
        => vault.Categories
            .OrderBy(category => category.SortOrder)
            .Select(category => category.Name)
            .ToList();

    [Fact]
    public Task DraggingOntoTheTopEdgeReordersSiblings() => Headless.Run(() =>
    {
        var (window, vault, directory) = Show("Alpha", "Beta", "Gamma");
        using var _ = vault;
        try
        {
            // Alphabetical to start with, so moving Gamma above Alpha is only
            // observable if the drop actually wrote a new order.
            Assert.Equal(["Alpha", "Beta", "Gamma"], SidebarOrder(vault));

            Drag(window, Row(window, "Gamma"), TopEdge(Row(window, "Alpha"), window));

            Assert.Equal(["Gamma", "Alpha", "Beta"], SidebarOrder(vault));

            // The written order is dense and unique, so a reopen is a no-op.
            Assert.Equal([1, 2, 3], vault.Categories.Select(category => category.SortOrder).Order().ToList());
        }
        finally
        {
            Cleanup(window, directory);
        }
    });

    [Fact]
    public Task DraggingOntoARowReparentsTheCategory() => Headless.Run(() =>
    {
        var (window, vault, directory) = Show("Alpha", "Beta");
        using var _ = vault;
        try
        {
            Drag(window, Row(window, "Beta"), Centre(Row(window, "Alpha"), window));

            var alpha = vault.Categories.Single(category => category.Name == "Alpha");
            var beta = vault.Categories.Single(category => category.Name == "Beta");
            Assert.Equal(alpha.Id, beta.ParentId);

            // The child is appended inside its new parent, so it keeps a dense order.
            Assert.Equal(1, beta.SortOrder);
        }
        finally
        {
            Cleanup(window, directory);
        }
    });

    [Fact]
    public Task DroppingACategoryIntoItsOwnSubtreeIsRejected() => Headless.Run(() =>
    {
        Guid rootId = Guid.Empty;
        Guid childId = Guid.Empty;
        var (window, vault, directory) = Show(
            service =>
            {
                var root = service.AddCategory("Root");
                var child = service.AddCategory("Child", parentId: root.Id);
                service.AddCategory("Other");
                rootId = root.Id;
                childId = child.Id;
            });
        using var _ = vault;
        try
        {
            Drag(window, Row(window, "Root"), Centre(Row(window, "Child"), window));

            // A tree cannot contain itself: the gesture must be a no-op.
            Assert.Null(vault.Categories.Single(category => category.Id == rootId).ParentId);
            Assert.Equal(rootId, vault.Categories.Single(category => category.Id == childId).ParentId);

            // A legal sibling drop still works afterwards, so the rejected drop did
            // not leave the gesture state wedged.
            Drag(window, Row(window, "Root"), TopEdge(Row(window, "Other"), window));
            Assert.Equal(1, vault.Categories.Single(category => category.Id == rootId).SortOrder);
        }
        finally
        {
            Cleanup(window, directory);
        }
    });

    [Fact]
    public Task DraggingLeftBehindAMarkerAndClearsItOnRelease() => Headless.Run(() =>
    {
        var (window, vault, directory) = Show("Alpha", "Beta");
        using var _ = vault;
        try
        {
            var source = Row(window, "Beta");
            var start = Centre(source, window);

            window.MouseDown(start, MouseButton.Left);
            window.MouseMove(new Point(start.X + 8, start.Y + 8));
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            window.MouseMove(TopEdge(Row(window, "Alpha"), window));
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();

            // Mid-gesture: the source is dimmed and exactly one drop bar is shown.
            Assert.Contains("dragSource", source.Classes);
            var markers = window.GetVisualDescendants()
                .OfType<Border>()
                .Where(candidate => candidate.Name == "CategoryDropBefore" && candidate.IsVisible)
                .ToList();
            Assert.Single(markers);

            window.MouseUp(TopEdge(Row(window, "Alpha"), window), MouseButton.Left);
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();

            // After the drop everything is back to normal.
            Assert.DoesNotContain("dragSource", Row(window, "Beta").Classes);
            Assert.DoesNotContain(
                window.GetVisualDescendants().OfType<Border>(),
                candidate => candidate.Name == "CategoryDropBefore" && candidate.IsVisible);
        }
        finally
        {
            Cleanup(window, directory);
        }
    });

    [Fact]
    public Task APlainClickStillSelectsAndDoesNotStartADrag() => Headless.Run(() =>
    {
        var (window, vault, directory) = Show("Alpha", "Beta");
        using var _ = vault;
        try
        {
            var viewModel = (MainViewModel)window.DataContext!;
            var row = Row(window, "Beta");
            var point = Centre(row, window);

            window.MouseDown(point, MouseButton.Left);
            window.MouseUp(point, MouseButton.Left);
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();

            Assert.Equal("Beta", viewModel.SelectedCategory?.DisplayName);
            Assert.DoesNotContain("dragSource", row.Classes);
            Assert.Equal(["Alpha", "Beta"], vault.Categories.Select(category => category.Name));
        }
        finally
        {
            Cleanup(window, directory);
        }
    });
}
