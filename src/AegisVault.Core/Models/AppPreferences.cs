namespace AegisVault.Core.Models;

/// <summary>
/// Non-sensitive application preferences kept in a plaintext local file.
/// Secrets (vault contents, device keys) never belong here.
/// </summary>
public sealed record AppPreferences
{
    /// <summary>Last successfully opened vault path (filled at startup).</summary>
    public string? LastVaultPath { get; init; }

    /// <summary>
    /// Recently opened/created vault paths, newest first (max three). Plaintext
    /// like the rest of this file; missing values are normalised to an empty
    /// list by the store.
    /// </summary>
    public IReadOnlyList<string>? RecentVaultPaths { get; init; }

    /// <summary>True once the one-time first-run guide has been dismissed.</summary>
    public bool StartupGuideDismissed { get; init; }

    /// <summary>"system" | "light" | "dark".</summary>
    public string Theme { get; init; } = "system";

    /// <summary>"system" | "zh" | "en". Applied at startup; changing it requires a restart.</summary>
    public string Language { get; init; } = "system";

    /// <summary>Shows the optional floating quick-access ball (default off).</summary>
    public bool ShowFloatingBall { get; init; }

    /// <summary>
    /// Reduces motion: disables AtomUI control animations and the app's own
    /// splash/ball transitions (default off).
    /// </summary>
    public bool ReduceMotion { get; init; }

    /// <summary>Starts with the OS session (Windows Run key; default off).</summary>
    public bool AutoStart { get; init; }

    /// <summary>Starts hidden in the tray instead of showing the main window.</summary>
    public bool StartMinimized { get; init; }

    /// <summary>
    /// "ask" | "minimize" | "exit": what closing the main window does. Unknown
    /// values are normalised to "ask" by the store; see <see cref="CloseActions"/>.
    /// </summary>
    public string CloseAction { get; init; } = CloseActions.Ask;

    /// <summary>Floating ball position in screen pixels ("x,y"); null when never moved.</summary>
    public string? BallPosition { get; init; }

    /// <summary>"left" | "right" while the ball is docked to a screen edge; otherwise null.</summary>
    public string? BallDockedSide { get; init; }

    /// <summary>
    /// Main window rectangle in screen pixels ("x,y,w,h") while restored, newest
    /// value written when the window closes or its state changes. Null until the
    /// window has been placed at least once; a stale rectangle (monitor layout
    /// changed) is ignored at startup.
    /// </summary>
    public string? MainWindowBounds { get; init; }

    /// <summary>Whether the main window was maximized when it last closed.</summary>
    public bool MainWindowMaximized { get; init; }
}
