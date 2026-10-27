using System.Linq;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Headless;
using Avalonia.VisualTree;
using AegisVault.App.ViewModels;
using AegisVault.App.Views;
using Xunit;
using Xunit.Abstractions;

namespace AegisVault.App.Tests;

/// <summary>
/// The "move to" picker must show the hierarchy: each level indents by 16px and draws one
/// guide line per ancestor. Before this, every row rendered at the same offset because the
/// indent was made of leading spaces, which TextBlock collapses.
/// </summary>
public sealed class CategoryPickerIndentTests
{
    private readonly ITestOutputHelper _out;

    public CategoryPickerIndentTests(ITestOutputHelper output) => _out = output;

    private static (Window Window, MainViewModel Vm, Core.Services.VaultService Vault, string Dir) Show()
    {
        var dir = Path.Combine(Path.GetTempPath(), "aegis-picker2", Guid.NewGuid().ToString("N"));
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
        var infra = vault.AddCategory("Infra", parentId: work.Id);
        vault.AddCategory("Database", parentId: infra.Id);
        vault.AddCategory("Mail");

        var vm = new MainViewModel(vault);
        return (new Window(), vm, vault, dir);
    }

    private static List<TextBlock> Labels(Window window, IEnumerable<CategoryChoice> choices)
        => window.GetVisualDescendants().OfType<TextBlock>()
            .Where(t => t.Text is not null && choices.Any(c => c.Name == t.Text))
            .ToList();

    [Fact]
    public Task ChoicesCarryTheirRealDepthAndCleanNames() => Headless.Run(() =>
    {
        var (_, vm, vault, dir) = Show();
        using var _ = vault;
        try
        {
            var choices = vm.BuildParentChoices(movingId: null);
            foreach (var c in choices)
            {
                _out.WriteLine($"'{c.Name}' depth={c.Depth}");
                Assert.Equal(c.Name.TrimStart(), c.Name);
            }

            var byName = choices.ToDictionary(c => c.Name, c => c.Depth);
            Assert.Equal(0, byName["（顶层）"]);
            Assert.Equal(1, byName["Work"]);
            Assert.Equal(2, byName["Infra"]);
            Assert.Equal(3, byName["Database"]);
            Assert.Equal(1, byName["Mail"]);
        }
        finally
        {
            TryDelete(dir);
        }
    });

    [Fact]
    public Task EachLevelRendersSixteenPixelsFurtherRight() => Headless.Run(() =>
    {
        var (_, vm, vault, dir) = Show();
        using var _ = vault;
        try
        {
            var choices = vm.BuildParentChoices(movingId: null);
            var window = new CategoryParentWindow("Move", "Pick:", choices, null);
            window.Show();
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();

            var list = window.GetVisualDescendants().OfType<ListBox>().First();
            var labels = Labels(window, choices);

            double X(string name)
            {
                var label = labels.First(t => t.Text == name);
                return label.TranslatePoint(new Point(0, 0), list)!.Value.X;
            }

            var root = X("（顶层）");
            var one = X("Work");
            var two = X("Infra");
            var three = X("Database");

            _out.WriteLine($"root={root:N1} d1={one:N1} d2={two:N1} d3={three:N1}");

            // "（顶层）" is the pseudo choice (depth 0); real top-level categories sit at
            // depth 1, so Mail must line up with Work.
            Assert.Equal(one, X("Mail"), 1);
            Assert.Equal(root + 16, one, 1);
            Assert.Equal(root + 32, two, 1);
            Assert.Equal(root + 48, three, 1);

            window.Close();
        }
        finally { TryDelete(dir); }
    });

    [Fact]
    public Task TheSelectorUsesBrandColorsNotTheSystemAccent() => Headless.Run(() =>
    {
        var (_, vm, vault, dir) = Show();
        using var _ = vault;
        try
        {
            var choices = vm.BuildParentChoices(movingId: null);
            var window = new CategoryParentWindow("Move", "Pick:", choices, null);
            window.Show();
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();

            var list = window.GetVisualDescendants().OfType<ListBox>().First();
            var items = list.GetRealizedContainers().OfType<ListBoxItem>().ToList();
            Assert.NotEmpty(items);

            items[0].IsSelected = true;
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();

            var presenter = items[0].GetVisualDescendants().OfType<ContentPresenter>().First();
            var background = presenter.Background;
            _out.WriteLine($"selected background={background}");
            Assert.NotNull(background);

            var color = Assert.IsType<Avalonia.Media.SolidColorBrush>(background).Color;
            // The brand ramp is a desaturated green; Fluent's accent is a saturated blue.
            // Compare hue rather than an exact hex so palette tweaks stay allowed.
            var max = Math.Max(color.R, Math.Max(color.G, color.B));
            var min = Math.Min(color.R, Math.Min(color.G, color.B));
            _out.WriteLine($"rgb=#{color.R:X2}{color.G:X2}{color.B:X2} spread={max - min}");
            Assert.True(color.G >= color.R && color.G >= color.B - 4,
                $"expected a green-tinted selection, got #{color.R:X2}{color.G:X2}{color.B:X2}");

            window.Close();
        }
        finally { TryDelete(dir); }
    });

    private static void TryDelete(string dir)
    {
        try { Directory.Delete(dir, true); } catch (IOException) { }
    }
}
