using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AegisVault.Core.Storage;

namespace AegisVault.Core.Services;

/// <summary>
/// Creates, lists and rotates local backup files.
/// <para>
/// A backup is a <c>VACUUM INTO</c> snapshot (consistent even under WAL) with
/// the machine's device keys stripped, plus a plaintext <c>.manifest.json</c>
/// carrying versions and the content hash. The live vault is never modified.
/// </para>
/// </summary>
public sealed partial class LocalBackupService
{
    /// <summary>Default folder name created next to the vault.</summary>
    public const string DefaultDirectoryName = "backups";

    public const string FilePrefix = "aegis-backup-";

    public const int MinimumKeepCount = 1;
    public const int MaximumKeepCount = 100;

    private readonly string _vaultPath;
    private readonly TimeProvider _time;

    public LocalBackupService(string vaultPath, string? backupDirectory = null, TimeProvider? timeProvider = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(vaultPath);
        _vaultPath = Path.GetFullPath(vaultPath);
        BackupDirectory = backupDirectory ?? Path.Combine(
            Path.GetDirectoryName(_vaultPath) ?? ".",
            DefaultDirectoryName);
        _time = timeProvider ?? TimeProvider.System;
    }

    public string VaultPath => _vaultPath;

    public string BackupDirectory { get; }

    /// <summary>Clamps a requested retention count into the supported range.</summary>
    public static int ClampKeepCount(int keepCount)
        => Math.Clamp(keepCount, MinimumKeepCount, MaximumKeepCount);

    /// <summary>
    /// Takes a snapshot of the vault, sanitizes it, verifies the written hash,
    /// drops a manifest next to it and applies retention. Works while the vault
    /// is unlocked or locked (the snapshot never decrypts anything).
    /// </summary>
    public BackupResult CreateBackup(
        VaultService vault,
        string appVersion,
        int keepCount,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(vault);
        if (!File.Exists(_vaultPath))
        {
            return BackupResult.Failed("vault-missing", "The vault file no longer exists.");
        }

        Directory.CreateDirectory(BackupDirectory);

        var stagingDirectory = Path.Combine(Path.GetTempPath(), "aegis-backup-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stagingDirectory);
        var stagingPath = Path.Combine(stagingDirectory, "snapshot.aegis");

        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            vault.SnapshotTo(stagingPath);
            VaultDatabase.SanitizeSnapshot(stagingPath);

            var sha256 = ComputeSha256(stagingPath);
            var bytes = new FileInfo(stagingPath).Length;
            var createdUtc = _time.GetUtcNow();

            var destinationPath = ReserveDestination(createdUtc, appVersion, sha256);
            File.Move(stagingPath, destinationPath, overwrite: false);

            var manifest = new BackupManifest
            {
                AppVersion = appVersion,
                SchemaVersion = SchemaMigrations.CurrentSchemaVersion,
                EntryFormatVersion = EntryRepository.EntryFormatVersion,
                CategoriesPayloadVersion = Models.CategoriesPayload.CurrentVersion,
                CreatedUtc = createdUtc,
                Bytes = bytes,
                Sha256 = sha256,
                Sanitized = true,
            };
            WriteManifest(destinationPath, manifest);
            File.Delete(stagingPath);

            Rotate(keepCount);
            return BackupResult.Ok(destinationPath);
        }
        catch (OperationCanceledException)
        {
            return BackupResult.Failed("cancelled", "Backup cancelled.");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return BackupResult.Failed("io", exception.Message);
        }
        finally
        {
            TryDeleteDirectory(stagingDirectory);
        }
    }

    /// <summary>Existing backups, newest first.</summary>
    public IReadOnlyList<BackupArtifact> ListBackups()
    {
        if (!Directory.Exists(BackupDirectory))
        {
            return [];
        }

        var artifacts = new List<BackupArtifact>();
        foreach (var path in Directory.EnumerateFiles(BackupDirectory, FilePrefix + "*"))
        {
            if (!path.EndsWith(".aegis", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            try
            {
                var info = new FileInfo(path);
                artifacts.Add(new BackupArtifact(path, info.CreationTimeUtc, info.Length));
            }
            catch (IOException)
            {
                // A file that vanished mid-scan simply is not listed.
            }
        }

        return artifacts
            .OrderByDescending(artifact => ParseTimestamp(artifact.Path) ?? artifact.CreatedUtc)
            .ToList();
    }

    /// <summary>
    /// Deletes the oldest backups so at most <paramref name="keepCount"/> remain.
    /// Only files matching our naming scheme are ever touched.
    /// </summary>
    public int Rotate(int keepCount)
    {
        var keep = ClampKeepCount(keepCount);
        var backups = ListBackups();
        var removed = 0;

        for (var index = keep; index < backups.Count; index++)
        {
            var artifact = backups[index];
            try
            {
                File.Delete(artifact.Path);
                var manifestPath = artifact.Path + ".manifest.json";
                if (File.Exists(manifestPath))
                {
                    File.Delete(manifestPath);
                }

                removed++;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // Retention is best effort: a locked file must not fail the backup.
            }
        }

        return removed;
    }

    /// <summary>Path of the manifest that belongs to a backup file.</summary>
    public static string ManifestPathFor(string backupPath) => backupPath + ".manifest.json";

    /// <summary>
    /// Reads a backup's manifest and re-computes its SHA-256. A restore is only
    /// offered when this returns <see cref="BackupValidationStatus.Ok"/>.
    /// </summary>
    public static BackupValidation Validate(string backupPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(backupPath);

        if (!File.Exists(backupPath))
        {
            return new BackupValidation(BackupValidationStatus.FileMissing, null);
        }

        var manifestPath = ManifestPathFor(backupPath);
        if (!File.Exists(manifestPath))
        {
            return new BackupValidation(BackupValidationStatus.ManifestMissing, null);
        }

        BackupManifest? manifest;
        try
        {
            manifest = JsonSerializer.Deserialize(
                File.ReadAllText(manifestPath),
                VaultJsonContext.Default.BackupManifest);
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
        {
            return new BackupValidation(BackupValidationStatus.ManifestUnreadable, null);
        }

        if (manifest is null)
        {
            return new BackupValidation(BackupValidationStatus.ManifestUnreadable, null);
        }

        // A backup from a newer release may contain payload versions this build
        // would silently misread, so refuse instead of guessing.
        if (manifest.Version > BackupManifest.CurrentVersion
            || manifest.SchemaVersion > SchemaMigrations.CurrentSchemaVersion
            || manifest.EntryFormatVersion > EntryRepository.EntryFormatVersion
            || manifest.CategoriesPayloadVersion > Models.CategoriesPayload.CurrentVersion)
        {
            return new BackupValidation(BackupValidationStatus.VersionTooNew, manifest);
        }

        string actual;
        try
        {
            actual = ComputeSha256(backupPath);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return new BackupValidation(BackupValidationStatus.ManifestUnreadable, manifest);
        }

        return string.Equals(actual, manifest.Sha256, StringComparison.OrdinalIgnoreCase)
            ? new BackupValidation(BackupValidationStatus.Ok, manifest)
            : new BackupValidation(BackupValidationStatus.HashMismatch, manifest);
    }

    /// <summary>Suffix used for the safety copy taken right before a restore.</summary>
    public const string BeforeRestoreSuffix = ".before-restore.bak";

    /// <summary>
    /// Replaces the live vault with <paramref name="backupPath"/>. The current
    /// vault is first copied to <c>&lt;vault&gt;.before-restore.bak</c> (never
    /// rotated) so a failed restore can be rolled back.
    /// </summary>
    public BackupResult Restore(string backupPath, string vaultPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(backupPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(vaultPath);

        var validation = Validate(backupPath);
        if (!validation.CanRestore)
        {
            return BackupResult.Failed(validation.Status.ToString(), "The backup did not pass validation.");
        }

        var target = Path.GetFullPath(vaultPath);
        var safetyCopy = target + BeforeRestoreSuffix;
        var stagingTarget = target + ".restoring";

        try
        {
            if (File.Exists(target))
            {
                File.Copy(target, safetyCopy, overwrite: true);
                VaultFilePermissions.Restrict(safetyCopy);
            }

            // Copy next to the target first so the final move is atomic-ish.
            File.Copy(backupPath, stagingTarget, overwrite: true);
            VaultFilePermissions.Restrict(stagingTarget);

            // SQLite side files from the previous database must not survive.
            foreach (var sideFile in new[] { target + "-wal", target + "-shm" })
            {
                if (File.Exists(sideFile))
                {
                    File.Delete(sideFile);
                }
            }

            File.Move(stagingTarget, target, overwrite: true);
            VaultFilePermissions.Restrict(target);
            return BackupResult.Ok(target);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            TryRollback(stagingTarget, safetyCopy, target);
            return BackupResult.Failed("io", exception.Message);
        }
    }

    private static void TryRollback(string stagingTarget, string safetyCopy, string target)
    {
        try
        {
            if (File.Exists(stagingTarget))
            {
                File.Delete(stagingTarget);
            }

            if (File.Exists(safetyCopy))
            {
                File.Copy(safetyCopy, target, overwrite: true);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Rollback is best effort; the safety copy stays on disk either way.
        }
    }

    /// <summary>
    /// Finds a free file name, adding a numeric suffix when a same-second backup
    /// with the same content already exists (never overwrites).
    /// </summary>
    private string ReserveDestination(DateTimeOffset createdUtc, string appVersion, string sha256)
    {
        for (var attempt = 1; attempt <= 99; attempt++)
        {
            var candidate = Path.Combine(BackupDirectory, BuildFileName(createdUtc, appVersion, sha256, attempt));
            if (!File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new IOException("Could not find a free backup file name.");
    }

    /// <summary>
    /// <c>aegis-backup-&lt;yyyyMMdd-HHmmssZ&gt;-a&lt;app&gt;-s&lt;schema&gt;-e&lt;entries&gt;-&lt;sha8&gt;.aegis</c>.
    /// A same-second collision gets a <c>-2</c>, <c>-3</c> … suffix instead of overwriting.
    /// </summary>
    internal static string BuildFileName(DateTimeOffset createdUtc, string appVersion, string sha256, int attempt = 1)
    {
        var stamp = createdUtc.UtcDateTime.ToString("yyyyMMdd-HHmmssZ", CultureInfo.InvariantCulture);
        var app = SanitizeSegment(appVersion);
        var sha8 = sha256.Length >= 8 ? sha256[..8] : sha256.PadRight(8, '0');
        var suffix = attempt <= 1 ? string.Empty : "-" + attempt.ToString(CultureInfo.InvariantCulture);
        return $"{FilePrefix}{stamp}-a{app}-s{SchemaMigrations.CurrentSchemaVersion}-e{EntryRepository.EntryFormatVersion}-{sha8}{suffix}.aegis";
    }

    internal static DateTimeOffset? ParseTimestamp(string path)
    {
        var match = TimestampPattern().Match(Path.GetFileName(path));
        if (!match.Success)
        {
            return null;
        }

        return DateTimeOffset.TryParseExact(
            match.Groups["stamp"].Value,
            "yyyyMMdd-HHmmssZ",
            CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal,
            out var parsed)
            ? parsed
            : null;
    }

    [GeneratedRegex("^aegis-backup-(?<stamp>[0-9]{8}-[0-9]{6}Z)-a", RegexOptions.CultureInvariant)]
    private static partial Regex TimestampPattern();

    internal static string ComputeSha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    private static void WriteManifest(string backupPath, BackupManifest manifest)
    {
        var json = JsonSerializer.Serialize(manifest, VaultJsonContext.Default.BackupManifest);
        File.WriteAllText(ManifestPathFor(backupPath), json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        VaultFilePermissions.Restrict(ManifestPathFor(backupPath));
    }

    private static string SanitizeSegment(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var character in value)
        {
            builder.Append(character is >= '0' and <= '9' or >= 'a' and <= 'z' or >= 'A' and <= 'Z' or '.' or '-'
                ? character
                : '_');
        }

        return builder.Length == 0 ? "unknown" : builder.ToString();
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (IOException)
        {
            // Temp cleanup is best effort.
        }
    }
}
