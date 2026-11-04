using System.Text;
using AegisVault.App.Localization;
using AegisVault.App.Services;
using AegisVault.Core.Models;
using AegisVault.Core.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AegisVault.App.ViewModels;

/// <summary>
/// Per-field validation results; <c>null</c> means that field has no problem.
/// Every field is evaluated in one pass so all problems show up together.
/// </summary>
public sealed record UnlockFormErrors(string? Password, string? Confirm, string? Path);

/// <summary>One row of the "recent vaults" shortcut list on the open page.</summary>
public sealed record RecentVaultItem(string Path, string FileName)
{
    public override string ToString() => Path;
}

public partial class UnlockViewModel : ObservableObject
{
    private const int MinimumPasswordLength = 8;

    public const int OpenMode = 0;
    public const int CreateMode = 1;

    public event Action<VaultService>? VaultOpened;

    public IKeyProtector? DeviceKeyProtector { get; set; }

    public bool CanRememberDevice => DeviceKeyProtector?.IsAvailable == true;

    public bool SecureInputAvailable { get; init; }

    /// <summary>0 = open an existing vault, 1 = create a new vault.</summary>
    [ObservableProperty]
    private int modeIndex;

    [ObservableProperty]
    private string vaultPath = GetDefaultVaultPath();

    [ObservableProperty]
    private string newVaultPath = GetDefaultVaultPath();

    [ObservableProperty]
    private string masterPassword = string.Empty;

    [ObservableProperty]
    private string confirmPassword = string.Empty;

    [ObservableProperty]
    private bool rememberDevice;

    /// <summary>True when the vault opened but the device key could not be stored.</summary>
    public bool RememberDeviceFailed { get; private set; }

    [ObservableProperty]
    private string? errorMessage;

    /// <summary>Error shown under the master-password box (both tabs).</summary>
    [ObservableProperty]
    private string? passwordError;

    /// <summary>Error shown under the confirm-password box (create tab).</summary>
    [ObservableProperty]
    private string? confirmPasswordError;

    /// <summary>Error shown under the vault-file box (open tab).</summary>
    [ObservableProperty]
    private string? pathError;

    /// <summary>Error shown under the save-location box (create tab).</summary>
    [ObservableProperty]
    private string? newVaultPathError;

    [ObservableProperty]
    private bool isBusy;

    private PasswordStrengthResult? _masterPasswordStrength;
    private readonly string _defaultVaultPath;
    private bool _isFirstRun;

    /// <param name="defaultVaultPath">
    /// Overrides the default vault location (tests use a temp directory);
    /// defaults to <c>Documents\AegisVault\vault.aegis</c>.
    /// </param>
    public UnlockViewModel(string? defaultVaultPath = null)
    {
        _defaultVaultPath = defaultVaultPath ?? GetDefaultVaultPath();
        vaultPath = _defaultVaultPath;
        newVaultPath = _defaultVaultPath;
    }

    public bool IsCreateMode => ModeIndex == CreateMode;

    /// <summary>True when there is no known vault at all (create page gets a welcome block).</summary>
    public bool IsFirstRun => _isFirstRun;

    /// <summary>Recently opened vaults that still exist, newest first (at most three).</summary>
    public IReadOnlyList<RecentVaultItem> RecentVaultItems { get; private set; } = [];

    public bool HasRecentVaultItems => RecentVaultItems.Count > 0;

    /// <summary>
    /// Short version (<c>0.6.3</c>) for the unlock page. The build hash lives in the
    /// settings page instead: someone about to unlock does not need it, but a bug
    /// report does.
    /// </summary>
    public string VersionText => AppVersion.Short;

    /// <summary>Mode-specific busy text (creating is not the same as opening).</summary>
    public string BusyText => Loc.T(IsCreateMode ? "Unlock_BusyCreating" : "Unlock_BusyUnlocking");

    /// <summary>
    /// Hint under the save-location box: shows the ".aegis" extension that will
    /// be appended without rewriting what the user typed.
    /// </summary>
    public string NewVaultPathHint
    {
        get
        {
            var raw = NewVaultPath.Trim();
            if (raw.Length == 0)
            {
                return string.Empty;
            }

            var normalized = NormalizeVaultPath(raw);
            return string.Equals(normalized, raw, StringComparison.OrdinalIgnoreCase)
                ? string.Empty
                : Loc.Format("Unlock_PathWillSaveAs", normalized);
        }
    }

    public string MasterPasswordStrengthSummary => _masterPasswordStrength is null
        ? string.Empty
        : StrengthFormatting.FormatSummary(
            _masterPasswordStrength.Score,
            _masterPasswordStrength.Label,
            _masterPasswordStrength.CrackTime);

    public double MasterPasswordStrengthPercent => (_masterPasswordStrength?.Score ?? 0) * 25;

    partial void OnMasterPasswordChanged(string value)
    {
        _masterPasswordStrength = string.IsNullOrEmpty(value)
            ? null
            : PasswordStrengthEstimator.Evaluate(value);

        PasswordError = null;
        ErrorMessage = null;
        OnPropertyChanged(nameof(MasterPasswordStrengthSummary));
        OnPropertyChanged(nameof(MasterPasswordStrengthPercent));
    }

    partial void OnConfirmPasswordChanged(string value)
    {
        ConfirmPasswordError = null;
        ErrorMessage = null;
    }

    partial void OnVaultPathChanged(string value)
    {
        PathError = null;
        ErrorMessage = null;
    }

    partial void OnNewVaultPathChanged(string value)
    {
        NewVaultPathError = null;
        ErrorMessage = null;
        OnPropertyChanged(nameof(NewVaultPathHint));
    }

    partial void OnModeIndexChanged(int value)
    {
        OnPropertyChanged(nameof(IsCreateMode));
        OnPropertyChanged(nameof(BusyText));
        ClearErrors();
    }

    private void ClearErrors()
    {
        PasswordError = null;
        ConfirmPasswordError = null;
        PathError = null;
        NewVaultPathError = null;
        ErrorMessage = null;
    }

    /// <summary>
    /// Applies persisted preferences: prefers the last vault (or the default
    /// path) when it exists, otherwise starts on the create tab. Also seeds the
    /// recent-vault shortcuts and the first-run flag.
    /// </summary>
    public void ApplyPreferences(AppPreferences preferences)
    {
        ArgumentNullException.ThrowIfNull(preferences);

        var recent = preferences.LastVaultPath;

        // A usable recent vault means this is not a first run: showing the
        // welcome block next to the recent-vault shortcuts looked like a
        // contradiction (the shortcuts are right there).
        RecentVaultItems = BuildRecentVaults(preferences);
        OnPropertyChanged(nameof(RecentVaultItems));
        OnPropertyChanged(nameof(HasRecentVaultItems));

        var hasKnownVault = RecentVaultItems.Count > 0 ||
                            (!string.IsNullOrWhiteSpace(recent) && File.Exists(recent));
        _isFirstRun = !hasKnownVault && !File.Exists(_defaultVaultPath);
        OnPropertyChanged(nameof(IsFirstRun));

        if (!string.IsNullOrWhiteSpace(recent) && File.Exists(recent))
        {
            VaultPath = recent;
            ModeIndex = OpenMode;
            return;
        }

        VaultPath = _defaultVaultPath;
        ModeIndex = File.Exists(_defaultVaultPath) ? OpenMode : CreateMode;
    }

    /// <summary>Fills the path from a recent-vault row and clears any error.</summary>
    [RelayCommand]
    private void SelectRecentVault(RecentVaultItem? item)
    {
        if (item is null)
        {
            return;
        }

        VaultPath = item.Path;
        ModeIndex = OpenMode;
        ErrorMessage = null;
    }

    /// <summary>
    /// Checks both fields of the "open" form in one pass. Pure so the
    /// aggregation is unit-testable.
    /// </summary>
    public static UnlockFormErrors ValidateOpen(string? password, string? path)
    {
        var passwordError = string.IsNullOrEmpty(password)
            ? Loc.T("Unlock_ErrorPasswordRequired")
            : null;

        string? pathError = null;
        var trimmed = path?.Trim() ?? string.Empty;
        if (trimmed.Length == 0)
        {
            pathError = Loc.T("Unlock_ErrorPathRequired");
        }
        else if (!File.Exists(trimmed))
        {
            pathError = Loc.T("Unlock_ErrorVaultNotFound");
        }

        return new UnlockFormErrors(passwordError, null, pathError);
    }

    /// <summary>Checks every field of the "create" form in one pass.</summary>
    public static UnlockFormErrors ValidateCreate(string? password, string? confirmPassword, string? path)
    {
        var passwordValue = password ?? string.Empty;

        string? passwordError = null;
        string? confirmError = null;
        if (passwordValue.Length == 0)
        {
            passwordError = Loc.T("Unlock_ErrorPasswordRequired");
        }
        else
        {
            if (passwordValue.Length < MinimumPasswordLength)
            {
                passwordError = Loc.T("Unlock_ErrorPasswordTooShort");
            }

            if (!string.Equals(passwordValue, confirmPassword, StringComparison.Ordinal))
            {
                confirmError = Loc.T("Unlock_ErrorConfirmMismatch");
            }
        }

        string? pathError = null;
        var normalized = NormalizeVaultPath(path);
        if (normalized.Length == 0)
        {
            pathError = Loc.T("Unlock_ErrorPathRequired");
        }
        else if (File.Exists(normalized))
        {
            pathError = Loc.T("Unlock_ErrorFileExists");
        }

        return new UnlockFormErrors(passwordError, confirmError, pathError);
    }

    /// <summary>Applies a dropped ".aegis" file: fills the path and clears errors.</summary>
    public bool TryAcceptDroppedFile(string? path)
    {
        var trimmed = path?.Trim() ?? string.Empty;
        if (trimmed.Length == 0 || !trimmed.EndsWith(".aegis", StringComparison.OrdinalIgnoreCase))
        {
            PathError = Loc.T("Unlock_ErrorNotVaultFile");
            return false;
        }

        VaultPath = trimmed;
        ModeIndex = OpenMode;
        ErrorMessage = null;
        return true;
    }

    private static IReadOnlyList<RecentVaultItem> BuildRecentVaults(AppPreferences preferences)
    {
        var paths = Core.Services.RecentVaults.Normalize(preferences.RecentVaultPaths);
        var items = new List<RecentVaultItem>(paths.Count);
        foreach (var path in paths)
        {
            if (!File.Exists(path))
            {
                continue;
            }

            items.Add(new RecentVaultItem(path, Path.GetFileName(path)));
        }

        return items;
    }

    /// <summary>
    /// Attempts a password-less unlock using the remembered device key.
    /// Returns the unlocked vault, or <c>null</c> when the device key is
    /// unavailable, the vault is missing, or the stored key no longer matches.
    /// Does not raise <see cref="VaultOpened"/>: the caller decides which
    /// window to show (the unlock window must never appear when this succeeds).
    /// </summary>
    public async Task<VaultService?> TryDeviceUnlockAsync()
    {
        if (DeviceKeyProtector is not { IsAvailable: true } protector)
        {
            return null;
        }

        var path = VaultPath.Trim();
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
        {
            return null;
        }

        VaultService? vault = null;
        var unlocked = false;

        await Task.Run(() =>
        {
            try
            {
                vault = VaultService.Open(path);
                unlocked = vault.TryUnlockWithDeviceKey(protector);
                if (!unlocked)
                {
                    vault.Dispose();
                    vault = null;
                }
            }
            catch (Exception)
            {
                vault?.Dispose();
                vault = null;
                unlocked = false;
            }
        });

        return unlocked ? vault : null;
    }

    [RelayCommand]
    private async Task UnlockAsync()
    {
        if (IsBusy)
        {
            return;
        }

        var path = VaultPath.Trim();
        var errors = ValidateOpen(MasterPassword, path);
        PasswordError = errors.Password;
        PathError = errors.Path;
        ErrorMessage = null;
        if (errors.Password is not null || errors.Path is not null)
        {
            return;
        }

        IsBusy = true;
        try
        {
            var passwordBytes = Encoding.UTF8.GetBytes(MasterPassword);
            try
            {
                VaultService? vault = null;
                var status = VaultUnlockStatus.Corrupted;

                await Task.Run(() =>
                {
                    vault = VaultService.Open(path);
                    status = vault.Unlock(passwordBytes);
                    if (status != VaultUnlockStatus.Success)
                    {
                        vault.Dispose();
                        vault = null;
                    }
                });

                switch (status)
                {
                    case VaultUnlockStatus.Success:
                        MasterPassword = string.Empty;
                        RememberDeviceIfRequested(vault!);
                        VaultOpened?.Invoke(vault!);
                        break;
                    case VaultUnlockStatus.WrongPassword:
                        PasswordError = Loc.T("Unlock_ErrorWrongPassword");
                        break;
                    case VaultUnlockStatus.UnsupportedVersion:
                        ErrorMessage = Loc.T("Unlock_ErrorUnsupportedVersion");
                        break;
                    default:
                        ErrorMessage = Loc.T("Unlock_ErrorCorrupted");
                        break;
                }
            }
            finally
            {
                System.Security.Cryptography.CryptographicOperations.ZeroMemory(passwordBytes);
            }
        }
        catch (UnsupportedVaultVersionException)
        {
            // Same message as the header-version guard: the vault belongs to a
            // newer build, whatever part of it carries the newer version.
            ErrorMessage = Loc.T("Unlock_ErrorUnsupportedVersion");
        }
        catch (Exception)
        {
            ErrorMessage = Loc.T("Unlock_ErrorOpenFailed");
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task CreateAsync()
    {
        if (IsBusy)
        {
            return;
        }

        var errors = ValidateCreate(MasterPassword, ConfirmPassword, NewVaultPath);
        PasswordError = errors.Password;
        ConfirmPasswordError = errors.Confirm;
        NewVaultPathError = errors.Path;
        ErrorMessage = null;
        if (errors.Password is not null || errors.Confirm is not null || errors.Path is not null)
        {
            return;
        }

        var path = NormalizeVaultPath(NewVaultPath);
        NewVaultPath = path;

        IsBusy = true;
        try
        {
            var passwordBytes = Encoding.UTF8.GetBytes(MasterPassword);
            try
            {
                VaultService? vault = null;
                await Task.Run(() => vault = VaultService.CreateNew(path, passwordBytes));

                MasterPassword = string.Empty;
                ConfirmPassword = string.Empty;
                RememberDeviceIfRequested(vault!);
                VaultOpened?.Invoke(vault!);
            }
            finally
            {
                System.Security.Cryptography.CryptographicOperations.ZeroMemory(passwordBytes);
            }
        }
        catch (Exception)
        {
            ErrorMessage = Loc.T("Unlock_ErrorCreateFailed");
        }
        finally
        {
            IsBusy = false;
        }
    }

    internal static string NormalizeVaultPath(string? raw)
    {
        var path = raw?.Trim() ?? string.Empty;
        if (path.Length == 0)
        {
            return string.Empty;
        }

        return path.EndsWith(".aegis", StringComparison.OrdinalIgnoreCase)
            ? path
            : path + ".aegis";
    }

    private void RememberDeviceIfRequested(VaultService vault)
    {
        if (!RememberDevice || DeviceKeyProtector is not { IsAvailable: true } protector)
        {
            return;
        }

        try
        {
            vault.RememberDevice(protector);
        }
        catch (Exception exception)
        {
            // The vault is open; the caller surfaces this as a status hint
            // instead of silently pretending the device was remembered.
            RememberDeviceFailed = true;
            System.Diagnostics.Trace.TraceWarning($"Remembering this device failed: {exception.Message}");
        }
    }

    private static string GetDefaultVaultPath()
    {
        var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        return Path.Combine(documents, "AegisVault", "vault.aegis");
    }
}
