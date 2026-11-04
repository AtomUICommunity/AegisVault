using System.Security.Cryptography;
using AegisVault.Core.Models;
using Sodium;

namespace AegisVault.Core.Crypto;

/// <summary>
/// Argon2id (libsodium crypto_pwhash, version 1.3) key derivation.
/// Memory-hard default parameters: 64 MiB, 3 passes, parallelism 1 (libsodium fixed).
/// </summary>
public sealed class Argon2idKeyDerivation : IKeyDerivation
{
    public const long MinMemoryBytes = 8L * 1024;
    public const long MaxMemoryBytes = 1024L * 1024 * 1024;

    public string Algorithm => KdfParameters.AlgorithmArgon2id;

    public SecureBuffer DeriveKey(ReadOnlySpan<byte> password, ReadOnlySpan<byte> salt, KdfParameters parameters)
    {
        if (parameters.Algorithm != Algorithm)
        {
            throw new ArgumentException($"Unsupported KDF algorithm '{parameters.Algorithm}'.", nameof(parameters));
        }

        // libsodium's crypto_pwhash requires a salt of exactly crypto_pwhash_SALTBYTES
        // (16). Accepting a longer salt here deferred the failure to Sodium with an
        // opaque SaltOutOfRangeException at key-derivation time, so a vault whose
        // header said SaltSize=32 could be created but never unlocked.
        if (salt.Length != KdfParameters.DefaultSaltSize)
        {
            throw new ArgumentException(
                $"Salt must be exactly {KdfParameters.DefaultSaltSize} bytes.",
                nameof(salt));
        }

        var iterations = parameters.Iterations;
        // libsodium (via Sodium.Core) enforces a minimum of 3 passes.
        if (iterations < 3 || iterations > 16)
        {
            throw new ArgumentOutOfRangeException(nameof(parameters), "Argon2id iterations out of range.");
        }

        var memoryBytes = parameters.MemoryBytes;
        if (memoryBytes < MinMemoryBytes || memoryBytes > MaxMemoryBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(parameters), "Argon2id memory cost out of range.");
        }

        // The derived key is used directly as the AES-256 KEK, so its length is not
        // negotiable: AesGcm only accepts 16/24/32 and this vault is AES-256. The old
        // 16..64 range let a vault be created that failed later inside the cipher.
        var keyLength = parameters.KeySize;
        if (keyLength != KdfParameters.DefaultKeySize)
        {
            throw new ArgumentOutOfRangeException(
                nameof(parameters),
                $"Key length must be {KdfParameters.DefaultKeySize} bytes.");
        }

        // Sodium.Core exposes array-based APIs only; copy, derive, then scrub the copies.
        var passwordCopy = password.ToArray();
        var saltCopy = salt.ToArray();
        byte[]? derived = null;
        try
        {
            derived = PasswordHash.ArgonHashBinary(
                passwordCopy,
                saltCopy,
                iterations,
                checked((int)memoryBytes),
                keyLength,
                PasswordHash.ArgonAlgorithm.Argon_2ID13);

            return SecureBuffer.From(derived);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(passwordCopy);
            CryptographicOperations.ZeroMemory(saltCopy);
            if (derived is not null)
            {
                CryptographicOperations.ZeroMemory(derived);
            }
        }
    }
}
