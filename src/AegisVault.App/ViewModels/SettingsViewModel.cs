using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using AegisVault.App.Localization;
using AegisVault.App.Services;
using AegisVault.Core.Models;
using AegisVault.Core.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AegisVault.App.ViewModels;

public sealed record AutoLockOption(int Minutes, string Label);

public sealed record ClipboardOption(int Seconds, string Label);

public sealed record ThemeOption(string Value, string Label);

public sealed record LanguageOption(string Value, string Label);

public sealed record CloseBehaviorOption(string Value, string Label);

public sealed record BackupIntervalOption(int Minutes, string Label);

public sealed record BackupKeepOption(int Count, string Label);

public partial class SettingsViewModel : ObservableObject
{
    private readonly VaultService _vault;
    private readonly SecureConfigService _config;
    private readonly IKeyProtector? _protector;
    private readonly Action<string>? _applyTheme;
    private readonly Action<bool>? _applyScreenGuard;
    private readonly Action<bool>? _applyFloatingBall;
    private readonly Action<string>? _applyLanguage;
    private readonly Action? _imported;
    private readonly Func<bool, bool>? _applyAutoStart;
    private readonly Action<bool>? _applyStartMinimized;
    private readonly Action<string>? _applyCloseBehavior;
    private readonly Action<bool>? _applyReduceMotion;
    private readonly Action<bool>? _applyBackup;
    private readonly Func<Task<BackupResult>>? _backupNow;
    private readonly Func<string, BackupValidation>? _validateBackup;
    private readonly Func<string, BackupResult>? _restoreBackup;
    private readonly Func<IReadOnlyList<BackupArtifact>>? _listBackups;

    public SettingsViewModel(
        VaultService vault,
        SecureConfigService config,
        IKeyProtector? protector,
        Action<string>? applyTheme,
        string currentTheme,
        bool screenGuardSupported = false,
        Action<bool>? applyScreenGuard = null,
        bool showFloatingBall = false,
        Action<bool>? applyFloatingBall = null,
        Action? imported = null,
        string currentLanguage = Loc.System,
        Action<string>? applyLanguage = null,
        bool startupSupported = false,
        bool autoStart = false,
        bool startMinimized = false,
        Func<bool, bool>? applyAutoStart = null,
        Action<bool>? applyStartMinimized = null,
        string closeBehavior = CloseActions.Ask,
        Action<string>? applyCloseBehavior = null,
        bool reduceMotion = false,
        Action<bool>? applyReduceMotion = null,
        bool backupEnabled = false,
        bool backupLocalEnabled = true,
        int backupKeepCount = 7,
        int backupIntervalMinutes = 1440,
        string? backupLastSuccessUtc = null,
        string? backupLastResult = null,
        Action<bool>? applyBackup = null,
        Func<Task<BackupResult>>? backupNow = null,
        Func<string, BackupValidation>? validateBackup = null,
        Func<string, BackupResult>? restoreBackup = null,
        Func<IReadOnlyList<BackupArtifact>>? listBackups = null)
    {
        _vault = vault;
        _config = config;
        _protector = protector;
        _applyTheme = applyTheme;
        _applyScreenGuard = applyScreenGuard;
        _applyFloatingBall = applyFloatingBall;
        _applyLanguage = applyLanguage;
        _imported = imported;
        _applyAutoStart = applyAutoStart;
        _applyStartMinimized = applyStartMinimized;
        _applyCloseBehavior = applyCloseBehavior;
        _applyReduceMotion = applyReduceMotion;
        _applyBackup = applyBackup;
        _backupNow = backupNow;
        _validateBackup = validateBackup;
        _restoreBackup = restoreBackup;
        _listBackups = listBackups;

        var user = config.Current;
        SelectedAutoLock = AutoLockOptions.FirstOrDefault(option => option.Minutes == user.AutoLockMinutes) ?? AutoLockOptions[0];
        LockOnMinimize = user.LockOnMinimize;
        LockOnScreenLock = user.LockOnScreenLock;
        LockOnSuspend = user.LockOnSuspend;
        SelectedClipboard = ClipboardOptions.FirstOrDefault(option => option.Seconds == user.ClipboardClearSeconds) ?? ClipboardOptions[0];
        GeneratorLength = user.Generator.Length;
        GeneratorSymbols = user.Generator.IncludeSymbols;
        GeneratorExcludeAmbiguous = user.Generator.ExcludeAmbiguous;
        SelectedTheme = ThemeOptions.FirstOrDefault(option => option.Value == currentTheme) ?? ThemeOptions[0];
        SelectedLanguage = LanguageOptions.FirstOrDefault(option => option.Value == currentLanguage) ?? LanguageOptions[0];
        SelectedCloseBehavior = CloseBehaviorOptions.FirstOrDefault(option => option.Value == closeBehavior) ?? CloseBehaviorOptions[0];
        BackupEnabled = backupEnabled;
        BackupLocalEnabled = backupLocalEnabled;
        SelectedBackupInterval = BackupIntervalOptions.FirstOrDefault(option => option.Minutes == backupIntervalMinutes) ?? BackupIntervalOptions[2];
        SelectedBackupKeep = BackupKeepOptions.FirstOrDefault(option => option.Count == backupKeepCount) ?? BackupKeepOptions[1];
        BackupStatus = BuildBackupStatus(backupLastSuccessUtc, backupLastResult);

        DeviceKeySupported = protector?.IsAvailable == true;
        HasDeviceKey = DeviceKeySupported && vault.HasDeviceKey(protector!);

        ScreenGuardSupported = screenGuardSupported;
        DisableScreenCapture = user.DisableScreenCapture;
        ShowFloatingBall = showFloatingBall;
        ReduceMotion = reduceMotion;

        StartupSupported = startupSupported;
        AutoStart = autoStart;
        StartMinimized = startMinimized;
    }

    public IReadOnlyList<AutoLockOption> AutoLockOptions { get; } =
        [new(0, Loc.T("Settings_Off")),
         new(1, Loc.Format("Settings_Minutes", 1)),
         new(5, Loc.Format("Settings_Minutes", 5)),
         new(15, Loc.Format("Settings_Minutes", 15)),
         new(30, Loc.Format("Settings_Minutes", 30))];

    public IReadOnlyList<ClipboardOption> ClipboardOptions { get; } =
        [new(0, Loc.T("Settings_ClipboardOff")),
         new(15, Loc.Format("Settings_Seconds", 15)),
         new(30, Loc.Format("Settings_Seconds", 30)),
         new(60, Loc.Format("Settings_Minutes", 1)),
         new(120, Loc.Format("Settings_Minutes", 2))];

    public IReadOnlyList<ThemeOption> ThemeOptions { get; } =
        [new("system", Loc.T("Settings_ThemeSystem")),
         new("light", Loc.T("Settings_ThemeLight")),
         new("dark", Loc.T("Settings_ThemeDark"))];

    public IReadOnlyList<LanguageOption> LanguageOptions { get; } =
        [new(Loc.System, Loc.T("Settings_LanguageSystem")),
         new(Loc.Chinese, Loc.T("Settings_LanguageZh")),
         new(Loc.English, Loc.T("Settings_LanguageEn"))];

    public IReadOnlyList<CloseBehaviorOption> CloseBehaviorOptions { get; } =
        [new(CloseActions.Ask, Loc.T("Settings_CloseBehaviorAsk")),
         new(CloseActions.Minimize, Loc.T("Settings_CloseBehaviorMinimize")),
         new(CloseActions.Exit, Loc.T("Settings_CloseBehaviorExit"))];

    public IReadOnlyList<BackupIntervalOption> BackupIntervalOptions { get; } =
        [new(60, Loc.T("Settings_BackupIntervalHourly")),
         new(360, Loc.T("Settings_BackupInterval6Hours")),
         new(1440, Loc.T("Settings_BackupIntervalDaily")),
         new(10080, Loc.T("Settings_BackupIntervalWeekly"))];

    public IReadOnlyList<BackupKeepOption> BackupKeepOptions { get; } =
        [new(3, Loc.Format("Settings_BackupKeepItems", 3)),
         new(7, Loc.Format("Settings_BackupKeepItems", 7)),
         new(14, Loc.Format("Settings_BackupKeepItems", 14)),
         new(30, Loc.Format("Settings_BackupKeepItems", 30))];

    [ObservableProperty]
    private AutoLockOption? selectedAutoLock;

    [ObservableProperty]
    private bool lockOnMinimize;

    [ObservableProperty]
    private bool lockOnScreenLock;

    [ObservableProperty]
    private bool lockOnSuspend;

    [ObservableProperty]
    private ClipboardOption? selectedClipboard;

    [ObservableProperty]
    private double generatorLength = 20;

    public string GeneratorLengthText => Loc.Format("Settings_GeneratorLength", GeneratorLength);

    partial void OnGeneratorLengthChanged(double value) => OnPropertyChanged(nameof(GeneratorLengthText));

    [ObservableProperty]
    private bool generatorSymbols = true;

    [ObservableProperty]
    private bool generatorExcludeAmbiguous;

    [ObservableProperty]
    private ThemeOption? selectedTheme;

    [ObservableProperty]
    private LanguageOption? selectedLanguage;

    [ObservableProperty]
    private bool screenGuardSupported;

    [ObservableProperty]
    private bool disableScreenCapture;

    [ObservableProperty]
    private bool showFloatingBall;

    [ObservableProperty]
    private bool reduceMotion;

    [ObservableProperty]
    private bool deviceKeySupported;

    [ObservableProperty]
    private bool startupSupported;

    [ObservableProperty]
    private bool autoStart;

    [ObservableProperty]
    private bool startMinimized;

    [ObservableProperty]
    private CloseBehaviorOption? selectedCloseBehavior;

    [ObservableProperty]
    private bool backupEnabled;

    [ObservableProperty]
    private bool backupLocalEnabled;

    [ObservableProperty]
    private BackupIntervalOption? selectedBackupInterval;

    [ObservableProperty]
    private BackupKeepOption? selectedBackupKeep;

    [ObservableProperty]
    private string? backupStatus;

    [ObservableProperty]
    private bool backupBusy;

    /// <summary>Re-reads the backup status line (called by the coordinator).</summary>
    public void RefreshBackupStatus(string? lastSuccessUtc, string? lastResult)
        => BackupStatus = BuildBackupStatus(lastSuccessUtc, lastResult);

    [ObservableProperty]
    private bool hasDeviceKey;

    [ObservableProperty]
    private string newMasterPassword = string.Empty;

    [ObservableProperty]
    private string confirmMasterPassword = string.Empty;

    [ObservableProperty]
    private string? statusMessage;

    public string VersionText =>
        $"AegisVault {typeof(SettingsViewModel).Assembly.GetName().Version?.ToString(3) ?? "0.1.0"}";

    [RelayCommand]
    private void Save()
    {
        var current = _config.Current;
        var updated = current with
        {
            AutoLockMinutes = SelectedAutoLock?.Minutes ?? current.AutoLockMinutes,
            LockOnMinimize = LockOnMinimize,
            LockOnScreenLock = LockOnScreenLock,
            LockOnSuspend = LockOnSuspend,
            ClipboardClearSeconds = SelectedClipboard?.Seconds ?? current.ClipboardClearSeconds,
            DisableScreenCapture = DisableScreenCapture,
            Generator = current.Generator with
            {
                Length = (int)Math.Round(GeneratorLength),
                IncludeSymbols = GeneratorSymbols,
                ExcludeAmbiguous = GeneratorExcludeAmbiguous,
            },
        };

        try
        {
            _config.Save(updated);
        }
        catch (Exception exception) when (exception is ObjectDisposedException or InvalidOperationException)
        {
            // The session was locked while the dialog stayed open; the window is
            // closed on lock, but be defensive instead of crashing.
            StatusMessage = Loc.T("Settings_StatusSessionLocked");
            return;
        }

        // Motion must be persisted before the theme is re-applied: the flag
        // rides in the same AtomUI theme configuration.
        _applyReduceMotion?.Invoke(ReduceMotion);
        _applyTheme?.Invoke(SelectedTheme?.Value ?? "system");
        _applyScreenGuard?.Invoke(!DisableScreenCapture);
        _applyFloatingBall?.Invoke(ShowFloatingBall);
        _applyLanguage?.Invoke(SelectedLanguage?.Value ?? Loc.System);

        // The auto-start entry lives in the OS, so the write can fail; keep the
        // switch honest and tell the user instead of pretending it worked.
        if (_applyAutoStart is { } applyAutoStart && !applyAutoStart(AutoStart))
        {
            AutoStart = false;
            StatusMessage = Loc.T("Settings_StatusAutoStartFailed");
            return;
        }

        _applyStartMinimized?.Invoke(StartMinimized);
        _applyCloseBehavior?.Invoke(SelectedCloseBehavior?.Value ?? CloseActions.Ask);
        _applyBackup?.Invoke(BackupEnabled);
        StatusMessage = Loc.T("Settings_StatusSaved");
    }

    [RelayCommand]
    private void ChangeMasterPassword()
    {
        StatusMessage = null;

        if (string.IsNullOrEmpty(NewMasterPassword))
        {
            StatusMessage = Loc.T("Settings_StatusPasswordRequired");
            return;
        }

        if (NewMasterPassword.Length < 8)
        {
            StatusMessage = Loc.T("Settings_StatusPasswordTooShort");
            return;
        }

        if (!string.Equals(NewMasterPassword, ConfirmMasterPassword, StringComparison.Ordinal))
        {
            StatusMessage = Loc.T("Settings_StatusPasswordMismatch");
            return;
        }

        var passwordBytes = Encoding.UTF8.GetBytes(NewMasterPassword);
        try
        {
            _vault.ChangeMasterPassword(passwordBytes);
            NewMasterPassword = string.Empty;
            ConfirmMasterPassword = string.Empty;
            HasDeviceKey = _protector is { IsAvailable: true } && _vault.HasDeviceKey(_protector);
            StatusMessage = Loc.T("Settings_StatusPasswordChanged");
        }
        catch (Exception exception) when (exception is ObjectDisposedException or InvalidOperationException)
        {
            StatusMessage = Loc.T("Settings_StatusSessionLocked");
        }
        catch (Exception)
        {
            StatusMessage = Loc.T("Settings_StatusPasswordChangeFailed");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(passwordBytes);
        }
    }

    [RelayCommand]
    private void RememberDevice()
    {
        if (_protector is not { IsAvailable: true } protector)
        {
            return;
        }

        try
        {
            _vault.RememberDevice(protector);
            HasDeviceKey = true;
            StatusMessage = Loc.T("Settings_StatusDeviceRemembered");
        }
        catch (Exception exception) when (exception is ObjectDisposedException or InvalidOperationException)
        {
            StatusMessage = Loc.T("Settings_StatusSessionLocked");
        }
        catch (Exception)
        {
            StatusMessage = Loc.T("Settings_StatusDeviceRememberFailed");
        }
    }

    [RelayCommand]
    private void ForgetDevice()
    {
        if (_protector is not { } protector)
        {
            return;
        }

        try
        {
            _vault.ForgetDevice(protector);
            HasDeviceKey = false;
            StatusMessage = Loc.T("Settings_StatusDeviceForgotten");
        }
        catch (Exception exception) when (exception is ObjectDisposedException or InvalidOperationException)
        {
            StatusMessage = Loc.T("Settings_StatusSessionLocked");
        }
    }

    public void SaveBackupTo(string path)
    {
        try
        {
            _vault.SaveBackup(path);
            StatusMessage = Loc.T("Settings_StatusBackupSaved");
        }
        catch (Exception exception) when (exception is ObjectDisposedException or InvalidOperationException)
        {
            StatusMessage = Loc.T("Settings_StatusSessionLocked");
        }
        catch (Exception)
        {
            StatusMessage = Loc.T("Settings_StatusBackupFailed");
        }
    }

    /// <summary>Imports entries from a CSV file (Bitwarden or generic layout).</summary>
    public Task<ImportResult> ImportCsvFromAsync(string path)
        => CsvImportFlow.RunAsync(_vault, path, message => StatusMessage = message, _imported);

    /// <summary>
    /// Writes a plaintext CSV of the live entries (the recycle bin is excluded).
    /// Callers must warn the user: the file holds passwords in the clear.
    /// </summary>
    public void ExportCsvTo(string path)
    {
        try
        {
            var csv = VaultCsvExporter.Export(_vault.Entries, _vault.Categories);

            // A BOM keeps Excel and other tools from misreading non-ASCII titles.
            File.WriteAllText(path, csv, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
            StatusMessage = Loc.T("Settings_StatusExportDone");
        }
        catch (Exception exception) when (exception is ObjectDisposedException or InvalidOperationException)
        {
            StatusMessage = Loc.T("Settings_StatusSessionLocked");
        }
        catch (Exception)
        {
            StatusMessage = Loc.T("Settings_StatusExportFailed");
        }
    }

    /// <summary>Writes a passphrase-protected JSON export (live entries + categories).</summary>
    public async Task ExportEncryptedToAsync(string path, string passphrase)
    {
        // Snapshot on the UI thread; the vault lists must not be enumerated from a worker.
        var entries = _vault.Entries.ToList();
        var categories = _vault.Categories.ToList();
        var passphraseBytes = Encoding.UTF8.GetBytes(passphrase);
        try
        {
            // Argon2id over the default parameters takes about a second: keep it
            // off the UI thread so the settings window stays responsive.
            var bytes = await Task.Run(() => VaultExportService.ExportEncrypted(entries, categories, passphraseBytes));
            await File.WriteAllBytesAsync(path, bytes);
            StatusMessage = Loc.T("Settings_StatusExportDone");
        }
        catch (Exception)
        {
            StatusMessage = Loc.T("Settings_StatusExportFailed");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(passphraseBytes);
        }
    }

    /// <summary>Runs a backup right now (the settings card button).</summary>
    [RelayCommand]
    private async Task BackupNowAsync()
    {
        if (_backupNow is null || BackupBusy)
        {
            return;
        }

        BackupBusy = true;
        try
        {
            var result = await _backupNow();
            BackupStatus = result.Success
                ? Loc.Format("Settings_BackupStatusOk", Path.GetFileName(result.ArtifactPath ?? string.Empty))
                : Loc.Format("Settings_BackupStatusFailed", result.FailureCode ?? "unknown");
        }
        finally
        {
            BackupBusy = false;
        }
    }

    /// <summary>Asks for a backup file and restores it (with version/hash checks).</summary>
    [RelayCommand]
    private async Task RestoreBackupAsync()
    {
        if (_restoreBackup is null || _validateBackup is null || _listBackups is null || BackupBusy)
        {
            return;
        }

        var backups = _listBackups();
        if (backups.Count == 0)
        {
            BackupStatus = Loc.T("Settings_BackupNoBackups");
            return;
        }

        // Newest first; the dialog itself is the confirmation, and the service
        // refuses anything that fails validation.
        var newest = backups[0];
        var validation = _validateBackup(newest.Path);
        if (!validation.CanRestore)
        {
            BackupStatus = Loc.Format("Settings_BackupRestoreBlocked", validation.Status.ToString());
            return;
        }

        BackupBusy = true;
        try
        {
            var result = await Task.Run(() => _restoreBackup(newest.Path));
            BackupStatus = result.Success
                ? Loc.T("Settings_BackupRestoreDone")
                : Loc.Format("Settings_BackupStatusFailed", result.FailureCode ?? "unknown");
        }
        finally
        {
            BackupBusy = false;
        }
    }

    private static string? BuildBackupStatus(string? lastSuccessUtc, string? lastResult)
    {
        if (string.IsNullOrWhiteSpace(lastResult))
        {
            return null;
        }

        if (lastResult == "ok" && BackupPolicy.ParseLastSuccess(lastSuccessUtc) is { } when)
        {
            return Loc.Format("Settings_BackupStatusOk", when.ToLocalTime().ToString("g"));
        }

        return Loc.Format("Settings_BackupStatusFailed", lastResult.StartsWith("failed:", StringComparison.Ordinal)
            ? lastResult["failed:".Length..]
            : lastResult);
    }

    [RelayCommand]
    private void OpenDataFolder()    {
        try
        {
            var vaultPath = _vault.VaultPath;
            if (OperatingSystem.IsWindows())
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{vaultPath}\"") { UseShellExecute = true });
            }
            else
            {
                var directory = Path.GetDirectoryName(vaultPath);
                if (!string.IsNullOrEmpty(directory))
                {
                    Process.Start(new ProcessStartInfo(directory) { UseShellExecute = true });
                }
            }
        }
        catch (Exception)
        {
        }
    }
}
