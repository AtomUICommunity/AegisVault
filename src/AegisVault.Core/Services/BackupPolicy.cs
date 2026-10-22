using AegisVault.Core.Models;

namespace AegisVault.Core.Services;

/// <summary>
/// Decides whether an automatic backup should run right now. Kept pure so the
/// schedule is testable with an injected clock.
/// </summary>
public static class BackupPolicy
{
    public const int MinimumIntervalMinutes = 15;
    public const int MaximumIntervalMinutes = 43200;

    /// <summary>
    /// Returns why a backup should be skipped, or <see cref="BackupSkipReason.None"/>
    /// when it should run.
    /// </summary>
    /// <param name="preferences">Current preferences (interval, enable flags).</param>
    /// <param name="now">Current UTC time.</param>
    /// <param name="lastSuccessUtc">Last successful backup time, if any.</param>
    /// <param name="vaultChangedSinceLastBackup">
    /// True when the vault has been written since the last backup. Taking a
    /// snapshot of an unchanged vault would only burn disk space.
    /// </param>
    public static BackupSkipReason Evaluate(
        AppPreferences preferences,
        DateTimeOffset now,
        DateTimeOffset? lastSuccessUtc,
        bool vaultChangedSinceLastBackup)
    {
        ArgumentNullException.ThrowIfNull(preferences);

        if (!preferences.BackupEnabled || !preferences.LocalBackupEnabled)
        {
            return BackupSkipReason.Disabled;
        }

        var interval = TimeSpan.FromMinutes(Math.Clamp(
            preferences.BackupIntervalMinutes,
            MinimumIntervalMinutes,
            MaximumIntervalMinutes));

        if (lastSuccessUtc is { } previous && now - previous < interval)
        {
            return BackupSkipReason.IntervalNotElapsed;
        }

        if (!vaultChangedSinceLastBackup)
        {
            return BackupSkipReason.NoChangesSinceLastBackup;
        }

        return BackupSkipReason.None;
    }

    /// <summary>Parses the stored ISO-8601 timestamp, tolerating junk.</summary>
    public static DateTimeOffset? ParseLastSuccess(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return DateTimeOffset.TryParse(
            value,
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal,
            out var parsed)
            ? parsed
            : null;
    }
}
