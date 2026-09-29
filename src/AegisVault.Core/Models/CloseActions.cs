namespace AegisVault.Core.Models;

/// <summary>
/// What the main window does when the user closes it. Persisted as a string in
/// <see cref="AppPreferences.CloseAction"/> so that an unknown value (an older
/// or hand-edited file) degrades to <see cref="Ask"/> instead of failing.
/// </summary>
public static class CloseActions
{
    /// <summary>Prompt every time (default).</summary>
    public const string Ask = "ask";

    /// <summary>Hide the window to the tray instead of exiting.</summary>
    public const string Minimize = "minimize";

    /// <summary>Exit the application immediately.</summary>
    public const string Exit = "exit";

    public static string Normalize(string? value) => value switch
    {
        Minimize => Minimize,
        Exit => Exit,
        _ => Ask,
    };
}
