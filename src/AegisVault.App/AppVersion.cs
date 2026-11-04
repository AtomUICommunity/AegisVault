using System.Reflection;

namespace AegisVault.App;

/// <summary>
/// Single source for how the running build reports its version.
/// <para>
/// Two forms are needed and they must not drift apart:
/// </para>
/// <list type="bullet">
/// <item>
/// <see cref="Short"/> (<c>0.6.3</c>) is what the unlock page shows. A build hash
/// there is noise to someone deciding whether to unlock their vault.
/// </item>
/// <item>
/// <see cref="Full"/> (<c>0.6.3+8b2d550</c>) is what the settings page shows, because
/// a bug report that only says "0.6.3" cannot be tied to a specific build.
/// </item>
/// </list>
/// <para>
/// The informational version already carries the <c>+sha</c> suffix (the SDK appends
/// it from <c>SourceRevisionId</c>), so no parsing of build metadata is needed here.
/// </para>
/// </summary>
public static class AppVersion
{
    /// <summary>Three-part version, e.g. <c>0.6.3</c>.</summary>
    public static string Short { get; } = ReadShort();

    /// <summary>Version with the source revision, e.g. <c>0.6.3+8b2d550</c>.</summary>
    public static string Full { get; } = ReadFull();

    private static Assembly Self => typeof(AppVersion).Assembly;

    private static string ReadShort()
    {
        var version = Self.GetName().Version;
        return version is null ? "0.0.0" : version.ToString(3);
    }

    private static string ReadFull()
    {
        var informational = Self
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion;

        if (string.IsNullOrWhiteSpace(informational))
        {
            return Short;
        }

        // Keep the "+revision" but drop any extra build metadata after a second '+'
        // so the label stays short enough for the title bar.
        var plus = informational.IndexOf('+');
        if (plus < 0)
        {
            return informational;
        }

        var head = informational[..plus];
        var tail = informational[(plus + 1)..];
        var nextPlus = tail.IndexOf('+');
        if (nextPlus >= 0)
        {
            tail = tail[..nextPlus];
        }

        return $"{head}+{tail}";
    }
}
