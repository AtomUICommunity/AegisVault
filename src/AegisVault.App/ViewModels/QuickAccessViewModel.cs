using System.Collections.ObjectModel;
using System.Collections.Specialized;
using AegisVault.App.Services;
using AegisVault.Core.Models;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AegisVault.App.ViewModels;

/// <summary>
/// Backs the global quick-access overlay: type-to-filter entries and copy or
/// jump to the selected one, entirely from the keyboard.
/// </summary>
public partial class QuickAccessViewModel : ObservableObject, IDisposable
{
    private readonly MainViewModel _main;
    private readonly ClipboardService? _clipboard;
    private readonly IUrlLauncher _urlLauncher;
    private bool _isActive = true;
    private bool _filterDirty;

    /// <summary>Raised when the overlay should hide itself.</summary>
    public event Action? HideRequested;

    /// <summary>Raised after an entry was activated (jump to main window).</summary>
    public event Action? EntryActivated;

    public QuickAccessViewModel(MainViewModel main, ClipboardService? clipboard = null, IUrlLauncher? urlLauncher = null)
    {
        _main = main;
        _clipboard = clipboard;
        _urlLauncher = urlLauncher ?? SystemUrlLauncher.Instance;
        _main.Entries.CollectionChanged += OnEntriesChanged;
        ApplyFilter();
    }

    public void Dispose() => _main.Entries.CollectionChanged -= OnEntriesChanged;

    public ObservableCollection<PasswordEntry> FilteredEntries { get; } = [];

    [ObservableProperty]
    private string searchText = string.Empty;

    [ObservableProperty]
    private PasswordEntry? selectedEntry;

    public bool HasResults => FilteredEntries.Count > 0;

    partial void OnSearchTextChanged(string value) => ApplyFilter();

    partial void OnSelectedEntryChanged(PasswordEntry? value) => OnPropertyChanged(nameof(HasSelection));

    public bool HasSelection => SelectedEntry is not null;

    /// <summary>Selects the highlighted entry in the main window and hides the overlay.</summary>
    [RelayCommand]
    private void Activate()
    {
        if (SelectedEntry is null)
        {
            return;
        }

        _main.SelectedEntry = SelectedEntry;
        EntryActivated?.Invoke();
        HideRequested?.Invoke();
    }

    [RelayCommand]
    private async Task CopyPasswordAsync(PasswordEntry? entry)
    {
        if (_clipboard is null || string.IsNullOrEmpty(entry?.Password))
        {
            return;
        }

        await _clipboard.CopyAsync(entry.Password);
    }

    [RelayCommand]
    private async Task CopyUsernameAsync(PasswordEntry? entry)
    {
        if (_clipboard is null || string.IsNullOrEmpty(entry?.Username))
        {
            return;
        }

        await _clipboard.CopyAsync(entry.Username);
    }

    /// <summary>
    /// Copies the entry password and opens its URL (Ctrl+Enter in the overlay),
    /// then hides the overlay so the browser can take focus.
    /// </summary>
    [RelayCommand]
    private async Task CopyPasswordAndOpenAsync(PasswordEntry? entry)
    {
        var url = entry is null ? null : WebUrl.Normalize(entry.Url);
        if (url is null || url.Length == 0 || !_urlLauncher.IsSupported(url))
        {
            return;
        }

        if (_clipboard is not null && !string.IsNullOrEmpty(entry!.Password))
        {
            await _clipboard.CopyAsync(entry.Password);
        }

        if (_urlLauncher.TryOpen(url))
        {
            HideRequested?.Invoke();
        }
    }

    public void MoveSelection(int delta)
    {
        if (FilteredEntries.Count == 0)
        {
            return;
        }

        var index = SelectedEntry is null ? 0 : FilteredEntries.IndexOf(SelectedEntry);
        if (index < 0)
        {
            index = 0;
        }

        index = Math.Clamp(index + delta, 0, FilteredEntries.Count - 1);
        SelectedEntry = FilteredEntries[index];
    }

    private void OnEntriesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        // While prewarmed-but-hidden, only mark the filter dirty: duplicating the
        // main list's filtering on every change would be pure waste.
        if (!_isActive)
        {
            _filterDirty = true;
            return;
        }

        ApplyFilter();
    }

    /// <summary>
    /// Tells the view model whether the overlay is on screen. <c>false</c>
    /// suspends eager re-filtering until the next activation.
    /// </summary>
    public void SetActive(bool active)
    {
        _isActive = active;
        if (active && _filterDirty)
        {
            ApplyFilter();
        }
    }

    private void ApplyFilter()
    {
        _filterDirty = false;
        var query = SearchText?.Trim() ?? string.Empty;
        FilteredEntries.Clear();
        foreach (var entry in _main.Entries)
        {
            if (query.Length == 0 ||
                Contains(entry.Title, query) ||
                Contains(entry.Username, query) ||
                Contains(entry.Url, query) ||
                (entry.Urls?.Any(url => Contains(url, query)) ?? false) ||
                entry.Tags.Any(tag => Contains(tag, query)))
            {
                FilteredEntries.Add(entry);
            }
        }

        if (SelectedEntry is not null && !FilteredEntries.Contains(SelectedEntry))
        {
            SelectedEntry = FilteredEntries.Count > 0 ? FilteredEntries[0] : null;
        }

        OnPropertyChanged(nameof(HasResults));
    }

    private static bool Contains(string? value, string query)
        => value is not null && value.Contains(query, StringComparison.OrdinalIgnoreCase);
}
