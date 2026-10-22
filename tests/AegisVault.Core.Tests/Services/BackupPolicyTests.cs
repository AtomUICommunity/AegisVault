using AegisVault.Core.Models;
using AegisVault.Core.Services;
using Xunit;

namespace AegisVault.Core.Tests.Services;

/// <summary>
/// Automatic local backups must not hammer a disabled or unchanged vault, and the
/// interval must survive hand-edited configuration.
/// </summary>
public sealed class BackupPolicyTests
{
    private static AppPreferences Enabled(int intervalMinutes = 60) => new()
    {
        BackupEnabled = true,
        BackupLocalEnabled = true,
        BackupIntervalMinutes = intervalMinutes,
    };

    private static readonly DateTimeOffset Now = new(2026, 10, 22, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void DisabledMasterSwitchSkips()
    {
        var preferences = new AppPreferences { BackupEnabled = false, BackupLocalEnabled = true };

        Assert.Equal(
            BackupSkipReason.Disabled,
            BackupPolicy.Evaluate(preferences, Now, null, vaultChangedSinceLastBackup: true));
    }

    [Fact]
    public void DisabledLocalTargetSkips()
    {
        var preferences = new AppPreferences { BackupEnabled = true, BackupLocalEnabled = false };

        Assert.Equal(
            BackupSkipReason.Disabled,
            BackupPolicy.Evaluate(preferences, Now, null, vaultChangedSinceLastBackup: true));
    }

    [Fact]
    public void RunsWhenEnabledChangedAndNoPreviousBackupExists()
    {
        Assert.Equal(
            BackupSkipReason.None,
            BackupPolicy.Evaluate(Enabled(), Now, lastSuccessUtc: null, vaultChangedSinceLastBackup: true));
    }

    [Fact]
    public void SkipsBeforeTheIntervalElapsed()
    {
        var last = Now.AddMinutes(-30);

        Assert.Equal(
            BackupSkipReason.IntervalNotElapsed,
            BackupPolicy.Evaluate(Enabled(60), Now, last, vaultChangedSinceLastBackup: true));
    }

    [Fact]
    public void RunsAfterTheIntervalElapsed()
    {
        var last = Now.AddMinutes(-61);

        Assert.Equal(
            BackupSkipReason.None,
            BackupPolicy.Evaluate(Enabled(60), Now, last, vaultChangedSinceLastBackup: true));
    }

    [Fact]
    public void SkipsWhenNothingChangedEvenAfterTheInterval()
    {
        var last = Now.AddHours(-5);

        Assert.Equal(
            BackupSkipReason.NoChangesSinceLastBackup,
            BackupPolicy.Evaluate(Enabled(60), Now, last, vaultChangedSinceLastBackup: false));
    }

    [Theory]
    [InlineData(0, BackupPolicy.MinimumIntervalMinutes)]
    [InlineData(-100, BackupPolicy.MinimumIntervalMinutes)]
    [InlineData(999_999, BackupPolicy.MaximumIntervalMinutes)]
    public void OutOfRangeIntervalsAreClamped(int configured, int effective)
    {
        var preferences = Enabled(configured);

        // One minute before the clamped interval: still too early.
        var justInside = Now.AddMinutes(-(effective - 1));
        Assert.Equal(
            BackupSkipReason.IntervalNotElapsed,
            BackupPolicy.Evaluate(preferences, Now, justInside, vaultChangedSinceLastBackup: true));

        // One minute after the clamped interval: due.
        var justOutside = Now.AddMinutes(-(effective + 1));
        Assert.Equal(
            BackupSkipReason.None,
            BackupPolicy.Evaluate(preferences, Now, justOutside, vaultChangedSinceLastBackup: true));
    }

    [Fact]
    public void ParseLastSuccessToleratesJunk()
    {
        Assert.Null(BackupPolicy.ParseLastSuccess(null));
        Assert.Null(BackupPolicy.ParseLastSuccess("  "));
        Assert.Null(BackupPolicy.ParseLastSuccess("not a date"));
        Assert.Equal(Now, BackupPolicy.ParseLastSuccess("2026-10-22T12:00:00Z"));
    }
}
