using AtomUI;
using AtomUI.Theme;
using AtomUI.Theme.Algorithms;
using AtomUI.Theme.Configuration;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Styling;

namespace AegisVault.App.Theme;

/// <summary>
/// Neutral (shadcn-like) theme on top of the built-in AtomUI theme: near-black
/// primary, zinc grays, 10px radius. The light/dark preference is applied both
/// to Avalonia's theme variant (Fluent controls) and to AtomUI (ApplyThemeAsync),
/// so every control follows the same appearance.
/// </summary>
public static class AppTheme
{
    private const string ThemeId = "DaybreakBlue";

    /// <summary>Serializes theme transitions; only the newest request is applied.</summary>
    private static readonly SemaphoreSlim ApplyGate = new(1, 1);
    private static int _applyRevision;

    // Last appearance applied through ConfigureInitial/Apply. The motion token
    // lives in the same theme configuration, so a change there must defeat the
    // "redundant transition" early-out below.
    private static bool? _appliedDark;
    private static bool? _appliedReduceMotion;

    /// <summary>Ink Green suite, light appearance.</summary>
    private static readonly (string Key, string Value)[] LightTokens =
    [
        ("ColorPrimary", "#0F6B4F"),
        ("ColorInfo", "#0F6B4F"),
        ("ColorSuccess", "#16A34A"),
        ("ColorWarning", "#D97706"),
        ("ColorError", "#DC2626"),
        ("ColorBgLayout", "#F7F7F9"),
        ("ColorBgContainer", "#FFFFFF"),
        ("ColorText", "#09090B"),
        ("ColorTextSecondary", "#52525B"),
        ("ColorTextTertiary", "#A1A1AA"),
        ("ColorTextPlaceholder", "#A1A1AA"),
        ("ColorBorderSecondary", "#ECECEE"),
        ("BorderRadius", "8"),
        ("FontSize", "14"),
        ("ControlHeight", "32"),
    ];

    /// <summary>Ink Green suite, dark appearance.</summary>
    private static readonly (string Key, string Value)[] DarkTokens =
    [
        ("ColorPrimary", "#1F8A63"),
        ("ColorInfo", "#1F8A63"),
        ("ColorSuccess", "#34D399"),
        ("ColorWarning", "#FBBF24"),
        ("ColorError", "#F87171"),
        ("ColorBgLayout", "#0A0A0B"),
        ("ColorBgContainer", "#121214"),
        ("ColorText", "#FAFAFA"),
        ("ColorTextSecondary", "#A1A1AA"),
        ("ColorTextTertiary", "#71717A"),
        ("ColorTextPlaceholder", "#71717A"),
        ("ColorBorderSecondary", "#232326"),
        ("BorderRadius", "8"),
        ("FontSize", "14"),
        ("ControlHeight", "32"),
    ];

    /// <summary>Resolves a configured token (e.g. <c>ColorPrimary</c>) for code-behind use.</summary>
    public static Color TokenColor(string key, ThemeVariant variant)
    {
        foreach (var (candidate, value) in variant == ThemeVariant.Dark ? DarkTokens : LightTokens)
        {
            if (candidate == key && Color.TryParse(value, out var color))
            {
                return color;
            }
        }

        return Colors.Gray;
    }

    public static ThemeConfig BuildConfig(bool dark, bool reduceMotion = false)
    {
        var builder = new ThemeConfigBuilder()
            .WithAlgorithms(dark ? [ThemeAlgorithm.Dark] : [ThemeAlgorithm.Default]);

        foreach (var (key, value) in dark ? DarkTokens : LightTokens)
        {
            builder.WithToken(key, value);
        }

        // AtomUI keeps control motion behind this registered global token; it is
        // the only app-wide switch (controls bind IsMotionEnabled to it).
        builder.WithToken("EnableMotion", reduceMotion ? "false" : "true");

        return builder.Build();
    }

    public static bool IsDarkPreference(string preference) => preference switch
    {
        "dark" => true,
        "light" => false,
        _ => DetectSystemDark(),
    };

    private static bool DetectSystemDark()
        => Application.Current?.PlatformSettings?.GetColorValues().ThemeVariant == PlatformThemeVariant.Dark;

    /// <summary>Initial theme, applied during AtomUI initialization (before first render).</summary>
    public static void ConfigureInitial(IAtomUIBuilder builder, string preference, bool reduceMotion = false)
    {
        var dark = IsDarkPreference(preference);
        _appliedDark = dark;
        _appliedReduceMotion = reduceMotion;
        builder.WithInitialTheme(ThemeId, BuildConfig(dark, reduceMotion));
    }

    /// <summary>Applies the preference to the Avalonia theme variant and to AtomUI.</summary>
    public static void Apply(Application application, string preference, bool reduceMotion = false)
    {
        ArgumentNullException.ThrowIfNull(application);

        application.RequestedThemeVariant = preference switch
        {
            "light" => ThemeVariant.Light,
            "dark" => ThemeVariant.Dark,
            _ => ThemeVariant.Default,
        };

        try
        {
            var manager = application.GetThemeManager();
            if (manager is null)
            {
                return;
            }

            // Skip a redundant transition (e.g. startup, where WithInitialTheme
            // already applied the same configuration) unless the motion
            // preference changed, which needs the theme re-applied to take effect.
            var desiredDark = IsDarkPreference(preference);
            var current = manager.CurrentTheme;
            if (current is { ThemeId: ThemeId }
                && _appliedDark == desiredDark
                && _appliedReduceMotion == reduceMotion)
            {
                return;
            }

            _appliedDark = desiredDark;
            _appliedReduceMotion = reduceMotion;
            _ = ApplyQueuedAsync(manager, BuildConfig(desiredDark, reduceMotion), Interlocked.Increment(ref _applyRevision));
        }
        catch (Exception)
        {
            // Theme switching is best effort; the initial theme is already applied.
        }
    }

    /// <summary>
    /// Applies the theme serially and only for the newest request: rapid menu
    /// clicks could otherwise finish out of order and leave the wrong
    /// appearance on screen.
    /// </summary>
    private static async Task ApplyQueuedAsync(IThemeManager manager, ThemeConfig config, int revision)
    {
        try
        {
            await ApplyGate.WaitAsync();
            try
            {
                if (Volatile.Read(ref _applyRevision) != revision)
                {
                    // A newer click superseded this one while waiting.
                    return;
                }

                await manager.ApplyThemeAsync(
                    new ThemeRequest(ThemeId, config, ThemeTransitionReason.UserRequest),
                    CancellationToken.None);
            }
            finally
            {
                ApplyGate.Release();
            }
        }
        catch (Exception)
        {
            // Best effort: the previous appearance stays in place.
        }
    }
}
