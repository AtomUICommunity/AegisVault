using System.Linq;
using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using AegisVault.App.ViewModels;
using AegisVault.App.Views;
using Xunit;
using Xunit.Abstractions;

namespace AegisVault.App.Tests;

/// <summary>
/// Double-clicking a category row expands or collapses its branch.
/// <para>
/// The headless mouse API has no way to set <c>ClickCount</c> (it only takes the point,
/// button and modifiers), so the gesture cannot be produced by synthetic input here.
/// The test therefore raises <see cref="PointerPressedEventArgs"/> with the click count
/// set directly and asserts the row reacts - it still goes through the real handler and
/// the real toggle path, only the count is supplied.
/// </para>
/// </summary>
public sealed class CategoryDoubleClickTests
{
    private readonly ITestOutputHelper _out;

    public CategoryDoubleClickTests(ITestOutputHelper output) => _out = output;

    private static (Window Window, MainViewModel Vm, Core.Services.VaultService Vault, string Dir) Show()
    {
        var dir = Path.Combine(Path.GetTempPath(), "aegis-dblclick", Guid.NewGuid().ToString("N"));
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
                        && b.DataContext is CategoryTreeRow r
                        && r.Item.DisplayName == name);

    /// <summary>
    /// Raises the same routed event the pointer would, with an explicit click count.
    /// The count is a constructor argument (and read-only afterwards), so it is passed
    /// in directly rather than patched onto the instance.
    /// </summary>
    private static void PressRow(Border row, int clickCount)
    {
        var pressed = new PointerPressedEventArgs(
            row,
            new Avalonia.Input.Pointer(1, PointerType.Mouse, isPrimary: true),
            row,
            new Point(60, row.Bounds.Height / 2),
            0,
            new PointerPointProperties(
                RawInputModifiers.LeftMouseButton,
                PointerUpdateKind.LeftButtonPressed),
            KeyModifiers.None,
            clickCount);

        row.RaiseEvent(pressed);
    }

    [Fact]
    public Task DoubleClickExpandsAndCollapsesABranch() => Headless.Run(() =>
    {
        var (window, vm, vault, dir) = Show();
        using var _ = vault;
        try
        {
            var work = vm.CategoryRows.First(r => r.Item.DisplayName == "Work");
            Assert.True(work.HasChildren);
            Assert.True(work.IsExpanded, "branches start expanded");

            var row = Row(window, "Work");

            PressRow(row, clickCount: 2);
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();

            _out.WriteLine($"after double click: IsExpanded={work.IsExpanded}");
            Assert.False(work.IsExpanded);

            // And back again.
            row = Row(window, "Work");
            PressRow(row, clickCount: 2);
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            _out.WriteLine($"after second double click: IsExpanded={work.IsExpanded}");
            Assert.True(work.IsExpanded);

            window.Close();
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch (IOException) { }
        }
    });

    /// <summary>
    /// A single click must still select only - if the toggle fired on one click the row
    /// would be unusable, since every drag also starts with one press.
    /// </summary>
    [Fact]
    public Task SingleClickDoesNotToggle() => Headless.Run(() =>
    {
        var (window, vm, vault, dir) = Show();
        using var _ = vault;
        try
        {
            var work = vm.CategoryRows.First(r => r.Item.DisplayName == "Work");
            var row = Row(window, "Work");

            PressRow(row, clickCount: 1);
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();

            _out.WriteLine($"after single click: IsExpanded={work.IsExpanded}");
            Assert.True(work.IsExpanded);

            window.Close();
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch (IOException) { }
        }
    });

    /// <summary>
    /// Rows without children cannot be expanded, so a double click on them must not
    /// throw or change anything - it should just behave like a single click.
    /// </summary>
    [Fact]
    public Task DoubleClickOnALeafRowIsHarmless() => Headless.Run(() =>
    {
        var (window, vm, vault, dir) = Show();
        using var _ = vault;
        try
        {
            var mail = vm.CategoryRows.First(r => r.Item.DisplayName == "Mail");
            Assert.False(mail.HasChildren);

            var row = Row(window, "Mail");
            PressRow(row, clickCount: 2);
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();

            _out.WriteLine($"leaf row IsExpanded={mail.IsExpanded}");
            Assert.False(mail.IsExpanded);

            window.Close();
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch (IOException) { }
        }
    });
}
