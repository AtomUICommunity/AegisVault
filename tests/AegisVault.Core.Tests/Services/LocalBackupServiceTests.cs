using System.Security.Cryptography;
using System.Text;
using AegisVault.Core.Models;
using AegisVault.Core.Services;
using AegisVault.Core.Storage;
using Xunit;

namespace AegisVault.Core.Tests.Services;

/// <summary>
/// RM-1 P1: a backup is a VACUUM INTO snapshot with the machine's device keys
/// stripped, plus a manifest whose hash must match before a restore is offered.
/// The live vault must never be touched by taking a backup.
/// </summary>
public sealed class LocalBackupServiceTests : IDisposable
{
    private static readonly byte[] Password = Encoding.UTF8.GetBytes("correct horse battery staple");

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

    public LocalBackupServiceTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "aegis-backup-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
        _vaultPath = Path.Combine(_directory, "vault.aegis");
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

    private VaultService CreateVault(params string[] titles)
    {
        var vault = VaultService.CreateNew(_vaultPath, Password, FastOptions);
        foreach (var title in titles)
        {
            vault.AddEntry(new PasswordEntry { Title = title, Password = "p" });
        }

        return vault;
    }

    [Fact]
    public void BackupStripsDeviceKeysAndWritesAManifest()
    {
        using var vault = CreateVault("Mail");
        vault.RememberDevice(new FakeKeyProtector());
        Assert.True(vault.HasDeviceKey(new FakeKeyProtector()));

        var service = new LocalBackupService(_vaultPath);
        var result = service.CreateBackup(vault, "0.5.0", keepCount: 7);

        Assert.True(result.Success);
        Assert.NotNull(result.ArtifactPath);
        Assert.True(File.Exists(result.ArtifactPath));

        // The live vault keeps its device key; only the snapshot is sanitized.
        Assert.True(vault.HasDeviceKey(new FakeKeyProtector()));
        Assert.False(HasDeviceKeys(result.ArtifactPath!));

        var manifestPath = LocalBackupService.ManifestPathFor(result.ArtifactPath!);
        Assert.True(File.Exists(manifestPath));
        var manifest = System.Text.Json.JsonSerializer.Deserialize(
            File.ReadAllText(manifestPath),
            VaultJsonContext.Default.BackupManifest)!;
        Assert.True(manifest.Sanitized);
        Assert.Equal(7, manifest.Bytes is > 0 ? 7 : 0);
        Assert.Equal(LocalBackupService.ComputeSha256(result.ArtifactPath!), manifest.Sha256);
    }

    [Fact]
    public void BackupWorksWhileLocked()
    {
        using var vault = CreateVault("Mail");
        vault.Lock();
        Assert.False(vault.IsUnlocked);

        var result = new LocalBackupService(_vaultPath).CreateBackup(vault, "0.5.0", keepCount: 7);

        Assert.True(result.Success);
        Assert.True(File.Exists(result.ArtifactPath));
    }

    [Fact]
    public void BackupsAreListedNewestFirstAndRotated()
    {
        using var vault = CreateVault("Mail");
        var clock = new ManualClock();
        var service = new LocalBackupService(_vaultPath, timeProvider: clock);

        for (var index = 0; index < 4; index++)
        {
            clock.Advance(TimeSpan.FromMinutes(1));
            Assert.True(service.CreateBackup(vault, "0.5.0", keepCount: 10).Success);
        }

        Assert.Equal(4, service.ListBackups().Count);

        var removed = service.Rotate(2);

        Assert.Equal(2, removed);
        Assert.Equal(2, service.ListBackups().Count);
    }

    [Fact]
    public void RotationNeverDeletesFilesThatDoNotMatchTheScheme()
    {
        using var vault = CreateVault("Mail");
        var service = new LocalBackupService(_vaultPath);
        Directory.CreateDirectory(service.BackupDirectory);
        var stranger = Path.Combine(service.BackupDirectory, "notes.txt");
        File.WriteAllText(stranger, "keep me");

        for (var index = 0; index < 3; index++)
        {
            service.CreateBackup(vault, "0.5.0", keepCount: 10);
        }

        service.Rotate(1);

        Assert.True(File.Exists(stranger), "rotation must only touch aegis-backup-*.aegis files");
        Assert.Single(service.ListBackups());
    }

    [Fact]
    public void SameSecondBackupsDoNotOverwriteEachOther()
    {
        using var vault = CreateVault("Mail");
        var clock = new ManualClock();
        var service = new LocalBackupService(_vaultPath, timeProvider: clock);

        var first = service.CreateBackup(vault, "0.5.0", keepCount: 10);
        var second = service.CreateBackup(vault, "0.5.0", keepCount: 10);

        Assert.True(first.Success);
        Assert.True(second.Success);
        Assert.NotEqual(first.ArtifactPath, second.ArtifactPath);
        Assert.Equal(2, service.ListBackups().Count);
    }

    [Fact]
    public void ValidateAcceptsAGoodBackupAndRejectsATamperedOne()
    {
        using var vault = CreateVault("Mail");
        var service = new LocalBackupService(_vaultPath);
        var path = service.CreateBackup(vault, "0.5.0", keepCount: 7).ArtifactPath!;

        Assert.Equal(BackupValidationStatus.Ok, LocalBackupService.Validate(path).Status);

        // Flip a byte in the middle of the file.
        var bytes = File.ReadAllBytes(path);
        bytes[bytes.Length / 2] ^= 0xFF;
        File.WriteAllBytes(path, bytes);

        Assert.Equal(BackupValidationStatus.HashMismatch, LocalBackupService.Validate(path).Status);
    }

    [Fact]
    public void ValidateRejectsABackupFromANewerApp()
    {
        using var vault = CreateVault("Mail");
        var service = new LocalBackupService(_vaultPath);
        var path = service.CreateBackup(vault, "0.5.0", keepCount: 7).ArtifactPath!;
        var manifestPath = LocalBackupService.ManifestPathFor(path);
        var manifest = System.Text.Json.JsonSerializer.Deserialize(
            File.ReadAllText(manifestPath),
            VaultJsonContext.Default.BackupManifest)!;

        File.WriteAllText(
            manifestPath,
            System.Text.Json.JsonSerializer.Serialize(
                manifest with { EntryFormatVersion = EntryRepository.EntryFormatVersion + 1 },
                VaultJsonContext.Default.BackupManifest));

        var validation = LocalBackupService.Validate(path);

        Assert.Equal(BackupValidationStatus.VersionTooNew, validation.Status);
        Assert.False(validation.CanRestore);
    }

    [Fact]
    public void RestoreReplacesTheVaultAndKeepsASafetyCopy()
    {
        using var vault = CreateVault("Mail", "Bank");
        var service = new LocalBackupService(_vaultPath);
        var backup = service.CreateBackup(vault, "0.5.0", keepCount: 7).ArtifactPath!;

        // Mutate the live vault after the backup.
        vault.AddEntry(new PasswordEntry { Title = "Later" });
        vault.Dispose();

        var result = service.Restore(backup, _vaultPath);

        Assert.True(result.Success);
        Assert.True(File.Exists(_vaultPath + LocalBackupService.BeforeRestoreSuffix));

        using var restored = VaultService.Open(_vaultPath);
        Assert.Equal(VaultUnlockStatus.Success, restored.Unlock(Password));
        Assert.Equal(["Bank", "Mail"], restored.Entries.Select(entry => entry.Title).OrderBy(title => title));
    }

    [Fact]
    public void RestoreRefusesATamperedBackup()
    {
        using var vault = CreateVault("Mail");
        var service = new LocalBackupService(_vaultPath);
        var backup = service.CreateBackup(vault, "0.5.0", keepCount: 7).ArtifactPath!;
        var bytes = File.ReadAllBytes(backup);
        bytes[^1] ^= 0xFF;
        File.WriteAllBytes(backup, bytes);

        var result = service.Restore(backup, _vaultPath);

        Assert.False(result.Success);
        Assert.Equal("HashMismatch", result.FailureCode);
    }

    [Fact]
    public void ClampKeepCountStaysInRange()
    {
        Assert.Equal(LocalBackupService.MinimumKeepCount, LocalBackupService.ClampKeepCount(-5));
        Assert.Equal(LocalBackupService.MaximumKeepCount, LocalBackupService.ClampKeepCount(10_000));
        Assert.Equal(7, LocalBackupService.ClampKeepCount(7));
    }

    private static bool HasDeviceKeys(string path)
    {
        using var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path};Mode=ReadOnly;Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM device_keys;";
        return Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) > 0;
    }

    /// <summary>Deterministic clock so file names/rotation are reproducible.</summary>
    private sealed class ManualClock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 10, 22, 12, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan delta) => _now = _now.Add(delta);
    }
}
