using AegisVault.App.Theme;
using AegisVault.Core.Models;
using AegisVault.Core.Services;
using AegisVault.Platform;
using AtomUI;
using AtomUI.Desktop.Controls;
using Avalonia;

namespace AegisVault.App;

internal static class Program
{
    /// <summary>
    /// Window titles a second launch activates. The title bar follows the UI
    /// language, so both the neutral product name and the localized one are
    /// accepted (see <c>App_Title</c>).
    /// </summary>
    internal static readonly string[] WindowTitles = ["AegisVault", Localization.Loc.Get(Localization.Loc.Chinese, "App_Title")];

    /// <summary>
    /// Held for the whole process lifetime. The restart flow releases it early
    /// so the replacement process does not mistake itself for a second instance.
    /// </summary>
    internal static SingleInstanceGuard? Instance { get; private set; }

    /// <summary>
    /// AtomUI 6.2.6+ moved the entry point from <c>Application.UseAtomUI</c> to
    /// <c>AppBuilder.UseAtomUI</c>: the configuration callback now runs before the
    /// Application is constructed (on the UI thread, after platform services), and
    /// AtomUI is committed in the generated <c>RegisterServices</c> hook.
    /// <para>
    /// The callback must therefore stay side-effect free: no <c>Application.Current</c>,
    /// no <c>FontManager</c>, no control or bitmap creation. We only read the plaintext
    /// preferences file (no Avalonia types) and hand AtomUI its theme tokens.
    /// </para>
    /// </summary>
    /// <summary>
    /// Builder shared by the desktop entry point and the headless test host.
    /// Deliberately excludes <c>UseAtomUIPlatformDetect</c>: the headless session
    /// detects the missing windowing subsystem and installs its own backend.
    /// </summary>
    internal static AppBuilder CreateBuilder()
        => AppBuilder.Configure<App>()
            .WithAtomUIDefaultOptions()
            .UseAtomUI(ConfigureAtomUI)
            .WithInterFont()
            .LogToTrace();

    public static AppBuilder BuildAvaloniaApp()
        => CreateBuilder().UseAtomUIPlatformDetect();

    private static void ConfigureAtomUI(IAtomUIBuilder builder)
    {
        builder.UseDesktopControls();
        builder.UseDesktopColorPicker();

        // The splash is ours (plain Avalonia), so AtomUI must not show its own.
        var preferences = LoadStartupPreferences();
        AppTheme.ConfigureInitial(builder, preferences.Theme, preferences.ReduceMotion);
    }

    /// <summary>
    /// Reads the theme/motion preferences for the initial AtomUI configuration.
    /// Deliberately avoids Avalonia APIs: this runs before the Application exists.
    /// </summary>
    private static AppPreferences LoadStartupPreferences()
    {
        try
        {
            return new AppPreferencesStore().Load();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return new AppPreferences();
        }
    }

    [STAThread]
    public static void Main(string[] args)
    {
        CoreDumpGuard.DisableCoreDumps();

        Instance = SingleInstanceGuard.Acquire();
        if (!Instance.IsOwner)
        {
            // Another instance owns the vault: bring its window forward and exit.
            // The window may not exist yet when both launches race; the second
            // process still exits so no duplicate session ever starts.
            WindowActivation.TryActivateByAnyTitle(WindowTitles);
            return;
        }

        try
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        finally
        {
            Instance.Dispose();
            Instance = null;
        }
    }
}
