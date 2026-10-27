using System.Linq;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.VisualTree;
using AegisVault.App.ViewModels;
using AegisVault.App.Views;
using Xunit;
using Xunit.Abstractions;

namespace AegisVault.App.Tests;

/// <summary>
/// Expanding/collapsing must not make the list jump: rows below the branch should move
/// smoothly, and a newly created row must end up settled (no residual transform).
/// </summary>
public sealed class CategoryExpandMotionTests
{
    private readonly ITestOutputHelper _out;

    public CategoryExpandMotionTests(ITestOutputHelper output) => _out = output;

    private static (Window Window, MainViewModel Vm, Core.Services.VaultService Vault, string Dir) Show()
    {
        var dir = Path.Combine(Path.GetTempPath(), "aegis-motion", Guid.NewGuid().ToString("N"));
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

        var root = vault.AddCategory("Work");
        for (var i = 0; i < 4; i++) { vault.AddCategory($"Child{i}", parentId: root.Id); }
        for (var i = 0; i < 4; i++) { vault.AddCategory($"Top{i}"); }

        var vm = new MainViewModel(vault);
        var window = new MainWindow { DataContext = vm };
        window.Show();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        return (window, vm, vault, dir);
    }

    private static Border? RowOf(Window window, string name)
        => window.GetVisualDescendants().OfType<Border>()
            .FirstOrDefault(b => b.Classes.Contains("categoryRowLabel")
                                 && b.DataContext is CategoryTreeRow r && r.Item.DisplayName == name);

    private static void Settle(Window window, Func<bool> done, int timeoutMs = 3000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            System.Threading.Thread.Sleep(15);
            if (done())
            {
                Avalonia.Threading.Dispatcher.UIThread.RunJobs();
                return;
            }
        }
    }

    [Fact]
    public Task CollapsingDoesNotJumpTheRowsBelow() => Headless.Run(() =>
    {
        var (window, vm, vault, dir) = Show();
        using var _ = vault;
        try
        {
            var work = vm.CategoryRows.First(r => r.Item.DisplayName == "Work");
            Assert.Contains(vm.CategoryRows, r => r.Item.DisplayName == "Child0");

            // Collapse through the same path the chevron uses.
            vm.ToggleCategoryExpansion(work);
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();

            Assert.DoesNotContain(vm.CategoryRows, r => r.Item.DisplayName == "Child0");

            // Top0 must still be reachable and settle with no leftover transform.
            Settle(window, () => RowOf(window, "Top0") is { RenderTransform: null });
            var top0 = RowOf(window, "Top0");
            Assert.NotNull(top0);
            _out.WriteLine($"Top0 opacity={top0!.Opacity} transform={top0.RenderTransform}");
            Assert.Equal(1d, top0.Opacity, 3);
            Assert.Null(top0.RenderTransform);
        }
        finally
        {
            window.Close();
            try { Directory.Delete(dir, true); } catch (IOException) { }
        }
    });

    [Fact]
    public Task ExpandingSettlesEveryRow() => Headless.Run(() =>
    {
        var (window, vm, vault, dir) = Show();
        using var _ = vault;
        try
        {
            var work = vm.CategoryRows.First(r => r.Item.DisplayName == "Work");
            vm.ToggleCategoryExpansion(work);
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            vm.ToggleCategoryExpansion(work);

            Settle(window, () =>
            {
                var child = RowOf(window, "Child0");
                var top = RowOf(window, "Top0");
                return child is { RenderTransform: null } && Math.Abs(child.Opacity - 1) < 0.001
                       && top is { RenderTransform: null };
            });

            foreach (var name in new[] { "Work", "Child0", "Child3", "Top0", "Top3" })
            {
                var row = RowOf(window, name);
                Assert.NotNull(row);
                _out.WriteLine($"{name,-8} opacity={row!.Opacity:N2} transform={(row.RenderTransform is null ? "null" : "set")}");
                Assert.Equal(1d, row.Opacity, 3);
                Assert.Null(row.RenderTransform);
            }
        }
        finally
        {
            window.Close();
            try { Directory.Delete(dir, true); } catch (IOException) { }
        }
    });
}
