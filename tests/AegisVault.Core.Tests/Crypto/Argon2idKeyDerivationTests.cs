using System.Security.Cryptography;
using System.Text;
using AegisVault.Core.Crypto;
using AegisVault.Core.Models;
using Xunit;

namespace AegisVault.Core.Tests.Crypto;

public sealed class Argon2idKeyDerivationTests
{
    private static readonly byte[] Password = Encoding.UTF8.GetBytes("correct horse battery staple");

    // Small parameters to keep tests fast (libsodium/Sodium.Core enforce a minimum of 3 passes).
    private static readonly KdfParameters FastParameters = new()
    {
        Algorithm = KdfParameters.AlgorithmArgon2id,
        Iterations = 3,
        MemoryBytes = 8L * 1024 * 1024,
        KeySize = 32,
    };

    [Fact]
    public void IsDeterministic()
    {
        var derivation = new Argon2idKeyDerivation();
        var salt = RandomNumberGenerator.GetBytes(16);

        using var first = derivation.DeriveKey(Password, salt, FastParameters);
        using var second = derivation.DeriveKey(Password, salt, FastParameters);

        Assert.Equal(first.ReadOnlySpan.ToArray(), second.ReadOnlySpan.ToArray());
        Assert.Equal(32, first.Length);
    }

    [Fact]
    public void DifferentPasswordProducesDifferentKey()
    {
        var derivation = new Argon2idKeyDerivation();
        var salt = RandomNumberGenerator.GetBytes(16);

        using var first = derivation.DeriveKey(Password, salt, FastParameters);
        using var second = derivation.DeriveKey(Encoding.UTF8.GetBytes("wrong password"), salt, FastParameters);

        Assert.NotEqual(first.ReadOnlySpan.ToArray(), second.ReadOnlySpan.ToArray());
    }

    [Fact]
    public void DifferentSaltProducesDifferentKey()
    {
        var derivation = new Argon2idKeyDerivation();

        using var first = derivation.DeriveKey(Password, RandomNumberGenerator.GetBytes(16), FastParameters);
        using var second = derivation.DeriveKey(Password, RandomNumberGenerator.GetBytes(16), FastParameters);

        Assert.NotEqual(first.ReadOnlySpan.ToArray(), second.ReadOnlySpan.ToArray());
    }

    [Fact]
    public void DifferentCostParametersProduceDifferentKey()
    {
        var derivation = new Argon2idKeyDerivation();
        var salt = RandomNumberGenerator.GetBytes(16);

        using var first = derivation.DeriveKey(Password, salt, FastParameters);
        using var second = derivation.DeriveKey(Password, salt, FastParameters with { Iterations = 4 });
        using var third = derivation.DeriveKey(Password, salt, FastParameters with { MemoryBytes = 16L * 1024 * 1024 });

        Assert.NotEqual(first.ReadOnlySpan.ToArray(), second.ReadOnlySpan.ToArray());
        Assert.NotEqual(first.ReadOnlySpan.ToArray(), third.ReadOnlySpan.ToArray());
    }

    [Fact]
    public void WorksWithProductionDefaultParameters()
    {
        var derivation = new Argon2idKeyDerivation();
        var salt = RandomNumberGenerator.GetBytes(16);
        var parameters = new KdfParameters
        {
            Algorithm = KdfParameters.AlgorithmArgon2id,
            Iterations = KdfParameters.Argon2Defaults.Iterations,
            MemoryBytes = KdfParameters.Argon2Defaults.MemoryBytes,
        };

        using var key = derivation.DeriveKey(Password, salt, parameters);

        Assert.Equal(32, key.Length);
    }

    [Fact]
    public void RejectsMismatchedAlgorithm()
    {
        var derivation = new Argon2idKeyDerivation();
        var parameters = FastParameters with { Algorithm = KdfParameters.AlgorithmPbkdf2Sha256 };

        Assert.Throws<ArgumentException>(() =>
            derivation.DeriveKey(Password, RandomNumberGenerator.GetBytes(16), parameters));
    }

    [Fact]
    public void RejectsTooFewIterations()
    {
        var derivation = new Argon2idKeyDerivation();
        var parameters = FastParameters with { Iterations = 2 };

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            derivation.DeriveKey(Password, RandomNumberGenerator.GetBytes(16), parameters));
    }

    [Fact]
    public void RejectsShortSalt()
    {
        var derivation = new Argon2idKeyDerivation();

        Assert.Throws<ArgumentException>(() =>
            derivation.DeriveKey(Password, RandomNumberGenerator.GetBytes(8), FastParameters));
    }

    /// <summary>
    /// libsodium requires exactly 16 bytes of salt. Accepting more let a vault be
    /// created with SaltSize=32 and then fail forever inside crypto_pwhash with an
    /// opaque exception, so the bound has to be an equality, not a minimum.
    /// </summary>
    [Fact]
    public void RejectsALongerSaltInsteadOfDeferringToLibsodium()
    {
        var derivation = new Argon2idKeyDerivation();

        var exception = Assert.Throws<ArgumentException>(() =>
            derivation.DeriveKey(Password, RandomNumberGenerator.GetBytes(32), FastParameters));

        Assert.Contains("16", exception.Message);
    }

    /// <summary>
    /// The derived key is used as the AES-256 KEK with no further stretching, so any
    /// length other than 32 could be created but never used.
    /// </summary>
    [Theory]
    [InlineData(16)]
    [InlineData(24)]
    [InlineData(64)]
    public void RejectsAKeySizeThatIsNotAes256(int keySize)
    {
        var derivation = new Argon2idKeyDerivation();

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            derivation.DeriveKey(
                Password,
                RandomNumberGenerator.GetBytes(16),
                FastParameters with { KeySize = keySize }));
    }
}
