using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AegisVault.Core.Crypto;
using AegisVault.Core.Models;
using AegisVault.Core.Services;
using AegisVault.Core.Storage;
using Microsoft.Data.Sqlite;
using Xunit;

namespace AegisVault.Core.Tests.Services;

public sealed class VaultServiceTests : IDisposable
{
    private static readonly byte[] Password = Encoding.UTF8.GetBytes("correct horse battery staple");
    private static readonly byte[] NewPassword = Encoding.UTF8.GetBytes("a different master password");

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

    public VaultServiceTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "aegis-tests", Guid.NewGuid().ToString("N"));
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
            // Best effort cleanup.
        }
    }

    [Fact]
    public void CreateUnlock_And_EntryRoundTrip()
    {
        Guid entryId;
        using (var vault = VaultService.CreateNew(_vaultPath, Password, FastOptions))
        {
            Assert.True(vault.IsUnlocked);
            var added = vault.AddEntry(TestEntry("GitHub"));
            entryId = added.Id;
        }

        using var reopened = VaultService.Open(_vaultPath);
        Assert.False(reopened.IsUnlocked);
        Assert.Equal(VaultUnlockStatus.Success, reopened.Unlock(Password));
        Assert.True(reopened.IsUnlocked);

        var entry = Assert.Single(reopened.Entries);
        Assert.Equal(entryId, entry.Id);
        AssertEntryEquals(TestEntry("GitHub") with { Id = entryId }, entry);
    }

    [Fact]
    public void WrongPasswordReturnsWrongPasswordStatus()
    {
        using (VaultService.CreateNew(_vaultPath, Password, FastOptions))
        {
        }

        using var vault = VaultService.Open(_vaultPath);
        Assert.Equal(VaultUnlockStatus.WrongPassword, vault.Unlock(Encoding.UTF8.GetBytes("wrong")));
        Assert.False(vault.IsUnlocked);
        Assert.Empty(vault.Entries);
    }

    [Fact]
    public void ChangeMasterPasswordReWrapsKeyOnly()
    {
        PasswordEntry original;
        using (var vault = VaultService.CreateNew(_vaultPath, Password, FastOptions))
        {
            original = vault.AddEntry(TestEntry("Mail"));
            vault.ChangeMasterPassword(NewPassword);
        }

        using var reopened = VaultService.Open(_vaultPath);
        Assert.Equal(VaultUnlockStatus.WrongPassword, reopened.Unlock(Password));
        Assert.Equal(VaultUnlockStatus.Success, reopened.Unlock(NewPassword));

        var entry = Assert.Single(reopened.Entries);
        AssertEntryEquals(original, entry);
    }

    [Fact]
    public void UpdateAndDeleteEntry()
    {
        using var vault = VaultService.CreateNew(_vaultPath, Password, FastOptions);
        var entry = vault.AddEntry(TestEntry("GitHub"));

        var updated = entry with { Title = "GitHub (work)", Tags = ["work"] };
        Assert.True(vault.UpdateEntry(updated));
        Assert.Equal("GitHub (work)", Assert.Single(vault.Entries).Title);

        Assert.True(vault.DeleteEntry(entry.Id));
        Assert.Empty(vault.Entries);

        Assert.False(vault.UpdateEntry(updated));
        Assert.False(vault.DeleteEntry(entry.Id));
    }

    [Fact]
    public void LockClearsSession()
    {
        using var vault = VaultService.CreateNew(_vaultPath, Password, FastOptions);
        vault.AddEntry(TestEntry("GitHub"));

        vault.Lock();

        Assert.False(vault.IsUnlocked);
        Assert.Empty(vault.Entries);
        Assert.Throws<InvalidOperationException>(() => vault.AddEntry(TestEntry("Blocked")));
    }

    [Fact]
    public void TamperedEntryReportsCorrupted()
    {
        using (var vault = VaultService.CreateNew(_vaultPath, Password, FastOptions))
        {
            vault.AddEntry(TestEntry("GitHub"));
        }

        using (var connection = new SqliteConnection($"Data Source={_vaultPath}"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE entries SET ciphertext = zeroblob(length(ciphertext)) WHERE rowid = 1;";
            command.ExecuteNonQuery();
        }

        using var reopened = VaultService.Open(_vaultPath);
        Assert.Equal(VaultUnlockStatus.Corrupted, reopened.Unlock(Password));
    }

    [Fact]
    public void TruncatedNonceReportsCorrupted()
    {
        using (var vault = VaultService.CreateNew(_vaultPath, Password, FastOptions))
        {
            vault.AddEntry(TestEntry("GitHub"));
        }

        using (var connection = new SqliteConnection($"Data Source={_vaultPath}"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            // A wrong-length nonce makes AesGcmCipher.Decrypt throw
            // ArgumentException; it must be mapped to Corrupted like any other
            // damaged payload instead of escaping as an unexpected error.
            command.CommandText = "UPDATE entries SET nonce = zeroblob(4) WHERE rowid = 1;";
            command.ExecuteNonQuery();
        }

        using var reopened = VaultService.Open(_vaultPath);
        Assert.Equal(VaultUnlockStatus.Corrupted, reopened.Unlock(Password));
    }

    [Fact]
    public void VaultFileDoesNotContainPlaintextEntryContent()
    {
        using (var vault = VaultService.CreateNew(_vaultPath, Password, FastOptions))
        {
            vault.AddEntry(TestEntry("UniqueTitle-9f7d3e"));
        }

        var bytes = File.ReadAllBytes(_vaultPath);
        Assert.DoesNotContain("UniqueTitle-9f7d3e", Encoding.UTF8.GetString(bytes));
    }

    [Fact]
    public void BackupCreatesUsableCopy()
    {
        using (var vault = VaultService.CreateNew(_vaultPath, Password, FastOptions))
        {
            vault.AddEntry(TestEntry("GitHub"));
            vault.SaveBackup();
        }

        var backupPath = _vaultPath + ".bak";
        Assert.True(File.Exists(backupPath));

        using var backup = VaultService.Open(backupPath);
        Assert.Equal(VaultUnlockStatus.Success, backup.Unlock(Password));
        Assert.Single(backup.Entries);
    }

    [Fact]
    public void CreateOnExistingPathThrows()
    {
        using (VaultService.CreateNew(_vaultPath, Password, FastOptions))
        {
        }

        Assert.Throws<InvalidOperationException>(() => VaultService.CreateNew(_vaultPath, Password, FastOptions));
    }

    [Fact]
    public void Pbkdf2VaultCanBeCreatedAndUnlocked()
    {
        var options = new VaultOptions
        {
            Kdf = new KdfParameters
            {
                Algorithm = KdfParameters.AlgorithmPbkdf2Sha256,
                Iterations = 1_000,
            },
        };

        using (var vault = VaultService.CreateNew(_vaultPath, Password, options))
        {
            vault.AddEntry(TestEntry("Fallback"));
        }

        using var reopened = VaultService.Open(_vaultPath);
        Assert.Equal(VaultUnlockStatus.Success, reopened.Unlock(Password));
        Assert.Single(reopened.Entries);
    }

    [Fact]
    public void SettingsRoundTrip()
    {
        using var vault = VaultService.CreateNew(_vaultPath, Password, FastOptions);

        var aad = Encoding.UTF8.GetBytes("settings:v1");
        var plaintext = Encoding.UTF8.GetBytes("{\"autoLockMinutes\":5}");

        var (nonce, ciphertext, tag) = vault.EncryptSetting(plaintext, aad);
        vault.WriteSetting("user-config", nonce, ciphertext, tag);

        var stored = vault.ReadSetting("user-config");
        Assert.NotNull(stored);

        var decrypted = vault.DecryptSetting(stored!.Value.Nonce, stored.Value.Ciphertext, stored.Value.Tag, aad);
        Assert.Equal(plaintext, decrypted);
    }

    [Fact]
    public void NewerFormatVersionReturnsUnsupportedVersion()
    {
        using (VaultService.CreateNew(_vaultPath, Password, FastOptions))
        {
        }

        using (var connection = new SqliteConnection($"Data Source={_vaultPath}"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE vault_meta SET format_version = 99 WHERE id = 1;";
            command.ExecuteNonQuery();
        }

        using var vault = VaultService.Open(_vaultPath);
        Assert.Equal(VaultUnlockStatus.UnsupportedVersion, vault.Unlock(Password));
    }

    [Fact]
    public void NewerEntryVersionReportsUnsupportedVersion()
    {
        using (var vault = VaultService.CreateNew(_vaultPath, Password, FastOptions))
        {
            vault.AddEntry(TestEntry("GitHub"));
        }

        using (var connection = new SqliteConnection($"Data Source={_vaultPath}"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE entries SET version = 99;";
            command.ExecuteNonQuery();
        }

        using var reopened = VaultService.Open(_vaultPath);
        Assert.Equal(VaultUnlockStatus.UnsupportedVersion, reopened.Unlock(Password));
    }

    [Fact]
    public void NewerSchemaVersionIsRejectedWhenOpening()
    {
        using (var vault = VaultService.CreateNew(_vaultPath, Password, FastOptions))
        {
            vault.AddEntry(TestEntry("GitHub"));
        }

        using (var connection = new SqliteConnection($"Data Source={_vaultPath}"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA user_version = 99;";
            command.ExecuteNonQuery();
        }

        // A newer schema must not be mistaken for damage: the same
        // "unsupported version" message as a newer header applies.
        Assert.Throws<UnsupportedVaultVersionException>(() => VaultService.Open(_vaultPath));
    }

    [Fact]
    public void OpenNonVaultFileThrowsWithoutModifyingIt()
    {
        var bogusPath = Path.Combine(_directory, "notes.txt");
        var content = Encoding.UTF8.GetBytes("this is not a vault");
        File.WriteAllBytes(bogusPath, content);

        Assert.Throws<InvalidDataException>(() => VaultService.Open(bogusPath));
        Assert.Equal(content, File.ReadAllBytes(bogusPath));
    }

    [Fact]
    public void OpenMissingFileThrowsWithoutCreatingIt()
    {
        var missingPath = Path.Combine(_directory, "missing.aegis");

        Assert.Throws<InvalidDataException>(() => VaultService.Open(missingPath));
        Assert.False(File.Exists(missingPath));
    }

    [Fact]
    public void FavoriteFlagRoundTrips()
    {
        Guid favoriteId;
        using (var vault = VaultService.CreateNew(_vaultPath, Password, FastOptions))
        {
            favoriteId = vault.AddEntry(TestEntry("GitHub") with { IsFavorite = true }).Id;
            vault.AddEntry(TestEntry("Mail"));
        }

        using var reopened = VaultService.Open(_vaultPath);
        Assert.Equal(VaultUnlockStatus.Success, reopened.Unlock(Password));

        Assert.True(reopened.Entries.Single(entry => entry.Id == favoriteId).IsFavorite);
        Assert.False(reopened.Entries.Single(entry => entry.Title == "Mail").IsFavorite);
    }

    [Fact]
    public void UnlockUpgradesLegacyEntryPayloadsToCurrentVersion()
    {
        // Build a vault file the way an older application version would have
        // written it: header at the current format, entry payload at v1 without
        // the Urls member.
        var kdf = FastOptions.Kdf!;
        var salt = RandomNumberGenerator.GetBytes(kdf.SaltSize);
        using var kek = KeyDerivationFactory.Create(kdf.Algorithm).DeriveKey(Password, salt, kdf);
        var dek = RandomNumberGenerator.GetBytes(KeyEnvelope.DekSize);
        var legacyId = Guid.NewGuid();

        try
        {
            var legacyJson = JsonSerializer.SerializeToUtf8Bytes(new
            {
                Id = legacyId,
                Title = "Legacy",
                Username = "user",
                Password = "s3cret",
                Url = "https://legacy.example",
                Notes = string.Empty,
                TotpSecret = string.Empty,
                Tags = new List<string>(),
                CustomFields = new Dictionary<string, string>(),
                IsFavorite = false,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            });

            using (var database = VaultDatabase.OpenOrCreate(_vaultPath))
            {
                var now = DateTimeOffset.UtcNow;
                database.WriteMeta(new VaultHeader
                {
                    FormatVersion = VaultHeader.CurrentFormatVersion,
                    Kdf = kdf,
                    Salt = salt,
                    WrappedDek = KeyEnvelope.Wrap(
                        kek.ReadOnlySpan,
                        dek,
                        VaultHeader.ComputeAssociatedData(VaultHeader.CurrentFormatVersion, kdf, salt)),
                    CreatedAt = now,
                    UpdatedAt = now,
                });

                var (nonce, ciphertext, tag) = AesGcmCipher.Encrypt(
                    dek,
                    legacyJson,
                    EntryRepository.BuildAssociatedData(legacyId, 1));
                database.UpsertEntry(legacyId, 1, nonce, ciphertext, tag, now);
            }

            using var vault = VaultService.Open(_vaultPath);
            Assert.Equal(VaultUnlockStatus.Success, vault.Unlock(Password));

            var entry = Assert.Single(vault.Entries);
            Assert.Equal("Legacy", entry.Title);
            Assert.Empty(entry.Urls);
            Assert.NotNull(entry.Tags);
            Assert.Null(entry.DeletedAt);
            Assert.Null(entry.LastOpenedAt);

            using var verify = VaultDatabase.OpenOrCreate(_vaultPath);
            Assert.All(
                verify.ReadEntries(),
                record => Assert.Equal(EntryRepository.EntryFormatVersion, record.Version));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(dek);
        }
    }

    [Fact]
    public void MultipleUrlsRoundTrip()
    {
        Guid entryId;
        using (var vault = VaultService.CreateNew(_vaultPath, Password, FastOptions))
        {
            var added = vault.AddEntry(TestEntry("GitHub") with
            {
                Url = "https://github.com",
                Urls = ["https://github.com", "https://gist.github.com"],
            });
            entryId = added.Id;
        }

        using var reopened = VaultService.Open(_vaultPath);
        Assert.Equal(VaultUnlockStatus.Success, reopened.Unlock(Password));

        var entry = Assert.Single(reopened.Entries);
        Assert.Equal(entryId, entry.Id);
        Assert.Equal(["https://github.com", "https://gist.github.com"], entry.Urls);
        Assert.Equal("https://github.com", entry.Url);
    }

    [Fact]
    public void NullCollectionFieldsAreRepairedOnLoad()
    {
        using (var vault = VaultService.CreateNew(_vaultPath, Password, FastOptions))
        {
            vault.AddEntry(TestEntry("Legacy") with
            {
                Tags = null!,
                Urls = null!,
                CustomFields = null!,
            });
        }

        using var reopened = VaultService.Open(_vaultPath);
        Assert.Equal(VaultUnlockStatus.Success, reopened.Unlock(Password));

        var entry = Assert.Single(reopened.Entries);
        Assert.Empty(entry.Tags);
        Assert.Empty(entry.Urls);
        Assert.Empty(entry.CustomFields);
    }

    [Fact]
    public void DeleteEntryMovesToRecycleBinAndRestoreBringsItBack()
    {
        using var vault = VaultService.CreateNew(_vaultPath, Password, FastOptions);
        var entry = vault.AddEntry(TestEntry("GitHub"));

        Assert.True(vault.DeleteEntry(entry.Id));

        Assert.Empty(vault.Entries);
        var recycled = Assert.Single(vault.DeletedEntries);
        Assert.Equal(entry.Id, recycled.Id);
        Assert.NotNull(recycled.DeletedAt);

        Assert.True(vault.RestoreEntry(entry.Id));
        Assert.Empty(vault.DeletedEntries);
        Assert.Equal(entry.Id, Assert.Single(vault.Entries).Id);
    }

    [Fact]
    public void RecycledEntriesSurviveReopen()
    {
        Guid id;
        using (var vault = VaultService.CreateNew(_vaultPath, Password, FastOptions))
        {
            id = vault.AddEntry(TestEntry("GitHub")).Id;
            vault.DeleteEntry(id);
        }

        using var reopened = VaultService.Open(_vaultPath);
        Assert.Equal(VaultUnlockStatus.Success, reopened.Unlock(Password));

        Assert.Empty(reopened.Entries);
        var recycled = Assert.Single(reopened.DeletedEntries);
        Assert.Equal(id, recycled.Id);
        Assert.NotNull(recycled.DeletedAt);
    }

    [Fact]
    public void PurgeEntryPermanentlyRemovesIt()
    {
        using var vault = VaultService.CreateNew(_vaultPath, Password, FastOptions);
        var entry = vault.AddEntry(TestEntry("GitHub"));
        vault.DeleteEntry(entry.Id);

        Assert.True(vault.PurgeEntry(entry.Id));
        Assert.Empty(vault.DeletedEntries);
        Assert.False(vault.PurgeEntry(entry.Id));

        // Gone for good: unlocking again must not resurrect it.
        vault.Lock();
        Assert.Equal(VaultUnlockStatus.Success, vault.Unlock(Password));
        Assert.Empty(vault.Entries);
        Assert.Empty(vault.DeletedEntries);
    }

    [Fact]
    public void EmptyRecycleBinRemovesAllRecycledEntries()
    {
        using var vault = VaultService.CreateNew(_vaultPath, Password, FastOptions);
        var first = vault.AddEntry(TestEntry("One"));
        var second = vault.AddEntry(TestEntry("Two"));
        vault.AddEntry(TestEntry("Keep"));
        vault.DeleteEntry(first.Id);
        vault.DeleteEntry(second.Id);

        Assert.Equal(2, vault.EmptyRecycleBin());

        Assert.Empty(vault.DeletedEntries);
        Assert.Equal("Keep", Assert.Single(vault.Entries).Title);
        Assert.Equal(0, vault.EmptyRecycleBin());
    }

    [Fact]
    public void PurgeExpiredDeletedUsesTheRetentionWindow()
    {
        var start = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var clock = new MutableTimeProvider(start);

        Guid id;
        using (var vault = VaultService.CreateNew(_vaultPath, Password, FastOptions, clock))
        {
            id = vault.AddEntry(TestEntry("GitHub")).Id;
            vault.DeleteEntry(id);
        }

        // 29 days later: still inside the 30-day window.
        clock.Now = start.AddDays(29);
        using (var vault = VaultService.Open(_vaultPath, clock))
        {
            Assert.Equal(VaultUnlockStatus.Success, vault.Unlock(Password));
            Assert.Single(vault.DeletedEntries);
        }

        // 31 days later: purged on unlock.
        clock.Now = start.AddDays(31);
        using (var vault = VaultService.Open(_vaultPath, clock))
        {
            Assert.Equal(VaultUnlockStatus.Success, vault.Unlock(Password));
            Assert.Empty(vault.DeletedEntries);
            Assert.Empty(vault.Entries);
        }
    }

    [Fact]
    public void MarkEntryOpenedUpdatesLastOpenedAtWithoutTouchingUpdatedAt()
    {
        using var vault = VaultService.CreateNew(_vaultPath, Password, FastOptions);
        var entry = vault.AddEntry(TestEntry("GitHub"));

        Assert.Null(entry.LastOpenedAt);

        Assert.True(vault.MarkEntryOpened(entry.Id));

        var opened = Assert.Single(vault.Entries);
        Assert.NotNull(opened.LastOpenedAt);
        Assert.Equal(entry.UpdatedAt, opened.UpdatedAt);
    }

    [Fact]
    public void DeletingCategoryClearsRecycledEntryAssignment()
    {
        using var vault = VaultService.CreateNew(_vaultPath, Password, FastOptions);
        var category = vault.AddCategory("Work");
        var entry = vault.AddEntry(TestEntry("GitHub") with { CategoryId = category.Id });
        vault.DeleteEntry(entry.Id);

        vault.DeleteCategory(category.Id, CategoryDeleteMode.Cascade);

        Assert.Null(Assert.Single(vault.DeletedEntries).CategoryId);
    }

    private sealed class MutableTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static PasswordEntry TestEntry(string title) => new()
    {
        Title = title,
        Username = "user@example.com",
        Password = "s3cret!",
        Url = "https://example.com",
        Notes = "some note",
        TotpSecret = "JBSWY3DPEHPK3PXP",
        Tags = ["work", "mail"],
        CustomFields = new Dictionary<string, string> { ["pin"] = "1234" },
    };

    private static void AssertEntryEquals(PasswordEntry expected, PasswordEntry actual)
    {
        Assert.Equal(expected.Id, actual.Id);
        Assert.Equal(expected.Title, actual.Title);
        Assert.Equal(expected.Username, actual.Username);
        Assert.Equal(expected.Password, actual.Password);
        Assert.Equal(expected.Url, actual.Url);
        Assert.Equal(expected.Urls, actual.Urls);
        Assert.Equal(expected.Notes, actual.Notes);
        Assert.Equal(expected.TotpSecret, actual.TotpSecret);
        Assert.Equal(expected.Tags, actual.Tags);
        Assert.Equal(expected.CustomFields, actual.CustomFields);
    }

    [Fact]
    public void ChangingThePasswordAppendsThePreviousValueToHistory()
    {
        using var vault = VaultService.CreateNew(_vaultPath, Password, FastOptions);
        var added = vault.AddEntry(TestEntry("GitHub"));

        Assert.True(vault.UpdateEntry(added with { Password = "rotated-1" }));

        var reloaded = Assert.Single(vault.Entries);
        var history = Assert.Single(reloaded.PasswordHistory!);
        Assert.Equal("s3cret!", history.Password);

        Assert.True(vault.UpdateEntry(reloaded with { Password = "rotated-2" }));

        var afterSecond = Assert.Single(vault.Entries);
        Assert.Equal(2, afterSecond.PasswordHistory!.Count);
        Assert.Equal("rotated-1", afterSecond.PasswordHistory![0].Password);
        Assert.Equal("s3cret!", afterSecond.PasswordHistory![1].Password);
    }

    [Fact]
    public void EditingNonPasswordFieldsDoesNotTouchTheHistory()
    {
        using var vault = VaultService.CreateNew(_vaultPath, Password, FastOptions);
        var added = vault.AddEntry(TestEntry("GitHub"));
        var before = Assert.Single(vault.Entries);
        Assert.Null(before.PasswordHistory);

        Assert.True(vault.UpdateEntry(added with { Notes = "changed note", Username = "other@example.com" }));

        var reloaded = Assert.Single(vault.Entries);
        Assert.Equal("s3cret!", reloaded.Password);
        Assert.True(reloaded.PasswordHistory is null || reloaded.PasswordHistory.Count == 0);
        Assert.Equal("changed note", reloaded.Notes);
    }

    [Fact]
    public void PasswordHistoryIsCappedAndSurvivesAReopen()
    {
        using (var vault = VaultService.CreateNew(_vaultPath, Password, FastOptions))
        {
            var entry = vault.AddEntry(TestEntry("GitHub"));
            for (var index = 0; index < PasswordEntry.MaximumPasswordHistory + 5; index++)
            {
                Assert.True(vault.UpdateEntry(entry with { Password = "rotated-" + index }));
                entry = Assert.Single(vault.Entries);
            }

            Assert.Equal(PasswordEntry.MaximumPasswordHistory, entry.PasswordHistory!.Count);
        }

        using var reopened = VaultService.Open(_vaultPath);
        Assert.Equal(VaultUnlockStatus.Success, reopened.Unlock(Password));
        var restored = Assert.Single(reopened.Entries);
        Assert.Equal(PasswordEntry.MaximumPasswordHistory, restored.PasswordHistory!.Count);
        Assert.Equal("rotated-13", restored.PasswordHistory![0].Password);
    }

    [Fact]
    public void DeletingAnEntryRemovesItsHistoryWithIt()
    {
        using var vault = VaultService.CreateNew(_vaultPath, Password, FastOptions);
        var added = vault.AddEntry(TestEntry("GitHub"));
        vault.UpdateEntry(added with { Password = "rotated" });
        var entryId = added.Id;

        Assert.True(vault.DeleteEntry(entryId));
        Assert.True(vault.PurgeEntry(entryId));

        Assert.Empty(vault.Entries);
        Assert.Empty(vault.DeletedEntries);
    }
}
