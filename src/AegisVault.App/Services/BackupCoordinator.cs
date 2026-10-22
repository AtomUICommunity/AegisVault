using AegisVault.Core.Models;
using AegisVault.Core.Services;

namespace AegisVault.App.Services;

/// <summary>
/// Drives the automatic local backup: a coarse timer plus a change check, so an
/// idle vault never produces redundant snapshots. Runs on the UI thread's timer
/// but hands the (potentially slow) snapshot to the thread pool.
/// </summary>
public sealed class BackupCoordinator : IDisposable
{
    private readonly LocalBackupService _local;
    private readonly VaultService _vault;
    private readonly Func<AppPreferences> _readPreferences;
    private readonly Action<AppPreferences> _writePreferences;
    private readonly TimeProvider _time;
    private readonly string _appVersion;

    private readonly object _gate = new();
    private Timer? _timer;
    private DateTimeOffset? _lastVaultWriteUtc;
    private DateTimeOffset? _lastBackupUtc;
    private bool _running;
    private bool _disposed;

    public BackupCoordinator(
        LocalBackupService local,
        VaultService vault,
        Func<AppPreferences> readPreferences,
        Action<AppPreferences> writePreferences,
        string appVersion,
        TimeProvider? timeProvider = null)
    {
        _local = local;
        _vault = vault;
        _readPreferences = readPreferences;
        _writePreferences = writePreferences;
        _appVersion = appVersion;
        _time = timeProvider ?? TimeProvider.System;
        _lastBackupUtc = BackupPolicy.ParseLastSuccess(readPreferences().BackupLastSuccessUtc);
        // "Changed since last backup", not "changed since the coordinator started":
        // otherwise the first automatic run after a fresh unlock would always look
        // unchanged and never produce a backup.
        _lastVaultWriteUtc = _lastBackupUtc;
    }

    /// <summary>Raised after every attempt so the UI can refresh its status line.</summary>
    public event Action<BackupResult, BackupSkipReason>? Completed;

    /// <summary>Starts (or restarts) the timer using the current preferences.</summary>
    public void Start()
    {
        if (_disposed)
        {
            return;
        }

        lock (_gate)
        {
            _timer?.Dispose();
            _timer = null;

            var preferences = _readPreferences();
            if (!preferences.BackupEnabled || !preferences.LocalBackupEnabled)
            {
                return;
            }

            // Poll at a fraction of the interval so "every 24h" still fires
            // soon after the user reaches the threshold, without a tight loop.
            var interval = TimeSpan.FromMinutes(Math.Clamp(
                preferences.BackupIntervalMinutes,
                BackupPolicy.MinimumIntervalMinutes,
                BackupPolicy.MaximumIntervalMinutes));
            var period = TimeSpan.FromSeconds(Math.Clamp(interval.TotalSeconds / 4, 30, 900));

            _timer = new Timer(_ => _ = RunOnceAsync(automatic: true), null, period, period);
        }
    }

    /// <summary>Stops the timer (lock/exit). No snapshot is taken.</summary>
    public void Stop()
    {
        lock (_gate)
        {
            _timer?.Dispose();
            _timer = null;
        }
    }

    /// <summary>
    /// Applies the policy and, when due, takes a backup. Safe to call from the
    /// UI ("立即备份") as well as from the timer.
    /// </summary>
    /// <param name="automatic">False for a user-triggered backup (skips the
    /// interval/change checks but still honours the master switch).</param>
    public async Task<BackupResult> RunOnceAsync(bool automatic, CancellationToken cancellationToken = default)
    {
        if (_disposed)
        {
            return BackupResult.Failed("disposed", "Backup coordinator disposed.");
        }

        if (_running)
        {
            // A user click during an automatic run must not start a second snapshot.
            return BackupResult.Failed("busy", "A backup is already running.");
        }

        var preferences = _readPreferences();
        var now = _time.GetUtcNow();
        var changed = LatestWriteUtc(_vault) is { } latest
            && (_lastVaultWriteUtc is null || latest > _lastVaultWriteUtc);

        if (automatic)
        {
            var skip = BackupPolicy.Evaluate(preferences, now, _lastBackupUtc, changed);
            if (skip != BackupSkipReason.None)
            {
                Completed?.Invoke(BackupResult.Failed(skip.ToString(), null), skip);
                return BackupResult.Failed(skip.ToString(), null);
            }
        }
        else if (!preferences.BackupEnabled)
        {
            return BackupResult.Failed("disabled", "Backup is disabled.");
        }

        _running = true;
        try
        {
            var result = await Task.Run(
                () => _local.CreateBackup(_vault, _appVersion, preferences.BackupLocalKeepCount, cancellationToken),
                cancellationToken).ConfigureAwait(false);

            if (result.Success)
            {
                _lastBackupUtc = now;
                _lastVaultWriteUtc = LatestWriteUtc(_vault);
                Publish(preferences with
                {
                    BackupLastSuccessUtc = now.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
                    BackupLastResult = "ok",
                });
            }
            else
            {
                Publish(preferences with { BackupLastResult = "failed:" + (result.FailureCode ?? "unknown") });
            }

            Completed?.Invoke(result, BackupSkipReason.None);
            return result;
        }
        finally
        {
            _running = false;
        }
    }

    private void Publish(AppPreferences preferences)
    {
        try
        {
            _writePreferences(preferences);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Persisting the status line is cosmetic; the backup already exists.
        }
    }

    private static DateTimeOffset? LatestWriteUtc(VaultService vault)
    {
        DateTimeOffset? latest = null;
        foreach (var entry in vault.Entries)
        {
            if (latest is null || entry.UpdatedAt > latest)
            {
                latest = entry.UpdatedAt;
            }
        }

        foreach (var entry in vault.DeletedEntries)
        {
            if (latest is null || entry.UpdatedAt > latest)
            {
                latest = entry.UpdatedAt;
            }
        }

        return latest;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Stop();
    }
}
