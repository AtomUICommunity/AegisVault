using System.Text.Json;
using System.Text.Json.Serialization;
using AegisVault.Core.Models;

namespace AegisVault.Core.Services;

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(AppPreferences))]
internal sealed partial class AppPreferencesJsonContext : JsonSerializerContext;

/// <summary>
/// Reads and writes non-sensitive application preferences (recent vault path,
/// theme) to a plaintext JSON file. Loading is tolerant: a missing or corrupt
/// file yields defaults instead of failing startup.
/// </summary>
public sealed class AppPreferencesStore
{
    private readonly string _filePath;

    public AppPreferencesStore(string? filePath = null)
    {
        _filePath = filePath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "AegisVault",
            "app.json");
    }

    public string FilePath => _filePath;

    public AppPreferences Load()
    {
        try
        {
            if (!File.Exists(_filePath))
            {
                return new AppPreferences();
            }

            var json = File.ReadAllText(_filePath);
            var preferences = JsonSerializer.Deserialize(json, AppPreferencesJsonContext.Default.AppPreferences)
                ?? new AppPreferences();

            // Source-generated JSON leaves missing collections null (and older
            // files predate the field): normalise at the load boundary.
            return preferences with
            {
                RecentVaultPaths = RecentVaults.Normalize(preferences.RecentVaultPaths),
                CloseAction = CloseActions.Normalize(preferences.CloseAction),
                // Numeric fields clamp at the load boundary: a hand-edited or
                // older file must never push the scheduler into a tight loop.
                // Source-generated JSON does not run property initialisers, so a
                // missing field arrives as 0 — treat 0 as "unset" and use the
                // documented defaults (7 backups / 24 hours).
                BackupLocalKeepCount = Math.Clamp(
                    preferences.BackupLocalKeepCount == 0 ? 7 : preferences.BackupLocalKeepCount,
                    LocalBackupService.MinimumKeepCount,
                    LocalBackupService.MaximumKeepCount),
                BackupIntervalMinutes = Math.Clamp(
                    preferences.BackupIntervalMinutes == 0 ? 1440 : preferences.BackupIntervalMinutes,
                    15,
                    43200),
                // Materialise the null-means-enabled default so saved files carry
                // an explicit value instead of "null".
                BackupLocalEnabled = preferences.LocalBackupEnabled,            };
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException or NotSupportedException)
        {
            return new AppPreferences();
        }
    }

    public void Save(AppPreferences preferences)
    {
        ArgumentNullException.ThrowIfNull(preferences);

        var directory = Path.GetDirectoryName(_filePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var json = JsonSerializer.Serialize(preferences, AppPreferencesJsonContext.Default.AppPreferences);
        var temporaryPath = _filePath + ".tmp";
        File.WriteAllText(temporaryPath, json);
        File.Move(temporaryPath, _filePath, overwrite: true);
    }
}
