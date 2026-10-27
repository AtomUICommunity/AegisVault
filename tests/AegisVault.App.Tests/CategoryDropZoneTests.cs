using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.VisualTree;
using AegisVault.App.ViewModels;
using AegisVault.App.Views;
using Xunit;
using Xunit.Abstractions;

namespace AegisVault.App.Tests;

/// <summary>
/// Drop-zone contract for the three reported problems: appending at the very end
/// (below the uncategorized pseudo row), inserting after the last real category, and
/// dropping into the middle of a row.
/// </summary>
public sealed class CategoryDropZoneTests
{
    private readonly ITestOutputHelper _out;

    public CategoryDropZoneTests(ITestOutputHelper output) => _out = output;

    private static (Window Window, MainViewModel Vm, Core.Services.VaultService Vault, string Dir) Show()
    {
        var dir = Path.Combine(Path.GetTempPath(), "aegis-drop", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);

        var vault = Core.Services.VaultService.CreateNew(
            Path.Combine(dir, "v.aegis"),
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
        vault.AddCategory("Mail");
        vault.AddCategory("Personal");
        vault.AddEntry(new Core.Models.PasswordEntry { Title = "Loose" }); // ensures 未分类 exists

        var vm = new MainViewModel(vault);
        var window = new MainWindow { DataContext = vm };
        window.Show();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        return (window, vm, vault, dir);
    }

    private static Border Row(Window window, string name)
        => window.GetVisualDescendants().OfType<Border>()
            .First(candidate => candidate.Classes.Contains("categoryRowLabel")
                                && candidate.DataContext is CategoryTreeRow row
                                && row.Item.DisplayName == name);

    private static Point At(Border row, Window window, double fraction)
        => row.TranslatePoint(new Point(row.Bounds.Width / 2, row.Bounds.Height * fraction), window)!.Value;

    private static void DragTo(Window window, Border from, Point to)
    {
        var start = At(from, window, 0.5);
        window.MouseDown(start, MouseButton.Left);
        window.MouseMove(new Point(start.X + 8, start.Y + 8));
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        window.MouseMove(to);
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        window.MouseUp(to, MouseButton.Left);
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
    }

    private static string Order(Core.Services.VaultService vault)
        => string.Join(",", vault.Categories
            .Where(c => c.ParentId is null)
            .OrderBy(c => c.SortOrder)
            .Select(c => c.Name));

    private void Cleanup(Window window, string dir)
    {
        window.Close();
        try { Directory.Delete(dir, true); } catch (IOException) { }
    }

    [Fact]
    public Task DroppingBelowTheLastRowAppendsAtTheEnd() => Headless.Run(() =>
    {
        var (window, vm, vault, dir) = Show();
        using var _ = vault;
        try
        {
            Assert.Equal("Work,Mail,Personal", Order(vault));

            // The uncategorized pseudo row is last; drop well below it.
            var uncategorized = Row(window, "未分类");
            var below = uncategorized.TranslatePoint(
                new Point(uncategorized.Bounds.Width / 2, uncategorized.Bounds.Height + 30), window)!.Value;

            DragTo(window, Row(window, "Work"), below);

            Assert.Equal("Mail,Personal,Work", Order(vault));
        }
        finally { Cleanup(window, dir); }
    });

    [Fact]
    public Task DroppingOnTheBottomQuarterInsertsAfterThatRow() => Headless.Run(() =>
    {
        var (window, vm, vault, dir) = Show();
        using var _ = vault;
        try
        {
            var target = Row(window, "Mail");
            DragTo(window, Row(window, "Work"), At(target, window, 0.85));

            // "Work" now sits after "Mail" instead of first.
            Assert.Equal("Mail,Work,Personal", Order(vault));
        }
        finally { Cleanup(window, dir); }
    });

    [Fact]
    public Task DroppingOnTheMiddleReparentsIntoThatRow() => Headless.Run(() =>
    {
        var (window, vm, vault, dir) = Show();
        using var _ = vault;
        try
        {
            DragTo(window, Row(window, "Mail"), At(Row(window, "Work"), window, 0.5));

            var work = vault.Categories.Single(c => c.Name == "Work");
            var mail = vault.Categories.Single(c => c.Name == "Mail");
            Assert.Equal(work.Id, mail.ParentId);

            // And the moved row is actually rendered under Work.
            Assert.Contains(vm.CategoryRows, row => row.Item.DisplayName == "Mail" && row.Depth == 2);
        }
        finally { Cleanup(window, dir); }
    });

    [Fact]
    public Task TheUncategorizedRowKeepsItsChildrenCountZero() => Headless.Run(() =>
    {
        var (window, vm, vault, dir) = Show();
        using var _ = vault;
        try
        {
            var uncategorized = vm.CategoryRows.Single(row => row.Item.CategoryId is null);
            Assert.False(uncategorized.HasChildren);
            Assert.False(uncategorized.IsExpanded);
            Assert.False(uncategorized.IsUserCategory);
        }
        finally { Cleanup(window, dir); }
    });
}
