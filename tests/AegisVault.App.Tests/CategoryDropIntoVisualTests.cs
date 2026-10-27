using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media;
using Avalonia.VisualTree;
using AegisVault.App.ViewModels;
using AegisVault.App.Views;
using Xunit;
using Xunit.Abstractions;

namespace AegisVault.App.Tests;

/// <summary>
/// The "drop into" affordance must actually change the row's appearance. It used to be
/// a no-op because the template set Background as a local value, which wins over a style
/// setter; this pins the behaviour down.
/// </summary>
public sealed class CategoryDropIntoVisualTests
{
    private readonly ITestOutputHelper _out;

    public CategoryDropIntoVisualTests(ITestOutputHelper output) => _out = output;

    private static (Window Window, MainViewModel Vm, Core.Services.VaultService Vault, string Dir) Show()
    {
        var dir = Path.Combine(Path.GetTempPath(), "aegis-dropinto", Guid.NewGuid().ToString("N"));
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

    private static Border BarOf(Border row)
        => row.GetVisualDescendants().OfType<Border>()
            .First(candidate => candidate.Name == "CategoryDropIntoBar");

    private static string Describe(IBrush? brush)
        => brush is ISolidColorBrush solid
            ? $"#{solid.Color.A:X2}{solid.Color.R:X2}{solid.Color.G:X2}{solid.Color.B:X2}"
            : brush?.GetType().Name ?? "null";

    [Fact]
    public Task DropTargetClassActuallyPaintsTheRow() => Headless.Run(() =>
    {
        var (window, vm, vault, dir) = Show();
        using var _ = vault;
        try
        {
            var row = Row(window, "Work");
            var beforeBackground = Describe(row.Background);
            var beforeBar = BarOf(row).Opacity;

            row.Classes.Add("dropTarget");
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();

            var afterBackground = Describe(row.Background);
            _out.WriteLine($"background {beforeBackground} -> {afterBackground}");
            _out.WriteLine($"border     {Describe(row.BorderBrush)}");

            Assert.NotEqual(beforeBackground, afterBackground);
            Assert.True(row.Background is ISolidColorBrush { Color.A: > 0 }, "The drop-target tint is transparent.");
            Assert.True(row.BorderBrush is ISolidColorBrush { Color.A: > 0 }, "The drop-target border is transparent.");
            Assert.Equal(0d, beforeBar);

            row.Classes.Remove("dropTarget");
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            Assert.Equal(beforeBackground, Describe(row.Background));
        }
        finally
        {
            window.Close();
            try { Directory.Delete(dir, true); } catch (IOException) { }
        }
    });

    [Fact]
    public Task TheAccentBarIsPresentButHiddenByDefault() => Headless.Run(() =>
    {
        var (window, vm, vault, dir) = Show();
        using var _ = vault;
        try
        {
            var row = Row(window, "Work");
            var bar = BarOf(row);

            Assert.Equal(0d, bar.Opacity);
            Assert.False(bar.IsHitTestVisible);

            // The bar overlays the row's left edge without stealing width: it is a thin
            // sibling of the content grid inside a Panel, so the grid still spans the row.
            var content = row.GetVisualDescendants().OfType<Grid>()
                .First(candidate => candidate.Classes.Count == 0 && candidate.ColumnDefinitions.Count == 5);
            Assert.True(
                Math.Abs(content.Bounds.Width - row.Bounds.Width) < 3.5,
                $"The accent bar must not shrink the content: grid={content.Bounds.Width} row={row.Bounds.Width}");
        }
        finally
        {
            window.Close();
            try { Directory.Delete(dir, true); } catch (IOException) { }
        }
    });
}
