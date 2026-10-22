using System.Text;

namespace AegisVault.Platform;

/// <summary>
/// Linux auto-start through the XDG autostart convention
/// (<c>~/.config/autostart/*.desktop</c>), which GNOME/KDE/XFCE all honour.
/// </summary>
public sealed class LinuxXdgAutostartRegistration : IStartupRegistration
{
    public const string FileName = "aegisvault.desktop";
    private const string DisplayName = "AegisVault";

    private readonly string? _overrideDirectory;

    /// <param name="autostartDirectory">
    /// Overrides <c>~/.config/autostart</c>; tests point this at a temp folder.
    /// </param>
    public LinuxXdgAutostartRegistration(string? autostartDirectory = null)
        => _overrideDirectory = autostartDirectory;

    public bool IsSupported => OperatingSystem.IsLinux();

    public string Directory => _overrideDirectory
        ?? Path.Combine(
            Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") is { Length: > 0 } configHome
                ? configHome
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config"),
            "autostart");

    public string DesktopFilePath => Path.Combine(Directory, FileName);

    public bool IsEnabled(string executablePath)
    {
        if (!OperatingSystem.IsLinux() || string.IsNullOrWhiteSpace(executablePath))
        {
            return false;
        }

        try
        {
            if (!File.Exists(DesktopFilePath))
            {
                return false;
            }

            var content = File.ReadAllText(DesktopFilePath);
            if (content.Contains("Hidden=true", StringComparison.OrdinalIgnoreCase)
                && !content.Contains("Hidden=false", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            return content.Contains(executablePath.Trim(), StringComparison.Ordinal);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public bool TrySetEnabled(bool enabled, string executablePath, string? arguments = null)
    {
        if (!OperatingSystem.IsLinux() || string.IsNullOrWhiteSpace(executablePath))
        {
            return false;
        }

        try
        {
            if (!enabled)
            {
                if (File.Exists(DesktopFilePath))
                {
                    File.Delete(DesktopFilePath);
                }

                return true;
            }

            System.IO.Directory.CreateDirectory(Directory);
            File.WriteAllText(DesktopFilePath, BuildDesktopEntry(executablePath.Trim(), arguments), new UTF8Encoding(false));
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Desktop entry text; <c>Exec</c> is quoted per the XDG spec.</summary>
    public static string BuildDesktopEntry(string executablePath, string? arguments)
    {
        var command = string.IsNullOrWhiteSpace(arguments)
            ? Quote(executablePath)
            : $"{Quote(executablePath)} {arguments.Trim()}";

        return $"""
            [Desktop Entry]
            Type=Application
            Name={DisplayName}
            Exec={command}
            X-GNOME-Autostart-enabled=true
            Terminal=false
            NoDisplay=true
            """;
    }

    private static string Quote(string value)
        => value.Contains(' ', StringComparison.Ordinal) ? $"\"{value}\"" : value;
}
