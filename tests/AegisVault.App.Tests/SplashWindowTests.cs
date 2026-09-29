using Avalonia.Controls;
using AegisVault.App.Views;
using Xunit;

namespace AegisVault.App.Tests;

/// <summary>
/// The splash covers the AtomicUI initialisation window; it is deliberately
/// built from plain Avalonia controls so it can render before AtomUI themes
/// exist, and it must stay on screen for a moment instead of flickering.
/// </summary>
public sealed class SplashWindowTests
{
    [Fact]
    public Task ChromeIsBrandAndClickFree() => Headless.Run(() =>
    {
        var window = new SplashWindow("light");
        try
        {
            Assert.False(window.ShowInTaskbar);
            Assert.Equal(WindowDecorations.None, window.WindowDecorations);

            var title = window.FindControl<TextBlock>("TitleText");
            var subtitle = window.FindControl<TextBlock>("SubtitleText");
            Assert.NotNull(title);
            Assert.NotNull(subtitle);
            Assert.Equal(Localization.Loc.T("App_Title"), title!.Text);
            Assert.Equal(Localization.Loc.T("Unlock_Subtitle"), subtitle!.Text);
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public Task SetStatusUpdatesTextAndProgress() => Headless.Run(() =>
    {
        var window = new SplashWindow("light");
        try
        {
            window.SetStatus("Opening the vault…", 0.75);

            var status = window.FindControl<TextBlock>("StatusText");
            var progress = window.FindControl<ProgressBar>("Progress");
            Assert.NotNull(status);
            Assert.NotNull(progress);
            Assert.Equal("Opening the vault…", status!.Text);
            Assert.Equal(0.75, progress!.Value);
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public Task PaletteFollowsTheThemePreference() => Headless.Run(() =>
    {
        var light = new SplashWindow("light");
        var dark = new SplashWindow("dark");
        try
        {
            var lightTile = Assert.IsType<Avalonia.Media.SolidColorBrush>(
                light.FindControl<Border>("IconTile")!.Background);
            var darkTile = Assert.IsType<Avalonia.Media.SolidColorBrush>(
                dark.FindControl<Border>("IconTile")!.Background);

            var lightBrand = Theme.AppTheme.TokenColor("ColorPrimary", Avalonia.Styling.ThemeVariant.Light);
            var darkBrand = Theme.AppTheme.TokenColor("ColorPrimary", Avalonia.Styling.ThemeVariant.Dark);

            Assert.Equal(lightBrand.R, lightTile.Color.R);
            Assert.Equal(lightBrand.G, lightTile.Color.G);
            Assert.Equal(darkBrand.R, darkTile.Color.R);
            Assert.Equal(darkBrand.G, darkTile.Color.G);
            Assert.True(lightTile.Color.A < byte.MaxValue, "the tile should be a brand tint, not solid brand");
        }
        finally
        {
            light.Close();
            dark.Close();
        }
    });

    [Fact]
    public Task FadeOutAndCloseClosesAfterTheFade() => Headless.RunAsync<object?>(async () =>
    {
        var window = new SplashWindow("light");
        window.Show();

        // The fade itself is a transition (not observable headlessly without
        // render ticks), so assert the transition is installed and that the
        // window is closed once the fade duration has elapsed.
        var fade = Assert.Single(window.Transitions!);
        var doubleTransition = Assert.IsType<Avalonia.Animation.DoubleTransition>(fade);
        Assert.Equal(
            TimeSpan.FromMilliseconds(SplashWindow.FadeOutMilliseconds),
            doubleTransition.Duration);

        await window.FadeOutAndCloseAsync();

        Assert.False(window.IsVisible);
        return null;
    });

    [Fact]
    public Task ReducedMotionSkipsTheFadeAndTheProgressAnimation() => Headless.RunAsync<object?>(async () =>
    {
        var window = new SplashWindow("light", reduceMotion: true);
        window.Show();

        Assert.Null(window.Transitions);
        Assert.Null(window.FindControl<ProgressBar>("Progress")!.Transitions);

        await window.FadeOutAndCloseAsync();

        Assert.False(window.IsVisible);
        return null;
    });
}
