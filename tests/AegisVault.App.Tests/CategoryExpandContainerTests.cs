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
/// Expanding/collapsing must not rebuild the containers of rows that stay visible.
/// Clearing the bound collection detaches every container, which makes the rows (whose
/// background is painted by that container) blink on each click.
/// </summary>
public sealed class CategoryExpandContainerTests
{
    private readonly ITestOutputHelper _out;

    public CategoryExpandContainerTests(ITestOutputHelper output) => _out = output;

    private static (Window Window, MainViewModel Vm, Core.Services.VaultService Vault, string Dir) Show()
    {
        var dir = Path.Combine(Path.GetTempPath(), "aegis-containers", Guid.NewGuid().ToString("N"));
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
        for (var i = 0; i < 3; i++) { vault.AddCategory($"Child{i}", parentId: root.Id); }
        for (var i = 0; i < 3; i++) { vault.AddCategory($"Top{i}"); }

        var vm = new MainViewModel(vault);
        var window = new MainWindow { DataContext = vm };
        window.Show();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        return (window, vm, vault, dir);
    }

    private static ListBox Tree(Window window)
        => window.GetVisualDescendants().OfType<ListBox>()
            .First(c => (c.GetValue(AutomationProperties.AutomationIdProperty) as string) == "CategoryTree");

    private static CategoryTreeRow Row(MainViewModel vm, string name)
        => vm.CategoryRows.First(r => r.Item.DisplayName == name);

    [Fact]
    public Task ExpandingKeepsTheOtherContainersAlive() => Headless.Run(() =>
    {
        var (window, vm, vault, dir) = Show();
        using var _ = vault;
        try
        {
            var tree = Tree(window);
            var work = Row(vm, "Work");

            // Collapse first so the children genuinely have to be created on expand.
            vm.ToggleCategoryExpansion(work);
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();

            var before = new Dictionary<string, Control?>
            {
                ["Work"] = tree.ContainerFromItem(Row(vm, "Work")),
                ["Top0"] = tree.ContainerFromItem(Row(vm, "Top0")),
                ["Top2"] = tree.ContainerFromItem(Row(vm, "Top2")),
            };
            Assert.All(before.Values, container => Assert.NotNull(container));

            vm.ToggleCategoryExpansion(work);
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();

            // The rows that were already visible must keep their containers: recreating
            // them is what produced the background flicker on the clicked row.
            foreach (var (name, container) in before)
            {
                var now = tree.ContainerFromItem(Row(vm, name));
                _out.WriteLine($"{name,-6} same container = {ReferenceEquals(container, now)}");
                Assert.Same(container, now);
            }
        }
        finally
        {
            window.Close();
            try { Directory.Delete(dir, true); } catch (IOException) { }
        }
    });

    [Fact]
    public Task CollapsingKeepsTheOtherContainersAlive() => Headless.Run(() =>
    {
        var (window, vm, vault, dir) = Show();
        using var _ = vault;
        try
        {
            var tree = Tree(window);
            var work = Row(vm, "Work");
            Assert.Contains(vm.CategoryRows, r => r.Item.DisplayName == "Child0");

            var before = new Dictionary<string, Control?>
            {
                ["Work"] = tree.ContainerFromItem(Row(vm, "Work")),
                ["Top0"] = tree.ContainerFromItem(Row(vm, "Top0")),
            };
            Assert.All(before.Values, container => Assert.NotNull(container));

            vm.ToggleCategoryExpansion(work);
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();

            foreach (var (name, container) in before)
            {
                var now = tree.ContainerFromItem(Row(vm, name));
                _out.WriteLine($"{name,-6} same container = {ReferenceEquals(container, now)}");
                Assert.Same(container, now);
            }

            Assert.DoesNotContain(vm.CategoryRows, r => r.Item.DisplayName == "Child0");
        }
        finally
        {
            window.Close();
            try { Directory.Delete(dir, true); } catch (IOException) { }
        }
    });
}
