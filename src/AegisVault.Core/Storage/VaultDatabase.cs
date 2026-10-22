using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace AegisVault.Core.Storage;

internal sealed record EntryRecord(
    Guid Id,
    int Version,
    byte[] Nonce,
    byte[] Ciphertext,
    byte[] Tag,
    DateTimeOffset UpdatedAt);

internal sealed record SettingRecord(
    byte[] Nonce,
    byte[] Ciphertext,
    byte[] Tag);

/// <summary>
/// Low-level access to the encrypted SQLite vault file. This class never
/// touches key material; encryption/decryption happens in the service layer.
/// </summary>
internal sealed class VaultDatabase : IDisposable
{
    private readonly SqliteConnection _connection;

    private VaultDatabase(string path, SqliteConnection connection)
    {
        Path = path;
        _connection = connection;
    }

    public string Path { get; }

    /// <summary>Effective SQLite synchronous mode (1 = NORMAL); contract-tested.</summary>
    internal long SynchronousMode
    {
        get
        {
            using var command = _connection.CreateCommand();
            command.CommandText = "PRAGMA synchronous;";
            return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
        }
    }

    public static VaultDatabase OpenOrCreate(string path)
    {
        var fullPath = System.IO.Path.GetFullPath(path);
        var directory = System.IO.Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        if (File.Exists(fullPath) && !IsVaultFile(fullPath))
        {
            throw new InvalidDataException("The file is not an AegisVault vault.");
        }

        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = fullPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
        }.ToString();

        var connection = new SqliteConnection(connectionString);
        try
        {
            connection.Open();
            ExecuteNonQuery(connection, null, "PRAGMA journal_mode=WAL;");
            ExecuteNonQuery(connection, null, "PRAGMA foreign_keys=ON;");
            ExecuteNonQuery(connection, null, "PRAGMA busy_timeout=5000;");
            // WAL defaults to FULL and fsyncs on every commit; NORMAL is the
            // documented-safe pairing for WAL and keeps bulk writes fast.
            ExecuteNonQuery(connection, null, "PRAGMA synchronous=NORMAL;");
            SchemaMigrations.Apply(connection);
            VaultFilePermissions.RestrictVault(fullPath);
        }
        catch
        {
            connection.Dispose();
            throw;
        }

        return new VaultDatabase(fullPath, connection);
    }

    public bool HasMeta()
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM vault_meta WHERE id = 1;";
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture) == 1;
    }

    public void WriteMeta(VaultHeader header)
    {
        using var transaction = _connection.BeginTransaction();
        using var command = _connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO vault_meta (id, format_version, kdf_algorithm, kdf_salt, kdf_params, wrapped_dek, created_at, updated_at)
            VALUES (1, $formatVersion, $kdfAlgorithm, $kdfSalt, $kdfParams, $wrappedDek, $createdAt, $updatedAt)
            ON CONFLICT(id) DO UPDATE SET
                format_version = excluded.format_version,
                kdf_algorithm  = excluded.kdf_algorithm,
                kdf_salt       = excluded.kdf_salt,
                kdf_params     = excluded.kdf_params,
                wrapped_dek    = excluded.wrapped_dek,
                created_at     = excluded.created_at,
                updated_at     = excluded.updated_at;
            """;
        command.Parameters.AddWithValue("$formatVersion", header.FormatVersion);
        command.Parameters.AddWithValue("$kdfAlgorithm", header.Kdf.Algorithm);
        command.Parameters.AddWithValue("$kdfSalt", header.Salt);
        command.Parameters.AddWithValue("$kdfParams", JsonSerializer.Serialize(header.Kdf, VaultJsonContext.Default.KdfParameters));
        command.Parameters.AddWithValue("$wrappedDek", header.WrappedDek);
        command.Parameters.AddWithValue("$createdAt", header.CreatedAt.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$updatedAt", header.UpdatedAt.ToString("O", CultureInfo.InvariantCulture));
        command.ExecuteNonQuery();
        transaction.Commit();
    }

    public VaultHeader? ReadMeta()
    {
        using var command = _connection.CreateCommand();
        command.CommandText = """
            SELECT format_version, kdf_algorithm, kdf_salt, kdf_params, wrapped_dek, created_at, updated_at
            FROM vault_meta WHERE id = 1;
            """;

        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }

        var kdf = JsonSerializer.Deserialize(reader.GetString(3), VaultJsonContext.Default.KdfParameters)
            ?? throw new InvalidDataException("Vault KDF parameters are invalid.");

        return new VaultHeader
        {
            FormatVersion = reader.GetInt32(0),
            Kdf = kdf,
            Salt = (byte[])reader["kdf_salt"],
            WrappedDek = (byte[])reader["wrapped_dek"],
            CreatedAt = DateTimeOffset.Parse(reader.GetString(5), CultureInfo.InvariantCulture),
            UpdatedAt = DateTimeOffset.Parse(reader.GetString(6), CultureInfo.InvariantCulture),
        };
    }

    public void UpsertEntry(Guid id, int version, byte[] nonce, byte[] ciphertext, byte[] tag, DateTimeOffset updatedAt)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = UpsertEntrySql;
        AddEntryParameters(command, id, version, nonce, ciphertext, tag, updatedAt);
        command.ExecuteNonQuery();
    }

    /// <summary>
    /// Upserts a batch of entry blobs in a single transaction (used when
    /// upgrading payloads to the current format after unlocking).
    /// </summary>
    public void WriteEntries(IReadOnlyList<EntryRecord> records)
    {
        if (records.Count == 0)
        {
            return;
        }

        using var transaction = _connection.BeginTransaction();
        using var command = _connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = UpsertEntrySql;

        foreach (var record in records)
        {
            command.Parameters.Clear();
            AddEntryParameters(command, record.Id, record.Version, record.Nonce, record.Ciphertext, record.Tag, record.UpdatedAt);
            command.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    private const string UpsertEntrySql = """
        INSERT INTO entries (id, nonce, ciphertext, tag, version, updated_at)
        VALUES ($id, $nonce, $ciphertext, $tag, $version, $updatedAt)
        ON CONFLICT(id) DO UPDATE SET
            nonce      = excluded.nonce,
            ciphertext = excluded.ciphertext,
            tag        = excluded.tag,
            version    = excluded.version,
            updated_at = excluded.updated_at;
        """;

    private static void AddEntryParameters(
        SqliteCommand command,
        Guid id,
        int version,
        byte[] nonce,
        byte[] ciphertext,
        byte[] tag,
        DateTimeOffset updatedAt)
    {
        command.Parameters.AddWithValue("$id", id.ToString("D"));
        command.Parameters.AddWithValue("$nonce", nonce);
        command.Parameters.AddWithValue("$ciphertext", ciphertext);
        command.Parameters.AddWithValue("$tag", tag);
        command.Parameters.AddWithValue("$version", version);
        command.Parameters.AddWithValue("$updatedAt", updatedAt.ToString("O", CultureInfo.InvariantCulture));
    }

    public void DeleteEntry(Guid id)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "DELETE FROM entries WHERE id = $id;";
        command.Parameters.AddWithValue("$id", id.ToString("D"));
        command.ExecuteNonQuery();
    }

    /// <summary>
    /// Permanently deletes a batch of entries in a single transaction (recycle
    /// bin purge/empty).
    /// </summary>
    public void DeleteEntries(IReadOnlyList<Guid> ids)
    {
        if (ids.Count == 0)
        {
            return;
        }

        using var transaction = _connection.BeginTransaction();
        using var command = _connection.CreateCommand();
        command.Transaction = transaction;

        var placeholders = new string[ids.Count];
        for (var i = 0; i < ids.Count; i++)
        {
            var name = "$id" + i.ToString(CultureInfo.InvariantCulture);
            placeholders[i] = name;
            command.Parameters.AddWithValue(name, ids[i].ToString("D"));
        }

        command.CommandText = $"DELETE FROM entries WHERE id IN ({string.Join(", ", placeholders)});";
        command.ExecuteNonQuery();
        transaction.Commit();
    }

    public List<EntryRecord> ReadEntries()
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT id, version, nonce, ciphertext, tag, updated_at FROM entries;";

        var records = new List<EntryRecord>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            records.Add(new EntryRecord(
                Guid.Parse(reader.GetString(0)),
                reader.GetInt32(1),
                (byte[])reader["nonce"],
                (byte[])reader["ciphertext"],
                (byte[])reader["tag"],
                DateTimeOffset.Parse(reader.GetString(5), CultureInfo.InvariantCulture)));
        }

        return records;
    }

    public SettingRecord? ReadSetting(string key)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT nonce, ciphertext, tag FROM settings WHERE key = $key;";
        command.Parameters.AddWithValue("$key", key);

        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }

        return new SettingRecord(
            (byte[])reader["nonce"],
            (byte[])reader["ciphertext"],
            (byte[])reader["tag"]);
    }

    public void WriteSetting(string key, byte[] nonce, byte[] ciphertext, byte[] tag)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = """
            INSERT INTO settings (key, nonce, ciphertext, tag)
            VALUES ($key, $nonce, $ciphertext, $tag)
            ON CONFLICT(key) DO UPDATE SET
                nonce      = excluded.nonce,
                ciphertext = excluded.ciphertext,
                tag        = excluded.tag;
            """;
        command.Parameters.AddWithValue("$key", key);
        command.Parameters.AddWithValue("$nonce", nonce);
        command.Parameters.AddWithValue("$ciphertext", ciphertext);
        command.Parameters.AddWithValue("$tag", tag);
        command.ExecuteNonQuery();
    }

    public void Checkpoint()
    {
        ExecuteNonQuery(_connection, null, "PRAGMA wal_checkpoint(FULL);");
    }

    /// <summary>
    /// Writes a consistent single-file snapshot with <c>VACUUM INTO</c>. Unlike
    /// copying the vault file, this cannot capture a torn WAL state, and it works
    /// while the vault is locked (no DEK needed).
    /// </summary>
    public void SnapshotTo(string destinationPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        if (File.Exists(destinationPath))
        {
            File.Delete(destinationPath);
        }

        using var command = _connection.CreateCommand();
        // VACUUM INTO does not accept a bound parameter; the path is escaped by
        // doubling single quotes so it cannot break out of the literal.
        command.CommandText = $"VACUUM INTO '{destinationPath.Replace("'", "''", StringComparison.Ordinal)}';";
        command.ExecuteNonQuery();
        VaultFilePermissions.Restrict(destinationPath);
    }

    /// <summary>
    /// Removes device keys (and the future backup credentials) from a snapshot so
    /// a restored copy never carries the machine's password-less unlock material.
    /// Must only ever be called on a snapshot, never on the live vault.
    /// </summary>
    public static void SanitizeSnapshot(string snapshotPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(snapshotPath);

        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = snapshotPath,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false,
        }.ToString();

        using var connection = new SqliteConnection(connectionString);
        connection.Open();
        // Overwrite deleted pages before VACUUM rebuilds the file.
        ExecuteNonQuery(connection, null, "PRAGMA secure_delete=ON;");
        ExecuteNonQuery(connection, null, "DELETE FROM device_keys;");
        ExecuteNonQuery(connection, null, "DELETE FROM settings WHERE key = 'backup-credentials';");
        ExecuteNonQuery(connection, null, "VACUUM;");
        connection.Close();

        VaultFilePermissions.Restrict(snapshotPath);
    }

    public void UpsertDeviceKey(string protectorId, byte[] blob, DateTimeOffset createdAt)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = """
            INSERT INTO device_keys (protector, blob, created_at)
            VALUES ($protector, $blob, $createdAt)
            ON CONFLICT(protector) DO UPDATE SET
                blob       = excluded.blob,
                created_at = excluded.created_at;
            """;
        command.Parameters.AddWithValue("$protector", protectorId);
        command.Parameters.AddWithValue("$blob", blob);
        command.Parameters.AddWithValue("$createdAt", createdAt.ToString("O", CultureInfo.InvariantCulture));
        command.ExecuteNonQuery();
    }

    public byte[]? ReadDeviceKey(string protectorId)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT blob FROM device_keys WHERE protector = $protector;";
        command.Parameters.AddWithValue("$protector", protectorId);
        return command.ExecuteScalar() as byte[];
    }

    public void DeleteDeviceKey(string protectorId)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "DELETE FROM device_keys WHERE protector = $protector;";
        command.Parameters.AddWithValue("$protector", protectorId);
        command.ExecuteNonQuery();
    }

    public void DeleteAllDeviceKeys()
    {
        ExecuteNonQuery(_connection, null, "DELETE FROM device_keys;");
    }

    public void Dispose()
    {
        _connection.Dispose();
    }

    private static bool IsVaultFile(string path)
    {
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ToString();

        try
        {
            using var connection = new SqliteConnection(connectionString);
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'vault_meta';";
            return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture) == 1;
        }
        catch (SqliteException)
        {
            return false;
        }
    }

    private static void ExecuteNonQuery(SqliteConnection connection, SqliteTransaction? transaction, string sql)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}
