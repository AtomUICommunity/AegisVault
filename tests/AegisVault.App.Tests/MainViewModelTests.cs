using AegisVault.App.Localization;
using AegisVault.App.Services;
using AegisVault.App.ViewModels;
using AegisVault.App.Views;
using AegisVault.Core.Models;
using AegisVault.Core.Services;
using Xunit;

namespace AegisVault.App.Tests;

public sealed class MainViewModelTests : IDisposable
{
    private static readonly byte[] Password = "master password"u8.ToArray();

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

    public MainViewModelTests()
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
    public Task LoadsEntriesAndFiltersBySearch() => Headless.Run(() =>
    {
        using var vault = CreateVaultWithEntries();
        using var viewModel = new MainViewModel(vault);

        Assert.Equal(2, viewModel.FilteredEntries.Count);

        viewModel.SearchText = "git";
        Assert.Single(viewModel.FilteredEntries);
        Assert.Equal("GitHub", viewModel.FilteredEntries[0].Title);

        viewModel.SearchText = string.Empty;
        Assert.Equal(2, viewModel.FilteredEntries.Count);
    });

    [Fact]
    public Task EmptyVaultShowsWelcomeHeroInsteadOfHints() => Headless.Run(() =>
    {
        var vault = VaultService.CreateNew(_vaultPath, Password, FastOptions);
        using var viewModel = new MainViewModel(vault);

        Assert.True(viewModel.IsVaultEmpty);
        Assert.False(viewModel.ShowListEmptyHint);
        Assert.False(viewModel.ShowSelectEntryHint);

        viewModel.AddEntryCommand.Execute(null);

        Assert.False(viewModel.IsVaultEmpty);
        Assert.False(viewModel.ShowSelectEntryHint);

        viewModel.SelectedEntry = null;
        Assert.False(viewModel.ShowSelectEntryHint);
        Assert.True(viewModel.ShowStartupGuide);

        viewModel.StartupGuideDismissed = true;
        Assert.True(viewModel.ShowSelectEntryHint);
        Assert.False(viewModel.ShowStartupGuide);
    });

    [Fact]
    public Task StartupGuideDismissalIsReportedOnce() => Headless.Run(() =>
    {
        using var vault = CreateVaultWithEntries();
        using var viewModel = new MainViewModel(vault);

        var dismissals = 0;
        viewModel.AttachStartupGuideCallback(() => dismissals++);

        Assert.True(viewModel.ShowStartupGuide);

        viewModel.DismissStartupGuideCommand.Execute(null);

        Assert.True(viewModel.StartupGuideDismissed);
        Assert.False(viewModel.ShowStartupGuide);
        Assert.True(viewModel.ShowSelectEntryHint);
        Assert.Equal(1, dismissals);
    });

    [Fact]
    public Task StartupGuideYieldsToEntrySelection() => Headless.Run(() =>
    {
        using var vault = CreateVaultWithEntries();
        using var viewModel = new MainViewModel(vault);

        viewModel.SelectedEntry = viewModel.FilteredEntries[0];
        Assert.False(viewModel.ShowStartupGuide);

        viewModel.SelectedEntry = null;
        Assert.True(viewModel.ShowStartupGuide);

        viewModel.StartupGuideDismissed = true;
        Assert.False(viewModel.ShowStartupGuide);
    });

    [Fact]
    public Task HeroImportAddsEntriesAndReloads() => Headless.RunAsync<object?>(async () =>
    {
        using var vault = CreateVaultWithEntries();
        using var viewModel = new MainViewModel(vault);

        var csv = Path.Combine(_directory, "import.csv");
        File.WriteAllText(csv, "name,username,password,url\nImported,user,pw,https://example.com\n");

        var result = await viewModel.ImportCsvFromAsync(csv);

        Assert.Equal(1, result.Imported);
        Assert.Equal(3, viewModel.Entries.Count);
        Assert.Contains(viewModel.FilteredEntries, entry => entry.Title == "Imported");
        return null;
    });

    [Fact]
    public Task ToggleFavoriteUsesTheInjectedClock() => Headless.Run(() =>
    {
        var now = new DateTimeOffset(2026, 9, 18, 10, 30, 0, TimeSpan.Zero);
        using var vault = CreateVaultWithEntries();
        using var viewModel = new MainViewModel(vault, timeProvider: new FixedTimeProvider(now));

        viewModel.SelectedEntry = vault.Entries.Single(entry => entry.Title == "GitHub");
        viewModel.ToggleFavoriteCommand.Execute(null);

        // The view model's working copy uses the injected clock (the vault
        // re-stamps on persist, which is a separate concern).
        Assert.NotNull(viewModel.SelectedEntry);
        Assert.Equal(now, viewModel.SelectedEntry!.UpdatedAt);
    });

    [Fact]
    public Task TagsAreDeduplicatedIgnoringCase() => Headless.Run(() =>
    {
        using var vault = VaultService.CreateNew(_vaultPath, Password, FastOptions);
        using var viewModel = new MainViewModel(vault);

        viewModel.AddEntryCommand.Execute(null);
        viewModel.EditTitle = "Tagged";
        viewModel.EditTags = "Work, work, WORK";
        viewModel.SaveEntryCommand.Execute(null);

        Assert.Single(vault.Entries.Single(entry => entry.Title == "Tagged").Tags);
    });

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    [Fact]
    public Task ToggleTotpRevealFlipsTheFlag() => Headless.Run(() =>
    {
        using var vault = CreateVaultWithEntries();
        using var viewModel = new MainViewModel(vault);

        Assert.False(viewModel.IsTotpSecretRevealed);

        viewModel.ToggleTotpRevealCommand.Execute(null);
        Assert.True(viewModel.IsTotpSecretRevealed);

        viewModel.ToggleTotpRevealCommand.Execute(null);
        Assert.False(viewModel.IsTotpSecretRevealed);
    });

    [Fact]
    public Task DebouncedSearchWaitsForTheTypingPause() => Headless.Run(() =>
    {
        using var vault = CreateVaultWithEntries();
        using var viewModel = new MainViewModel(vault, searchDebounce: TimeSpan.FromMilliseconds(200));

        var baseline = viewModel.FilteredEntries.Count;
        viewModel.SearchText = "GitHub";

        // The debounce postpones the re-filter; clearing the box applies at once.
        Assert.Equal(baseline, viewModel.FilteredEntries.Count);
        viewModel.SearchText = string.Empty;
        Assert.Equal(baseline, viewModel.FilteredEntries.Count);
    });

    [Fact]
    public Task FilteringReplacesTheListInOneStep() => Headless.Run(() =>
    {
        using var vault = CreateVaultWithEntries();
        using var viewModel = new MainViewModel(vault);

        var before = viewModel.FilteredEntries;
        viewModel.SearchText = "GitHub";

        // The bound list is replaced wholesale instead of Clear+Add per entry.
        Assert.NotSame(before, viewModel.FilteredEntries);
        Assert.Single(viewModel.FilteredEntries);
    });

    [Fact]
    public Task HealthAnalysisAfterChangesReflectsTheLatestEntries() => Headless.RunAsync<object?>(async () =>
    {
        using var vault = VaultService.CreateNew(_vaultPath, Password, FastOptions);
        vault.AddEntry(new PasswordEntry { Title = "Strong", Password = "correct horse battery staple 42!" });
        using var viewModel = new MainViewModel(vault);

        // Change the vault while the constructor's analysis may still be
        // running: the newest analysis must win, a stale backfill must not
        // overwrite it.
        vault.AddEntry(new PasswordEntry { Title = "Weak", Password = "123456" });
        viewModel.ReloadFromVault();

        await viewModel.HealthAnalysis;

        Assert.Equal(1, viewModel.Health.WeakCount);
        return null;
    });

    [Fact]
    public Task ListEmptyHintOnlyShowsForNonEmptyVaults() => Headless.Run(() =>
    {
        using var vault = CreateVaultWithEntries();
        using var viewModel = new MainViewModel(vault);

        Assert.False(viewModel.ShowListEmptyHint);

        viewModel.SearchText = "no-such-entry";

        Assert.True(viewModel.ShowListEmptyHint);
        Assert.Empty(viewModel.FilteredEntries);

        viewModel.SearchText = string.Empty;

        Assert.False(viewModel.ShowListEmptyHint);
    });

    [Fact]
    public Task SavesExtraCredentialFields() => Headless.Run(() =>
    {
        using var vault = CreateVaultWithEntries();
        using var viewModel = new MainViewModel(vault);

        viewModel.SelectedEntry = viewModel.FilteredEntries.Single(entry => entry.Title == "GitHub");
        viewModel.EditPhone = "13800000000";
        viewModel.EditEmail = "dev-work@example.com";
        viewModel.EditAppId = "cli_123";
        viewModel.EditSecret = "shh-value";
        viewModel.EditApiKey = "key-value";
        viewModel.SaveEntryCommand.Execute(null);

        var saved = vault.Entries.Single(entry => entry.Title == "GitHub");
        Assert.Equal("13800000000", saved.Phone);
        Assert.Equal("dev-work@example.com", saved.Email);
        Assert.Equal("cli_123", saved.AppId);
        Assert.Equal("shh-value", saved.Secret);
        Assert.Equal("key-value", saved.ApiKey);
    });

    [Fact]
    public Task SearchMatchesPhoneAndAppIdButNeverSecrets() => Headless.Run(() =>
    {
        using var vault = CreateVaultWithEntries();
        using var viewModel = new MainViewModel(vault);

        viewModel.SelectedEntry = viewModel.FilteredEntries.Single(entry => entry.Title == "GitHub");
        viewModel.EditPhone = "13800000000";
        viewModel.EditEmail = "dev-work@example.com";
        viewModel.EditAppId = "cli_123";
        viewModel.EditSecret = "super-secret-value";
        viewModel.EditApiKey = "api-key-value";
        viewModel.SaveEntryCommand.Execute(null);

        viewModel.SearchText = "13800000000";
        Assert.Single(viewModel.FilteredEntries);

        viewModel.SearchText = "dev-work@example.com";
        Assert.Single(viewModel.FilteredEntries);

        viewModel.SearchText = "cli_123";
        Assert.Single(viewModel.FilteredEntries);

        viewModel.SearchText = "super-secret-value";
        Assert.Empty(viewModel.FilteredEntries);

        viewModel.SearchText = "api-key-value";
        Assert.Empty(viewModel.FilteredEntries);
    });

    [Fact]
    public Task SecretAndApiKeyPreviewsStayMaskedUntilRevealed() => Headless.Run(() =>
    {
        using var vault = CreateVaultWithEntries();
        using var viewModel = new MainViewModel(vault);

        viewModel.SelectedEntry = viewModel.FilteredEntries.Single(entry => entry.Title == "GitHub");
        viewModel.EditSecret = "shh-value";
        viewModel.EditApiKey = "key-value";

        Assert.DoesNotContain("shh", viewModel.SecretPreview);
        Assert.DoesNotContain("key", viewModel.ApiKeyPreview);

        viewModel.ToggleSecretRevealCommand.Execute(null);
        viewModel.ToggleApiKeyRevealCommand.Execute(null);

        Assert.Equal("shh-value", viewModel.SecretPreview);
        Assert.Equal("key-value", viewModel.ApiKeyPreview);

        viewModel.SelectedEntry = null;

        Assert.All(viewModel.SecretPreview, character => Assert.Equal('●', character));
        Assert.All(viewModel.ApiKeyPreview, character => Assert.Equal('●', character));
    });

    [Fact]
    public Task CreatesAndRecolorsCategories() => Headless.Run(() =>
    {
        using var vault = CreateVaultWithEntries();
        using var viewModel = new MainViewModel(vault);

        Assert.True(viewModel.TryCreateCategory("Work", out _, "blue"));
        var created = viewModel.UserCategories.Single(item => item.DisplayName == "Work");
        Assert.Equal("blue", created.Color);

        viewModel.SetCategoryColor(created.CategoryId!.Value, "#12ab34");
        Assert.Equal("#12AB34", viewModel.UserCategories.Single(item => item.DisplayName == "Work").Color);

        Assert.True(viewModel.TryCreateCategory("Loose", out _));
        Assert.Equal(string.Empty, viewModel.UserCategories.Single(item => item.DisplayName == "Loose").Color);
    });

    [Fact]
    public Task SelectingEntryPopulatesEditorAndSaves() => Headless.Run(() =>
    {
        using var vault = CreateVaultWithEntries();
        using var viewModel = new MainViewModel(vault);

        viewModel.SelectedEntry = viewModel.FilteredEntries.Single(entry => entry.Title == "GitHub");
        Assert.Equal("octocat", viewModel.EditUsername);

        viewModel.EditTitle = "GitHub Work";
        viewModel.SaveEntryCommand.Execute(null);

        Assert.Contains(vault.Entries, entry => entry.Title == "GitHub Work");
    });

    [Fact]
    public Task AddAndDeleteEntry() => Headless.Run(() =>
    {
        using var vault = CreateVaultWithEntries();
        using var viewModel = new MainViewModel(vault);

        viewModel.AddEntryCommand.Execute(null);
        Assert.Equal(3, viewModel.FilteredEntries.Count);
        Assert.NotNull(viewModel.SelectedEntry);

        viewModel.DeleteEntryCommand.Execute(null);
        Assert.Equal(2, viewModel.FilteredEntries.Count);
        Assert.Null(viewModel.SelectedEntry);
    });

    [Fact]
    public Task DeleteMovesEntryToRecycleBinAndUndoRestoresIt() => Headless.Run(() =>
    {
        using var vault = CreateVaultWithEntries();
        using var viewModel = new MainViewModel(vault);

        viewModel.SelectedEntry = vault.Entries.Single(entry => entry.Title == "GitHub");
        viewModel.DeleteEntryCommand.Execute(null);

        Assert.Single(viewModel.FilteredEntries);
        Assert.True(viewModel.CanUndoDelete);
        Assert.Single(vault.DeletedEntries);
        Assert.Equal("GitHub", vault.DeletedEntries[0].Title);

        viewModel.UndoDeleteCommand.Execute(null);

        Assert.False(viewModel.CanUndoDelete);
        Assert.Empty(vault.DeletedEntries);
        Assert.Equal(2, viewModel.FilteredEntries.Count);
    });

    [Fact]
    public Task RecycleBinRowAppearsOnlyWhenNotEmpty() => Headless.Run(() =>
    {
        using var vault = CreateVaultWithEntries();
        using var viewModel = new MainViewModel(vault);

        Assert.DoesNotContain(viewModel.Categories, category => category.Key == "recycle");

        vault.DeleteEntry(vault.Entries.First().Id);
        viewModel.ReloadFromVault();

        var bin = Assert.Single(viewModel.Categories, category => category.Key == "recycle");
        Assert.Equal(1, bin.Count);

        viewModel.SelectViewCommand.Execute("recycle");

        Assert.True(viewModel.IsViewRecycleBin);
        Assert.Equal("GitHub", Assert.Single(viewModel.FilteredEntries).Title);
    });

    [Fact]
    public Task RestoreSelectedEntryFromRecycleBin() => Headless.Run(() =>
    {
        using var vault = CreateVaultWithEntries();
        using var viewModel = new MainViewModel(vault);

        vault.DeleteEntry(vault.Entries.First(entry => entry.Title == "Mail").Id);
        viewModel.ReloadFromVault();
        viewModel.SelectViewCommand.Execute("recycle");

        var recycled = Assert.Single(viewModel.FilteredEntries);
        viewModel.SelectedEntry = recycled;
        Assert.Equal("Mail", recycled.Title);

        viewModel.RestoreSelectedEntryCommand.Execute(null);

        Assert.False(viewModel.IsViewRecycleBin);
        Assert.Empty(vault.DeletedEntries);
        Assert.Contains(vault.Entries, entry => entry.Title == "Mail");
    });

    [Fact]
    public Task EmptyRecycleBinReturnsToTheLiveView() => Headless.Run(() =>
    {
        using var vault = CreateVaultWithEntries();
        using var viewModel = new MainViewModel(vault);

        vault.DeleteEntry(vault.Entries.First().Id);
        vault.DeleteEntry(vault.Entries.First().Id);
        viewModel.ReloadFromVault();
        viewModel.SelectViewCommand.Execute("recycle");
        Assert.Equal(2, viewModel.FilteredEntries.Count);

        Assert.Equal(2, viewModel.EmptyRecycleBin());

        Assert.Empty(vault.DeletedEntries);
        Assert.DoesNotContain(viewModel.Categories, category => category.Key == "recycle");
        Assert.False(viewModel.IsViewRecycleBin);
    });

    [Fact]
    public Task RecycleBinHidesTheWelcomeHeroForAnEmptiedVault() => Headless.Run(() =>
    {
        using var vault = VaultService.CreateNew(_vaultPath, Password, FastOptions);
        var only = vault.AddEntry(new PasswordEntry { Title = "Only" });

        using var viewModel = new MainViewModel(vault);
        Assert.False(viewModel.IsVaultEmpty);
        Assert.False(viewModel.ShowVaultHero);

        vault.DeleteEntry(only.Id);
        viewModel.ReloadFromVault();
        viewModel.SelectViewCommand.Execute("recycle");

        Assert.True(viewModel.IsVaultEmpty);
        Assert.False(viewModel.ShowVaultHero);
        Assert.Equal("Only", Assert.Single(viewModel.FilteredEntries).Title);
    });

    [Fact]
    public Task DeleteThenOpenBinThenEmpty() => Headless.Run(() =>
    {
        using var vault = CreateVaultWithEntries();
        using var viewModel = new MainViewModel(vault);

        viewModel.SelectedEntry = vault.Entries.First();
        viewModel.DeleteEntryCommand.Execute(null);
        viewModel.SelectViewCommand.Execute("recycle");

        Assert.True(viewModel.IsViewRecycleBin);
        Assert.Single(viewModel.FilteredEntries);

        var purged = viewModel.EmptyRecycleBin();

        Assert.Equal(1, purged);
        Assert.Empty(vault.DeletedEntries);
        Assert.DoesNotContain(viewModel.Categories, category => category.Key == "recycle");
    });

    [Fact]
    public Task RecentViewTracksOpenedEntriesAndLimitsToTen() => Headless.Run(() =>
    {
        var start = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var clock = new MutableTimeProvider(start);
        using var vault = VaultService.CreateNew(_vaultPath, Password, FastOptions, clock);
        for (var i = 1; i <= 12; i++)
        {
            vault.AddEntry(new PasswordEntry { Title = $"E{i:D2}" });
        }

        using var viewModel = new MainViewModel(vault, timeProvider: clock);
        Assert.DoesNotContain(viewModel.Categories, category => category.Key == "recent");

        for (var i = 1; i <= 12; i++)
        {
            clock.Now = start.AddMinutes(i);
            viewModel.SelectedEntry = vault.Entries.Single(entry => entry.Title == $"E{i:D2}");
        }

        // Opening entries adds the row and never touches UpdatedAt.
        Assert.Contains(viewModel.Categories, category => category.Key == "recent");
        Assert.All(vault.Entries, entry => Assert.NotNull(entry.LastOpenedAt));
        Assert.All(vault.Entries, entry => Assert.Equal(start, entry.UpdatedAt));

        viewModel.SelectViewCommand.Execute("recent");

        Assert.True(viewModel.IsViewRecent);
        Assert.Equal(10, viewModel.FilteredEntries.Count);
        Assert.Equal("E12", viewModel.FilteredEntries[0].Title);
        Assert.DoesNotContain(viewModel.FilteredEntries, entry => entry.Title is "E01" or "E02");
    });

    [Fact]
    public Task SelectingAnEntryDoesNotChangeItsUpdatedAt() => Headless.Run(() =>
    {
        using var vault = CreateVaultWithEntries();
        using var viewModel = new MainViewModel(vault);

        var entry = vault.Entries.Single(candidate => candidate.Title == "GitHub");
        viewModel.SelectedEntry = entry;

        var reloaded = vault.Entries.Single(candidate => candidate.Id == entry.Id);
        Assert.NotNull(reloaded.LastOpenedAt);
        Assert.Equal(entry.UpdatedAt, reloaded.UpdatedAt);
    });

    [Fact]
    public Task RecycledEntriesAreNotMarkedAsOpened() => Headless.Run(() =>
    {
        using var vault = CreateVaultWithEntries();
        using var viewModel = new MainViewModel(vault);

        vault.DeleteEntry(vault.Entries.First().Id);
        viewModel.ReloadFromVault();
        viewModel.SelectViewCommand.Execute("recycle");
        viewModel.SelectedEntry = viewModel.FilteredEntries[0];

        Assert.All(vault.DeletedEntries, entry => Assert.Null(entry.LastOpenedAt));
        Assert.DoesNotContain(viewModel.Categories, category => category.Key == "recent");
    });

    [Fact]
    public Task ComputesTotpForSelectedEntry() => Headless.Run(() =>
    {
        using var vault = CreateVaultWithEntries();
        using var viewModel = new MainViewModel(vault);

        viewModel.SelectedEntry = vault.Entries.Single(entry => entry.Title == "GitHub");
        viewModel.EditTotpSecret = "JBSWY3DPEHPK3PXP";

        Assert.True(viewModel.HasTotp);
        Assert.Equal(6, viewModel.TotpCode.Length);
        Assert.InRange(viewModel.TotpRemaining, 1, 30);
    });

    [Fact]
    public Task InvalidTotpSecretShowsPlaceholder() => Headless.Run(() =>
    {
        using var vault = CreateVaultWithEntries();
        using var viewModel = new MainViewModel(vault);

        viewModel.SelectedEntry = vault.Entries.First();
        viewModel.EditTotpSecret = "not valid base32!";

        Assert.Equal("无效密钥", viewModel.TotpCode);
    });

    [Fact]
    public Task IsTotpValidReflectsCode() => Headless.Run(() =>
    {
        using var vault = CreateVaultWithEntries();
        using var viewModel = new MainViewModel(vault);

        viewModel.SelectedEntry = vault.Entries.Single(entry => entry.Title == "GitHub");
        viewModel.EditTotpSecret = "JBSWY3DPEHPK3PXP";
        Assert.True(viewModel.IsTotpValid);

        viewModel.EditTotpSecret = "not valid base32!";
        Assert.False(viewModel.IsTotpValid);
    });

    [Fact]
    public Task MainWindowCanBeConstructedWithViewModel() => Headless.Run(() =>
    {
        using var vault = CreateVaultWithEntries();
        using var viewModel = new MainViewModel(vault);
        var window = new MainWindow { DataContext = viewModel };

        Assert.Equal(Loc.T("App_Title"), window.Title);
    });

    [Fact]
    public Task CategoriesIncludeFavoritesAndTags() => Headless.Run(() =>
    {
        using var vault = CreateVaultWithEntries();
        using var viewModel = new MainViewModel(vault);

        Assert.Contains(viewModel.Categories, category => category.Key == "all" && category.Count == 2);
        Assert.Contains(viewModel.Categories, category => category.IsFavorites && category.Count == 0);
        Assert.Contains(viewModel.Categories, category => category.Tag == "work" && category.Count == 1);
    });

    [Fact]
    public Task ToggleFavoriteUpdatesEntryAndFilter() => Headless.Run(() =>
    {
        using var vault = CreateVaultWithEntries();
        using var viewModel = new MainViewModel(vault);

        viewModel.SelectedEntry = vault.Entries.Single(entry => entry.Title == "GitHub");
        viewModel.ToggleFavoriteCommand.Execute(null);

        Assert.True(vault.Entries.Single(entry => entry.Title == "GitHub").IsFavorite);
        Assert.Contains(viewModel.Categories, category => category.IsFavorites && category.Count == 1);

        viewModel.SelectedCategory = viewModel.Categories.Single(category => category.IsFavorites);
        Assert.Single(viewModel.FilteredEntries);
        Assert.Equal("GitHub", viewModel.FilteredEntries[0].Title);
    });

    [Fact]
    public Task BeginEditAndCancelRestoresFields() => Headless.Run(() =>
    {
        using var vault = CreateVaultWithEntries();
        using var viewModel = new MainViewModel(vault);

        viewModel.SelectedEntry = vault.Entries.Single(entry => entry.Title == "GitHub");
        viewModel.BeginEditCommand.Execute(null);
        Assert.True(viewModel.IsEditing);

        viewModel.EditTitle = "changed";
        viewModel.CancelEditCommand.Execute(null);

        Assert.False(viewModel.IsEditing);
        Assert.Equal("GitHub", viewModel.EditTitle);
    });

    [Fact]
    public Task CopyPasswordUsesClipboardService() => Headless.RunAsync<object?>(async () =>
    {
        var fake = new FakeClipboardAccess();
        using var clipboard = new ClipboardService(fake, () => new UserConfig());

        using var vault = CreateVaultWithEntries();
        using var viewModel = new MainViewModel(vault, clipboard);

        viewModel.SelectedEntry = vault.Entries.Single(entry => entry.Title == "GitHub");
        await viewModel.CopyPasswordCommand.ExecuteAsync(null);

        Assert.Equal("s3cret", fake.Text);
        return null;
    });

    [Fact]
    public Task ReportsPasswordStrengthForEditedEntry() => Headless.Run(() =>
    {
        using var vault = CreateVaultWithEntries();
        using var viewModel = new MainViewModel(vault);

        viewModel.SelectedEntry = vault.Entries.Single(entry => entry.Title == "GitHub");

        Assert.True(viewModel.EditPasswordStrengthPercent > 0);
        Assert.False(string.IsNullOrEmpty(viewModel.EditPasswordStrengthSummary));
    });

    [Fact]
    public Task CategoryCreateAssignFilterRenameDelete() => Headless.Run(() =>
    {
        using var vault = CreateVaultWithEntries();
        using var viewModel = new MainViewModel(vault);

        Assert.True(viewModel.TryCreateCategory("Work", out var error));
        Assert.Null(error);

        var created = viewModel.Categories.Single(category => category.DisplayName == "Work");
        Assert.True(created.IsUserCategory);
        Assert.Equal(0, created.Count);

        viewModel.SelectedEntry = viewModel.FilteredEntries.Single(entry => entry.Title == "GitHub");
        viewModel.BeginEditCommand.Execute(null);
        viewModel.SelectedCategoryChoice = viewModel.CategoryChoices.Single(choice => choice.Name == "Work");
        viewModel.SaveEntryCommand.Execute(null);

        Assert.Equal(created.CategoryId, vault.Entries.Single(entry => entry.Title == "GitHub").CategoryId);
        var refreshed = viewModel.Categories.Single(category => category.DisplayName == "Work");
        Assert.Equal(1, refreshed.Count);

        viewModel.SelectedCategory = refreshed;
        Assert.Single(viewModel.FilteredEntries);
        Assert.Equal("GitHub", viewModel.FilteredEntries[0].Title);

        Assert.True(viewModel.TryRenameCategory(refreshed.CategoryId!.Value, "Job", out var renameError));
        Assert.Null(renameError);
        Assert.Contains(viewModel.Categories, category => category.DisplayName == "Job");

        Assert.False(viewModel.TryCreateCategory("job", out var duplicateError));
        Assert.NotNull(duplicateError);
        Assert.False(viewModel.TryCreateCategory("   ", out var emptyError));
        Assert.NotNull(emptyError);

        viewModel.DeleteCategory(refreshed.CategoryId!.Value);
        Assert.DoesNotContain(viewModel.Categories, category => category.CategoryId == refreshed.CategoryId);
        Assert.Null(vault.Entries.Single(entry => entry.Title == "GitHub").CategoryId);
    });

    [Fact]
    public Task CategoryTreeNodesExposeBranchCountsAndFilterTheWholeBranch() => Headless.Run(() =>
    {
        using var vault = CreateVaultWithEntries();
        var root = vault.AddCategory("Work");
        var child = vault.AddCategory("Servers", parentId: root.Id);
        var grandChild = vault.AddCategory("Production", parentId: child.Id);

        var workEntry = vault.AddEntry(new PasswordEntry { Title = "Work item", CategoryId = root.Id });
        var serverEntry = vault.AddEntry(new PasswordEntry { Title = "Server item", CategoryId = child.Id });
        var prodEntry = vault.AddEntry(new PasswordEntry { Title = "Prod item", CategoryId = grandChild.Id });

        using var viewModel = new MainViewModel(vault);

        var userRows = viewModel.CategoryRows.Where(row => row.IsUserCategory).ToList();
        Assert.Equal(["Work", "Servers", "Production"], userRows.Select(row => row.Item.DisplayName));

        var rootRow = userRows[0];
        Assert.Equal(3, rootRow.Item.Count);
        Assert.True(rootRow.HasChildren);
        Assert.Equal(1, rootRow.Depth);

        var childRow = userRows[1];
        Assert.Equal(2, childRow.Item.Count);
        Assert.Equal(2, childRow.Depth);

        var grandChildRow = userRows[2];
        Assert.Equal(1, grandChildRow.Item.Count);
        Assert.Equal(3, grandChildRow.Depth);

        // Selecting the root filters the whole branch, not just its direct entries.
        viewModel.SelectedCategoryRow = rootRow;

        var titles = viewModel.FilteredEntries.Select(entry => entry.Title).OrderBy(title => title).ToList();
        Assert.Equal(["Prod item", "Server item", "Work item"], titles);
        Assert.DoesNotContain(viewModel.FilteredEntries, entry => entry.Title == "GitHub");
        Assert.Contains(workEntry.Id, viewModel.FilteredEntries.Select(entry => entry.Id));
        Assert.Contains(serverEntry.Id, viewModel.FilteredEntries.Select(entry => entry.Id));
        Assert.Contains(prodEntry.Id, viewModel.FilteredEntries.Select(entry => entry.Id));

        // Selecting the child narrows the branch.
        viewModel.SelectedCategoryRow = childRow;
        Assert.Equal(
            ["Prod item", "Server item"],
            viewModel.FilteredEntries.Select(entry => entry.Title).OrderBy(title => title));
    });

    [Fact]
    public Task CategoryTreeKeepsNodesWhenEntriesChange() => Headless.Run(() =>
    {
        using var vault = CreateVaultWithEntries();
        var root = vault.AddCategory("Work");

        using var viewModel = new MainViewModel(vault);
        var rootRow = Assert.Single(viewModel.CategoryRows, row => row.IsUserCategory);
        Assert.Equal(0, rootRow.Item.Count);

        // Entry edits refresh the row data in place: the row instance survives so the
        // tree keeps its expansion state.
        viewModel.SelectedCategoryRow = rootRow;
        vault.AddEntry(new PasswordEntry { Title = "Later", CategoryId = root.Id });
        viewModel.ReloadFromVault();

        Assert.Same(rootRow, Assert.Single(viewModel.CategoryRows, row => row.IsUserCategory));
        Assert.Equal(1, rootRow.Item.Count);
    });

    [Fact]
    public Task CategoryTreeAddsUncategorizedRowWhenEntryLosesCategory() => Headless.Run(() =>
    {
        using var vault = VaultService.CreateNew(_vaultPath, Password, FastOptions);
        var work = vault.AddCategory("Work");
        var entry = vault.AddEntry(new PasswordEntry { Title = "Only", CategoryId = work.Id });

        using var viewModel = new MainViewModel(vault);

        // Nothing is uncategorized yet, so the pseudo row must not exist.
        Assert.DoesNotContain(viewModel.CategoryRows, row => row.Item.Key == "cat:");
        Assert.DoesNotContain(viewModel.Categories, category => category.Key == "cat:");

        // Clearing the category used to leave the sidebar stale until a restart: the
        // row's existence comes from entry data, so it must join the tree signature.
        vault.UpdateEntry(entry with { CategoryId = null });
        viewModel.ReloadFromVault();

        var uncategorized = Assert.Single(viewModel.CategoryRows, row => row.Item.Key == "cat:");
        Assert.Equal(1, uncategorized.Item.Count);
        Assert.False(uncategorized.IsUserCategory);

        // The row filters the uncategorized entries.
        viewModel.SelectedCategoryRow = uncategorized;
        Assert.Equal("Only", Assert.Single(viewModel.FilteredEntries).Title);
    });

    [Fact]
    public Task CategoryTreeDropsUncategorizedRowWhenLastEntryLeaves() => Headless.Run(() =>
    {
        using var vault = VaultService.CreateNew(_vaultPath, Password, FastOptions);
        vault.AddCategory("Work");
        var only = vault.AddEntry(new PasswordEntry { Title = "Unfiled" });

        using var viewModel = new MainViewModel(vault);
        var uncategorized = Assert.Single(viewModel.CategoryRows, row => row.Item.Key == "cat:");
        Assert.Equal(1, uncategorized.Item.Count);

        viewModel.SelectedCategoryRow = uncategorized;
        Assert.False(viewModel.IsViewAll);

        // Deleting the last uncategorized entry used to leave a stale "1" behind.
        Assert.True(vault.DeleteEntry(only.Id));
        viewModel.ReloadFromVault();

        Assert.DoesNotContain(viewModel.CategoryRows, row => row.Item.Key == "cat:");
        Assert.DoesNotContain(viewModel.Categories, category => category.Key == "cat:");
        Assert.True(viewModel.IsViewAll);
    });

    [Fact]
    public Task CategoryTreeSupportsMoveAndMerge() => Headless.Run(() =>
    {
        using var vault = CreateVaultWithEntries();
        using var viewModel = new MainViewModel(vault);

        viewModel.TryCreateCategory("Work", out _);
        viewModel.TryCreateCategory("Servers", out _);
        var work = viewModel.Categories.Single(category => category.DisplayName == "Work");
        var servers = viewModel.Categories.Single(category => category.DisplayName == "Servers");

        Assert.True(viewModel.TryMoveCategory(servers.CategoryId!.Value, work.CategoryId, out var moveError));
        Assert.Null(moveError);

        var userRows = viewModel.CategoryRows.Where(row => row.IsUserCategory).ToList();
        Assert.Equal(["Work", "Servers"], userRows.Select(row => row.Item.DisplayName));
        Assert.Equal(2, userRows[1].Depth);

        // Merging folds the child back into its parent and drops the source.
        Assert.True(viewModel.TryMergeCategory(servers.CategoryId!.Value, work.CategoryId!.Value, out var mergeError));
        Assert.Null(mergeError);
        Assert.Single(viewModel.CategoryRows, row => row.IsUserCategory);
        Assert.DoesNotContain(viewModel.Categories, category => category.DisplayName == "Servers");
    });

    [Fact]
    public Task CategoryTreeReportsLocalizedNameClashOnMoveAndMerge() => Headless.Run(() =>
    {
        using var vault = CreateVaultWithEntries();
        using var viewModel = new MainViewModel(vault);

        viewModel.TryCreateCategory("Left", out _);
        viewModel.TryCreateCategory("Right", out _);
        var left = viewModel.Categories.Single(category => category.DisplayName == "Left");
        var right = viewModel.Categories.Single(category => category.DisplayName == "Right");
        viewModel.TryCreateCategory("Shared", out _, parentId: left.CategoryId);
        viewModel.TryCreateCategory("Shared", out _, parentId: right.CategoryId);
        var leftShared = viewModel.Categories.Single(category =>
            category.DisplayName == "Shared" && category.ParentId == left.CategoryId);

        var statusBefore = viewModel.StatusMessage;

        // Moving would give "Right" two children called "Shared".
        Assert.False(viewModel.TryMoveCategory(leftShared.CategoryId!.Value, right.CategoryId, out var moveError));
        Assert.Equal(Loc.T("Main_CategoryNameDuplicate"), moveError);
        Assert.Equal(statusBefore, viewModel.StatusMessage);
        Assert.Equal(left.CategoryId, viewModel.Categories.Single(category => category.CategoryId == leftShared.CategoryId).ParentId);

        // Merging "Left" into "Right" would do the same thing to its child.
        Assert.False(viewModel.TryMergeCategory(left.CategoryId!.Value, right.CategoryId!.Value, out var mergeError));
        Assert.Equal(Loc.T("Main_CategoryNameDuplicate"), mergeError);
        Assert.Contains(viewModel.Categories, category => category.CategoryId == left.CategoryId);
    });

    [Fact]
    public Task CategoryTreeReportsLocalizedDepthLimit() => Headless.Run(() =>
    {
        using var vault = CreateVaultWithEntries();
        using var viewModel = new MainViewModel(vault);

        var branch = vault.AddCategory("Branch");
        vault.AddCategory("Leaf", parentId: branch.Id);
        var level1 = vault.AddCategory("L1");
        var level2 = vault.AddCategory("L2", parentId: level1.Id);
        var level3 = vault.AddCategory("L3", parentId: level2.Id);
        viewModel.ReloadFromVault();

        Assert.False(viewModel.TryMoveCategory(branch.Id, level3.Id, out var error));
        Assert.Equal(Loc.T("Main_CategoryTooDeep"), error);
    });

    [Fact]
    public Task DeleteCategoryReportsPromotedChildRenames() => Headless.Run(() =>
    {
        using var vault = CreateVaultWithEntries();
        using var viewModel = new MainViewModel(vault);

        viewModel.TryCreateCategory("Work", out _);
        var work = viewModel.Categories.Single(category => category.DisplayName == "Work");
        viewModel.TryCreateCategory("Legacy", out _, parentId: work.CategoryId);
        var legacy = viewModel.Categories.Single(category => category.DisplayName == "Legacy");
        viewModel.TryCreateCategory("Servers", out _, parentId: legacy.CategoryId);
        viewModel.TryCreateCategory("Servers", out _, parentId: work.CategoryId);

        viewModel.DeleteCategory(legacy.CategoryId!.Value);

        // The promoted child kept its data but had to give up its name.
        Assert.Contains("Servers (2)", viewModel.StatusMessage);
        Assert.Contains(viewModel.Categories, category => category.DisplayName == "Servers (2)");
        Assert.Contains(viewModel.Categories, category => category.DisplayName == "Servers");
        Assert.DoesNotContain(viewModel.Categories, category => category.CategoryId == legacy.CategoryId);
    });

    [Fact]
    public Task NewEntryInheritsSelectedCategory() => Headless.Run(() =>
    {
        using var vault = CreateVaultWithEntries();
        using var viewModel = new MainViewModel(vault);

        viewModel.TryCreateCategory("Work", out _);
        viewModel.SelectedCategory = viewModel.Categories.Single(category => category.DisplayName == "Work");

        viewModel.AddEntryCommand.Execute(null);

        Assert.Equal(
            viewModel.SelectedCategory!.CategoryId,
            vault.Entries.Single(entry => entry.Title == "新条目").CategoryId);
    });

    [Fact]
    public Task SortModesAndViewChipsWork() => Headless.Run(() =>
    {
        using var vault = CreateVaultWithEntries();
        using var viewModel = new MainViewModel(vault);

        Assert.True(viewModel.IsSortByName);
        Assert.Equal(["GitHub", "Mail"], viewModel.FilteredEntries.Select(entry => entry.Title).ToList());

        var mail = vault.Entries.Single(entry => entry.Title == "Mail");
        vault.UpdateEntry(mail);
        viewModel.ReloadFromVault();

        viewModel.SortByRecentCommand.Execute(null);
        Assert.True(viewModel.IsSortByRecent);
        Assert.Equal(["Mail", "GitHub"], viewModel.FilteredEntries.Select(entry => entry.Title).ToList());

        viewModel.SelectViewCommand.Execute("favorites");
        Assert.True(viewModel.IsViewFavorites);
        Assert.False(viewModel.IsViewAll);
        Assert.Contains("共 0 条", viewModel.FilteredCountText);
    });

    [Fact]
    public Task SaveRequiresTitle() => Headless.Run(() =>
    {
        using var vault = CreateVaultWithEntries();
        using var viewModel = new MainViewModel(vault);

        viewModel.SelectedEntry = viewModel.FilteredEntries.Single(entry => entry.Title == "GitHub");
        viewModel.BeginEditCommand.Execute(null);
        viewModel.EditTitle = "   ";
        viewModel.SaveEntryCommand.Execute(null);

        Assert.NotEmpty(viewModel.TitleError);
        Assert.True(viewModel.IsEditing);
        Assert.Contains(vault.Entries, entry => entry.Title == "GitHub");

        viewModel.EditTitle = "GitHub Work";
        viewModel.SaveEntryCommand.Execute(null);

        Assert.Empty(viewModel.TitleError);
        Assert.False(viewModel.IsEditing);
        Assert.Contains(vault.Entries, entry => entry.Title == "GitHub Work");
    });

    [Fact]
    public Task SaveNormalizesAndValidatesUrl() => Headless.Run(() =>
    {
        using var vault = CreateVaultWithEntries();
        using var viewModel = new MainViewModel(vault);

        viewModel.SelectedEntry = viewModel.FilteredEntries.Single(entry => entry.Title == "GitHub");
        viewModel.BeginEditCommand.Execute(null);
        viewModel.EditUrls = "example.com";
        viewModel.SaveEntryCommand.Execute(null);

        Assert.Empty(viewModel.UrlError);
        Assert.Equal("https://example.com", vault.Entries.Single(entry => entry.Title == "GitHub").Url);

        viewModel.BeginEditCommand.Execute(null);
        viewModel.EditUrls = "ftp://example.com";
        viewModel.SaveEntryCommand.Execute(null);

        Assert.NotEmpty(viewModel.UrlError);
        Assert.True(viewModel.IsEditing);
        Assert.Equal("https://example.com", vault.Entries.Single(entry => entry.Title == "GitHub").Url);

        viewModel.EditUrls = string.Empty;
        viewModel.SaveEntryCommand.Execute(null);

        Assert.Empty(viewModel.UrlError);
        Assert.False(viewModel.IsEditing);
        Assert.Equal(string.Empty, vault.Entries.Single(entry => entry.Title == "GitHub").Url);
    });

    [Fact]
    public Task SaveReportsTitleAndUrlErrorsTogether() => Headless.Run(() =>
    {
        using var vault = CreateVaultWithEntries();
        using var viewModel = new MainViewModel(vault);

        viewModel.SelectedEntry = viewModel.FilteredEntries.Single(entry => entry.Title == "GitHub");
        viewModel.BeginEditCommand.Execute(null);
        viewModel.EditTitle = "   ";
        viewModel.EditUrls = "ftp://example.com";
        viewModel.SaveEntryCommand.Execute(null);

        Assert.Equal(Loc.T("Main_TitleRequired"), viewModel.TitleError);
        Assert.Equal(Loc.Format("Main_UrlInvalidLine", 1), viewModel.UrlError);
        Assert.True(viewModel.IsEditing);

        viewModel.EditTitle = "GitHub";
        Assert.Empty(viewModel.TitleError);
        viewModel.EditUrls = "github.com";
        Assert.Empty(viewModel.UrlError);
    });

    [Fact]
    public Task SaveStoresMultipleUrls() => Headless.Run(() =>
    {
        using var vault = CreateVaultWithEntries();
        using var viewModel = new MainViewModel(vault);

        viewModel.SelectedEntry = viewModel.FilteredEntries.Single(entry => entry.Title == "GitHub");
        viewModel.BeginEditCommand.Execute(null);
        viewModel.EditUrls = "github.com\n  https://gist.github.com  \n\ngithub.com";
        viewModel.SaveEntryCommand.Execute(null);

        var saved = vault.Entries.Single(entry => entry.Title == "GitHub");
        Assert.Equal(["https://github.com", "https://gist.github.com"], saved.Urls);
        Assert.Equal("https://github.com", saved.Url);
        Assert.Equal(["https://github.com", "https://gist.github.com"], viewModel.UrlItems);
        Assert.Equal("https://github.com\nhttps://gist.github.com", viewModel.EditUrls);
    });

    [Fact]
    public Task SaveRejectsInvalidSecondUrl() => Headless.Run(() =>
    {
        using var vault = CreateVaultWithEntries();
        using var viewModel = new MainViewModel(vault);

        viewModel.SelectedEntry = viewModel.FilteredEntries.Single(entry => entry.Title == "GitHub");
        viewModel.BeginEditCommand.Execute(null);
        viewModel.EditUrls = "github.com\nnot-a-url";
        viewModel.SaveEntryCommand.Execute(null);

        Assert.Equal(Loc.Format("Main_UrlInvalidLine", 2), viewModel.UrlError);
        Assert.True(viewModel.IsEditing);
        Assert.Empty(vault.Entries.Single(entry => entry.Title == "GitHub").Urls);
    });

    [Fact]
    public Task LegacyUrlFillsUrlItemsAndSearchMatchesAllUrls() => Headless.Run(() =>
    {
        using var vault = CreateVaultWithEntries();
        var github = vault.Entries.Single(entry => entry.Title == "GitHub");
        vault.UpdateEntry(github with { Url = "https://github.com", Urls = ["https://github.com", "https://gist.github.com"] });

        using var viewModel = new MainViewModel(vault);
        viewModel.SelectedEntry = viewModel.FilteredEntries.Single(entry => entry.Title == "GitHub");

        Assert.Equal(["https://github.com", "https://gist.github.com"], viewModel.UrlItems);
        Assert.Equal("https://github.com\nhttps://gist.github.com", viewModel.EditUrls);

        viewModel.SearchText = "gist";
        Assert.Single(viewModel.FilteredEntries);
        Assert.Equal("GitHub", viewModel.FilteredEntries[0].Title);
    });

    [Fact]
    public Task SelectingEntryWithNullUrlsStillLoadsDetail() => Headless.Run(() =>
    {
        using var vault = CreateVaultWithEntries();
        var github = vault.Entries.Single(entry => entry.Title == "GitHub");
        vault.UpdateEntry(github with { Url = "https://github.com", Urls = null! });

        using var viewModel = new MainViewModel(vault);
        viewModel.SelectedEntry = viewModel.FilteredEntries.Single(entry => entry.Title == "GitHub");

        Assert.True(viewModel.HasSelection);
        Assert.Equal("GitHub", viewModel.EditTitle);
        Assert.Equal(["https://github.com"], viewModel.UrlItems);
        Assert.Equal("https://github.com", viewModel.EditUrls);
    });

    [Fact]
    public Task SelectionShowsCreationTime() => Headless.Run(() =>
    {
        using var vault = CreateVaultWithEntries();
        using var viewModel = new MainViewModel(vault);

        var github = viewModel.FilteredEntries.Single(entry => entry.Title == "GitHub");
        viewModel.SelectedEntry = github;

        Assert.Equal(github.CreatedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm"), viewModel.CreatedAtDisplay);

        viewModel.SelectedEntry = null;
        Assert.Empty(viewModel.CreatedAtDisplay);
    });

    [Fact]
    public Task OpenUrlNormalizesAndReportsFailures() => Headless.Run(() =>
    {
        using var vault = CreateVaultWithEntries();
        var launcher = new RecordingUrlLauncher();
        using var viewModel = new MainViewModel(vault, urlLauncher: launcher);

        viewModel.OpenUrlCommand.Execute("github.com");
        Assert.Equal(["https://github.com"], launcher.Opened);

        viewModel.OpenUrlCommand.Execute("javascript:alert(1)");
        Assert.Equal(Loc.T("Main_UrlInvalid"), viewModel.StatusMessage);

        viewModel.OpenUrlCommand.Execute(string.Empty);
        Assert.Equal(Loc.T("Main_StatusUrlEmpty"), viewModel.StatusMessage);

        launcher.Result = false;
        viewModel.OpenUrlCommand.Execute("https://example.com");
        Assert.Equal(Loc.T("Main_StatusUrlOpenFailed"), viewModel.StatusMessage);
    });

    [Fact]
    public void SetThemeUpdatesPreferenceAndNotifiesShell()
    {
        using var vault = CreateVaultWithEntries();
        using var viewModel = new MainViewModel(vault);
        var applied = new List<string>();
        viewModel.AttachAppearanceCallbacks(applied.Add, null);

        Assert.Equal("system", viewModel.ThemePreference);

        viewModel.SetThemeCommand.Execute("dark");

        Assert.Equal("dark", viewModel.ThemePreference);
        Assert.Equal(["dark"], applied);

        // Unknown values are ignored instead of poisoning the preference.
        viewModel.SetThemeCommand.Execute(null);
        Assert.Equal("dark", viewModel.ThemePreference);
    }

    [Fact]
    public void SetLanguageUpdatesPreferenceAndNotifiesShell()
    {
        using var vault = CreateVaultWithEntries();
        using var viewModel = new MainViewModel(vault);
        var applied = new List<string>();
        viewModel.AttachAppearanceCallbacks(null, applied.Add);

        Assert.Equal(Loc.System, viewModel.LanguagePreference);

        viewModel.SetLanguageCommand.Execute(Loc.English);

        Assert.Equal(Loc.English, viewModel.LanguagePreference);
        Assert.Equal([Loc.English], applied);
    }

    [Fact]
    public Task SmartViewShortcutIgnoresViewsThatAreNotOffered() => Headless.Run(() =>
    {
        using var vault = CreateVaultWithEntries();
        using var viewModel = new MainViewModel(vault);
        var before = viewModel.SelectedCategory?.Key;

        // Index 99 is not a smart view; the selection must stay put.
        viewModel.ShowSmartViewCommand.Execute(99);

        Assert.Equal(before, viewModel.SelectedCategory?.Key);
    });

    [Fact]
    public Task SmartViewShortcutSelectsAllAndFavorites() => Headless.Run(() =>
    {
        using var vault = CreateVaultWithEntries();
        using var viewModel = new MainViewModel(vault);

        viewModel.ShowSmartViewCommand.Execute(2);
        Assert.Equal("favorites", viewModel.SelectedCategory?.Key);
        Assert.True(viewModel.IsViewFavorites);

        viewModel.ShowSmartViewCommand.Execute(1);
        Assert.Equal("all", viewModel.SelectedCategory?.Key);
        Assert.True(viewModel.IsViewAll);
    });

    [Fact]
    public Task ClearSearchEmptiesTheQuery() => Headless.Run(() =>
    {
        using var vault = CreateVaultWithEntries();
        using var viewModel = new MainViewModel(vault);

        viewModel.SearchText = "git";
        viewModel.ClearSearchCommand.Execute(null);

        Assert.Equal(string.Empty, viewModel.SearchText);
    });

    private VaultService CreateVaultWithEntries()
    {
        var vault = VaultService.CreateNew(_vaultPath, Password, FastOptions);
        vault.AddEntry(new PasswordEntry
        {
            Title = "GitHub",
            Username = "octocat",
            Password = "s3cret",
            Tags = ["work", "mail"],
        });
        vault.AddEntry(new PasswordEntry { Title = "Mail", Username = "me@example.com" });
        return vault;
    }

    private sealed class MutableTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class RecordingUrlLauncher : IUrlLauncher
    {
        public List<string?> Opened { get; } = [];

        public bool Result { get; set; } = true;

        public bool IsSupported(string? url) => !string.IsNullOrEmpty(url);

        public bool TryOpen(string? url)
        {
            Opened.Add(url);
            return Result;
        }
    }
}
