using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Styling;
using AegisVault.App.Theme;

namespace AegisVault.App.Views;

/// <summary>
/// Brand splash shown while the application initialises. Built from plain
/// Avalonia controls and the app-owned token table only: it must render before
/// AtomUI finishes its (slow) theme initialisation, so it cannot depend on
/// AtomUI resources, controls or styles.
/// </summary>
public partial class SplashWindow : Window
{
    /// <summary>Fade-out duration once the destination window is up.</summary>
    public const int FadeOutMilliseconds = 250;

    private readonly bool _dark;
    private readonly bool _reduceMotion;
    private bool _fading;

    /// <summary>Parameterless overload for the XAML runtime loader.</summary>
    public SplashWindow()
        : this("system")
    {
    }

    public SplashWindow(string themePreference, bool reduceMotion = false)
    {
        InitializeComponent();

        _dark = AppTheme.IsDarkPreference(themePreference);
        _reduceMotion = reduceMotion;
        ApplyPalette();

        if (!reduceMotion)
        {
            Progress.Transitions =
            [
                new DoubleTransition
                {
                    Property = ProgressBar.ValueProperty,
                    Duration = TimeSpan.FromMilliseconds(300),
                    Easing = new CubicEaseOut(),
                },
            ];

            Transitions =
            [
                new DoubleTransition
                {
                    Property = OpacityProperty,
                    Duration = TimeSpan.FromMilliseconds(FadeOutMilliseconds),
                    Easing = new CubicEaseOut(),
                },
            ];
        }
    }

    /// <summary>Updates the status line and the determinate progress (0..1).</summary>
    public void SetStatus(string text, double progress)
    {
        StatusText.Text = text;
        Progress.Value = Math.Clamp(progress, 0, 1);
    }

    /// <summary>Fades the splash out and closes it; safe to call once.</summary>
    public async Task FadeOutAndCloseAsync()
    {
        if (_fading)
        {
            return;
        }

        _fading = true;
        Opacity = 0;

        if (!_reduceMotion)
        {
            await Task.Delay(FadeOutMilliseconds);
        }

        Close();
    }

    private void ApplyPalette()
    {
        var variant = _dark ? ThemeVariant.Dark : ThemeVariant.Light;
        var surface = AppTheme.TokenColor("ColorBgContainer", variant);
        var text = AppTheme.TokenColor("ColorText", variant);
        var secondary = AppTheme.TokenColor("ColorTextSecondary", variant);
        var brand = AppTheme.TokenColor("ColorPrimary", variant);
        var border = AppTheme.TokenColor("ColorBorderSecondary", variant);

        Card.Background = new SolidColorBrush(surface);
        Card.BorderBrush = new SolidColorBrush(border);
        Card.BoxShadow = _dark
            ? default
            : new BoxShadows(new BoxShadow
            {
                OffsetX = 0,
                OffsetY = 10,
                Blur = 32,
                Color = Color.FromArgb(0x26, 0x00, 0x00, 0x00),
            });

        TitleText.Foreground = new SolidColorBrush(text);
        SubtitleText.Foreground = new SolidColorBrush(secondary);
        StatusText.Foreground = new SolidColorBrush(secondary);

        Progress.Foreground = new SolidColorBrush(brand);

        IconTile.Background = new SolidColorBrush(Color.FromArgb(0x1F, brand.R, brand.G, brand.B));
        ShieldPath.Fill = new SolidColorBrush(brand);
        ShieldCore.Fill = new SolidColorBrush(surface);
    }
}
