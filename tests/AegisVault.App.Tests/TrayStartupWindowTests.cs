using Avalonia.Controls;
using Avalonia.Threading;
using AegisVault.App.Views;
using Xunit;

namespace AegisVault.App.Tests;

/// <summary>
/// Tray mode opens the main window and hides it again so the desktop lifetime keeps
/// the application alive; the user must never see it and it must not leave anything
/// behind on the desktop.
/// <para>
/// The residual "frame outline" bug cannot be reproduced headlessly - it is a DWM
/// composition artefact that needs a real compositor. What is asserted here is the
/// mechanism the fix relies on: the window is shown, it is <em>not</em> hidden in the
/// same turn, and once the deferred hide runs it ends up invisible with its opacity
/// restored. That last part matters because a window left at opacity 0 would look
/// dead when the tray icon brings it back.
/// </para>
/// </summary>
public sealed class TrayStartupWindowTests
{
    [Fact]
    public Task ShowThenHideLeavesTheWindowHiddenAndOpaque() => Headless.RunAsync<object?>(async () =>
    {
        var window = new MainWindow();

        window.ShowThenHideForTray();

        // The hide is deferred by a frame: hiding synchronously is what produced the
        // stale DWM frame outline.
        Assert.True(window.IsVisible);
        Assert.Equal(0, window.Opacity);

        await FlushFramesAsync();

        Assert.False(window.IsVisible);
        Assert.Equal(1, window.Opacity);

        window.Close();
        return null;
    });

    [Fact]
    public Task ShowThenHideRestoresANonDefaultOpacity() => Headless.RunAsync<object?>(async () =>
    {
        var window = new MainWindow { Opacity = 0.8 };

        window.ShowThenHideForTray();
        await FlushFramesAsync();

        Assert.False(window.IsVisible);
        Assert.Equal(0.8, window.Opacity);

        window.Close();
        return null;
    });

    [Fact]
    public Task ShownWindowStaysUsableAfterTheTrayHide() => Headless.RunAsync<object?>(async () =>
    {
        var window = new MainWindow();
        window.ShowThenHideForTray();
        await FlushFramesAsync();
        Assert.False(window.IsVisible);

        // The tray icon / hotkey path must be able to bring it back, fully opaque.
        window.Show();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        Assert.True(window.IsVisible);
        Assert.Equal(1, window.Opacity);

        window.Close();
        return null;
    });

    [Fact]
    public Task ReShowingBeforeTheDeferredHideWins() => Headless.RunAsync<object?>(async () =>
    {
        var window = new MainWindow();
        window.ShowThenHideForTray();

        // The user clicks the tray icon inside the frame the hide is parked in.
        window.Show();

        await FlushFramesAsync();

        // The deferred hide must stand down: a window the user just opened cannot
        // be yanked away by the startup hide.
        Assert.True(window.IsVisible);
        Assert.Equal(1, window.Opacity);

        window.Close();
        return null;
    });

    /// <summary>
    /// Runs the dispatcher past the render turn the deferred hide is posted to.
    /// <c>RunJobs()</c> alone only drains the current queue, so this yields through a
    /// few posted frames at the same priority the fix uses.
    /// </summary>
    private static async Task FlushFramesAsync()
    {
        for (var i = 0; i < 4; i++)
        {
            await Dispatcher.UIThread.InvokeAsync(static () => { }, DispatcherPriority.Render);
            Dispatcher.UIThread.RunJobs();
        }
    }
}
