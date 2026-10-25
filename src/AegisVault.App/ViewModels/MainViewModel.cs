using System.Collections;
using System.Collections.ObjectModel;
using AegisVault.App.Localization;
using AegisVault.App.Services;
using AegisVault.Core.Models;
using AegisVault.Core.Services;
using AtomUI.Controls.Primitives;
using AtomUI.Desktop.Controls;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AegisVault.App.ViewModels;

public enum CategoryKind
{
    System,
    Category,
    Tag,
}

public enum EntrySortMode
{
    Name,
    RecentlyUpdated,
}

public sealed record CategoryItem(
    string Key,
    string DisplayName,
    string? Tag,
    bool IsFavorites,
    int Count,
    CategoryKind Kind = CategoryKind.System,
    Guid? CategoryId = null,
    string Color = "",
    int Depth = 1,
    bool HasChildren = false,
    Guid? ParentId = null)
{
    public bool IsUserCategory => Kind == CategoryKind.Category;

    public string Glyph => Kind switch
    {
        CategoryKind.Category => "📁",
        CategoryKind.Tag => "#",
        _ => string.Empty,
    };
}

/// <summary>ComboBox item for assigning an entry to a category (null = uncategorized).</summary>
public sealed record CategoryChoice(Guid? Id, string Name);

public partial class MainViewModel : ObservableObject, IDisposable
{
    private const string AllCategoryKey = "all";
    private const string FavoritesCategoryKey = "favorites";
    private const string RecentCategoryKey = "recent";
    private const string WeakCategoryKey = "weak";
    private const string StaleCategoryKey = "old";
    private const string RecycleBinCategoryKey = "recycle";
    private const string CategoryKeyPrefix = "cat:";

    /// <summary>How many entries the "recent" smart view shows.</summary>
    private const int RecentViewLimit = 10;

    private readonly VaultService _vault;
    private readonly ClipboardService? _clipboard;
    private readonly TimeProvider _timeProvider;
    private readonly DispatcherTimer _totpTimer;
    private readonly DispatcherTimer _toastTimer;
    private readonly DispatcherTimer _undoTimer;
    private readonly DispatcherTimer? _openTimer;
    private readonly DispatcherTimer? _searchTimer;
    private readonly DispatcherTimer? _healthTimer;
    private Guid? _lastDeletedId;
    private Guid? _pendingOpenedId;

    /// <summary>
    /// Last-viewed timestamps keyed by entry id ("recent" smart view). Seeded from
    /// the vault's persisted <see cref="PasswordEntry.LastOpenedAt"/> and updated
    /// in memory immediately, so the view is responsive without re-encrypting an
    /// entry on every selection.
    /// </summary>
    private readonly Dictionary<Guid, DateTimeOffset> _openedAt = [];
    private int _healthRevision;
    private bool _disposed;
    private bool _loadingEditor;
    private PasswordStrengthResult? _editPasswordStrength;
    private readonly IUrlLauncher _urlLauncher;
    private Action<string>? _applyTheme;
    private Action<string>? _applyLanguage;
    private Action? _dismissStartupGuide;
    private VaultHealthReport _health = new(0, 0, 0, new HashSet<Guid>(), new HashSet<Guid>(), new HashSet<Guid>());

    public event Action? LockRequested;

    public MainViewModel(
        VaultService vault,
        ClipboardService? clipboard = null,
        TimeProvider? timeProvider = null,
        IUrlLauncher? urlLauncher = null,
        TimeSpan? searchDebounce = null,
        TimeSpan? healthDebounce = null,
        TimeSpan? openDebounce = null)
    {
        _vault = vault;
        _clipboard = clipboard;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _urlLauncher = urlLauncher ?? SystemUrlLauncher.Instance;

        Entries = new ObservableCollection<PasswordEntry>(vault.Entries);
        FilteredEntries = [];
        Categories = [];

        foreach (var entry in vault.Entries)
        {
            if (entry.LastOpenedAt is { } opened)
            {
                _openedAt[entry.Id] = opened;
            }
        }

        _searchTimer = CreateDebounceTimer(searchDebounce ?? TimeSpan.Zero, ApplyFilter);
        _healthTimer = CreateDebounceTimer(healthDebounce ?? TimeSpan.Zero, StartHealthAnalysis);

        // "Recently opened" is written back with a debounce so arrowing through
        // the list does not re-encrypt an entry per keystroke.
        _openTimer = CreateDebounceTimer(openDebounce ?? TimeSpan.Zero, FlushOpenedEntry);

        // The sidebar shows up immediately; the health analysis (the expensive
        // part on large vaults) runs on a background thread and backfills.
        RefreshCategoryViews();
        ApplyFilter();
        ScheduleHealthAnalysis();

        _totpTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _totpTimer.Tick += (_, _) => UpdateTotp();
        _totpTimer.Start();

        _toastTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _toastTimer.Tick += (_, _) => OnToastTimerTick();

        // Undo window after a delete; the entry stays in the recycle bin afterwards.
        _undoTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(8) };
        _undoTimer.Tick += (_, _) => ClearUndoDelete();

        if (_clipboard is not null)
        {
            _clipboard.CopyStarted += OnClipboardCopyStarted;
        }
    }

    private static DispatcherTimer? CreateDebounceTimer(TimeSpan delay, Action action)
    {
        if (delay <= TimeSpan.Zero)
        {
            return null;
        }

        var timer = new DispatcherTimer { Interval = delay };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            action();
        };
        return timer;
    }

    public ObservableCollection<PasswordEntry> Entries { get; }

    /// <summary>
    /// Filtered view of <see cref="Entries"/>. Replaced wholesale (one change
    /// notification) instead of Clear+Add per entry on every keystroke.
    /// </summary>
    [ObservableProperty]
    private List<PasswordEntry> filteredEntries = [];

    public ObservableCollection<CategoryItem> Categories { get; }

    /// <summary>Sidebar sections: smart views, user categories, tag views.</summary>
    public ObservableCollection<CategoryItem> SystemCategories { get; } = [];

    public ObservableCollection<CategoryItem> UserCategories { get; } = [];

    public ObservableCollection<CategoryItem> TagCategories { get; } = [];

    /// <summary>Nested tree the sidebar's inline NavMenu renders.</summary>
    public ObservableCollection<CategoryNavNode> CategoryNodes { get; } = [];

    /// <summary>
    /// Paths expanded when the tree is (re)built. Roots start expanded so the
    /// common flat case looks exactly like the previous sidebar list.
    /// </summary>
    public ObservableCollection<TreeNodePath> CategoryOpenPaths { get; } = [];

    /// <summary>Shape of the last built tree; entry edits do not rebuild it.</summary>
    private string? _categoryTreeSignature;

    public ObservableCollection<CategoryChoice> CategoryChoices { get; } = [];

    [ObservableProperty]
    private string searchText = string.Empty;

    [ObservableProperty]
    private CategoryItem? selectedCategory;

    [ObservableProperty]
    private CategoryItem? selectedSystemCategory;

    [ObservableProperty]
    private CategoryItem? selectedUserCategory;

    [ObservableProperty]
    private CategoryItem? selectedTagCategory;

    [ObservableProperty]
    private CategoryNavNode? selectedCategoryNode;

    /// <summary>True while the view-model pushes a selection into the sidebar.</summary>
    private bool _syncingCategorySelection;

    /// <summary>
    /// True while the sidebar collections are being rebuilt. A selection change
    /// raised during the rebuild would otherwise re-enter and repopulate the
    /// collection that is still being enumerated.
    /// </summary>
    private bool _refreshingCategoryViews;

    /// <summary>Category ids the current filter matches (the selected branch).</summary>
    private HashSet<Guid> _categoryFilterIds = [];

    partial void OnSelectedSystemCategoryChanged(CategoryItem? value)
    {
        if (value is null)
        {
            return;
        }

        SelectedCategory = value;
        SelectedUserCategory = null;
        SelectedTagCategory = null;
        ClearCategoryNodeSelection();
    }

    partial void OnSelectedUserCategoryChanged(CategoryItem? value)
    {
        if (value is null)
        {
            return;
        }

        SelectedCategory = value;
        SelectedSystemCategory = null;
        SelectedTagCategory = null;
    }

    partial void OnSelectedTagCategoryChanged(CategoryItem? value)
    {
        if (value is null)
        {
            return;
        }

        SelectedCategory = value;
        SelectedSystemCategory = null;
        SelectedUserCategory = null;
        ClearCategoryNodeSelection();
    }

    partial void OnSelectedCategoryNodeChanged(CategoryNavNode? value)
    {
        if (_syncingCategorySelection || value is null)
        {
            return;
        }

        SelectedCategory = value.Item;
        SelectedSystemCategory = null;
        SelectedTagCategory = null;
    }

    private void ClearCategoryNodeSelection()
    {
        if (SelectedCategoryNode is null)
        {
            return;
        }

        _syncingCategorySelection = true;
        try
        {
            SelectedCategoryNode = null;
        }
        finally
        {
            _syncingCategorySelection = false;
        }
    }

    [ObservableProperty]
    private CategoryChoice? selectedCategoryChoice;

    public string CategoryDisplayName => SelectedCategoryChoice?.Name ?? Loc.T("Main_NoCategory");

    partial void OnSelectedCategoryChoiceChanged(CategoryChoice? value) => OnPropertyChanged(nameof(CategoryDisplayName));

    [ObservableProperty]
    private PasswordEntry? selectedEntry;

    [ObservableProperty]
    private bool isEditing;

    [ObservableProperty]
    private bool isPasswordRevealed;

    [ObservableProperty]
    private bool isSecretRevealed;

    [ObservableProperty]
    private bool isApiKeyRevealed;

    [ObservableProperty]
    private string editTitle = string.Empty;

    [ObservableProperty]
    private string editUsername = string.Empty;

    [ObservableProperty]
    private string editPassword = string.Empty;

    [ObservableProperty]
    private string editUrls = string.Empty;

    [ObservableProperty]
    private string editNotes = string.Empty;

    [ObservableProperty]
    private string editTotpSecret = string.Empty;

    /// <summary>The editor masks the TOTP seed until the user reveals it.</summary>
    [ObservableProperty]
    private bool isTotpSecretRevealed;

    [ObservableProperty]
    private string editPhone = string.Empty;

    [ObservableProperty]
    private string editEmail = string.Empty;

    [ObservableProperty]
    private string editAppId = string.Empty;

    [ObservableProperty]
    private string editSecret = string.Empty;

    [ObservableProperty]
    private string editApiKey = string.Empty;

    [ObservableProperty]
    private string editTags = string.Empty;

    [ObservableProperty]
    private bool editIsFavorite;

    /// <summary>Inline validation message under the title field (empty when valid).</summary>
    [ObservableProperty]
    private string titleError = string.Empty;

    /// <summary>Inline validation message under the URL field (empty when valid).</summary>
    [ObservableProperty]
    private string urlError = string.Empty;

    /// <summary>Local-time creation timestamp shown in the detail preview.</summary>
    [ObservableProperty]
    private string createdAtDisplay = string.Empty;

    /// <summary>
    /// "Last changed: N days ago" for the selected entry (ROT-1a). Derived from
    /// <see cref="PasswordEntry.UpdatedAt"/>; no payload field is added.
    /// </summary>
    [ObservableProperty]
    private string passwordAgeDisplay = string.Empty;

    /// <summary>Superseded passwords for the selected entry, newest first.</summary>
    [ObservableProperty]
    private IReadOnlyList<PasswordHistoryItem> passwordHistory = [];

    /// <summary>True when the selected entry has any retained history.</summary>
    [ObservableProperty]
    private bool hasPasswordHistory;

    [ObservableProperty]
    private string totpCode = string.Empty;

    [ObservableProperty]
    private int totpRemaining;

    [ObservableProperty]
    private string? statusMessage;

    [ObservableProperty]
    private bool isClipboardToastVisible;

    [ObservableProperty]
    private int clipboardToastRemaining;

    public string ClipboardToastText => Loc.Format("Main_ToastFormat", ClipboardToastRemaining);

    public VaultHealthReport Health => _health;

    /// <summary>True while the initial (background) health analysis is running.</summary>
    [ObservableProperty]
    private bool isHealthAnalyzing;

    /// <summary>Awaits the initial analysis; the shell shows a placeholder until it lands.</summary>
    internal Task HealthAnalysis => _healthAnalysis ?? Task.CompletedTask;

    private Task? _healthAnalysis;

    partial void OnIsHealthAnalyzingChanged(bool value) => NotifyHealthChanged();

    public bool HasSecurityIssues => !IsHealthAnalyzing && (_health.WeakCount > 0 || _health.ReusedCount > 0);

    public string HealthSummary => IsHealthAnalyzing
        ? Loc.T("Main_HealthAnalyzing")
        : _health.TotalEntries == 0
        ? Loc.T("Main_HealthEmpty")
        : _health.WeakCount > 0 || _health.ReusedCount > 0
        ? Loc.Format("Main_HealthIssues", _health.WeakCount, _health.ReusedCount, _health.OldCount)
        : _health.OldCount > 0
        ? Loc.Format("Main_HealthStale", _health.TotalEntries, _health.OldCount)
        : Loc.Format("Main_HealthOk", _health.TotalEntries);

    public string HealthCompactText => IsHealthAnalyzing
        ? Loc.T("Main_HealthAnalyzingCompact")
        : Loc.Format("Main_HealthCompact", _health.WeakCount, _health.ReusedCount, _health.OldCount);

    public bool HasSelection => SelectedEntry is not null;

    public bool HasFilteredEntries => FilteredEntries.Count > 0;

    /// <summary>True when the vault itself has no entries (welcome hero shows).</summary>
    public bool IsVaultEmpty => Entries.Count == 0;

    /// <summary>The welcome hero is suppressed while browsing the recycle bin.</summary>
    public bool ShowVaultHero => IsVaultEmpty && !IsViewRecycleBin;

    /// <summary>True when the vault has entries but none match the current view/search.</summary>
    public bool ShowListEmptyHint => Entries.Count > 0 && FilteredEntries.Count == 0;

    /// <summary>The "select an entry" placeholder is only meaningful for a non-empty vault.</summary>
    public bool ShowSelectEntryHint => !HasSelection && !IsVaultEmpty && !ShowStartupGuide;

    /// <summary>
    /// One-time guide for a freshly created/opened vault: hidden once dismissed
    /// (the shell persists that in the preferences).
    /// </summary>
    [ObservableProperty]
    private bool startupGuideDismissed;

    public bool ShowStartupGuide => !StartupGuideDismissed && !IsVaultEmpty && !HasSelection;

    /// <summary>Persists the dismissal; wired by the shell.</summary>
    public void AttachStartupGuideCallback(Action? guideDismissed) => _dismissStartupGuide = guideDismissed;

    [RelayCommand]
    private void DismissStartupGuide()
    {
        StartupGuideDismissed = true;
        _dismissStartupGuide?.Invoke();
    }

    /// <summary>
    /// Imports a CSV / Bitwarden export (same flow as the settings page) and
    /// reloads the entry list so the hero shortcut shows the result.
    /// </summary>
    public Task<ImportResult> ImportCsvFromAsync(string path)
        => CsvImportFlow.RunAsync(_vault, path, message => StatusMessage = message, ReloadFromVault);

    public bool HasTotp => !string.IsNullOrWhiteSpace(EditTotpSecret);

    public bool IsTotpValid => TotpCode.Length is 6 or 8 && TotpCode.All(char.IsDigit);

    public string PasswordPreview => IsPasswordRevealed
        ? EditPassword
        : new string('●', Math.Clamp(EditPassword.Length, 6, 14));

    /// <summary>Masked preview of the client secret; revealed on demand only.</summary>
    public string SecretPreview => IsSecretRevealed ? EditSecret : MaskValue(EditSecret);

    /// <summary>Masked preview of the API key; revealed on demand only.</summary>
    public string ApiKeyPreview => IsApiKeyRevealed ? EditApiKey : MaskValue(EditApiKey);

    /// <summary>Shared masking helper so no field can forget to hide its value.</summary>
    public static string MaskValue(string? value)
        => new('●', Math.Clamp(value?.Length ?? 0, 6, 14));

    public string EditPasswordStrengthSummary => _editPasswordStrength is null
        ? string.Empty
        : StrengthFormatting.FormatSummary(
            _editPasswordStrength.Score,
            _editPasswordStrength.Label,
            _editPasswordStrength.CrackTime);

    public double EditPasswordStrengthPercent => (_editPasswordStrength?.Score ?? 0) * 25;

    partial void OnSearchTextChanged(string value)
    {
        // Clearing the box applies immediately (and headless tests run without
        // a debounce timer); longer queries wait for a short typing pause so
        // every keystroke does not re-filter thousands of entries.
        if (_searchTimer is null || string.IsNullOrEmpty(value))
        {
            _searchTimer?.Stop();
            ApplyFilter();
            return;
        }

        _searchTimer.Stop();
        _searchTimer.Start();
    }

    [ObservableProperty]
    private EntrySortMode sortMode = EntrySortMode.Name;

    public bool IsSortByName => SortMode == EntrySortMode.Name;

    public bool IsSortByRecent => SortMode == EntrySortMode.RecentlyUpdated;

    public bool IsViewAll => SelectedCategory?.Key == AllCategoryKey;

    public bool IsViewFavorites => SelectedCategory?.Key == FavoritesCategoryKey;

    public bool IsViewRecent => SelectedCategory?.Key == RecentCategoryKey;

    public bool IsViewSecurity => SelectedCategory?.Key == WeakCategoryKey;

    public bool IsViewStale => SelectedCategory?.Key == StaleCategoryKey;

    public bool IsViewRecycleBin => SelectedCategory?.Key == RecycleBinCategoryKey;

    /// <summary>Number of entries currently in the recycle bin.</summary>
    public int DeletedCount => _vault.DeletedEntries.Count;

    /// <summary>"Deleted on …" line shown for a recycled entry.</summary>
    public string DeletedAtDisplay => SelectedEntry?.DeletedAt is { } deletedAt
        ? Loc.Format("Main_DeletedAtFormat", deletedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm"))
        : string.Empty;

    /// <summary>True while the delete that just happened can still be undone.</summary>
    [ObservableProperty]
    private bool canUndoDelete;

    public string FilteredCountText => Loc.Format("Main_EntryCountFormat", FilteredEntries.Count);

    partial void OnSortModeChanged(EntrySortMode value)
    {
        OnPropertyChanged(nameof(IsSortByName));
        OnPropertyChanged(nameof(IsSortByRecent));
        ApplyFilter();
    }

    [RelayCommand]
    private void SortByName() => SortMode = EntrySortMode.Name;

    [RelayCommand]
    private void SortByRecent() => SortMode = EntrySortMode.RecentlyUpdated;

    [RelayCommand]
    private void SelectView(string? key)
    {
        var category = Categories.FirstOrDefault(item => item.Key == key);
        if (category is not null)
        {
            SelectedCategory = category;
        }
    }

    partial void OnSelectedCategoryChanged(CategoryItem? value)
    {
        // Switching views always leaves the editor (a recycled entry is not editable).
        IsEditing = false;
        RefreshCategoryFilter();
        ApplyFilter();
        OnPropertyChanged(nameof(IsViewAll));
        OnPropertyChanged(nameof(IsViewFavorites));
        OnPropertyChanged(nameof(IsViewRecent));
        OnPropertyChanged(nameof(IsViewSecurity));
        OnPropertyChanged(nameof(IsViewStale));
        OnPropertyChanged(nameof(IsViewRecycleBin));
        OnPropertyChanged(nameof(ShowVaultHero));
    }

    /// <summary>
    /// Resolves the ids the current category filter matches. Selecting a category
    /// includes its whole branch, so a parent shows everything underneath it.
    /// </summary>
    private void RefreshCategoryFilter()
    {
        if (SelectedCategory is { Kind: CategoryKind.Category, CategoryId: { } categoryId })
        {
            _categoryFilterIds = [.. _vault.GetCategorySubtree(categoryId)];
        }
        else
        {
            _categoryFilterIds = [];
        }
    }

    partial void OnSelectedEntryChanged(PasswordEntry? value)
    {
        IsEditing = false;
        IsPasswordRevealed = false;
        LoadEditor(value);
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(ShowSelectEntryHint));
        OnPropertyChanged(nameof(ShowStartupGuide));
        OnPropertyChanged(nameof(DeletedAtDisplay));

        // Viewing an entry (live list or quick access) is what counts as "opened";
        // recycled entries do not update the recent view.
        if (value is not null && !IsViewRecycleBin)
        {
            RecordOpened(value.Id);
        }
    }

    /// <summary>Queues a "last opened" write for the selected entry.</summary>
    private void RecordOpened(Guid id)
    {
        // The sidebar count only moves when an entry is opened for the first time.
        var isNew = !_openedAt.ContainsKey(id);
        _openedAt[id] = _timeProvider.GetUtcNow();
        _pendingOpenedId = id;

        if (_openTimer is null)
        {
            FlushOpenedEntry();
        }
        else
        {
            _openTimer.Stop();
            _openTimer.Start();
        }

        if (isNew)
        {
            // Adds the "recent" row the first time and keeps its count current.
            RefreshCategories();
        }
    }

    /// <summary>
    /// Persists the pending "last opened" timestamp. Deliberately goes through
    /// <see cref="VaultService.MarkEntryOpened"/> so UpdatedAt (and therefore the
    /// stale-health view and the recently-updated sort) is unaffected.
    /// </summary>
    private void FlushOpenedEntry()
    {
        _openTimer?.Stop();
        if (_pendingOpenedId is not { } id)
        {
            return;
        }

        _pendingOpenedId = null;

        // Best effort: the entry may have been deleted or recycled meanwhile, and
        // the session may already be gone on the shutdown path.
        if (_vault.IsUnlocked)
        {
            _vault.MarkEntryOpened(id);
        }
    }

    partial void OnStartupGuideDismissedChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowStartupGuide));
        OnPropertyChanged(nameof(ShowSelectEntryHint));
    }

    partial void OnEditTotpSecretChanged(string value)
    {
        OnPropertyChanged(nameof(HasTotp));
        UpdateTotp();
    }

    partial void OnTotpCodeChanged(string value) => OnPropertyChanged(nameof(IsTotpValid));

    partial void OnIsPasswordRevealedChanged(bool value) => OnPropertyChanged(nameof(PasswordPreview));

    partial void OnIsSecretRevealedChanged(bool value) => OnPropertyChanged(nameof(SecretPreview));

    partial void OnIsApiKeyRevealedChanged(bool value) => OnPropertyChanged(nameof(ApiKeyPreview));

    partial void OnEditSecretChanged(string value) => OnPropertyChanged(nameof(SecretPreview));

    partial void OnEditApiKeyChanged(string value) => OnPropertyChanged(nameof(ApiKeyPreview));

    partial void OnEditPasswordChanged(string value)
    {
        _editPasswordStrength = string.IsNullOrEmpty(value)
            ? null
            : PasswordStrengthEstimator.Evaluate(value);

        OnPropertyChanged(nameof(PasswordPreview));
        OnPropertyChanged(nameof(EditPasswordStrengthSummary));
        OnPropertyChanged(nameof(EditPasswordStrengthPercent));
    }

    partial void OnEditTitleChanged(string value)
    {
        if (TitleError.Length > 0)
        {
            TitleError = string.Empty;
        }
    }

    partial void OnEditUrlsChanged(string value)
    {
        if (UrlError.Length > 0)
        {
            UrlError = string.Empty;
        }
    }

    private static List<string> EffectiveUrls(PasswordEntry entry)
    {
        // Source-generated JSON leaves new collection properties null when the
        // field is missing from older vault files, so never assume non-null.
        if (entry.Urls is { Count: > 0 })
        {
            return entry.Urls.Where(url => !string.IsNullOrWhiteSpace(url)).ToList();
        }

        return string.IsNullOrWhiteSpace(entry.Url) ? [] : [entry.Url];
    }

    [RelayCommand]
    private void AddEntry()
    {
        var defaultCategoryId = SelectedCategory is { Kind: CategoryKind.Category, CategoryId: { } categoryId }
            ? categoryId
            : (Guid?)null;

        var entry = _vault.AddEntry(new PasswordEntry
        {
            Title = Loc.T("Main_NewEntryTitle"),
            CategoryId = defaultCategoryId,
        });
        Entries.Add(entry);
        RefreshCategories();
        ApplyFilter();
        SelectedEntry = entry;
        IsEditing = true;
        StatusMessage = Loc.T("Main_StatusNewEntry");
    }

    [RelayCommand]
    private void BeginEdit()
    {
        if (SelectedEntry is null)
        {
            return;
        }

        LoadEditor(SelectedEntry);
        IsEditing = true;
    }

    [RelayCommand]
    private void CancelEdit()
    {
        LoadEditor(SelectedEntry);
        IsEditing = false;
    }

    /// <summary>URLs of the selected entry, one preview row each.</summary>
    public ObservableCollection<string> UrlItems { get; } = [];

    [RelayCommand]
    private void SaveEntry()
    {
        if (SelectedEntry is null)
        {
            return;
        }

        var title = EditTitle.Trim();
        var urls = new List<string>();
        string? urlError = null;
        var ordinal = 0;

        foreach (var line in EditUrls.Replace("\r\n", "\n").Split('\n'))
        {
            var raw = line.Trim();
            if (raw.Length == 0)
            {
                continue;
            }

            ordinal++;
            var normalized = WebUrl.Normalize(raw);
            if (normalized is null)
            {
                urlError ??= Loc.Format("Main_UrlInvalidLine", ordinal);
                continue;
            }

            if (!urls.Contains(normalized, StringComparer.OrdinalIgnoreCase))
            {
                urls.Add(normalized);
            }
        }

        TitleError = title.Length == 0 ? Loc.T("Main_TitleRequired") : string.Empty;
        UrlError = urlError ?? string.Empty;
        if (title.Length == 0 || urlError is not null)
        {
            return;
        }

        var updated = SelectedEntry with
        {
            Title = title,
            Username = EditUsername,
            Password = EditPassword,
            Url = urls.Count > 0 ? urls[0] : string.Empty,
            Urls = urls,
            Notes = EditNotes,
            TotpSecret = EditTotpSecret.Trim(),
            Phone = EditPhone.Trim(),
            Email = EditEmail.Trim(),
            AppId = EditAppId.Trim(),
            Secret = EditSecret.Trim(),
            ApiKey = EditApiKey.Trim(),
            Tags = ParseTags(EditTags),
            IsFavorite = EditIsFavorite,
            CategoryId = SelectedCategoryChoice?.Id,
        };

        if (!_vault.UpdateEntry(updated))
        {
            StatusMessage = Loc.T("Main_StatusEntryMissing");
            return;
        }

        // Re-read from the vault: UpdateEntry owns rotation history, so the local
        // record is stale (it predates the append).
        var saved = _vault.Entries.FirstOrDefault(candidate => candidate.Id == updated.Id) ?? updated;
        ReplaceInList(saved);
        SelectedEntry = saved;
        IsEditing = false;
        RefreshCategories();
        ApplyFilter();
        StatusMessage = Loc.T("Main_StatusSaved");
    }

    [RelayCommand]
    private void DeleteEntry()
    {
        if (SelectedEntry is null)
        {
            return;
        }

        var target = SelectedEntry;
        if (_vault.DeleteEntry(target.Id))
        {
            Entries.Remove(target);
            SelectedEntry = null;
            IsEditing = false;
            RefreshCategories();
            ApplyFilter();

            // Keep a short undo window; after it expires the entry is only
            // reachable from the recycle bin.
            _lastDeletedId = target.Id;
            CanUndoDelete = true;
            _undoTimer.Stop();
            _undoTimer.Start();
            StatusMessage = Loc.T("Main_StatusMovedToRecycleBin");
        }
    }

    /// <summary>Restores the entry deleted moments ago (status-bar undo).</summary>
    [RelayCommand]
    private void UndoDelete()
    {
        if (_lastDeletedId is { } id && _vault.RestoreEntry(id))
        {
            ReloadFromVault();
            StatusMessage = Loc.T("Main_StatusEntryRestored");
        }

        ClearUndoDelete();
    }

    private void ClearUndoDelete()
    {
        _undoTimer.Stop();
        _lastDeletedId = null;
        CanUndoDelete = false;
    }

    /// <summary>Restores the entry selected inside the recycle bin.</summary>
    [RelayCommand]
    private void RestoreSelectedEntry()
    {
        if (SelectedEntry is not { } target || !IsViewRecycleBin)
        {
            return;
        }

        if (_vault.RestoreEntry(target.Id))
        {
            ReloadFromVault();
            StatusMessage = Loc.T("Main_StatusEntryRestored");
        }
    }

    /// <summary>
    /// Permanently deletes the entry selected inside the recycle bin. Called from
    /// the shell after the user confirms the irreversible action.
    /// </summary>
    public bool PurgeSelectedEntry()
    {
        if (SelectedEntry is not { } target || !IsViewRecycleBin || !_vault.PurgeEntry(target.Id))
        {
            return false;
        }

        ReloadFromVault();
        StatusMessage = Loc.T("Main_StatusEntryPurged");
        return true;
    }

    /// <summary>
    /// Empties the recycle bin (called from the shell after confirmation);
    /// returns how many entries were purged.
    /// </summary>
    public int EmptyRecycleBin()
    {
        var purged = _vault.EmptyRecycleBin();
        if (purged > 0)
        {
            ReloadFromVault();
            StatusMessage = Loc.T("Main_StatusRecycleBinEmptied");
        }

        return purged;
    }

    [RelayCommand]
    private void ToggleFavorite()
    {
        if (SelectedEntry is null)
        {
            return;
        }

        var updated = SelectedEntry with
        {
            IsFavorite = !SelectedEntry.IsFavorite,
            UpdatedAt = _timeProvider.GetUtcNow(),
        };

        if (!_vault.UpdateEntry(updated))
        {
            return;
        }

        ReplaceInList(updated);
        SelectedEntry = updated;
        RefreshCategories();
        ApplyFilter();
        StatusMessage = updated.IsFavorite ? Loc.T("Main_StatusFavoriteAdded") : Loc.T("Main_StatusFavoriteRemoved");
    }

    [RelayCommand]
    private void TogglePasswordReveal() => IsPasswordRevealed = !IsPasswordRevealed;

    [RelayCommand]
    private void ToggleSecretReveal() => IsSecretRevealed = !IsSecretRevealed;

    [RelayCommand]
    private void ToggleApiKeyReveal() => IsApiKeyRevealed = !IsApiKeyRevealed;

    [RelayCommand]
    private void ToggleTotpReveal() => IsTotpSecretRevealed = !IsTotpSecretRevealed;

    /// <summary>Copies an explicit value (used by the extra credential rows).</summary>
    [RelayCommand]
    private async Task CopyValueAsync(string? value)
    {
        if (_clipboard is null || string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        await _clipboard.CopyAsync(value.Trim());
        StatusMessage = Loc.T("Main_StatusFieldCopied");
    }

    [RelayCommand]
    private void ShowSecurity()
    {
        var category = Categories.FirstOrDefault(item => item.Key == WeakCategoryKey)
            ?? Categories.FirstOrDefault(item => item.Key == StaleCategoryKey);
        if (category is not null)
        {
            SelectedCategory = category;
        }
    }

    /// <summary>Clears the search box (Escape).</summary>
    [RelayCommand]
    private void ClearSearch() => SearchText = string.Empty;

    /// <summary>
    /// Smart-view shortcuts (Ctrl+1…4). Views that are not offered right now (the
    /// security/stale rows only exist while they have entries) are ignored instead
    /// of falling back to something the user did not ask for.
    /// </summary>
    [RelayCommand]
    private void ShowSmartView(int index)
    {
        var key = index switch
        {
            1 => AllCategoryKey,
            2 => FavoritesCategoryKey,
            3 => WeakCategoryKey,
            4 => StaleCategoryKey,
            _ => null,
        };

        if (key is not null &&
            SystemCategories.FirstOrDefault(category => category.Key == key) is { } target)
        {
            SelectedSystemCategory = target;
        }
    }

    [RelayCommand]
    private async Task CopyPasswordAsync()
    {
        if (_clipboard is null || string.IsNullOrEmpty(EditPassword))
        {
            return;
        }

        await _clipboard.CopyAsync(EditPassword);
        StatusMessage = Loc.T("Main_StatusPasswordCopied");
    }

    [RelayCommand]
    private async Task CopyHistoryPasswordAsync(PasswordHistoryItem? item)
    {
        if (_clipboard is null || item is null || string.IsNullOrEmpty(item.Password))
        {
            return;
        }

        // Same clipboard policy as the live password: auto-clear + history exclusion.
        await _clipboard.CopyAsync(item.Password);
        StatusMessage = Loc.T("Main_StatusHistoryPasswordCopied");
    }

    [RelayCommand]
    private async Task CopyUsernameAsync()
    {
        if (_clipboard is null || string.IsNullOrEmpty(EditUsername))
        {
            return;
        }

        await _clipboard.CopyAsync(EditUsername);
        StatusMessage = Loc.T("Main_StatusUsernameCopied");
    }

    [RelayCommand]
    private async Task CopyTotpAsync()
    {
        if (_clipboard is null || !IsTotpValid)
        {
            return;
        }

        await _clipboard.CopyAsync(TotpCode);
        StatusMessage = Loc.T("Main_StatusTotpCopied");
    }

    [RelayCommand]
    private void OpenUrl(string? url)
    {
        var normalized = WebUrl.Normalize(url);
        if (normalized is null)
        {
            StatusMessage = Loc.T("Main_UrlInvalid");
            return;
        }

        if (normalized.Length == 0)
        {
            StatusMessage = Loc.T("Main_StatusUrlEmpty");
            return;
        }

        if (!_urlLauncher.TryOpen(normalized))
        {
            StatusMessage = Loc.T("Main_StatusUrlOpenFailed");
        }
    }

    /// <summary>
    /// ROT-1a guidance: open the site so the user can change the password there,
    /// then paste the new value back here (saving it records the old one).
    /// </summary>
    [RelayCommand]
    private void StartRotation()
    {
        if (SelectedEntry is null)
        {
            return;
        }

        if (!string.IsNullOrEmpty(SelectedEntry.Url))
        {
            OpenUrl(SelectedEntry.Url);
        }

        StatusMessage = Loc.T("Main_StatusRotationStarted");
    }

    [RelayCommand]
    private void Lock()
    {
        FlushOpenedEntry();
        ClearUndoDelete();
        _vault.Lock();
        LockRequested?.Invoke();
    }

    /// <summary>Rebuilds the entry list from the vault (e.g. after an import).</summary>
    public void ReloadFromVault()
    {
        SelectedEntry = null;
        Entries.Clear();
        foreach (var entry in _vault.Entries)
        {
            Entries.Add(entry);
        }

        RefreshCategories();
        ApplyFilter();
    }

    public void Dispose()
    {
        _disposed = true;

        // Persist a pending "last opened" before the session goes away.
        FlushOpenedEntry();
        _searchTimer?.Stop();
        _healthTimer?.Stop();
        _openTimer?.Stop();
        _totpTimer.Stop();
        _toastTimer.Stop();
        _undoTimer.Stop();

        if (_clipboard is not null)
        {
            _clipboard.CopyStarted -= OnClipboardCopyStarted;
        }
    }

    private void OnClipboardCopyStarted(TimeSpan delay)
    {
        var seconds = Math.Max(1, (int)Math.Ceiling(delay.TotalSeconds));
        ClipboardToastRemaining = seconds;
        IsClipboardToastVisible = true;
        _toastTimer.Stop();
        _toastTimer.Start();
    }

    private void OnToastTimerTick()
    {
        if (ClipboardToastRemaining <= 1)
        {
            _toastTimer.Stop();
            IsClipboardToastVisible = false;
            ClipboardToastRemaining = 0;
            return;
        }

        ClipboardToastRemaining--;
    }

    [RelayCommand]
    private async Task ClearClipboardNowAsync()
    {
        _toastTimer.Stop();
        IsClipboardToastVisible = false;
        if (_clipboard is not null)
        {
            await _clipboard.ClearIfUnchangedAsync();
        }
    }

    private void RefreshCategories()
    {
        // The sidebar counts that come from the entry list (favorites, tags,
        // categories) are cheap and must update immediately; the weak/reused
        // metrics are recomputed off the UI thread and refresh the sidebar
        // again when they land.
        RefreshCategoryViews();
        ScheduleHealthAnalysis();
    }

    /// <summary>
    /// Queues a health re-analysis. A revision number makes sure only the
    /// latest run may publish its result: a slow first analysis must not
    /// overwrite the report of a newer edit (and a disposed view model must
    /// not be touched at all).
    /// </summary>
    private void ScheduleHealthAnalysis()
    {
        _healthRevision++;
        IsHealthAnalyzing = true;

        if (_healthTimer is null)
        {
            StartHealthAnalysis();
            return;
        }

        _healthTimer.Stop();
        _healthTimer.Start();
    }

    private void StartHealthAnalysis()
    {
        if (_disposed)
        {
            return;
        }

        _healthAnalysis = AnalyzeHealthAsync(_healthRevision);
    }

    /// <summary>
    /// Health analysis that runs off the UI thread (thousands of entries used
    /// to stall "unlock → main window") and marshals the result back through
    /// the dispatcher context.
    /// </summary>
    private async Task AnalyzeHealthAsync(int revision)
    {
        try
        {
            var entries = Entries.ToArray();
            var report = await Task.Run(() => VaultHealth.Analyze(entries, _timeProvider));

            if (_disposed || revision != _healthRevision)
            {
                return;
            }

            _health = report;
            RefreshCategoryViews();
        }
        finally
        {
            if (!_disposed && revision == _healthRevision)
            {
                IsHealthAnalyzing = false;
                NotifyHealthChanged();
            }
        }
    }

    private void NotifyHealthChanged()
    {
        OnPropertyChanged(nameof(Health));
        OnPropertyChanged(nameof(HasSecurityIssues));
        OnPropertyChanged(nameof(HealthSummary));
        OnPropertyChanged(nameof(HealthCompactText));
    }

    /// <summary>Rebuilds the sidebar from the current entries and health report.</summary>
    private void RefreshCategoryViews()
    {
        var selectedKey = SelectedCategory?.Key;

        NotifyHealthChanged();

        // Guard against re-entrancy: rebuilding the sidebar can raise a selection
        // change, which calls back into this method and repopulates `Categories`
        // while the outer call is still using it.
        if (_refreshingCategoryViews)
        {
            return;
        }

        _refreshingCategoryViews = true;
        try
        {
            RefreshCategoryViewsCore(selectedKey);
        }
        finally
        {
            _refreshingCategoryViews = false;
        }
    }

    private void RefreshCategoryViewsCore(string? selectedKey)
    {
        Categories.Clear();
        Categories.Add(new CategoryItem(AllCategoryKey, Loc.T("Main_CategoryAll"), null, false, Entries.Count));
        Categories.Add(new CategoryItem(
            FavoritesCategoryKey,
            Loc.T("Main_CategoryFavorites"),
            null,
            true,
            Entries.Count(entry => entry.IsFavorite)));

        // Only offered once something has been opened (same rule as weak/stale).
        var recentCount = Entries.Count(entry => _openedAt.ContainsKey(entry.Id));
        if (recentCount > 0)
        {
            Categories.Add(new CategoryItem(
                RecentCategoryKey,
                Loc.T("Main_CategoryRecent"),
                null,
                false,
                recentCount));
        }

        var issueCount = _health.IssueCount;
        if (issueCount > 0)
        {
            Categories.Add(new CategoryItem(WeakCategoryKey, Loc.T("Main_CategorySecurity"), null, false, issueCount));
        }
        if (_health.OldCount > 0)
        {
            Categories.Add(new CategoryItem(StaleCategoryKey, Loc.T("Main_CategoryStale"), null, false, _health.OldCount));
        }

        // Only offered while the bin has something in it (same rule as weak/stale).
        if (_vault.DeletedEntries.Count > 0)
        {
            Categories.Add(new CategoryItem(
                RecycleBinCategoryKey,
                Loc.T("Main_CategoryRecycleBin"),
                null,
                false,
                _vault.DeletedEntries.Count));
        }

        var directCounts = new Dictionary<Guid, int>();
        foreach (var entry in Entries)
        {
            if (entry.CategoryId is { } categoryId)
            {
                directCounts[categoryId] = directCounts.GetValueOrDefault(categoryId) + 1;
            }
        }

        foreach (var category in _vault.Categories)
        {
            Categories.Add(new CategoryItem(
                CategoryKeyPrefix + category.Id.ToString("D"),
                category.Name,
                null,
                false,
                CountBranch(category.Id, directCounts),
                CategoryKind.Category,
                category.Id,
                category.Color,
                ParentId: category.ParentId));
        }

        if (HasUncategorizedView)
        {
            Categories.Add(new CategoryItem(
                CategoryKeyPrefix,
                Loc.T("Main_Uncategorized"),
                null,
                false,
                UncategorizedCount,
                CategoryKind.Category));
        }

        var tags = Entries
            .SelectMany(entry => entry.Tags)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(tag => tag, StringComparer.OrdinalIgnoreCase);

        foreach (var tag in tags)
        {
            Categories.Add(new CategoryItem(
                "tag:" + tag,
                tag,
                tag,
                false,
                Entries.Count(entry => entry.Tags.Contains(tag, StringComparer.OrdinalIgnoreCase)),
                CategoryKind.Tag));
        }

        SelectedCategory = Categories.FirstOrDefault(category => category.Key == selectedKey) ?? Categories[0];
        RebuildCategorySections(SelectedCategory);
        RebuildCategoryNodes();
        RefreshCategoryFilter();
        RefreshCategoryChoices();
    }

    /// <summary>Entries directly in a category plus everything in its branch.</summary>
    private int CountBranch(Guid categoryId, IReadOnlyDictionary<Guid, int> directCounts)
    {
        var total = directCounts.GetValueOrDefault(categoryId);
        foreach (var category in _vault.Categories)
        {
            if (category.ParentId == categoryId)
            {
                total += CountBranch(category.Id, directCounts);
            }
        }

        return total;
    }

    /// <summary>
    /// Rebuilds the nested sidebar tree. Counts are branch totals so a collapsed
    /// parent still shows how much sits underneath it. Entry edits only refresh
    /// the row data in place, so the tree keeps its expansion state.
    /// </summary>
    private void RebuildCategoryNodes()
    {
        var signature = CategoryTreeSignature();
        if (signature == _categoryTreeSignature)
        {
            RefreshCategoryNodeItems();
            return;
        }

        _categoryTreeSignature = signature;
        CategoryNodes.Clear();
        CategoryOpenPaths.Clear();

        // Every branch starts expanded, matching the flat list this tree replaces;
        // paths are filled before the nodes are added so the NavMenu creates the
        // containers already open.
        AddOpenPaths(null, TreeNodePath.Empty);
        foreach (var category in SortedChildrenOf(null))
        {
            CategoryNodes.Add(BuildCategoryNode(category, depth: 1));
        }

        if (HasUncategorizedView)
        {
            var uncategorized = Categories.First(category =>
                category.Kind == CategoryKind.Category && category.CategoryId is null);
            CategoryNodes.Add(new CategoryNavNode(uncategorized));
        }

        RestoreSelectedCategoryNode();

        CategoryNavNode BuildCategoryNode(Category category, int depth)
        {
            var children = SortedChildrenOf(category.Id);
            var node = new CategoryNavNode(RowItemFor(category, depth, children.Count > 0));
            foreach (var child in children)
            {
                node.Entries.Add(BuildCategoryNode(child, depth + 1));
            }

            return node;
        }
    }

    /// <summary>
    /// Reorders the sidebar tree in place after a drag. Only the rows that actually
    /// moved get their container recreated; every other row (and its expansion
    /// state) stays alive. A full <see cref="RefreshCategoryViews"/> here would
    /// destroy and re-create the whole tree and replay the expand animation of
    /// every open branch, which reads as a hard flicker.
    /// </summary>
    /// <returns>
    /// False when the change cannot be expressed in place — a node appeared or
    /// disappeared, or a branch changed parent — so the caller falls back to a full
    /// rebuild (which has to construct the new subtree anyway).
    /// </returns>
    private bool TryRefreshCategoryOrder()
    {
        if (CategoryNodes is not IList roots)
        {
            return false;
        }

        if (!TrySyncSiblings(roots, parentId: null, SortedChildrenOf))
        {
            return false;
        }

        foreach (var node in EnumerateNodes(CategoryNodes))
        {
            // Entries is declared as IList<INavMenuEntry>; only the runtime type
            // (AtomUI's NavMenuEntryCollection) also implements the non-generic IList
            // that offers RemoveAt/Insert without a generic cast.
            if (node.Entries is not IList children)
            {
                return false;
            }

            if (!TrySyncSiblings(children, node.Item.CategoryId, SortedChildrenOf))
            {
                return false;
            }
        }

        _categoryTreeSignature = CategoryTreeSignature();
        return true;

        /// <summary>
        /// Reorders one sibling collection in place. Takes the non-generic
        /// <see cref="IList"/> because the roots are an
        /// <c>ObservableCollection&lt;CategoryNavNode&gt;</c> while a node's children
        /// are AtomUI's own <c>NavMenuEntryCollection</c>; both implement it, and it is
        /// the only common surface with index access + remove/insert.
        /// </summary>
        static bool TrySyncSiblings(
            IList target,
            Guid? parentId,
            Func<Guid?, List<Category>> childrenOf)
        {
            var nodes = target.OfType<CategoryNavNode>().ToList();

            // The uncategorized pseudo row rides at the end and never reorders; it is
            // not part of the category list, so keep it out of the comparison.
            var pseudo = nodes.Where(node => node.Item.CategoryId is null).ToList();

            var desired = childrenOf(parentId)
                .Select(category => nodes.FirstOrDefault(node => node.Item.CategoryId == category.Id))
                .OfType<CategoryNavNode>()
                .Concat(pseudo)
                .ToList();

            // A different count means a category appeared or disappeared here; that
            // needs a real node, not a reorder.
            if (desired.Count != target.Count)
            {
                return false;
            }

            for (var index = 0; index < desired.Count; index++)
            {
                var current = target.IndexOf(desired[index]);
                if (current == index)
                {
                    continue;
                }

                // AtomUI's node collection has no Move, so a reorder is always a
                // remove + insert. That recreates exactly one row container and
                // keeps the rest of the tree intact.
                target.RemoveAt(current);
                target.Insert(index, desired[index]);
            }

            return true;
        }
    }

    private void AddOpenPaths(Guid? parentId, TreeNodePath parentPath)
    {
        foreach (var category in SortedChildrenOf(parentId))
        {
            var path = parentPath.Append(CategoryKeyPrefix + category.Id.ToString("D"));
            CategoryOpenPaths.Add(path);
            AddOpenPaths(category.Id, path);
        }
    }

    private CategoryItem RowItemFor(Category category, int depth, bool hasChildren)        => Categories.First(candidate => candidate.CategoryId == category.Id) with
        {
            Depth = depth,
            HasChildren = hasChildren,
        };

    /// <summary>
    /// Entries that have no category. The "uncategorized" sidebar row is a pseudo
    /// row derived from entry data, so its count comes from here.
    /// </summary>
    private int UncategorizedCount => Entries.Count(entry => entry.CategoryId is null);

    /// <summary>
    /// Whether the sidebar shows the "uncategorized" pseudo row. Its existence is
    /// derived from entry data, so this must be the single source of truth for
    /// every decision about the row — including <see cref="CategoryTreeSignature"/>,
    /// otherwise the row could never appear/disappear without a full restart.
    /// </summary>
    private bool HasUncategorizedView => _vault.Categories.Count > 0 && UncategorizedCount > 0;

    /// <summary>
    /// Identity of the tree shape; entry counts deliberately excluded. Anything that
    /// changes the *set* of nodes (not just their data) must be part of this signature,
    /// because the incremental refresh can only update existing rows.
    /// </summary>
    private string CategoryTreeSignature()
        => string.Join(
            '|',
            _vault.Categories
                .OrderBy(category => category.Id)
                .Select(category => $"{category.Id:D}:{category.ParentId:D}:{category.SortOrder}:{category.Name}"))
           + $"|uncat:{HasUncategorizedView}";

    private void RefreshCategoryNodeItems()
    {
        foreach (var node in EnumerateNodes(CategoryNodes))
        {
            if (node.Item.CategoryId is { } id &&
                _vault.Categories.FirstOrDefault(category => category.Id == id) is { } category)
            {
                node.Item = RowItemFor(category, node.Item.Depth, node.Item.HasChildren);
            }
            else if (node.Item.CategoryId is null &&
                     Categories.FirstOrDefault(candidate =>
                         candidate.Kind == CategoryKind.Category && candidate.CategoryId is null) is { } uncategorized)
            {
                node.Item = uncategorized;
            }
        }
    }

    private static IEnumerable<CategoryNavNode> EnumerateNodes(IEnumerable<CategoryNavNode> nodes)
    {
        foreach (var node in nodes)
        {
            yield return node;
            foreach (var nested in EnumerateNodes(node.Entries.OfType<CategoryNavNode>()))
            {
                yield return nested;
            }
        }
    }

    private void RestoreSelectedCategoryNode()
    {
        var selectedKey = SelectedCategory?.Key;
        var restored = selectedKey is null ? null : FindNode(CategoryNodes, selectedKey);
        if (ReferenceEquals(restored, SelectedCategoryNode))
        {
            return;
        }

        _syncingCategorySelection = true;
        try
        {
            SelectedCategoryNode = restored;
        }
        finally
        {
            _syncingCategorySelection = false;
        }
    }

    private List<Category> SortedChildrenOf(Guid? parentId)
        => _vault.Categories
            .Where(category => category.ParentId == parentId)
            .OrderBy(category => category.SortOrder > 0 ? category.SortOrder : int.MaxValue)
            .ThenBy(category => category.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static CategoryNavNode? FindNode(IEnumerable<CategoryNavNode> nodes, string key)
    {
        foreach (var node in nodes)
        {
            if (node.Item.Key == key)
            {
                return node;
            }

            if (FindNode(node.Entries.OfType<CategoryNavNode>(), key) is { } nested)
            {
                return nested;
            }
        }

        return null;
    }

    private void RebuildCategorySections(CategoryItem? selected)
    {
        SystemCategories.Clear();
        UserCategories.Clear();
        TagCategories.Clear();

        // Snapshot first: the section collections are bound to the sidebar, and a
        // selection change raised while adding can refresh categories again. Enumerating
        // the live ObservableCollection then throws "Collection was modified".
        var snapshot = Categories.ToArray();
        foreach (var category in snapshot)
        {
            switch (category.Kind)
            {
                case CategoryKind.System:
                    SystemCategories.Add(category);
                    break;
                case CategoryKind.Category:
                    UserCategories.Add(category);
                    break;
                default:
                    TagCategories.Add(category);
                    break;
            }
        }

        SelectedSystemCategory = null;
        SelectedUserCategory = null;
        SelectedTagCategory = null;

        switch (selected?.Kind)
        {
            case CategoryKind.System:
                SelectedSystemCategory = SystemCategories.FirstOrDefault(category => category.Key == selected.Key);
                break;
            case CategoryKind.Category:
                SelectedUserCategory = UserCategories.FirstOrDefault(category => category.Key == selected.Key);
                break;
            case CategoryKind.Tag:
                SelectedTagCategory = TagCategories.FirstOrDefault(category => category.Key == selected.Key);
                break;
        }
    }

    private void RefreshCategoryChoices()
    {
        var selectedId = SelectedCategoryChoice?.Id;

        CategoryChoices.Clear();
        CategoryChoices.Add(new CategoryChoice(null, Loc.T("Main_NoCategory")));
        foreach (var category in _vault.Categories)
        {
            CategoryChoices.Add(new CategoryChoice(category.Id, category.Name));
        }

        SelectedCategoryChoice = CategoryChoices.FirstOrDefault(choice => choice.Id == selectedId) ?? CategoryChoices[0];
    }

    /// <summary>Creates a user category; returns false with a localized reason when invalid.</summary>
    public bool TryCreateCategory(string? name, out string? error, string? color = null, Guid? parentId = null)
    {
        error = ValidateCategoryName(name, excludeId: null, parentId);
        if (error is not null)
        {
            return false;
        }

        var category = AddCategorySafely(name!.Trim(), color, parentId, ref error);
        if (category is null)
        {
            return false;
        }

        RefreshCategoryViews();
        SelectedCategoryChoice = CategoryChoices.First(choice => choice.Id == category.Id);
        StatusMessage = Loc.T("Main_StatusCategoryCreated");
        return true;
    }

    /// <summary>Adds a category, turning a Core rejection into a localized reason.</summary>
    private Category? AddCategorySafely(string name, string? color, Guid? parentId, ref string? error)
    {
        try
        {
            return _vault.AddCategory(name, color, parentId);
        }
        catch (CategoryValidationException exception)
        {
            error = LocalizeCategoryError(exception.Error);
            return null;
        }
        catch (ArgumentException)
        {
            error = Loc.T("Main_CategoryOperationFailed");
            return null;
        }
    }

    /// <summary>Moves a category to another parent (null = top level).</summary>
    public bool TryMoveCategory(Guid id, Guid? parentId, out string? error)
    {
        error = null;

        var category = _vault.Categories.FirstOrDefault(candidate => candidate.Id == id);
        if (category is null)
        {
            error = Loc.T("Main_CategoryMissing");
            return false;
        }

        // Same rule as create/rename: a parent may not end up with two children
        // called the same. Checked here so the dialog can stay open with a
        // localized reason instead of surfacing Core's English message.
        error = ValidateCategoryName(category.Name, excludeId: id, parentId);
        if (error is not null)
        {
            return false;
        }

        try
        {
            if (_vault.MoveCategory(id, parentId))
            {
                RefreshCategoryViews();
                StatusMessage = Loc.T("Main_StatusCategoryMoved");
                return true;
            }

            error = Loc.T("Main_CategoryMissing");
            return false;
        }
        catch (CategoryValidationException exception)
        {
            error = LocalizeCategoryError(exception.Error);
            return false;
        }
        catch (ArgumentException)
        {
            error = Loc.T("Main_CategoryOperationFailed");
            return false;
        }
    }

    /// <summary>Merges every entry and child of one category into another.</summary>
    public bool TryMergeCategory(Guid sourceId, Guid targetId, out string? error)
    {
        error = null;
        try
        {
            if (!_vault.MergeCategories(sourceId, targetId))
            {
                error = Loc.T("Main_CategoryMissing");
                return false;
            }

            ReloadFromVault();
            StatusMessage = Loc.T("Main_StatusCategoryMerged");
            return true;
        }
        catch (CategoryValidationException exception)
        {
            error = LocalizeCategoryError(exception.Error);
            return false;
        }
        catch (ArgumentException)
        {
            error = Loc.T("Main_CategoryOperationFailed");
            return false;
        }
    }

    /// <summary>Maps a language-neutral Core rejection to user-facing text.</summary>
    private static string LocalizeCategoryError(CategoryValidationError error) => error switch
    {
        CategoryValidationError.NameEmpty => Loc.T("Main_CategoryNameRequired"),
        CategoryValidationError.NameDuplicate => Loc.T("Main_CategoryNameDuplicate"),
        CategoryValidationError.ParentMissing => Loc.T("Main_CategoryMissing"),
        CategoryValidationError.DepthExceeded => Loc.T("Main_CategoryTooDeep"),
        _ => Loc.T("Main_CategoryOperationFailed"),
    };

    /// <summary>Selects a category in the sidebar tree by id.</summary>
    public void SelectCategoryNode(Guid categoryId)
    {
        var key = CategoryKeyPrefix + categoryId.ToString("D");
        var node = FindNode(CategoryNodes, key);
        if (node is not null)
        {
            SelectedCategoryNode = node;
        }
    }

    /// <summary>First level of the sidebar tree, for the parent pickers.</summary>
    public IEnumerable<CategoryNavNode> CategoryTree => CategoryNodes;

    /// <summary>Applies a new palette/hex colour to a category.</summary>
    public void SetCategoryColor(Guid id, string? color)
    {
        if (!_vault.SetCategoryColor(id, color))
        {
            return;
        }

        RefreshCategoryViews();
    }

    /// <summary>Renames a user category; returns false with a localized reason when invalid.</summary>
    public bool TryRenameCategory(Guid id, string? name, out string? error)
    {
        var parentId = _vault.Categories.FirstOrDefault(category => category.Id == id)?.ParentId;
        error = ValidateCategoryName(name, excludeId: id, parentId);
        if (error is not null)
        {
            return false;
        }

        if (!_vault.RenameCategory(id, name!.Trim()))
        {
            error = Loc.T("Main_CategoryMissing");
            return false;
        }

        RefreshCategoryViews();
        StatusMessage = Loc.T("Main_StatusCategoryRenamed");
        return true;
    }

    public void DeleteCategory(Guid id)
        => DeleteCategory(id, CategoryDeleteMode.PromoteChildren);

    public void DeleteCategory(Guid id, CategoryDeleteMode mode)
    {
        CategoryDeleteResult result;
        try
        {
            result = _vault.DeleteCategory(id, mode);
        }
        catch (InvalidOperationException)
        {
            return;
        }

        if (!result.Removed)
        {
            return;
        }

        var wasSelected = SelectedCategory?.CategoryId == id;
        ReloadFromVault();
        if (wasSelected)
        {
            SelectedCategory = Categories[0];
        }

        // Promoted children that clashed keep their data but change name; say so
        // instead of silently renaming something the user named.
        StatusMessage = result.Renamed.Count > 0
            ? Loc.Format("Main_StatusCategoryPromoted", string.Join(", ", result.Renamed.Select(rename => rename.To)))
            : mode == CategoryDeleteMode.Cascade
                ? Loc.T("Main_StatusCategoryBranchDeleted")
                : Loc.T("Main_StatusCategoryDeleted");
    }

    /// <summary>Child categories and entries a delete would touch.</summary>
    public (int Children, int Entries) DescribeCategory(Guid id)
    {
        var subtree = _vault.GetCategorySubtree(id);
        return (subtree.Count - 1, Entries.Count(entry => entry.CategoryId is { } categoryId && subtree.Contains(categoryId)));
    }

    /// <summary>Validates a category name for the create/rename dialogs (null = valid).</summary>
    public string? ValidateCategoryName(string? name, Guid? excludeId = null, Guid? parentId = null)
    {
        var trimmed = name?.Trim() ?? string.Empty;
        if (trimmed.Length == 0)
        {
            return Loc.T("Main_CategoryNameRequired");
        }

        var duplicate = _vault.Categories.Any(category =>
            category.Id != excludeId &&
            category.ParentId == parentId &&
            string.Equals(category.Name, trimmed, StringComparison.OrdinalIgnoreCase));
        return duplicate ? Loc.T("Main_CategoryNameDuplicate") : null;
    }

    /// <summary>Ids of a category and everything nested under it.</summary>
    public IReadOnlySet<Guid> GetCategorySubtreeIds(Guid id) => _vault.GetCategorySubtree(id);

    /// <summary>
    /// Drops <paramref name="id"/> before one of its own siblings (sidebar
    /// reorder). Returns false when the pair is not a valid drop.
    /// </summary>
    public bool TryReorderCategory(Guid id, Guid? beforeId)
    {
        if (!_vault.ReorderCategory(id, beforeId))
        {
            return false;
        }

        if (!TryRefreshCategoryOrder())
        {
            RefreshCategoryViews();
        }

        StatusMessage = Loc.T("Main_StatusCategoryReordered");
        return true;
    }

    /// <summary>
    /// Drop handler for the sidebar: onto a row (re-parent into it) or between
    /// two rows (reorder among siblings). Returns a localized failure reason, or
    /// null when the drop was applied (or was a harmless no-op).
    /// </summary>
    public string? DropCategory(Guid id, Guid? newParentId, Guid? beforeId)
    {
        var current = _vault.Categories.FirstOrDefault(candidate => candidate.Id == id);
        if (current is null)
        {
            return Loc.T("Main_CategoryMissing");
        }

        if (newParentId == current.ParentId)
        {
            // Same parent: this is a pure reorder. Dropping a row on itself is not a
            // position; treat it as a no-op rather than a failure.
            if (beforeId == id)
            {
                return null;
            }

            if (!_vault.ReorderCategory(id, beforeId))
            {
                return Loc.T("Main_CategoryOperationFailed");
            }

            if (!TryRefreshCategoryOrder())
            {
                RefreshCategoryViews();
            }

            StatusMessage = Loc.T("Main_StatusCategoryReordered");
            return null;
        }

        return TryMoveCategory(id, newParentId, out var error) ? null : error;
    }

    /// <summary>
    /// Whether a drag may end on <paramref name="targetId"/>: not itself, and not
    /// inside its own subtree (which would detach the branch from the tree).
    /// </summary>
    public bool CanDropCategoryOn(Guid id, Guid targetId)
        => id != targetId && !_vault.GetCategorySubtree(id).Contains(targetId);

    /// <summary>Categories that may become the parent of <paramref name="movingId"/>.</summary>
    public IReadOnlyList<CategoryChoice> BuildParentChoices(Guid? movingId)    {
        var excluded = movingId is { } id ? _vault.GetCategorySubtree(id) : new HashSet<Guid>();
        var choices = new List<CategoryChoice> { new(null, Loc.T("Main_CategoryTopLevel")) };
        AddLevel(null, 0);
        return choices;

        void AddLevel(Guid? parentId, int depth)
        {
            if (depth >= 3)
            {
                return;
            }

            foreach (var category in SortedChildrenOf(parentId))
            {
                if (!excluded.Contains(category.Id))
                {
                    choices.Add(new CategoryChoice(category.Id, new string(' ', depth * 2) + category.Name));
                    AddLevel(category.Id, depth + 1);
                }
            }
        }
    }

    /// <summary>
    /// Human-readable age of the password ("N 天前"/"today"). Uses the injected
    /// clock so the label is testable and matches the health analysis.
    /// </summary>
    private string FormatPasswordAge(DateTimeOffset updatedAt)
    {
        var days = (int)Math.Floor((_timeProvider.GetUtcNow() - updatedAt).TotalDays);
        if (days <= 0)
        {
            return Loc.T("Main_PasswordAgeToday");
        }

        return days == 1
            ? Loc.T("Main_PasswordAgeYesterday")
            : Loc.Format("Main_PasswordAgeDays", days);
    }

    private void ApplyFilter()
    {
        if (_loadingEditor)
        {
            return;
        }

        // Replacing the bound list makes the ListBox drop its selection and
        // write null back, so remember it first and restore it when it still
        // matches the new filter.
        var previousSelection = SelectedEntry;
        var query = SearchText?.Trim() ?? string.Empty;

        // The recycle bin is sourced from the vault's recycled entries rather than
        // the live list, and always shows the most recently deleted first.
        IEnumerable<PasswordEntry> ordered;
        if (IsViewRecycleBin)
        {
            ordered = _vault.DeletedEntries
                .Where(entry => MatchesSearch(entry, query))
                .OrderByDescending(static entry => entry.DeletedAt);
        }
        else if (IsViewRecent)
        {
            // Fixed order (most recently viewed first, top N) — the sort toggle
            // does not apply to this view.
            ordered = Entries
                .Where(entry => _openedAt.ContainsKey(entry.Id) && MatchesSearch(entry, query))
                .OrderByDescending(entry => _openedAt[entry.Id])
                .Take(RecentViewLimit);
        }
        else
        {
            var matches = Entries.Where(entry => MatchesCategory(entry) && MatchesSearch(entry, query));
            ordered = SortMode == EntrySortMode.RecentlyUpdated
                ? matches.OrderByDescending(static entry => entry.UpdatedAt)
                : matches.OrderByDescending(static entry => entry.IsFavorite)
                    .ThenBy(static entry => entry.Title, StringComparer.OrdinalIgnoreCase);
        }

        // One notification instead of Clear+Add per entry: on large vaults the
        // per-item collection changes dominated every keystroke.
        FilteredEntries = ordered.ToList();

        if (previousSelection is not null && FilteredEntries.Contains(previousSelection))
        {
            SelectedEntry = previousSelection;
        }
        else if (SelectedEntry is not null && !FilteredEntries.Contains(SelectedEntry))
        {
            SelectedEntry = null;
        }

        OnPropertyChanged(nameof(HasFilteredEntries));
        OnPropertyChanged(nameof(FilteredCountText));
        OnPropertyChanged(nameof(IsVaultEmpty));
        OnPropertyChanged(nameof(ShowVaultHero));
        OnPropertyChanged(nameof(ShowListEmptyHint));
        OnPropertyChanged(nameof(ShowSelectEntryHint));
        OnPropertyChanged(nameof(ShowStartupGuide));
    }

    private bool MatchesCategory(PasswordEntry entry)
    {
        if (SelectedCategory is null || SelectedCategory.Key == AllCategoryKey)
        {
            return true;
        }

        if (SelectedCategory.Key == WeakCategoryKey)
        {
            return _health.WeakEntryIds.Contains(entry.Id) || _health.ReusedEntryIds.Contains(entry.Id);
        }

        if (SelectedCategory.Key == StaleCategoryKey)
        {
            return _health.OldEntryIds.Contains(entry.Id);
        }

        if (SelectedCategory.Key.StartsWith(CategoryKeyPrefix, StringComparison.Ordinal))
        {
            var suffix = SelectedCategory.Key[CategoryKeyPrefix.Length..];
            if (suffix.Length == 0)
            {
                return entry.CategoryId is null;
            }

            return entry.CategoryId is { } categoryId && _categoryFilterIds.Contains(categoryId);
        }

        if (SelectedCategory.IsFavorites)
        {
            return entry.IsFavorite;
        }

        return SelectedCategory.Tag is { } tag &&
               entry.Tags.Contains(tag, StringComparer.OrdinalIgnoreCase);
    }

    private bool MatchesSearch(PasswordEntry entry, string query)
    {
        if (query.Length == 0)
        {
            return true;
        }

        // Secrets (Password/Secret/ApiKey) never participate in search so they
        // cannot leak into the quick-access list or the results header.
        return Contains(entry.Title, query) ||
               Contains(entry.Username, query) ||
               Contains(entry.Phone, query) ||
               Contains(entry.Email, query) ||
               Contains(entry.AppId, query) ||
               Contains(entry.Url, query) ||
               (entry.Urls?.Any(url => Contains(url, query)) ?? false) ||
               entry.Tags.Any(tag => Contains(tag, query));
    }

    private void LoadEditor(PasswordEntry? entry)
    {
        _loadingEditor = true;
        try
        {
            EditTitle = entry?.Title ?? string.Empty;
            EditUsername = entry?.Username ?? string.Empty;
            EditPassword = entry?.Password ?? string.Empty;
            var urls = entry is null ? [] : EffectiveUrls(entry);
            EditUrls = string.Join("\n", urls);
            UrlItems.Clear();
            foreach (var url in urls)
            {
                UrlItems.Add(url);
            }

            EditNotes = entry?.Notes ?? string.Empty;
            EditTotpSecret = entry?.TotpSecret ?? string.Empty;
            EditPhone = entry?.Phone ?? string.Empty;
            EditEmail = entry?.Email ?? string.Empty;
            EditAppId = entry?.AppId ?? string.Empty;
            EditSecret = entry?.Secret ?? string.Empty;
            EditApiKey = entry?.ApiKey ?? string.Empty;
            EditTags = entry is null ? string.Empty : string.Join(", ", entry.Tags);
            EditIsFavorite = entry?.IsFavorite ?? false;
            SelectedCategoryChoice = CategoryChoices.FirstOrDefault(choice => choice.Id == entry?.CategoryId)
                ?? CategoryChoices.FirstOrDefault();
            TitleError = string.Empty;
            UrlError = string.Empty;
            CreatedAtDisplay = entry is null
                ? string.Empty
                : entry.CreatedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
            PasswordAgeDisplay = entry is null
                ? string.Empty
                : FormatPasswordAge(entry.UpdatedAt);
            PasswordHistory = entry?.PasswordHistory ?? [];
            HasPasswordHistory = PasswordHistory.Count > 0;
            StatusMessage = null;
        }
        finally
        {
            _loadingEditor = false;
        }

        IsPasswordRevealed = false;
        IsSecretRevealed = false;
        IsApiKeyRevealed = false;
        IsTotpSecretRevealed = false;
        OnPropertyChanged(nameof(PasswordPreview));
        OnPropertyChanged(nameof(SecretPreview));
        OnPropertyChanged(nameof(ApiKeyPreview));
        UpdateTotp();
    }

    private void ReplaceInList(PasswordEntry updated)
    {
        var previous = SelectedEntry;
        if (previous is null)
        {
            return;
        }

        var allIndex = Entries.IndexOf(previous);
        if (allIndex >= 0)
        {
            Entries[allIndex] = updated;
        }

        var filteredIndex = FilteredEntries.IndexOf(previous);
        if (filteredIndex >= 0)
        {
            FilteredEntries[filteredIndex] = updated;
        }
    }

    partial void OnClipboardToastRemainingChanged(int value) => OnPropertyChanged(nameof(ClipboardToastText));

    /// <summary>Current theme mode: "system", "light" or "dark".</summary>
    [ObservableProperty]
    private string themePreference = "system";

    /// <summary>Current language preference: "system", "zh" or "en".</summary>
    [ObservableProperty]
    private string languagePreference = Loc.System;

    /// <summary>Wires the shell callbacks that persist and apply appearance changes.</summary>
    public void AttachAppearanceCallbacks(Action<string>? applyTheme, Action<string>? applyLanguage)
    {
        _applyTheme = applyTheme;
        _applyLanguage = applyLanguage;
    }

    [RelayCommand]
    private void SetTheme(string? theme)
    {
        if (string.IsNullOrEmpty(theme))
        {
            return;
        }

        ThemePreference = theme;
        _applyTheme?.Invoke(theme);
    }

    [RelayCommand]
    private void SetLanguage(string? language)
    {
        if (string.IsNullOrEmpty(language))
        {
            return;
        }

        LanguagePreference = language;
        _applyLanguage?.Invoke(language);
    }

    private void UpdateTotp()
    {
        if (_loadingEditor)
        {
            return;
        }

        if (!HasTotp)
        {
            TotpCode = string.Empty;
            TotpRemaining = 0;
            return;
        }

        try
        {
            var now = _timeProvider.GetUtcNow();
            TotpCode = TotpService.GenerateCode(EditTotpSecret, now);
            TotpRemaining = TotpService.GetRemainingSeconds(now);
        }
        catch (Exception exception) when (exception is FormatException or ArgumentException)
        {
            TotpCode = Loc.T("Main_StatusInvalidTotp");
            TotpRemaining = 0;
        }
    }

    private static bool Contains(string? value, string query)
        => value is not null && value.Contains(query, StringComparison.OrdinalIgnoreCase);
    private static List<string> ParseTags(string text)
        => [.. text.Split([',', '，'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)];
}
