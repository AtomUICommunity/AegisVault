using System.Security.Cryptography;
using AegisVault.Core.Models;

namespace AegisVault.Core.Crypto;

/// <summary>
/// PBKDF2-HMAC-SHA256 fallback key derivation for environments where the
/// native libsodium library cannot be loaded.
/// </summary>
public sealed class Pbkdf2KeyDerivation : IKeyDerivation
{
    public const int DefaultIterations = 600_000;

    public string Algorithm => KdfParameters.AlgorithmPbkdf2Sha256;

    public SecureBuffer DeriveKey(ReadOnlySpan<byte> password, ReadOnlySpan<byte> salt, KdfParameters parameters)
    {
        if (parameters.Algorithm != Algorithm)
        {
            throw new ArgumentException($"Unsupported KDF algorithm '{parameters.Algorithm}'.", nameof(parameters));
        }

        // The vault layer always generates a 16-byte salt; the derivation itself
        // accepts any non-empty salt so standard test vectors remain usable.
        if (salt.IsEmpty)
        {
            throw new ArgumentException("Salt must not be empty.", nameof(salt));
        }

        // Checked: a value above int.MaxValue silently wrapped to 1 in an unchecked
        // cast, which turned a tampered header into a near-instant derivation instead
        // of a rejected file.
        int iterations;
        try
        {
            iterations = parameters.Iterations is > 0 ? checked((int)parameters.Iterations) : DefaultIterations;
        }
        catch (OverflowException)
        {
            throw new ArgumentOutOfRangeException(nameof(parameters), "PBKDF2 iteration count is too large.");
        }

        if (iterations < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(parameters), "PBKDF2 iterations out of range.");
        }

        // Deliberately permissive: this primitive also has to reproduce the RFC 2898
        // test vectors (which use a 40-byte key), so the AES-256 constraint belongs to
        // the vault layer, not here. VaultService/KdfParameters enforce KeySize=32.
        var keyLength = parameters.KeySize;
        if (keyLength is < 16 or > 64)
        {
            throw new ArgumentOutOfRangeException(nameof(parameters), "Key length out of range.");
        }

        var buffer = new SecureBuffer(keyLength);
        try
        {
            Rfc2898DeriveBytes.Pbkdf2(password, salt, buffer.Span, iterations, HashAlgorithmName.SHA256);
            return buffer;
        }
        catch
        {
            buffer.Dispose();
            throw;
        }
    }
}
