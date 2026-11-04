using System.Text;
using AegisVault.Core.Services;
using AegisVault.Core.Storage;
using Xunit;

namespace AegisVault.Core.Tests.Storage;

/// <summary>
/// Owner-only permissions for vault files, plus the atomic writer used for exports.
/// <para>
/// Exports hold every password in plaintext, so they must be owner-only and must never
/// exist in a half-written state. Both properties used to be missing: the CSV and the
/// encrypted JSON were written with File.WriteAllText/Bytes, which takes the process
/// umask (0644 on a typical Unix) and leaves a truncated file if the write fails.
/// </para>
/// </summary>
public sealed class VaultFilePermissionsTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "aegis-perm-tests", Guid.NewGuid().ToString("N"));

    // ---------- existing coverage: the vault database and its backups ----------

    [Fact]
    public void VaultDatabaseUsesOwnerOnlyPermissionsOnUnix()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "vault.aegis");
        using (VaultDatabase.OpenOrCreate(path))
        {
        }

        Assert.Equal(VaultFilePermissions.OwnerOnly, File.GetUnixFileMode(path));
    }

    [Fact]
    public void BackupUsesOwnerOnlyPermissionsOnUnix()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "vault.aegis");
        using (var vault = VaultService.CreateNew(path, "master password"u8))
        {
            vault.SaveBackup();
        }

        Assert.Equal(VaultFilePermissions.OwnerOnly, File.GetUnixFileMode(path + ".bak"));
    }

    [Fact]
    public void MissingPathsAreIgnored()
    {
        VaultFilePermissions.Restrict(Path.Combine(_directory, "does-not-exist.aegis"));
    }

    // ---------- new coverage: the atomic writer used for secrets on disk ----------

    private string Target => Path.Combine(_directory, "export.csv");

    [Fact]
    public void WriteSecretFileWritesThePayloadAndLeavesNoStagingFile()
    {
        Directory.CreateDirectory(_directory);

        VaultFilePermissions.WriteSecretFile(Target, stream =>
        {
            using var writer = new StreamWriter(stream, new UTF8Encoding(false), leaveOpen: true);
            writer.Write("title,password\nGitHub,s3cret\n");
        });

        Assert.Equal("title,password\nGitHub,s3cret\n", File.ReadAllText(Target));
        Assert.Empty(Directory.GetFiles(_directory, "*.tmp"));
    }

    [Fact]
    public void WriteSecretFileReplacesExistingContent()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(Target, "old");

        VaultFilePermissions.WriteSecretFile(Target, stream =>
        {
            var bytes = "new"u8.ToArray();
            stream.Write(bytes);
        });

        Assert.Equal("new", File.ReadAllText(Target));
    }

    /// <summary>
    /// A failure part-way through must not leave a partial secret on disk, and must not
    /// destroy whatever the destination held before.
    /// </summary>
    [Fact]
    public void AFailedWriteLeavesTheOriginalFileIntactAndNoPartialCopy()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(Target, "original content");

        Assert.Throws<InvalidOperationException>(() =>
            VaultFilePermissions.WriteSecretFile(Target, stream =>
            {
                var partial = "half a password"u8.ToArray();
                stream.Write(partial);
                throw new InvalidOperationException("simulated crash");
            }));

        Assert.Equal("original content", File.ReadAllText(Target));
        Assert.Empty(Directory.GetFiles(_directory, "*.tmp"));
    }

    /// <summary>
    /// 0600 on Unix. Windows keeps its ACL defaults (the vault lives in the user
    /// profile), so this assertion is Unix-only by design.
    /// </summary>
    [Fact]
    public void WrittenFileIsOwnerOnlyOnUnix()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        Directory.CreateDirectory(_directory);
        VaultFilePermissions.WriteSecretFile(Target, stream => stream.Write("x"u8));

        var mode = File.GetUnixFileMode(Target);
        Assert.Equal(VaultFilePermissions.OwnerOnly, mode);
        Assert.False(mode.HasFlag(UnixFileMode.GroupRead));
        Assert.False(mode.HasFlag(UnixFileMode.OtherRead));
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
}
