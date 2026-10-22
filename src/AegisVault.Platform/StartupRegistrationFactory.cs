namespace AegisVault.Platform;

/// <summary>
/// Picks the auto-start implementation for the current OS. Callers depend only on
/// <see cref="IStartupRegistration"/>, so the platform detail stays here.
/// </summary>
public static class StartupRegistrationFactory
{
    public static IStartupRegistration Create()
    {
        if (OperatingSystem.IsWindows())
        {
            return new WindowsStartupRegistration();
        }

        if (OperatingSystem.IsMacOS())
        {
            return new MacLaunchAgentStartupRegistration();
        }

        if (OperatingSystem.IsLinux())
        {
            return new LinuxXdgAutostartRegistration();
        }

        return new UnsupportedStartupRegistration();
    }
}

/// <summary>Used on platforms with no auto-start integration (reports unsupported).</summary>
public sealed class UnsupportedStartupRegistration : IStartupRegistration
{
    public bool IsSupported => false;

    public bool IsEnabled(string executablePath) => false;

    public bool TrySetEnabled(bool enabled, string executablePath, string? arguments = null) => false;
}
