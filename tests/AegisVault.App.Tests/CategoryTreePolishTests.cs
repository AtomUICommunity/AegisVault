using System.Linq;
using Avalonia;
using Avalonia.Automation;
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
/// The second round of category-tree polish:
/// CT-P2b (collapsed branches survive a restart), CT-P2c (Left/Right toggle the focused
/// branch) and CT-P2d (the delete menu item uses the error colour).
/// </summary>
public sealed class CategoryTreePolishTests
{
    private readonly ITestOutputHelper _out;

    public CategoryTreePolishTests(ITestOutputHelper output) => _out = output;

    private static (Window Window, MainViewModel Vm, Core.Services.VaultService Vault, string Dir) Show()
    {
        var dir = Path.Combine(Path.GetTempPath(), "aegis-polish", Guid.NewGuid().ToString("N"));
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

    private static void Cleanup(Window window, string dir)
    {
        window.Close();
        try { Directory.Delete(dir, true); } catch (IOException) { }
    }

    private static CategoryTreeRow Row(MainViewModel vm, string name)
        => vm.CategoryRows.First(row => row.Item.DisplayName == name);

    [Fact]
    public Task CollapsingIsReportedToTheStoreAndReloaded() => Headless.Run(() =>
    {
        List<string>? saved = null;
        var (window, vm, vault, dir) = Show();
        using var _ = vault;
        try
        {
            // The shell wires this up; here we just capture what it would persist.
            vm.AttachCategoryExpansionStore([], ids => saved = ids.ToList());

            var work = Row(vm, "Work");
            Assert.True(work.IsExpanded);

            vm.ToggleCategoryExpansion(work);
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();

            Assert.NotNull(saved);
            var persisted = Assert.Single(saved!);
            Assert.Equal(work.CategoryId!.Value.ToString("D"), persisted);

            // Expanding again must clear the stored exception.
            vm.ToggleCategoryExpansion(Row(vm, "Work"));
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            Assert.Empty(saved!);
        }
        finally { Cleanup(window, dir); }
    });

    [Fact]
    public Task CollapsedStateIsAppliedWhenTheShellRestoresIt() => Headless.Run(() =>
    {
        var (window, vm, vault, dir) = Show();
        using var _ = vault;
        try
        {
            // A fresh instance, as after a restart: the shell hands back the saved ids.
            var work = Row(vm, "Work");
            var workId = work.CategoryId!.Value;

            using var vault2 = Core.Services.VaultService.Open(vault.VaultPath);
            Assert.Equal(Core.Services.VaultUnlockStatus.Success, vault2.Unlock("master password"u8.ToArray()));

            var restored = new MainViewModel(vault2);
            restored.AttachCategoryExpansionStore([workId.ToString("D")], _ => { });
            restored.ReloadFromVault();
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();

            Assert.DoesNotContain(restored.CategoryRows, r => r.Item.DisplayName == "Servers");
            Assert.False(Row(restored, "Work").IsExpanded);
            restored.Dispose();
        }
        finally { Cleanup(window, dir); }
    });

    [Fact]
    public Task DeleteMenuItemUsesTheErrorColour() => Headless.Run(() =>
    {
        var (window, vm, vault, dir) = Show();
        using var _ = vault;
        try
        {
            // Resolve through the same helper the flyout uses, then compare with the token.
            var method = typeof(MainWindow).GetMethod(
                "DangerColor",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
            Assert.NotNull(method);

            var color = (Avalonia.Media.Color)method!.Invoke(null, null)!;
            var expected = AegisVault.App.Theme.AppTheme.TokenColor(
                "ColorError",
                Avalonia.Application.Current?.ActualThemeVariant == Avalonia.Styling.ThemeVariant.Dark
                    ? Avalonia.Styling.ThemeVariant.Dark
                    : Avalonia.Styling.ThemeVariant.Light);

            _out.WriteLine($"danger = #{color.R:X2}{color.G:X2}{color.B:X2}  expected = #{expected.R:X2}{expected.G:X2}{expected.B:X2}");
            Assert.Equal(expected, color);
            Assert.NotEqual(Avalonia.Media.Colors.Gray, color);
        }
        finally { Cleanup(window, dir); }
    });

    [Fact]
    public Task LeftAndRightToggleTheFocusedBranch() => Headless.Run(() =>
    {
        var (window, vm, vault, dir) = Show();
        using var _ = vault;
        try
        {
            var work = Row(vm, "Work");
            var label = window.GetVisualDescendants().OfType<Border>()
                .First(b => b.Classes.Contains("categoryRowLabel") && ReferenceEquals(b.DataContext, work));

            label.Focus();
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();

            // Right on a collapsed branch expands it.
            vm.ToggleCategoryExpansion(work);
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            Assert.False(Row(vm, "Work").IsExpanded);

            var anchor = window.FocusManager?.GetFocusedElement();
            _out.WriteLine($"focused = {anchor?.GetType().Name ?? "null"}");

            // The handler is keyed on the row's DataContext, so drive it directly.
            var method = typeof(MainWindow).GetMethod(
                "OnCategoryRowLabelKeyDown",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            Assert.NotNull(method);

            var right = new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Right };
            method!.Invoke(window, [label, right]);
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();

            Assert.True(Row(vm, "Work").IsExpanded);
            Assert.True(right.Handled);

            var left = new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Left };
            method.Invoke(window, [label, left]);
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();

            Assert.False(Row(vm, "Work").IsExpanded);
            Assert.True(left.Handled);
        }
        finally { Cleanup(window, dir); }
    });
}
