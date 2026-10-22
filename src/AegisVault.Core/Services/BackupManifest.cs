namespace AegisVault.Core.Services;

/// <summary>
/// Metadata written next to a backup file. It is plaintext on purpose: it only
/// carries version numbers and a content hash so the user (and the restore flow)
/// can tell what a backup contains without unlocking it.
/// </summary>
public sealed record BackupManifest
{
    public const int CurrentVersion = 1;

    /// <summary>Manifest format version (not the vault format).</summary>
    public int Version { get; init; } = CurrentVersion;

    /// <summary>Application version that produced the backup, e.g. <c>0.5.0</c>.</summary>
    public string AppVersion { get; init; } = string.Empty;

    /// <summary>SQLite schema version at backup time.</summary>
    public int SchemaVersion { get; init; }

    /// <summary>Entry payload version at backup time.</summary>
    public int EntryFormatVersion { get; init; }

    /// <summary>Categories payload version at backup time.</summary>
    public int CategoriesPayloadVersion { get; init; }

    public DateTimeOffset CreatedUtc { get; init; }

    public long Bytes { get; init; }

    /// <summary>Lowercase hex SHA-256 of the backup file.</summary>
    public string Sha256 { get; init; } = string.Empty;

    /// <summary>True when device keys were stripped from the snapshot.</summary>
    public bool Sanitized { get; init; }
}

/// <summary>Outcome of a backup attempt, surfaced to the UI status line.</summary>
public sealed record BackupResult(bool Success, string? ArtifactPath, string? FailureCode, string? Message)
{
    public static BackupResult Ok(string path) => new(true, path, null, null);

    public static BackupResult Failed(string code, string? message = null) => new(false, null, code, message);
}

/// <summary>Why a backup was skipped (not an error).</summary>
public enum BackupSkipReason
{
    None,
    Disabled,
    IntervalNotElapsed,
    NoChangesSinceLastBackup,
    VaultMissing,
}

/// <summary>One backup file on disk, as discovered by the retention scan.</summary>
public sealed record BackupArtifact(string Path, DateTimeOffset CreatedUtc, long Bytes);

/// <summary>How a backup file checked out before restoring it.</summary>
public enum BackupValidationStatus
{
    Ok,
    FileMissing,
    ManifestMissing,
    ManifestUnreadable,
    HashMismatch,
    /// <summary>Written by a newer app version whose format this build cannot read.</summary>
    VersionTooNew,
}

/// <summary>Validation outcome plus the manifest when it could be read.</summary>
public sealed record BackupValidation(BackupValidationStatus Status, BackupManifest? Manifest)
{
    public bool CanRestore => Status == BackupValidationStatus.Ok;
}
