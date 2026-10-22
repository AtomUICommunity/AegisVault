using System.Text;

namespace AegisVault.Platform;

/// <summary>
/// macOS auto-start through a per-user LaunchAgent
/// (<c>~/Library/LaunchAgents/com.aegisvault.app.plist</c>). The published app is
/// a bare Mach-O executable, so the plist points at its absolute path with the
/// <c>--tray</c> argument instead of a bundle.
/// </summary>
public sealed class MacLaunchAgentStartupRegistration : IStartupRegistration
{
    public const string Label = "com.aegisvault.app";
    public const string FileName = Label + ".plist";

    private readonly string? _overrideDirectory;

    /// <param name="launchAgentsDirectory">
    /// Overrides <c>~/Library/LaunchAgents</c>; tests point this at a temp folder.
    /// </param>
    public MacLaunchAgentStartupRegistration(string? launchAgentsDirectory = null)
        => _overrideDirectory = launchAgentsDirectory;

    public bool IsSupported => OperatingSystem.IsMacOS();

    public string Directory => _overrideDirectory ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        "Library",
        "LaunchAgents");

    public string PlistPath => Path.Combine(Directory, FileName);

    public bool IsEnabled(string executablePath)
    {
        if (!OperatingSystem.IsMacOS() || string.IsNullOrWhiteSpace(executablePath))
        {
            return false;
        }

        try
        {
            if (!File.Exists(PlistPath))
            {
                return false;
            }

            // The OS (the file) is the source of truth, but a plist pointing at a
            // moved executable must count as disabled so the caller rewrites it.
            var content = File.ReadAllText(PlistPath);
            return content.Contains(Escape(executablePath.Trim()), StringComparison.Ordinal);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public bool TrySetEnabled(bool enabled, string executablePath, string? arguments = null)
    {
        if (!OperatingSystem.IsMacOS() || string.IsNullOrWhiteSpace(executablePath))
        {
            return false;
        }

        try
        {
            if (!enabled)
            {
                if (File.Exists(PlistPath))
                {
                    File.Delete(PlistPath);
                }

                return true;
            }

            System.IO.Directory.CreateDirectory(Directory);
            File.WriteAllText(PlistPath, BuildPlist(executablePath.Trim(), arguments), new UTF8Encoding(false));
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// LaunchAgent payload. Kept minimal (RunAtLoad + ProgramArguments) so it works
    /// without a bundle; launchd reads it at the next login.
    /// </summary>
    public static string BuildPlist(string executablePath, string? arguments)
    {
        var argumentXml = new StringBuilder();
        argumentXml.Append($"        <string>{Escape(executablePath)}</string>\n");
        if (!string.IsNullOrWhiteSpace(arguments))
        {
            foreach (var argument in arguments.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                argumentXml.Append($"        <string>{Escape(argument)}</string>\n");
            }
        }

        return $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
            <plist version="1.0">
            <dict>
                <key>Label</key>
                <string>{Label}</string>
                <key>ProgramArguments</key>
                <array>
            {argumentXml}    </array>
                <key>RunAtLoad</key>
                <true/>
            </dict>
            </plist>
            """;
    }

    private static string Escape(string value) => value
        .Replace("&", "&amp;", StringComparison.Ordinal)
        .Replace("<", "&lt;", StringComparison.Ordinal)
        .Replace(">", "&gt;", StringComparison.Ordinal)
        .Replace("\"", "&quot;", StringComparison.Ordinal);
}
