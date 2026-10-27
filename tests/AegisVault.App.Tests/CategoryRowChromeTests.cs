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
/// Row chrome contract: the count keeps a readable gap from the selected edge, and the
/// uncategorized pseudo row does not offer a per-row actions menu.
/// </summary>
public sealed class CategoryRowChromeTests
{
    private readonly ITestOutputHelper _out;

    public CategoryRowChromeTests(ITestOutputHelper output) => _out = output;

    private static (Window Window, MainViewModel Vm, Core.Services.VaultService Vault, string Dir) Show()
    {
        var dir = Path.Combine(Path.GetTempPath(), "aegis-chrome2", Guid.NewGuid().ToString("N"));
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
        vault.AddEntry(new Core.Models.PasswordEntry { Title = "Loose" });

        var vm = new MainViewModel(vault);
        var window = new MainWindow { DataContext = vm };
        window.Show();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        return (window, vm, vault, dir);
    }

    private static Border Row(Window window, string name)
        => window.GetVisualDescendants().OfType<Border>()
            .First(b => b.Classes.Contains("categoryRowLabel")
                        && b.DataContext is CategoryTreeRow r && r.Item.DisplayName == name);

    private static ContentPresenter Presenter(Control row)
        => row.GetVisualAncestors().OfType<ContentPresenter>()
            .First(p => p.GetVisualAncestors().OfType<ListBoxItem>().Any());

    private static TextBlock Count(Control row)
        => row.GetVisualDescendants().OfType<TextBlock>()
            .First(t => (t.GetValue(AutomationProperties.AutomationIdProperty) as string) == "CategoryCountText");

    private static StackPanel Actions(Control row)
        => row.GetVisualDescendants().OfType<StackPanel>()
            .First(s => s.Classes.Contains("categoryActions"));

    [Fact]
    public Task CountKeepsAGapFromTheSelectedEdge() => Headless.Run(() =>
    {
        var (window, vm, vault, dir) = Show();
        using var _ = vault;
        try
        {
            var row = Row(window, "Work");
            row.GetVisualAncestors().OfType<ListBoxItem>().First().IsSelected = true;
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();

            var presenter = Presenter(row);
            var count = Count(row);
            var countRight = count.TranslatePoint(new Point(count.Bounds.Width, 0), presenter)!.Value.X;
            var gap = presenter.Bounds.Width - countRight;

            _out.WriteLine($"countRight={countRight:N1} presenterWidth={presenter.Bounds.Width:N1} gap={gap:N1}");
            Assert.True(gap >= 7, $"The count sits {gap:N1}px from the selected edge; expected at least 7px.");
        }
        finally { Cleanup(window, dir); }
    });

    [Fact]
    public Task TheUncategorizedRowHasNoActionsMenu() => Headless.Run(() =>
    {
        var (window, vm, vault, dir) = Show();
        using var _ = vault;
        try
        {
            var pseudo = Row(window, "未分类");
            Assert.False(((CategoryTreeRow)pseudo.DataContext!).IsUserCategory);

            var actions = Actions(pseudo);
            _out.WriteLine($"pseudo actions opacity={actions.Opacity} hitTest={actions.IsHitTestVisible}");
            Assert.Equal(0d, actions.Opacity);
            Assert.False(actions.IsHitTestVisible);

            // A real category still shows it.
            var real = Actions(Row(window, "Work"));
            _out.WriteLine($"real actions opacity={real.Opacity} hitTest={real.IsHitTestVisible}");
            Assert.Equal(1d, real.Opacity);
            Assert.True(real.IsHitTestVisible);
        }
        finally { Cleanup(window, dir); }
    });

    [Fact]
    public Task HidingTheActionsDoesNotShiftTheCountColumn() => Headless.Run(() =>
    {
        var (window, vm, vault, dir) = Show();
        using var _ = vault;
        try
        {
            // The pseudo row hides the actions; the count must still line up with a real row.
            var pseudo = Row(window, "未分类");
            var real = Row(window, "Work");

            var pseudoPresenter = Presenter(pseudo);
            var realPresenter = Presenter(real);

            double Right(Control row, ContentPresenter presenter)
            {
                var count = Count(row);
                return count.TranslatePoint(new Point(count.Bounds.Width, 0), presenter)!.Value.X;
            }

            var pseudoRight = pseudoPresenter.Bounds.Width - Right(pseudo, pseudoPresenter);
            var realRight = realPresenter.Bounds.Width - Right(real, realPresenter);

            _out.WriteLine($"pseudo gap={pseudoRight:N1}  real gap={realRight:N1}");
            Assert.Equal(realRight, pseudoRight, 1);
        }
        finally { Cleanup(window, dir); }
    });

    private static void Cleanup(Window window, string dir)
    {
        window.Close();
        try { Directory.Delete(dir, true); } catch (IOException) { }
    }
}
