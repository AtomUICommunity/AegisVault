using System.Security.Cryptography;
using System.Text.Json;
using AegisVault.Core.Crypto;
using AegisVault.Core.Models;
using AegisVault.Core.Storage;
using Microsoft.Data.Sqlite;

namespace AegisVault.Core.Services;

public enum VaultUnlockStatus
{
    Success,
    WrongPassword,
    Corrupted,
    UnsupportedVersion,
}

/// <summary>
/// High-level vault API: create, unlock, lock, change master password and
/// entry CRUD. Key material (DEK) lives only in libsodium secure memory while
/// the vault is unlocked.
/// </summary>
public sealed class VaultService : IDisposable
{
    private const int DeviceKeyPayloadVersion = 1;
    private const int DeviceKeyPayloadSize = 1 + 32 + KeyEnvelope.DekSize;
    private const string CategoriesSettingKey = "categories";

    /// <summary>
    /// AAD for a categories payload version. The version participates in the AAD
    /// so a payload written by a newer release fails to decrypt instead of being
    /// misread (see <c>docs/数据迁移设计.md</c>).
    /// </summary>
    private static byte[] CategoriesAssociatedData(int version)
        => System.Text.Encoding.UTF8.GetBytes($"AegisVault|settings|categories|v{version}");

    /// <summary>
    /// How long a soft-deleted entry stays in the recycle bin before it is
    /// purged on unlock.
    /// </summary>
    public static readonly TimeSpan RecycleBinRetention = TimeSpan.FromDays(30);

    private readonly VaultDatabase _database;
    private readonly List<PasswordEntry> _entries = [];
    private readonly List<PasswordEntry> _deleted = [];
    private readonly List<Category> _categories = [];
    private readonly TimeProvider _time;

    private VaultHeader _header;
    private SecureBuffer? _dek;
    private bool _disposed;

    private VaultService(VaultDatabase database, VaultHeader header, TimeProvider time)
    {
        _database = database;
        _header = header;
        _time = time;
    }

    public bool IsUnlocked => _dek is not null;

    public string VaultPath => _database.Path;

    /// <summary>Live entries; soft-deleted ones are exposed via <see cref="DeletedEntries"/>.</summary>
    public IReadOnlyList<PasswordEntry> Entries => _entries;

    /// <summary>Entries currently in the recycle bin (soft-deleted, newest first).</summary>
    public IReadOnlyList<PasswordEntry> DeletedEntries => _deleted;

    public IReadOnlyList<Category> Categories => _categories;

    /// <summary>
    /// True when the vault carries a categories blob this application cannot read
    /// (damaged, or written by a newer release). Unlocking still succeeds so the
    /// entries stay usable, but the UI should tell the user instead of silently
    /// showing no categories.
    /// </summary>
    public bool CategoriesUnreadable { get; private set; }

    /// <summary>Creates a new vault and returns it in the unlocked state.</summary>
    public static VaultService CreateNew(
        string path,
        ReadOnlySpan<byte> password,
        VaultOptions? options = null,
        TimeProvider? timeProvider = null)
    {
        var time = timeProvider ?? TimeProvider.System;
        var database = VaultDatabase.OpenOrCreate(path);
        try
        {
            if (database.HasMeta())
            {
                throw new InvalidOperationException("A vault already exists at the specified path.");
            }

            var kdf = options?.Kdf ?? new KdfParameters();
            var derivation = KeyDerivationFactory.Create(kdf.Algorithm);
            var salt = RandomNumberGenerator.GetBytes(kdf.SaltSize);

            using var kek = derivation.DeriveKey(password, salt, kdf);

            var dekBytes = RandomNumberGenerator.GetBytes(KeyEnvelope.DekSize);
            try
            {
                var now = time.GetUtcNow();
                var associatedData = VaultHeader.ComputeAssociatedData(VaultHeader.CurrentFormatVersion, kdf, salt);

                var header = new VaultHeader
                {
                    Kdf = kdf,
                    Salt = salt,
                    WrappedDek = KeyEnvelope.Wrap(kek.ReadOnlySpan, dekBytes, associatedData),
                    CreatedAt = now,
                    UpdatedAt = now,
                };

                database.WriteMeta(header);

                var dek = SecureBuffer.From(dekBytes);
                dek.ProtectReadOnly();

                return new VaultService(database, header, time) { _dek = dek };
            }
            finally
            {
                CryptographicOperations.ZeroMemory(dekBytes);
            }
        }
        catch
        {
            database.Dispose();
            throw;
        }
    }

    /// <summary>Opens an existing vault file without unlocking it.</summary>
    public static VaultService Open(string path, TimeProvider? timeProvider = null)
    {
        if (!File.Exists(path))
        {
            throw new InvalidDataException("The file is not an AegisVault vault (file not found).");
        }

        var database = VaultDatabase.OpenOrCreate(path);
        try
        {
            var header = database.ReadMeta()
                ?? throw new InvalidDataException("The file is not an AegisVault vault (metadata missing).");

            return new VaultService(database, header, timeProvider ?? TimeProvider.System);
        }
        catch
        {
            database.Dispose();
            throw;
        }
    }

    public VaultUnlockStatus Unlock(ReadOnlySpan<byte> password)
    {
        ThrowIfDisposed();

        if (_header.FormatVersion > VaultHeader.CurrentFormatVersion)
        {
            return VaultUnlockStatus.UnsupportedVersion;
        }

        IKeyDerivation derivation;
        try
        {
            derivation = KeyDerivationFactory.Create(_header.Kdf.Algorithm);
        }
        catch (NotSupportedException)
        {
            return VaultUnlockStatus.Corrupted;
        }

        SecureBuffer dek;
        try
        {
            using var kek = derivation.DeriveKey(password, _header.Salt, _header.Kdf);
            dek = KeyEnvelope.Unwrap(kek.ReadOnlySpan, _header.WrappedDek, _header.ComputeAssociatedData());
        }
        catch (CryptographicException)
        {
            return VaultUnlockStatus.WrongPassword;
        }

        return EstablishSession(dek);
    }

    /// <summary>Clears all decrypted state and zeroes the session key.</summary>
    public void Lock()
    {
        _entries.Clear();
        _deleted.Clear();
        _categories.Clear();
        _dek?.Dispose();
        _dek = null;
    }

    /// <summary>Re-wraps the DEK under a new password (no entry re-encryption).</summary>
    public void ChangeMasterPassword(ReadOnlySpan<byte> newPassword, VaultOptions? options = null)
    {
        ThrowIfDisposed();
        EnsureUnlocked();

        var kdf = options?.Kdf ?? _header.Kdf;
        var derivation = KeyDerivationFactory.Create(kdf.Algorithm);
        var salt = RandomNumberGenerator.GetBytes(kdf.SaltSize);

        using var kek = derivation.DeriveKey(newPassword, salt, kdf);
        var associatedData = VaultHeader.ComputeAssociatedData(_header.FormatVersion, kdf, salt);

        var updated = new VaultHeader
        {
            FormatVersion = _header.FormatVersion,
            Kdf = kdf,
            Salt = salt,
            WrappedDek = KeyEnvelope.Wrap(kek.ReadOnlySpan, _dek!.ReadOnlySpan, associatedData),
            CreatedAt = _header.CreatedAt,
            UpdatedAt = _time.GetUtcNow(),
        };

        _database.WriteMeta(updated);
        _header = updated;

        // Ciphertext of the wrapped DEK changed; remembered device keys no longer match.
        _database.DeleteAllDeviceKeys();
    }

    public PasswordEntry AddEntry(PasswordEntry entry)
    {
        ThrowIfDisposed();
        EnsureUnlocked();

        var now = _time.GetUtcNow();
        var item = entry with
        {
            Id = entry.Id == Guid.Empty ? Guid.NewGuid() : entry.Id,
            CreatedAt = entry.CreatedAt == default ? now : entry.CreatedAt,
            UpdatedAt = now,
        };

        Persist(item);
        _entries.Add(item);
        return item;
    }

    /// <summary>
    /// Adds many entries in a single transaction (bulk import). Runs on the
    /// calling thread so the vault's connection is never used concurrently;
    /// callers on a UI thread should parse off-thread and call this on the UI
    /// thread.
    /// </summary>
    public IReadOnlyList<PasswordEntry> AddEntries(IEnumerable<PasswordEntry> entries)
    {
        ThrowIfDisposed();
        EnsureUnlocked();
        ArgumentNullException.ThrowIfNull(entries);

        var now = _time.GetUtcNow();
        var items = new List<PasswordEntry>();
        foreach (var entry in entries)
        {
            items.Add(entry with
            {
                Id = entry.Id == Guid.Empty ? Guid.NewGuid() : entry.Id,
                CreatedAt = entry.CreatedAt == default ? now : entry.CreatedAt,
                UpdatedAt = now,
            });
        }

        if (items.Count == 0)
        {
            return items;
        }

        var records = new List<EntryRecord>(items.Count);
        foreach (var item in items)
        {
            var (nonce, ciphertext, tag) = EntryRepository.Encrypt(_dek!.ReadOnlySpan, item);
            records.Add(new EntryRecord(
                item.Id,
                EntryRepository.EntryFormatVersion,
                nonce,
                ciphertext,
                tag,
                item.UpdatedAt));
        }

        _database.WriteEntries(records);
        _entries.AddRange(items);
        return items;
    }

    public bool UpdateEntry(PasswordEntry entry)
    {
        ThrowIfDisposed();
        EnsureUnlocked();

        var index = _entries.FindIndex(existing => existing.Id == entry.Id);
        if (index < 0)
        {
            return false;
        }

        var item = entry with { UpdatedAt = _time.GetUtcNow() };
        Persist(item);
        _entries[index] = item;
        return true;
    }

    /// <summary>
    /// Moves an entry to the recycle bin (soft delete). The payload keeps a
    /// <c>DeletedAt</c> timestamp and the entry leaves <see cref="Entries"/>;
    /// <see cref="UpdatedAt"/> is left untouched because deleting is not editing.
    /// </summary>
    public bool DeleteEntry(Guid id)
    {
        ThrowIfDisposed();
        EnsureUnlocked();

        var index = _entries.FindIndex(existing => existing.Id == id);
        if (index < 0)
        {
            return false;
        }

        var item = _entries[index] with { DeletedAt = _time.GetUtcNow() };
        Persist(item);
        _entries.RemoveAt(index);
        _deleted.Insert(0, item);
        return true;
    }

    /// <summary>Restores a soft-deleted entry back to the live list.</summary>
    public bool RestoreEntry(Guid id)
    {
        ThrowIfDisposed();
        EnsureUnlocked();

        var index = _deleted.FindIndex(existing => existing.Id == id);
        if (index < 0)
        {
            return false;
        }

        var item = _deleted[index] with { DeletedAt = null };
        Persist(item);
        _deleted.RemoveAt(index);
        _entries.Add(item);
        return true;
    }

    /// <summary>Permanently deletes a single recycled entry.</summary>
    public bool PurgeEntry(Guid id)
    {
        ThrowIfDisposed();
        EnsureUnlocked();

        var index = _deleted.FindIndex(existing => existing.Id == id);
        if (index < 0)
        {
            return false;
        }

        _database.DeleteEntry(id);
        _deleted.RemoveAt(index);
        return true;
    }

    /// <summary>Permanently deletes every recycled entry; returns how many were removed.</summary>
    public int EmptyRecycleBin()
    {
        ThrowIfDisposed();
        EnsureUnlocked();

        if (_deleted.Count == 0)
        {
            return 0;
        }

        var count = _deleted.Count;
        _database.DeleteEntries(_deleted.Select(entry => entry.Id).ToList());
        _deleted.Clear();
        return count;
    }

    /// <summary>
    /// Permanently deletes recycled entries older than <see cref="RecycleBinRetention"/>.
    /// Called once on unlock so the bin cannot grow without bound.
    /// </summary>
    public int PurgeExpiredDeleted()
    {
        ThrowIfDisposed();
        EnsureUnlocked();

        var cutoff = _time.GetUtcNow() - RecycleBinRetention;
        var expired = _deleted
            .Where(entry => entry.DeletedAt is { } deletedAt && deletedAt < cutoff)
            .ToList();
        if (expired.Count == 0)
        {
            return 0;
        }

        _database.DeleteEntries(expired.Select(entry => entry.Id).ToList());
        foreach (var entry in expired)
        {
            _deleted.Remove(entry);
        }

        return expired.Count;
    }

    /// <summary>
    /// Records that an entry was viewed. Deliberately does <b>not</b> change
    /// <see cref="PasswordEntry.UpdatedAt"/> so the "stale" health view and the
    /// "recently updated" sort stay meaningful.
    /// </summary>
    public bool MarkEntryOpened(Guid id)
    {
        ThrowIfDisposed();
        EnsureUnlocked();

        var index = _entries.FindIndex(existing => existing.Id == id);
        if (index < 0)
        {
            return false;
        }

        var item = _entries[index] with { LastOpenedAt = _time.GetUtcNow() };
        Persist(item);
        _entries[index] = item;
        return true;
    }

    /// <summary>
    /// Creates a category. Names are trimmed, non-empty and unique among the
    /// siblings under <paramref name="parentId"/> (case-insensitive).
    /// </summary>
    public Category AddCategory(string name, string? color = null, Guid? parentId = null)
    {
        ThrowIfDisposed();
        EnsureUnlocked();

        var normalized = NormalizeCategoryName(name);
        EnsureCategoryParent(parentId, movingId: null);
        EnsureUniqueCategoryName(normalized, excludeId: null, parentId);

        var category = new Category
        {
            Name = normalized,
            Color = CategoryColors.Normalize(color),
            ParentId = parentId,
        };
        _categories.Add(category);
        SortCategories();
        PersistCategories();
        return category;
    }

    public bool RenameCategory(Guid id, string name)
    {
        ThrowIfDisposed();
        EnsureUnlocked();

        var index = _categories.FindIndex(category => category.Id == id);
        if (index < 0)
        {
            return false;
        }

        var normalized = NormalizeCategoryName(name);
        EnsureUniqueCategoryName(normalized, excludeId: id, _categories[index].ParentId);

        _categories[index] = _categories[index] with { Name = normalized };
        SortCategories();
        PersistCategories();
        return true;
    }

    /// <summary>Sets (or clears) the palette/hex colour of a category.</summary>
    public bool SetCategoryColor(Guid id, string? color)
    {
        ThrowIfDisposed();
        EnsureUnlocked();

        var index = _categories.FindIndex(category => category.Id == id);
        if (index < 0)
        {
            return false;
        }

        _categories[index] = _categories[index] with { Color = CategoryColors.Normalize(color) };
        PersistCategories();
        return true;
    }

    /// <summary>Deletes a category; its entries become uncategorized.</summary>
    public bool DeleteCategory(Guid id)
        => DeleteCategory(id, CategoryDeleteMode.PromoteChildren).Removed;

    /// <summary>
    /// Deletes a category. Entries assigned to it become uncategorized; children
    /// are either lifted to the deleted category's parent, deleted with it, or
    /// block the deletion (<see cref="CategoryDeleteMode"/>). Promoted children
    /// whose name is already taken at the destination are renamed (never lost,
    /// never duplicated) and reported in <see cref="CategoryDeleteResult.Renamed"/>.
    /// </summary>
    public CategoryDeleteResult DeleteCategory(Guid id, CategoryDeleteMode mode)
    {
        ThrowIfDisposed();
        EnsureUnlocked();

        var index = _categories.FindIndex(category => category.Id == id);
        if (index < 0)
        {
            return new CategoryDeleteResult(false, []);
        }

        var parentId = _categories[index].ParentId;
        var children = _categories.Where(category => category.ParentId == id).ToList();
        if (children.Count > 0 && mode == CategoryDeleteMode.Deny)
        {
            throw new InvalidOperationException("The category still has child categories.");
        }

        var renames = new List<CategoryRename>();
        if (mode == CategoryDeleteMode.Cascade)
        {
            foreach (var child in children)
            {
                DeleteCategory(child.Id, CategoryDeleteMode.Cascade);
            }
        }
        else
        {
            // Names already taken at the destination, including the ones claimed
            // by children promoted earlier in this loop.
            var taken = new HashSet<string>(
                _categories
                    .Where(category => category.ParentId == parentId && category.Id != id)
                    .Select(category => category.Name),
                StringComparer.OrdinalIgnoreCase);

            for (var i = 0; i < _categories.Count; i++)
            {
                if (_categories[i].ParentId != id)
                {
                    continue;
                }

                var child = _categories[i];
                var name = child.Name;
                if (!taken.Add(name))
                {
                    name = MakeUniqueCategoryName(child.Name, taken);
                    taken.Add(name);
                    renames.Add(new CategoryRename(child.Id, child.Name, name));
                }

                _categories[i] = child with { ParentId = parentId, Name = name };
            }
        }

        var current = _categories.FindIndex(category => category.Id == id);
        if (current < 0)
        {
            return new CategoryDeleteResult(false, []);
        }

        _categories.RemoveAt(current);
        SortCategories();
        PersistCategories();

        for (var i = 0; i < _entries.Count; i++)
        {
            if (_entries[i].CategoryId != id)
            {
                continue;
            }

            var item = _entries[i] with { CategoryId = null, UpdatedAt = _time.GetUtcNow() };
            Persist(item);
            _entries[i] = item;
        }

        // Recycled entries must not keep a dangling category either, otherwise a
        // later restore would point at a category that no longer exists.
        for (var i = 0; i < _deleted.Count; i++)
        {
            if (_deleted[i].CategoryId != id)
            {
                continue;
            }

            var item = _deleted[i] with { CategoryId = null };
            Persist(item);
            _deleted[i] = item;
        }

        return new CategoryDeleteResult(true, renames);
    }

    /// <summary>Encrypts a settings payload with the session key.</summary>
    public (byte[] Nonce, byte[] Ciphertext, byte[] Tag) EncryptSetting(ReadOnlySpan<byte> plaintext, ReadOnlySpan<byte> associatedData)
    {
        ThrowIfDisposed();
        EnsureUnlocked();
        return AesGcmCipher.Encrypt(_dek!.ReadOnlySpan, plaintext, associatedData);
    }

    /// <summary>Decrypts a settings payload with the session key.</summary>
    public byte[] DecryptSetting(ReadOnlySpan<byte> nonce, ReadOnlySpan<byte> ciphertext, ReadOnlySpan<byte> tag, ReadOnlySpan<byte> associatedData)
    {
        ThrowIfDisposed();
        EnsureUnlocked();
        return AesGcmCipher.Decrypt(_dek!.ReadOnlySpan, nonce, ciphertext, tag, associatedData);
    }

    public (byte[] Nonce, byte[] Ciphertext, byte[] Tag)? ReadSetting(string key)
    {
        ThrowIfDisposed();
        var record = _database.ReadSetting(key);
        return record is null ? null : (record.Nonce, record.Ciphertext, record.Tag);
    }

    public void WriteSetting(string key, byte[] nonce, byte[] ciphertext, byte[] tag)
    {
        ThrowIfDisposed();
        _database.WriteSetting(key, nonce, ciphertext, tag);
    }

    /// <summary>Writes a consistent copy of the encrypted database (defaults to <c>.bak</c>).</summary>
    public void SaveBackup(string? destinationPath = null)
    {
        ThrowIfDisposed();
        var destination = destinationPath ?? VaultPath + ".bak";
        _database.Checkpoint();
        File.Copy(VaultPath, destination, overwrite: true);
        VaultFilePermissions.Restrict(destination);
    }

    /// <summary>
    /// Writes a consistent <c>VACUUM INTO</c> snapshot of the vault file. No
    /// decryption happens, so this is safe while the vault is locked.
    /// </summary>
    public void SnapshotTo(string destinationPath)
    {
        ThrowIfDisposed();
        _database.SnapshotTo(destinationPath);
    }

    /// <summary>Whether a usable device key is stored for the given protector.</summary>
    public bool HasDeviceKey(IKeyProtector protector)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(protector);
        return protector.IsAvailable && _database.ReadDeviceKey(protector.Id) is not null;
    }

    /// <summary>
    /// Stores the session DEK protected by the OS key protector so the vault can
    /// be unlocked without the master password on this device.
    /// </summary>
    public void RememberDevice(IKeyProtector protector)
    {
        ThrowIfDisposed();
        EnsureUnlocked();
        ArgumentNullException.ThrowIfNull(protector);

        if (!protector.IsAvailable)
        {
            throw new InvalidOperationException("The key protector is not available on this machine.");
        }

        var payload = BuildDeviceKeyPayload();
        try
        {
            var blob = protector.Protect(payload);
            _database.UpsertDeviceKey(protector.Id, blob, DateTimeOffset.UtcNow);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(payload);
        }
    }

    public void ForgetDevice(IKeyProtector protector)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(protector);
        _database.DeleteDeviceKey(protector.Id);
    }

    /// <summary>
    /// Attempts to unlock using a previously remembered device key. Returns
    /// <c>false</c> when no key exists, it belongs to another vault state, or
    /// the entries cannot be decrypted.
    /// </summary>
    public bool TryUnlockWithDeviceKey(IKeyProtector protector)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(protector);

        if (!protector.IsAvailable || _header.FormatVersion > VaultHeader.CurrentFormatVersion)
        {
            return false;
        }

        var blob = _database.ReadDeviceKey(protector.Id);
        if (blob is null)
        {
            return false;
        }

        byte[] payload;
        try
        {
            payload = protector.Unprotect(blob);
        }
        catch (Exception exception) when (exception is CryptographicException or PlatformNotSupportedException)
        {
            return false;
        }

        try
        {
            if (payload.Length != DeviceKeyPayloadSize || payload[0] != DeviceKeyPayloadVersion)
            {
                return false;
            }

            var expected = SHA256.HashData(_header.WrappedDek);
            if (!CryptographicOperations.FixedTimeEquals(payload.AsSpan(1, 32), expected))
            {
                return false;
            }

            var dek = SecureBuffer.From(payload.AsSpan(33, KeyEnvelope.DekSize));
            return EstablishSession(dek) == VaultUnlockStatus.Success;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(payload);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Lock();
        _database.Dispose();
    }

    private List<PasswordEntry> LoadEntries(SecureBuffer dek, List<PasswordEntry> outdated)
    {
        var loaded = new List<PasswordEntry>();
        foreach (var record in _database.ReadEntries())
        {
            if (record.Version > EntryRepository.EntryFormatVersion)
            {
                throw new UnsupportedVaultVersionException(
                    $"Entry format version {record.Version} is newer than this application supports.");
            }

            var entry = EntryRepository.Decrypt(
                dek.ReadOnlySpan,
                record.Id,
                record.Version,
                record.Nonce,
                record.Ciphertext,
                record.Tag);

            loaded.Add(entry);

            if (record.Version < EntryRepository.EntryFormatVersion)
            {
                outdated.Add(entry);
            }
        }

        return loaded;
    }

    /// <summary>
    /// Re-encrypts entries whose payload was written by an older format so the
    /// upgrade is persisted (one-time, best effort: a failed rewrite leaves the
    /// in-memory upgrade intact and is retried on the next unlock).
    /// </summary>
    private void PersistOutdatedEntries(List<PasswordEntry> outdated, SecureBuffer dek)
    {
        if (outdated.Count == 0)
        {
            return;
        }

        try
        {
            var records = new List<EntryRecord>(outdated.Count);
            foreach (var entry in outdated)
            {
                var (nonce, ciphertext, tag) = EntryRepository.Encrypt(dek.ReadOnlySpan, entry);
                records.Add(new EntryRecord(
                    entry.Id,
                    EntryRepository.EntryFormatVersion,
                    nonce,
                    ciphertext,
                    tag,
                    entry.UpdatedAt));
            }

            _database.WriteEntries(records);
        }
        catch (Exception exception) when (exception is SqliteException or IOException)
        {
            // The vault stays usable; the payloads are upgraded in memory and
            // will be written back on the next unlock or entry save.
        }
    }

    private void Persist(PasswordEntry entry)
    {
        var (nonce, ciphertext, tag) = EntryRepository.Encrypt(_dek!.ReadOnlySpan, entry);
        _database.UpsertEntry(entry.Id, EntryRepository.EntryFormatVersion, nonce, ciphertext, tag, entry.UpdatedAt);
    }

    private void PersistCategories()
    {
        var payload = new CategoriesPayload
        {
            Version = CategoriesPayload.CurrentVersion,
            Categories = _categories,
        };
        var json = JsonSerializer.SerializeToUtf8Bytes(payload, VaultJsonContext.Default.CategoriesPayload);
        try
        {
            var (nonce, ciphertext, tag) = EncryptSetting(json, CategoriesAssociatedData(CategoriesPayload.CurrentVersion));
            WriteSetting(CategoriesSettingKey, nonce, ciphertext, tag);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(json);
        }
    }

    /// <summary>
    /// Reads the encrypted categories blob. The current version is a versioned
    /// envelope; older releases wrote a bare list under their own AAD, so every
    /// known version is tried in turn and the result is reported as needing a
    /// rewrite when it came from an older one.
    /// </summary>
    private (List<Category> Categories, bool Upgraded) LoadCategories()
    {
        CategoriesUnreadable = false;
        var stored = ReadSetting(CategoriesSettingKey);
        if (stored is null)
        {
            return ([], false);
        }

        byte[]? json = null;
        var version = CategoriesPayload.CurrentVersion;
        for (var candidate = CategoriesPayload.CurrentVersion;
             candidate >= CategoriesPayload.OldestSupportedVersion;
             candidate--)
        {
            try
            {
                json = DecryptSetting(
                    stored.Value.Nonce,
                    stored.Value.Ciphertext,
                    stored.Value.Tag,
                    CategoriesAssociatedData(candidate));
                version = candidate;
                break;
            }
            catch (CryptographicException)
            {
            }
        }

        if (json is null)
        {
            // Never break unlocking on a categories blob we cannot read, but do
            // report it so the UI can warn instead of pretending the vault has no
            // categories at all.
            CategoriesUnreadable = true;
            return ([], false);
        }

        try
        {
            List<Category>? categories;
            if (version >= CategoriesPayload.CurrentVersion)
            {
                var payload = JsonSerializer.Deserialize(json, VaultJsonContext.Default.CategoriesPayload);
                if (payload is not null && payload.Version > CategoriesPayload.CurrentVersion)
                {
                    throw new UnsupportedVaultVersionException(
                        $"Categories payload version {payload.Version} is newer than this application supports.");
                }

                categories = payload?.Categories;
            }
            else
            {
                categories = JsonSerializer.Deserialize(json, VaultJsonContext.Default.ListCategory);
            }

            return (ModelMigrations.UpgradeCategories(categories, version),
                version != CategoriesPayload.CurrentVersion);
        }
        catch (JsonException)
        {
            CategoriesUnreadable = true;
            return ([], false);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(json);
        }
    }

    private void SortCategories()
        => _categories.Sort(static (a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Ids of a category and everything nested under it. Used by the UI to filter
    /// whole branches and by <see cref="MoveCategory"/> to reject moving a node
    /// into its own subtree.
    /// </summary>
    public IReadOnlySet<Guid> GetCategorySubtree(Guid id)
    {
        ThrowIfDisposed();
        EnsureUnlocked();

        var subtree = new HashSet<Guid> { id };
        var pending = new Queue<Guid>();
        pending.Enqueue(id);
        while (pending.Count > 0)
        {
            var current = pending.Dequeue();
            foreach (var category in _categories)
            {
                if (category.ParentId == current && subtree.Add(category.Id))
                {
                    pending.Enqueue(category.Id);
                }
            }
        }

        return subtree;
    }

    /// <summary>
    /// Re-parents a category (or lifts it to the top level when
    /// <paramref name="parentId"/> is <c>null</c>).
    /// </summary>
    public bool MoveCategory(Guid id, Guid? parentId)
    {
        ThrowIfDisposed();
        EnsureUnlocked();

        var index = _categories.FindIndex(category => category.Id == id);
        if (index < 0)
        {
            return false;
        }

        if (parentId is { } target && GetCategorySubtree(id).Contains(target))
        {
            throw new CategoryValidationException(
                CategoryValidationError.SelfOrSubtree,
                "A category cannot be moved into itself or its own subtree.",
                nameof(parentId));
        }

        EnsureCategoryParent(parentId, movingId: id);

        if (_categories[index].ParentId == parentId)
        {
            return true;
        }

        // Moving into another parent must not create a duplicate sibling name
        // (excludeId keeps "stay where you are" legal).
        EnsureUniqueCategoryName(_categories[index].Name, excludeId: id, parentId);

        _categories[index] = _categories[index] with { ParentId = parentId };
        SortCategories();
        PersistCategories();
        return true;
    }

    /// <summary>
    /// Moves everything from <paramref name="sourceId"/> into
    /// <paramref name="targetId"/> and deletes the source: entries are re-assigned
    /// and child categories are re-parented.
    /// </summary>
    public bool MergeCategories(Guid sourceId, Guid targetId)
    {
        ThrowIfDisposed();
        EnsureUnlocked();

        if (sourceId == targetId)
        {
            throw new CategoryValidationException(
                CategoryValidationError.SelfOrSubtree,
                "A category cannot be merged into itself.",
                nameof(targetId));
        }

        var sourceIndex = _categories.FindIndex(category => category.Id == sourceId);
        if (sourceIndex < 0)
        {
            return false;
        }

        if (_categories.FindIndex(category => category.Id == targetId) < 0)
        {
            return false;
        }

        if (GetCategorySubtree(sourceId).Contains(targetId))
        {
            throw new CategoryValidationException(
                CategoryValidationError.SelfOrSubtree,
                "A category cannot be merged into its own subtree.",
                nameof(targetId));
        }

        // The source's children are re-parented onto the target: they must still
        // fit inside the depth cap and must not collide with the target's own
        // children (or with each other).
        var movingChildren = _categories.Where(category => category.ParentId == sourceId).ToList();
        var takenNames = new HashSet<string>(
            _categories.Where(category => category.ParentId == targetId).Select(category => category.Name),
            StringComparer.OrdinalIgnoreCase);
        foreach (var child in movingChildren)
        {
            EnsureCategoryParent(targetId, movingId: child.Id);
            if (!takenNames.Add(child.Name))
            {
                throw new CategoryValidationException(
                    CategoryValidationError.NameDuplicate,
                    $"A category named '{child.Name}' already exists under the target category.",
                    nameof(targetId));
            }
        }

        _categories.RemoveAt(sourceIndex);

        for (var i = 0; i < _categories.Count; i++)
        {
            if (_categories[i].ParentId == sourceId)
            {
                _categories[i] = _categories[i] with { ParentId = targetId };
            }
        }

        SortCategories();
        PersistCategories();
        ReassignEntriesToCategory(sourceId, targetId);
        return true;

        void ReassignEntriesToCategory(Guid from, Guid to)
        {
            for (var i = 0; i < _entries.Count; i++)
            {
                if (_entries[i].CategoryId != from)
                {
                    continue;
                }

                var item = _entries[i] with { CategoryId = to, UpdatedAt = _time.GetUtcNow() };
                Persist(item);
                _entries[i] = item;
            }
        }
    }

    /// <summary>
    /// Validates a prospective parent: it must exist, and the deepest branch
    /// below <paramref name="movingId"/> must still fit inside the depth cap.
    /// </summary>
    private void EnsureCategoryParent(Guid? parentId, Guid? movingId)
    {
        if (parentId is not { } target)
        {
            return;
        }

        if (movingId == target)
        {
            throw new CategoryValidationException(
                CategoryValidationError.SelfOrSubtree,
                "A category cannot be its own parent.",
                nameof(parentId));
        }

        if (_categories.All(category => category.Id != target))
        {
            throw new CategoryValidationException(
                CategoryValidationError.ParentMissing,
                "The parent category does not exist.",
                nameof(parentId));
        }

        var parentDepth = CategoryDepth(target);
        var subtreeHeight = movingId is { } id ? CategorySubtreeHeight(id) : 1;
        if (parentDepth + subtreeHeight > ModelMigrations.MaxCategoryDepth)
        {
            throw new CategoryValidationException(
                CategoryValidationError.DepthExceeded,
                $"A category tree may not be deeper than {ModelMigrations.MaxCategoryDepth} levels.",
                nameof(parentId));
        }
    }

    private int CategoryDepth(Guid id)
    {
        var depth = 1;
        var current = _categories.FirstOrDefault(category => category.Id == id);
        var guard = 0;
        while (current?.ParentId is { } parentId && guard++ < _categories.Count)
        {
            depth++;
            var parent = _categories.FirstOrDefault(category => category.Id == parentId);
            if (parent is null)
            {
                break;
            }

            current = parent;
        }

        return depth;
    }

    /// <summary>Number of levels the branch below (and including) a node occupies.</summary>
    private int CategorySubtreeHeight(Guid id)
    {
        var height = 1;
        foreach (var category in _categories)
        {
            if (category.ParentId == id)
            {
                height = Math.Max(height, 1 + CategorySubtreeHeight(category.Id));
            }
        }

        return height;
    }

    private static string NormalizeCategoryName(string name)
    {
        var trimmed = name?.Trim() ?? string.Empty;
        if (trimmed.Length == 0)
        {
            throw new CategoryValidationException(
                CategoryValidationError.NameEmpty,
                "Category name must not be empty.",
                nameof(name));
        }

        return trimmed;
    }

    private void EnsureUniqueCategoryName(string name, Guid? excludeId, Guid? parentId)
    {
        if (_categories.Any(category =>
                category.Id != excludeId &&
                category.ParentId == parentId &&
                string.Equals(category.Name, name, StringComparison.OrdinalIgnoreCase)))
        {
            throw new CategoryValidationException(
                CategoryValidationError.NameDuplicate,
                $"A category named '{name}' already exists.",
                nameof(name));
        }
    }

    /// <summary>
    /// Builds a name that is free among <paramref name="taken"/> by appending
    /// " (2)", " (3)"… to the original. Used when children are promoted into a
    /// parent that already uses their name, so no category is ever lost.
    /// </summary>
    private static string MakeUniqueCategoryName(string name, IReadOnlySet<string> taken)
    {
        for (var suffix = 2; ; suffix++)
        {
            var candidate = $"{name} ({suffix})";
            if (!taken.Contains(candidate))
            {
                return candidate;
            }
        }
    }

    private void EnsureUnlocked()
    {
        if (!IsUnlocked)
        {
            throw new InvalidOperationException("The vault is locked.");
        }
    }

    private byte[] BuildDeviceKeyPayload()
    {
        var payload = new byte[DeviceKeyPayloadSize];
        payload[0] = DeviceKeyPayloadVersion;
        SHA256.HashData(_header.WrappedDek).CopyTo(payload.AsSpan(1, 32));
        _dek!.ReadOnlySpan.CopyTo(payload.AsSpan(33));
        return payload;
    }

    private VaultUnlockStatus EstablishSession(SecureBuffer dek)
    {
        List<PasswordEntry> loaded;
        List<PasswordEntry> outdated = [];
        try
        {
            loaded = LoadEntries(dek, outdated);
        }
        catch (UnsupportedVaultVersionException)
        {
            // A newer payload version must be reported as such, not as damage.
            dek.Dispose();
            return VaultUnlockStatus.UnsupportedVersion;
        }
        catch (Exception exception) when (exception is CryptographicException or ArgumentException or JsonException or InvalidDataException)
        {
            dek.Dispose();
            return VaultUnlockStatus.Corrupted;
        }

        dek.ProtectReadOnly();

        _entries.Clear();
        _deleted.Clear();
        foreach (var entry in loaded)
        {
            if (entry.DeletedAt is null)
            {
                _entries.Add(entry);
            }
            else
            {
                _deleted.Add(entry);
            }
        }

        // Newest deleted first (Nullable.Compare keeps nulls consistent if any slip through).
        _deleted.Sort((left, right) => Nullable.Compare(right.DeletedAt, left.DeletedAt));

        _dek?.Dispose();
        _dek = dek;

        // Persist any payload upgrades now that the session key is in place.
        PersistOutdatedEntries(outdated, dek);

        // Drop recycled entries past the retention window. Best effort: a failure
        // must not block unlocking (they are simply purged on the next unlock).
        try
        {
            PurgeExpiredDeleted();
        }
        catch (Exception exception) when (exception is SqliteException or IOException)
        {
        }

        // Categories are decrypted with the session key, so load them only after
        // the DEK is in place. A payload this application cannot support fails the
        // unlock instead of leaving a half-established session behind.
        _categories.Clear();
        List<Category> categories;
        bool upgraded;
        try
        {
            (categories, upgraded) = LoadCategories();
        }
        catch (UnsupportedVaultVersionException)
        {
            Lock();
            return VaultUnlockStatus.UnsupportedVersion;
        }
        catch (Exception exception) when (exception is CryptographicException or ArgumentException or JsonException or InvalidDataException)
        {
            Lock();
            return VaultUnlockStatus.Corrupted;
        }

        _categories.AddRange(categories);

        if (upgraded)
        {
            // One-shot best-effort rewrite at the current AAD version; a failure
            // simply retries on the next unlock.
            try
            {
                PersistCategories();
            }
            catch (Exception exception) when (exception is SqliteException or IOException)
            {
            }
        }

        return VaultUnlockStatus.Success;
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }
}
