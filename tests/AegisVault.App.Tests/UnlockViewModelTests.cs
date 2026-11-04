using AegisVault.App.Localization;
using AegisVault.App.ViewModels;
using AegisVault.Core.Models;
using AegisVault.Core.Services;
using Xunit;

namespace AegisVault.App.Tests;

public sealed class UnlockViewModelTests : IDisposable
{
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

    public UnlockViewModelTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "aegis-ui-tests", Guid.NewGuid().ToString("N"));
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
        }
    }

    [Fact]
    public Task CreateThenUnlockFlow() => Headless.RunAsync<object?>(async () =>
    {
        var createModel = new UnlockViewModel
        {
            NewVaultPath = _vaultPath,
            ModeIndex = UnlockViewModel.CreateMode,
            MasterPassword = "master password",
            ConfirmPassword = "master password",
        };

        VaultService? created = null;
        createModel.VaultOpened += vault => created = vault;

        await createModel.CreateCommand.ExecuteAsync(null);

        Assert.Null(createModel.ErrorMessage);
        Assert.NotNull(created);
        created!.Dispose();

        var unlockModel = new UnlockViewModel
        {
            VaultPath = _vaultPath,
            MasterPassword = "master password",
        };

        VaultService? unlocked = null;
        unlockModel.VaultOpened += vault => unlocked = vault;

        await unlockModel.UnlockCommand.ExecuteAsync(null);

        Assert.Null(unlockModel.ErrorMessage);
        Assert.NotNull(unlocked);
        Assert.True(unlocked!.IsUnlocked);
        unlocked.Dispose();
        return null;
    });

    [Fact]
    public Task WrongPasswordShowsFriendlyError() => Headless.RunAsync<object?>(async () =>
    {
        using (VaultService.CreateNew(_vaultPath, "master password"u8, FastOptions))
        {
        }

        var model = new UnlockViewModel
        {
            VaultPath = _vaultPath,
            MasterPassword = "wrong password",
        };

        await model.UnlockCommand.ExecuteAsync(null);

        Assert.Equal(Localization.Loc.T("Unlock_ErrorWrongPassword"), model.PasswordError);
        return null;
    });

    [Fact]
    public Task NewerSchemaShowsUnsupportedVersionMessage() => Headless.RunAsync<object?>(async () =>
    {
        using (var vault = VaultService.CreateNew(_vaultPath, "master password"u8, FastOptions))
        {
        }

        using (var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={_vaultPath}"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA user_version = 99;";
            command.ExecuteNonQuery();
        }

        var model = new UnlockViewModel
        {
            VaultPath = _vaultPath,
            MasterPassword = "master password",
        };

        await model.UnlockCommand.ExecuteAsync(null);

        // Same copy as a newer header version, not "corrupted"/"could not open".
        Assert.Equal(Loc.T("Unlock_ErrorUnsupportedVersion"), model.ErrorMessage);
        return null;
    });

    [Fact]
    public Task RememberDeviceFailureIsReportedButTheVaultOpens() => Headless.RunAsync<object?>(async () =>
    {
        using (VaultService.CreateNew(_vaultPath, "master password"u8, FastOptions))
        {
        }

        var model = new UnlockViewModel
        {
            VaultPath = _vaultPath,
            MasterPassword = "master password",
            RememberDevice = true,
            DeviceKeyProtector = new ThrowingKeyProtector(),
        };
        VaultService? opened = null;
        model.VaultOpened += vault => opened = vault;

        try
        {
            await model.UnlockCommand.ExecuteAsync(null);

            Assert.NotNull(opened);
            Assert.True(model.RememberDeviceFailed);
        }
        finally
        {
            opened?.Dispose();
        }

        return null;
    });

    private sealed class ThrowingKeyProtector : IKeyProtector
    {
        public string Id => "throwing";

        public bool IsAvailable => true;

        public byte[] Protect(ReadOnlySpan<byte> data) => throw new InvalidOperationException("no key store");

        public byte[] Unprotect(ReadOnlySpan<byte> data) => throw new InvalidOperationException("no key store");
    }

    [Fact]
    public Task MissingFileShowsFriendlyError() => Headless.RunAsync<object?>(async () =>
    {
        var model = new UnlockViewModel
        {
            VaultPath = Path.Combine(_directory, "missing.aegis"),
            MasterPassword = "whatever",
        };

        await model.UnlockCommand.ExecuteAsync(null);

        Assert.Equal(Localization.Loc.T("Unlock_ErrorVaultNotFound"), model.PathError);
        return null;
    });

    [Fact]
    public Task MismatchedConfirmationShowsError() => Headless.RunAsync<object?>(async () =>
    {
        var model = new UnlockViewModel
        {
            NewVaultPath = _vaultPath,
            ModeIndex = UnlockViewModel.CreateMode,
            MasterPassword = "master password",
            ConfirmPassword = "different",
        };

        await model.CreateCommand.ExecuteAsync(null);

        Assert.Equal(Localization.Loc.T("Unlock_ErrorConfirmMismatch"), model.ConfirmPasswordError);
        Assert.False(File.Exists(_vaultPath));
        return null;
    });

    [Fact]
    public Task ApplyPreferencesUsesExistingRecentVault() => Headless.RunAsync<object?>(async () =>
    {
        using (VaultService.CreateNew(_vaultPath, "master password"u8, FastOptions))
        {
        }

        var model = new UnlockViewModel();
        model.ApplyPreferences(new AppPreferences { LastVaultPath = _vaultPath });

        Assert.False(model.IsCreateMode);
        Assert.Equal(_vaultPath, model.VaultPath);
        return null;
    });

    [Fact]
    public Task ApplyPreferencesFallsBackWhenNoVaultExists() => Headless.RunAsync<object?>(async () =>
    {
        var defaultPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "AegisVault",
            "vault.aegis");

        var model = new UnlockViewModel();
        model.ApplyPreferences(new AppPreferences { LastVaultPath = Path.Combine(_directory, "missing.aegis") });

        Assert.Equal(
            File.Exists(defaultPath) ? UnlockViewModel.OpenMode : UnlockViewModel.CreateMode,
            model.ModeIndex);
        return null;
    });

    [Fact]
    public Task CreateAppendsAegisExtension() => Headless.RunAsync<object?>(async () =>
    {
        var model = new UnlockViewModel
        {
            NewVaultPath = Path.Combine(_directory, "myvault"),
            ModeIndex = UnlockViewModel.CreateMode,
            MasterPassword = "master password",
            ConfirmPassword = "master password",
        };

        VaultService? created = null;
        model.VaultOpened += vault => created = vault;

        await model.CreateCommand.ExecuteAsync(null);

        Assert.Null(model.ErrorMessage);
        Assert.NotNull(created);
        Assert.EndsWith(".aegis", created!.VaultPath, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(Path.Combine(_directory, "myvault.aegis")));
        created.Dispose();
        return null;
    });

    [Fact]
    public Task CreateRejectsExistingFile() => Headless.RunAsync<object?>(async () =>
    {
        var existing = Path.Combine(_directory, "existing.aegis");
        File.WriteAllText(existing, "not a vault");

        var model = new UnlockViewModel
        {
            NewVaultPath = existing,
            ModeIndex = UnlockViewModel.CreateMode,
            MasterPassword = "master password",
            ConfirmPassword = "master password",
        };

        await model.CreateCommand.ExecuteAsync(null);

        Assert.Equal(Localization.Loc.T("Unlock_ErrorFileExists"), model.NewVaultPathError);
        return null;
    });

    [Fact]
    public void ValidateOpenReportsEveryProblemAtOnce()
    {
        var errors = UnlockViewModel.ValidateOpen(null, "   ");

        Assert.Equal(Localization.Loc.T("Unlock_ErrorPasswordRequired"), errors.Password);
        Assert.Null(errors.Confirm);
        Assert.Equal(Localization.Loc.T("Unlock_ErrorPathRequired"), errors.Path);
    }

    [Fact]
    public void ValidateOpenAcceptsAValidForm()
    {
        File.WriteAllText(_vaultPath, "exists");

        var errors = UnlockViewModel.ValidateOpen("secret", _vaultPath);

        Assert.Null(errors.Password);
        Assert.Null(errors.Path);
    }

    [Fact]
    public void ValidateCreateReportsEveryProblemAtOnce()
    {
        var errors = UnlockViewModel.ValidateCreate("short", "different", "   ");

        Assert.Equal(Localization.Loc.T("Unlock_ErrorPasswordTooShort"), errors.Password);
        Assert.Equal(Localization.Loc.T("Unlock_ErrorConfirmMismatch"), errors.Confirm);
        Assert.Equal(Localization.Loc.T("Unlock_ErrorPathRequired"), errors.Path);
    }

    [Fact]
    public Task ValidationErrorsLandOnTheirOwnFields() => Headless.Run(() =>
    {
        var model = new UnlockViewModel
        {
            MasterPassword = string.Empty,
            VaultPath = Path.Combine(_directory, "missing.aegis"),
        };

        model.UnlockCommand.Execute(null);

        // The password prompt belongs under the password box; the missing-file
        // problem under the path box; nothing in the shared bottom line.
        Assert.Equal(Localization.Loc.T("Unlock_ErrorPasswordRequired"), model.PasswordError);
        Assert.Equal(Localization.Loc.T("Unlock_ErrorVaultNotFound"), model.PathError);
        Assert.Null(model.ErrorMessage);

        // Editing one field clears only its own error.
        model.MasterPassword = "secret";
        Assert.Null(model.PasswordError);
        Assert.NotNull(model.PathError);
    });

    [Fact]
    public Task EditingAFieldClearsTheError() => Headless.RunAsync<object?>(async () =>
    {
        var model = new UnlockViewModel
        {
            VaultPath = Path.Combine(_directory, "missing.aegis"),
            MasterPassword = string.Empty,
        };

        await model.UnlockCommand.ExecuteAsync(null);
        Assert.NotNull(model.PasswordError);
        Assert.NotNull(model.PathError);

        // Editing a field clears only its own error.
        model.MasterPassword = "correct horse";
        Assert.Null(model.PasswordError);
        Assert.NotNull(model.PathError);

        await model.UnlockCommand.ExecuteAsync(null);
        Assert.NotNull(model.PathError);

        model.VaultPath = _vaultPath;
        Assert.Null(model.PathError);

        model.NewVaultPath = Path.Combine(_directory, "elsewhere");
        Assert.Null(model.NewVaultPathError);
        model.ConfirmPassword = "something";
        Assert.Null(model.ConfirmPasswordError);
        return null;
    });

    [Fact]
    public void BusyTextFollowsTheMode()
    {
        var model = new UnlockViewModel { ModeIndex = UnlockViewModel.CreateMode };
        Assert.Equal(Localization.Loc.T("Unlock_BusyCreating"), model.BusyText);

        model.ModeIndex = UnlockViewModel.OpenMode;
        Assert.Equal(Localization.Loc.T("Unlock_BusyUnlocking"), model.BusyText);
    }

    [Fact]
    public Task FirstRunIsDetectedWithoutAKnownVault() => Headless.RunAsync<object?>(async () =>
    {
        var emptyDefault = Path.Combine(_directory, "default", "vault.aegis");

        var fresh = new UnlockViewModel(emptyDefault);
        fresh.ApplyPreferences(new AppPreferences());
        Assert.True(fresh.IsFirstRun);

        using (VaultService.CreateNew(_vaultPath, "master password"u8, FastOptions))
        {
        }

        var known = new UnlockViewModel(emptyDefault);
        known.ApplyPreferences(new AppPreferences { LastVaultPath = _vaultPath });
        Assert.False(known.IsFirstRun);

        var withDefaultVault = new UnlockViewModel(_vaultPath);
        withDefaultVault.ApplyPreferences(new AppPreferences());
        Assert.False(withDefaultVault.IsFirstRun);

        // A usable recent vault also means "not first run": the welcome block
        // and the recent-vault shortcuts must not show up together.
        var withRecentVault = new UnlockViewModel(emptyDefault);
        withRecentVault.ApplyPreferences(new AppPreferences { RecentVaultPaths = [_vaultPath] });
        Assert.False(withRecentVault.IsFirstRun);
        Assert.NotEmpty(withRecentVault.RecentVaultItems);

        var withStaleRecentVault = new UnlockViewModel(emptyDefault);
        withStaleRecentVault.ApplyPreferences(
            new AppPreferences { RecentVaultPaths = [Path.Combine(_directory, "gone.aegis")] });
        Assert.True(withStaleRecentVault.IsFirstRun);
        return null;
    });

    [Fact]
    public void PathHintShowsTheAppendedExtension()
    {
        var model = new UnlockViewModel { NewVaultPath = Path.Combine(_directory, "myvault") };
        Assert.Contains("myvault.aegis", model.NewVaultPathHint);

        model.NewVaultPath = Path.Combine(_directory, "myvault.aegis");
        Assert.Equal(string.Empty, model.NewVaultPathHint);
    }

    [Fact]
    public Task RecentVaultsFilterMissingFilesAndSelect() => Headless.RunAsync<object?>(async () =>
    {
        using (VaultService.CreateNew(_vaultPath, "master password"u8, FastOptions))
        {
        }

        var missing = Path.Combine(_directory, "gone.aegis");
        var model = new UnlockViewModel();
        model.ApplyPreferences(new AppPreferences { RecentVaultPaths = [missing, _vaultPath] });

        var item = Assert.Single(model.RecentVaultItems);
        Assert.Equal(_vaultPath, item.Path);
        Assert.Equal("vault.aegis", item.FileName);
        Assert.True(model.HasRecentVaultItems);

        model.SelectRecentVaultCommand.Execute(item);

        Assert.Equal(UnlockViewModel.OpenMode, model.ModeIndex);
        Assert.Equal(_vaultPath, model.VaultPath);
        Assert.Null(model.ErrorMessage);
        return null;
    });

    [Fact]
    public void DroppedFileMustBeAVault()
    {
        var model = new UnlockViewModel();

        Assert.False(model.TryAcceptDroppedFile(Path.Combine(_directory, "notes.txt")));
        Assert.Equal(Localization.Loc.T("Unlock_ErrorNotVaultFile"), model.PathError);

        var vault = Path.Combine(_directory, "dropped.aegis");
        Assert.True(model.TryAcceptDroppedFile($"  {vault}  "));
        Assert.Equal(vault, model.VaultPath);
        Assert.Equal(UnlockViewModel.OpenMode, model.ModeIndex);
        Assert.Null(model.PathError);
        Assert.Null(model.ErrorMessage);
    }
}
