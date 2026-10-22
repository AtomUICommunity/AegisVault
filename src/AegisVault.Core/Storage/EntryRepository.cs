using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AegisVault.Core.Crypto;
using AegisVault.Core.Models;

namespace AegisVault.Core.Storage;

/// <summary>
/// Converts between <see cref="PasswordEntry"/> models and their encrypted
/// on-disk representation (AES-256-GCM with entry-bound associated data).
/// </summary>
internal static class EntryRepository
{
    /// <summary>
    /// Current entry payload format. History:
    /// v1 = initial payload (collections could be missing),
    /// v2 = collections guaranteed non-null and always written.
    /// v3 = added scalar credential fields (Phone/AppId/Secret/ApiKey).
    /// v4 = added the scalar Email field.
    /// v5 = added DeletedAt/LastOpenedAt (recycle bin + "recent" view).
    /// </summary>
    public const int EntryFormatVersion = 6;

    public static (byte[] Nonce, byte[] Ciphertext, byte[] Tag) Encrypt(ReadOnlySpan<byte> dek, PasswordEntry entry)
        => Encrypt(dek, entry, EntryFormatVersion);

    /// <summary>Encrypts with an explicit payload version (migrations/tests).</summary>
    internal static (byte[] Nonce, byte[] Ciphertext, byte[] Tag) Encrypt(ReadOnlySpan<byte> dek, PasswordEntry entry, int version)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(entry, VaultJsonContext.Default.PasswordEntry);
        try
        {
            return AesGcmCipher.Encrypt(dek, json, BuildAssociatedData(entry.Id, version));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(json);
        }
    }

    public static PasswordEntry Decrypt(
        ReadOnlySpan<byte> dek,
        Guid id,
        int version,
        byte[] nonce,
        byte[] ciphertext,
        byte[] tag)
    {
        var json = AesGcmCipher.Decrypt(dek, nonce, ciphertext, tag, BuildAssociatedData(id, version));
        try
        {
            var entry = JsonSerializer.Deserialize(json, VaultJsonContext.Default.PasswordEntry)
                ?? throw new InvalidDataException("Entry payload is empty.");

            // Normalize missing members and apply version upgrades so callers
            // always receive a fully populated model.
            return ModelMigrations.UpgradeEntry(entry, version);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(json);
        }
    }

    /// <summary>Associated data binding a ciphertext to its entry id and format version.</summary>
    public static byte[] BuildAssociatedData(Guid id, int version)
        => Encoding.UTF8.GetBytes($"AegisVault|entry|{id:D}|v{version}");
}
