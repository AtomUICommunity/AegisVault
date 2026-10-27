using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.VisualTree;
using AegisVault.App.ViewModels;
using AegisVault.App.Views;
using Xunit;
using Xunit.Abstractions;

namespace AegisVault.App.Tests;

/// <summary>
/// The entry editor's category dropdown must indent nested categories. Its rows are rendered
/// by AtomUI's combo popup, so the indent is only real if the label actually moves right.
/// </summary>
public sealed class EntryEditorCategoryComboTests
{
    /// <summary>Must match <c>CategoryPickerIndentConverter.IndentPerLevel</c>.</summary>
    private const double CategoryPickerIndentPerLevel = 16;

    private readonly ITestOutputHelper _out;

    public EntryEditorCategoryComboTests(ITestOutputHelper output) => _out = output;

    private static (Window Window, MainViewModel Vm, Core.Services.VaultService Vault, string Dir) Show()
    {
        var dir = Path.Combine(Path.GetTempPath(), "aegis-combo", Guid.NewGuid().ToString("N"));
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
        vault.AddEntry(new Core.Models.PasswordEntry { Title = "Loose" });

        var vm = new MainViewModel(vault);
        var window = new MainWindow { DataContext = vm };
        window.Show();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        return (window, vm, vault, dir);
    }

    [Fact]
    public Task TheDropdownIndentsEachLevel() => Headless.Run(() =>
    {
        var (window, vm, vault, dir) = Show();
        using var _ = vault;
        try
        {
            // Open the editor so the ComboBox exists in the visual tree.
            vm.AddEntryCommand.Execute(null);
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();

            var combo = window.GetVisualDescendants().OfType<ComboBox>()
                .FirstOrDefault(c => c.ItemsSource is IEnumerable<CategoryChoice>);
            Assert.NotNull(combo);
            Assert.NotNull(combo!.ItemTemplate);

            // The popup is a separate visual root and is not realised in headless mode, so
            // render the item template directly at three depths and measure the label.
            double Measure(int depth, string name)
            {
                var host = new ContentControl
                {
                    ContentTemplate = combo.ItemTemplate,
                    Content = new CategoryChoice(Guid.NewGuid(), name, depth),
                };
                var probe = new Window { Content = host, Width = 400, Height = 100 };
                probe.Show();
                Avalonia.Threading.Dispatcher.UIThread.RunJobs();
                probe.UpdateLayout();

                var label = host.GetVisualDescendants().OfType<TextBlock>()
                    .FirstOrDefault(t => t.Text == name);
                Assert.NotNull(label);
                var x = label!.TranslatePoint(new Point(0, 0), host)!.Value.X;
                _out.WriteLine($"{name} (depth {depth}) x={x:N1}");
                probe.Close();
                return x;
            }

            var d1 = Measure(1, "Work");
            var d2 = Measure(2, "Infra");
            var d3 = Measure(3, "Database");

            Assert.Equal(d1 + CategoryPickerIndentPerLevel, d2, 1);
            Assert.Equal(d1 + (CategoryPickerIndentPerLevel * 2), d3, 1);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch (IOException) { }
        }
    });

    [Fact]
    public void ChoicesReportTheirNameNotARecordDump()
    {
        // The automation name of a picker row comes from ToString(); a raw record dump would
        // make a screen reader announce "CategoryChoice { Id = <guid>, ... }".
        var choice = new CategoryChoice(Guid.NewGuid(), "Mail", 1);
        Assert.Equal("Mail", choice.ToString());
    }
}
