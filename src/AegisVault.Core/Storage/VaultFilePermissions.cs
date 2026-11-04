namespace AegisVault.Core.Storage;

/// <summary>
/// Owner-only permissions for vault files on Unix. The default 0644 mode leaks
/// the existence/size/mtime of an otherwise encrypted vault to other users.
/// Windows keeps its own ACL defaults (the vault lives in the user profile).
/// </summary>
public static class VaultFilePermissions
{
    /// <summary>Read and write for the owner only (0600).</summary>
    public const UnixFileMode OwnerOnly = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    /// <summary>Restricts the given paths to the owner (best effort).</summary>
    internal static void Restrict(params string[] paths)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        foreach (var path in paths)
        {
            try
            {
                if (File.Exists(path))
                {
                    File.SetUnixFileMode(path, OwnerOnly);
                }
            }
            catch (Exception)
            {
                // Some file systems (or mounted volumes) do not support Unix
                // modes; the vault stays usable either way.
            }
        }
    }

    /// <summary>Restricts a vault file together with its SQLite side files.</summary>
    internal static void RestrictVault(string path)
        => Restrict(path, path + "-wal", path + "-shm");

    /// <summary>
    /// Writes a file that holds secrets with owner-only permissions and without ever
    /// leaving a partial file behind.
    /// <para>
    /// Two problems this solves. First, <see cref="File.WriteAllText(string,string)"/>
    /// creates the file with the process umask (0644 on a typical Unix), so a plaintext
    /// export or its encrypted sibling is readable by every account on the machine.
    /// Second, a crash or a full disk mid-write leaves a truncated file that still
    /// carries part of the secrets. Writing to a sibling temp file, restricting it,
    /// then renaming means the destination is either the old content or the complete
    /// new content - never half of either.
    /// </para>
    /// </summary>
    /// <param name="path">Destination path; parent directory must exist.</param>
    /// <param name="contents">Callback that writes the payload to the given stream.</param>
    public static void WriteSecretFile(string path, Action<Stream> contents)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(contents);

        var target = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(target);
        if (string.IsNullOrEmpty(directory))
        {
            throw new ArgumentException("Path has no directory.", nameof(path));
        }

        var staging = Path.Combine(
            directory,
            $"{Path.GetFileName(target)}.{Guid.NewGuid():N}.tmp");

        try
        {
            var options = new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                Share = FileShare.None,
            };

            // 0600 from the moment the file exists: the payload must never be briefly
            // world-readable between create and chmod. The property itself is
            // Unix-only, so it cannot be set unconditionally.
            if (!OperatingSystem.IsWindows())
            {
                options.UnixCreateMode = OwnerOnly;
            }

            using (var stream = new FileStream(staging, options))
            {
                contents(stream);
                stream.Flush(flushToDisk: true);
            }

            Restrict(staging);
            File.Move(staging, target, overwrite: true);
            Restrict(target);
        }
        catch
        {
            // Never leave a partial secret behind on failure.
            try
            {
                if (File.Exists(staging))
                {
                    File.Delete(staging);
                }
            }
            catch (Exception)
            {
                // Best effort; the original exception matters more.
            }

            throw;
        }
    }
}
