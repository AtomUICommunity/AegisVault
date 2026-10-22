using AegisVault.App.Services;
using AegisVault.Core.Models;
using AegisVault.Core.Services;
using Xunit;

namespace AegisVault.App.Tests;

/// <summary>
/// The automatic backup coordinator must respect the schedule, never run twice at
/// once, and record its outcome in the preferences so the settings card can show it.
/// </summary>
public sealed class BackupCoordinatorTests : IDisposable
{
    private static readonly byte[] Password = "coordinator-password"u8.ToArray();

    private static readonly VaultOptions FastOptions = new()
    {
        Kdf = new KdfParameters
        {
            Algorithm = KdfParameters.AlgorithmArgon2id,
            Iterations = 3,
            MemoryBytes = 8L * 1024 * 1024,
        },
    };

    private readonly string _directory;
    private readonly string _vaultPath;
    private AppPreferences _preferences;
    private readonly ManualClock _clock = new();

    public BackupCoordinatorTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "aegis-coordinator-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
        _vaultPath = Path.Combine(_directory, "vault.aegis");
        _preferences = new AppPreferences();
    }

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

    private (VaultService Vault, BackupCoordinator Coordinator) Create(bool enabled = true, int intervalMinutes = 60)
    {
        var vault = VaultService.CreateNew(_vaultPath, Password, FastOptions);
        vault.AddEntry(new PasswordEntry { Title = "Mail", Password = "p" });
        _preferences = new AppPreferences
        {
            BackupEnabled = enabled,
            BackupLocalEnabled = true,
            BackupIntervalMinutes = intervalMinutes,
            BackupLocalKeepCount = 5,
        };

        var coordinator = new BackupCoordinator(
            new LocalBackupService(_vaultPath, timeProvider: _clock),
            vault,
            () => _preferences,
            preferences => _preferences = preferences,
            "0.5.0",
            _clock);
        return (vault, coordinator);
    }

    [Fact]
    public async Task AutomaticRunIsSkippedWhileDisabled()
    {
        var (vault, coordinator) = Create(enabled: false);
        using var _ = vault;
        using var __ = coordinator;

        var result = await coordinator.RunOnceAsync(automatic: true);

        Assert.False(result.Success);
        Assert.Equal("Disabled", result.FailureCode);
    }

    [Fact]
    public async Task ManualRunStillHonoursTheMasterSwitch()
    {
        var (vault, coordinator) = Create(enabled: false);
        using var _ = vault;
        using var __ = coordinator;

        var result = await coordinator.RunOnceAsync(automatic: false);

        Assert.False(result.Success);
        Assert.Equal("disabled", result.FailureCode);
    }

    [Fact]
    public async Task ManualRunBacksUpImmediately()
    {
        var (vault, coordinator) = Create();
        using var _ = vault;
        using var __ = coordinator;

        var result = await coordinator.RunOnceAsync(automatic: false);

        Assert.True(result.Success);
        Assert.True(File.Exists(result.ArtifactPath));
    }

    [Fact]
    public async Task SuccessfulRunRecordsTheTimestampInPreferences()
    {
        var (vault, coordinator) = Create(intervalMinutes: 15);
        using var _ = vault;
        using var __ = coordinator;

        var result = await coordinator.RunOnceAsync(automatic: true);

        Assert.True(result.Success);
        Assert.Equal("ok", _preferences.BackupLastResult);
        Assert.NotNull(BackupPolicy.ParseLastSuccess(_preferences.BackupLastSuccessUtc));
    }

    [Fact]
    public async Task AutomaticRunIsSkippedBeforeTheIntervalElapsed()
    {
        var (vault, coordinator) = Create(intervalMinutes: 60);
        using var _ = vault;
        using var __ = coordinator;

        Assert.True((await coordinator.RunOnceAsync(automatic: true)).Success);

        _clock.Advance(TimeSpan.FromMinutes(30));
        var second = await coordinator.RunOnceAsync(automatic: true);

        Assert.False(second.Success);
        Assert.Equal("IntervalNotElapsed", second.FailureCode);
    }

    [Fact]
    public async Task AutomaticRunIsSkippedWhenTheVaultDidNotChange()
    {
        var (vault, coordinator) = Create(intervalMinutes: 60);
        using var _ = vault;
        using var __ = coordinator;

        Assert.True((await coordinator.RunOnceAsync(automatic: true)).Success);

        // Past the interval but no writes since the last backup: no duplicate file.
        _clock.Advance(TimeSpan.FromHours(5));
        var second = await coordinator.RunOnceAsync(automatic: true);

        Assert.False(second.Success);
        Assert.Equal("NoChangesSinceLastBackup", second.FailureCode);
    }

    [Fact]
    public async Task AutomaticRunResumesAfterTheVaultChanged()
    {
        var (vault, coordinator) = Create(intervalMinutes: 60);
        using var _ = vault;
        using var __ = coordinator;

        Assert.True((await coordinator.RunOnceAsync(automatic: true)).Success);

        _clock.Advance(TimeSpan.FromHours(5));
        vault.AddEntry(new PasswordEntry { Title = "Bank", Password = "q" });

        var second = await coordinator.RunOnceAsync(automatic: true);

        Assert.True(second.Success);
    }

    [Fact]
    public void StartingWithoutTheMasterSwitchArmsNoTimer()
    {
        var (vault, coordinator) = Create(enabled: false);
        using var _ = vault;
        using var __ = coordinator;

        coordinator.Start();   // must be a no-op rather than a tight loop
        coordinator.Stop();
    }

    private sealed class ManualClock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 10, 22, 12, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan delta) => _now = _now.Add(delta);
    }
}
