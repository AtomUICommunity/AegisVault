using AegisVault.Platform;
using Xunit;

namespace AegisVault.Platform.Tests;

/// <summary>
/// STA-1: auto-start now has macOS (LaunchAgent) and Linux (XDG autostart)
/// implementations, not just the Windows registry.
/// </summary>
public sealed class CrossPlatformStartupRegistrationTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "aegis-startup-tests",
        Guid.NewGuid().ToString("N"));

    public CrossPlatformStartupRegistrationTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public void FactoryReturnsAnImplementationForTheCurrentPlatform()
    {
        var registration = StartupRegistrationFactory.Create();

        Assert.NotNull(registration);
        if (OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() || OperatingSystem.IsLinux())
        {
            Assert.True(registration.IsSupported);
        }
    }

    [Fact]
    public void UnsupportedImplementationNeverClaimsToWork()
    {
        var registration = new UnsupportedStartupRegistration();

        Assert.False(registration.IsSupported);
        Assert.False(registration.IsEnabled("C:/app/AegisVault.App.exe"));
        Assert.False(registration.TrySetEnabled(true, "C:/app/AegisVault.App.exe"));
    }

    [Fact]
    public void MacPlistContainsTheLabelPathAndTrayArgument()
    {
        var plist = MacLaunchAgentStartupRegistration.BuildPlist("/Applications/AegisVault.App", "--tray");

        Assert.Contains("com.aegisvault.app", plist);
        Assert.Contains("/Applications/AegisVault.App", plist);
        Assert.Contains("<string>--tray</string>", plist);
        Assert.Contains("RunAtLoad", plist);
    }

    [Fact]
    public void MacPlistEscapesXmlMetacharacters()
    {
        var plist = MacLaunchAgentStartupRegistration.BuildPlist("/tmp/a&b<c>", null);

        Assert.Contains("/tmp/a&amp;b&lt;c&gt;", plist);
        Assert.DoesNotContain("/tmp/a&b<c>", plist);
    }

    [Fact]
    public void LinuxDesktopEntryQuotesPathsWithSpaces()
    {
        var entry = LinuxXdgAutostartRegistration.BuildDesktopEntry("/opt/my apps/AegisVault.App", "--tray");

        Assert.Contains("Exec=\"/opt/my apps/AegisVault.App\" --tray", entry);
        Assert.Contains("X-GNOME-Autostart-enabled=true", entry);
        Assert.Contains("[Desktop Entry]", entry);
    }

    [Fact]
    public void LinuxDesktopEntryLeavesSimplePathsUnquoted()
    {
        var entry = LinuxXdgAutostartRegistration.BuildDesktopEntry("/usr/local/bin/aegisvault", null);

        Assert.Contains("Exec=/usr/local/bin/aegisvault", entry);
        Assert.DoesNotContain("\"", entry);
    }

    [Fact]
    public void MacRegistrationWritesAndRemovesItsPlist()
    {
        if (!OperatingSystem.IsMacOS())
        {
            return;   // The implementation is a no-op elsewhere by design.
        }

        var registration = new MacLaunchAgentStartupRegistration(_directory);
        var executable = "/Applications/AegisVault.App";

        Assert.True(registration.TrySetEnabled(true, executable, "--tray"));
        Assert.True(File.Exists(registration.PlistPath));
        Assert.True(registration.IsEnabled(executable));

        Assert.True(registration.TrySetEnabled(false, executable));
        Assert.False(File.Exists(registration.PlistPath));
        Assert.False(registration.IsEnabled(executable));
    }

    [Fact]
    public void LinuxRegistrationWritesAndRemovesItsDesktopFile()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;   // The implementation is a no-op elsewhere by design.
        }

        var registration = new LinuxXdgAutostartRegistration(_directory);
        var executable = "/usr/local/bin/aegisvault";

        Assert.True(registration.TrySetEnabled(true, executable, "--tray"));
        Assert.True(File.Exists(registration.DesktopFilePath));
        Assert.True(registration.IsEnabled(executable));

        Assert.True(registration.TrySetEnabled(false, executable));
        Assert.False(File.Exists(registration.DesktopFilePath));
        Assert.False(registration.IsEnabled(executable));
    }

    [Fact]
    public void IsEnabledReportsFalseWhenTheRecordedPathMoved()
    {
        if (!OperatingSystem.IsMacOS() && !OperatingSystem.IsLinux())
        {
            return;
        }

        IStartupRegistration registration = OperatingSystem.IsMacOS()
            ? new MacLaunchAgentStartupRegistration(_directory)
            : new LinuxXdgAutostartRegistration(_directory);

        Assert.True(registration.TrySetEnabled(true, "/old/location/AegisVault.App", "--tray"));

        // A moved executable must look disabled so the caller rewrites the entry.
        Assert.False(registration.IsEnabled("/new/location/AegisVault.App"));
    }
}
