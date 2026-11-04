using System.Linq;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.VisualTree;
using AtomUI.Desktop.Controls;
using AegisVault.App.Views;
using Xunit;
using Xunit.Abstractions;
using AtomUIMenuItem = AtomUI.Desktop.Controls.MenuItem;

namespace AegisVault.App.Tests;

/// <summary>
/// The sidebar toolbar was folded down to three controls: the health summary, one
/// settings button (which carries the theme menu) and the lock button.
/// <para>
/// Language was deliberately removed from the toolbar - it is a restart-level change
/// and stays in the settings window - and the theme menu moved into the settings
/// dropdown so the toolbar only holds what is used daily. The lock button stays
/// separate: it is a security action and must not be buried behind a menu.
/// </para>
/// <para>
/// Every test here returns the <see cref="Headless.Run"/> task. A <c>void</c> test body
/// would drop that task, and the session swallows the exception, so a failing assertion
/// would silently read as a pass.
/// </para>
/// </summary>
public sealed class MainWindowToolbarTests
{
    private readonly ITestOutputHelper _out;

    public MainWindowToolbarTests(ITestOutputHelper output) => _out = output;

    /// <summary>
    /// Theme entries live inside a <see cref="MenuItemGroup"/>, so they are one level
    /// down from the flyout's top-level items.
    /// </summary>
    private static List<AtomUIMenuItem> ThemeItemsIn(DropdownButton button)
    {
        var flyout = button.DropdownFlyout;
        Assert.NotNull(flyout);

        // Only radio entries count: the group container itself is also a MenuItem and
        // would otherwise be picked up by the non-group branch below.
        return flyout!.Items
            .SelectMany(item => item switch
            {
                AtomUI.Desktop.Controls.MenuItemGroup group => group.Items.OfType<AtomUIMenuItem>(),
                AtomUIMenuItem { ToggleType: MenuItemToggleType.Radio } single => [single],
                _ => Enumerable.Empty<AtomUIMenuItem>(),
            })
            .Where(item => item.ToggleType == MenuItemToggleType.Radio)
            .ToList();
    }

    [Fact]
    public Task SettingsButtonCarriesTheThreeThemeModes() => Headless.Run(() =>
    {
        var window = new MainWindow();

        var settings = window.FindControl<DropdownButton>("SettingsButton");
        Assert.NotNull(settings);
        Assert.False(settings!.IsArrowVisible);

        var items = ThemeItemsIn(settings);
        _out.WriteLine($"theme items = {items.Count}");
        foreach (var item in items)
        {
            _out.WriteLine($"  '{item.Header}' group={item.GroupName} toggle={item.ToggleType}");
        }

        Assert.Equal(3, items.Count);
        Assert.All(items, item => Assert.Equal(MenuItemToggleType.Radio, item.ToggleType));
        Assert.All(items, item => Assert.Equal("ThemeMode", item.GroupName));
    });

    /// <summary>
    /// Folding the gear into a dropdown must not remove the only path into the full
    /// settings window, so the menu needs a dedicated entry for it.
    /// </summary>
    [Fact]
    public Task SettingsMenuOffersTheFullSettingsEntry() => Headless.Run(() =>
    {
        var window = new MainWindow();
        var settings = window.FindControl<DropdownButton>("SettingsButton");
        Assert.NotNull(settings);

        var clickable = settings!.DropdownFlyout!.Items
            .OfType<AtomUIMenuItem>()
            .Where(item => item.ToggleType != MenuItemToggleType.Radio)
            .ToList();

        _out.WriteLine($"non-radio entries = {clickable.Count}");
        foreach (var item in clickable)
        {
            _out.WriteLine($"  '{item.Header}'");
        }

        Assert.NotEmpty(clickable);
    });

    [Fact]
    public Task TheLanguageMenuIsGoneFromTheToolbar() => Headless.Run(() =>
    {
        var window = new MainWindow();

        // Removing it from the toolbar is intentional: it takes a restart, so it lives
        // with the other settings, not next to the daily controls.
        Assert.Null(window.FindControl<DropdownButton>("LanguageButton"));
    });

    [Fact]
    public Task LockStaysAOneClickButtonOutsideTheMenu() => Headless.Run(() =>
    {
        var window = new MainWindow();
        window.Show();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        // The lock button carries only an AutomationId (that is what the UI automation
        // scripts key on), so it is located the same way instead of by x:Name.
        var lockButton = window.GetVisualDescendants()
            .OfType<Avalonia.Controls.Button>()
            .FirstOrDefault(button =>
                Avalonia.Automation.AutomationProperties.GetAutomationId(button) == "LockButton");
        Assert.NotNull(lockButton);

        // Not inside the settings dropdown: locking must never need two clicks.
        var settings = window.FindControl<DropdownButton>("SettingsButton");
        Assert.NotNull(settings);
        var labels = settings!.DropdownFlyout!.Items
            .SelectMany(item => item switch
            {
                AtomUI.Desktop.Controls.MenuItemGroup group => group.Items.OfType<AtomUIMenuItem>(),
                AtomUIMenuItem single => [single],
                _ => Enumerable.Empty<AtomUIMenuItem>(),
            })
            .Select(item => item.Header?.ToString() ?? string.Empty)
            .ToList();

        Assert.DoesNotContain(labels, text => text.Contains("锁定", StringComparison.Ordinal));

        window.Close();
    });
}
