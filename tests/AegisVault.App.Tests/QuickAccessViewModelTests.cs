using AegisVault.App.Services;
using AegisVault.App.ViewModels;
using AegisVault.App.Views;
using AegisVault.Core.Models;
using AegisVault.Core.Services;
using Avalonia.Controls;
using Xunit;

namespace AegisVault.App.Tests;

public sealed class QuickAccessViewModelTests : IDisposable
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

    public QuickAccessViewModelTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "aegis-ui-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
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

    private (VaultService Vault, MainViewModel Main) CreateMain(params PasswordEntry[] entries)
    {
        var vault = VaultService.CreateNew(Path.Combine(_directory, Guid.NewGuid().ToString("N") + ".aegis"), Password, FastOptions);
        foreach (var entry in entries)
        {
            vault.AddEntry(entry);
        }

        return (vault, new MainViewModel(vault));
    }

    [Fact]
    public void FiltersByTitleUsernameUrlAndTags()
    {
        var (vault, main) = CreateMain(
            new PasswordEntry { Title = "GitHub", Username = "alice", Url = "https://github.com" },
            new PasswordEntry { Title = "GitLab", Username = "bob", Tags = ["work"] },
            new PasswordEntry { Title = "Email", Username = "carol" });
        using var _ = vault;
        using var __ = main;
        var viewModel = new QuickAccessViewModel(main);

        Assert.Equal(3, viewModel.FilteredEntries.Count);

        viewModel.SearchText = "git";
        Assert.Equal(2, viewModel.FilteredEntries.Count);

        viewModel.SearchText = "work";
        Assert.Single(viewModel.FilteredEntries);
        Assert.Equal("GitLab", viewModel.FilteredEntries[0].Title);

        viewModel.SearchText = "alice";
        Assert.Single(viewModel.FilteredEntries);
        Assert.Equal("GitHub", viewModel.FilteredEntries[0].Title);

        viewModel.SearchText = "zzz";
        Assert.Empty(viewModel.FilteredEntries);
        Assert.False(viewModel.HasResults);
    }

    [Fact]
    public void SearchesEntriesWhoseUrlsLiveInTheCollection()
    {
        var (vault, main) = CreateMain(new PasswordEntry
        {
            Title = "Legacy",
            Url = string.Empty,
            Urls = ["https://gist.github.com"],
        });
        using var _ = vault;
        using var __ = main;
        var viewModel = new QuickAccessViewModel(main);

        viewModel.SearchText = "gist";

        Assert.Single(viewModel.FilteredEntries);
        Assert.Equal("Legacy", viewModel.FilteredEntries[0].Title);
    }

    [Fact]
    public void ActivateSelectsEntryInMainAndRaisesHide()
    {
        var (vault, main) = CreateMain(
            new PasswordEntry { Title = "GitHub" },
            new PasswordEntry { Title = "Email" });
        using var _ = vault;
        using var __ = main;
        var viewModel = new QuickAccessViewModel(main);

        var hidden = false;
        viewModel.HideRequested += () => hidden = true;

        viewModel.SearchText = "email";
        viewModel.SelectedEntry = viewModel.FilteredEntries[0];
        viewModel.ActivateCommand.Execute(null);

        Assert.True(hidden);
        Assert.Equal("Email", main.SelectedEntry?.Title);
    }

    [Fact]
    public void MoveSelectionClampsToRange()
    {
        var (vault, main) = CreateMain(
            new PasswordEntry { Title = "A" },
            new PasswordEntry { Title = "B" });
        using var _ = vault;
        using var __ = main;
        var viewModel = new QuickAccessViewModel(main);

        viewModel.MoveSelection(1);
        Assert.Equal("B", viewModel.SelectedEntry?.Title);

        viewModel.MoveSelection(10);
        Assert.Equal("B", viewModel.SelectedEntry?.Title);

        viewModel.MoveSelection(-10);
        Assert.Equal("A", viewModel.SelectedEntry?.Title);
    }

    [Fact]
    public async Task CopyPasswordWritesClipboard()
    {
        var (vault, main) = CreateMain(new PasswordEntry { Title = "GitHub", Password = "s3cret" });
        var fake = new FakeClipboardAccess();
        using var service = new ClipboardService(fake, () => new UserConfig { ClipboardClearSeconds = 30 });
        using var __ = main;
        try
        {
            var viewModel = new QuickAccessViewModel(main, service);

            await viewModel.CopyPasswordCommand.ExecuteAsync(viewModel.FilteredEntries[0]);
            Assert.Equal("s3cret", fake.Text);
        }
        finally
        {
            vault.Dispose();
        }
    }

    [Fact]
    public async Task CopyPasswordAndOpenCopiesLaunchesAndHides()
    {
        var (vault, main) = CreateMain(new PasswordEntry
        {
            Title = "GitHub",
            Password = "s3cret",
            Url = "https://github.com/login",
        });
        var fake = new FakeClipboardAccess();
        using var service = new ClipboardService(fake, () => new UserConfig { ClipboardClearSeconds = 30 });
        using var __ = main;
        try
        {
            var launcher = new FakeUrlLauncher();
            var viewModel = new QuickAccessViewModel(main, service, launcher);
            var hidden = false;
            viewModel.HideRequested += () => hidden = true;

            await viewModel.CopyPasswordAndOpenCommand.ExecuteAsync(viewModel.FilteredEntries[0]);

            Assert.Equal("s3cret", fake.Text);
            Assert.Equal(["https://github.com/login"], launcher.Opened);
            Assert.True(hidden);
        }
        finally
        {
            vault.Dispose();
        }
    }

    [Fact]
    public async Task CopyPasswordAndOpenIsNoOpWithoutLaunchableUrl()
    {
        var (vault, main) = CreateMain(new PasswordEntry
        {
            Title = "Email",
            Password = "s3cret",
            Url = "not-a-url",
        });
        var fake = new FakeClipboardAccess();
        using var service = new ClipboardService(fake, () => new UserConfig { ClipboardClearSeconds = 30 });
        using var __ = main;
        try
        {
            var launcher = new FakeUrlLauncher();
            var viewModel = new QuickAccessViewModel(main, service, launcher);
            var hidden = false;
            viewModel.HideRequested += () => hidden = true;

            await viewModel.CopyPasswordAndOpenCommand.ExecuteAsync(viewModel.FilteredEntries[0]);
            await viewModel.CopyPasswordAndOpenCommand.ExecuteAsync(null);

            Assert.Null(fake.Text);
            Assert.Empty(launcher.Opened);
            Assert.False(hidden);
        }
        finally
        {
            vault.Dispose();
        }
    }

    [Fact]
    public async Task CopyPasswordAndOpenNormalizesSchemeLessUrl()
    {
        var (vault, main) = CreateMain(new PasswordEntry
        {
            Title = "GitHub",
            Password = "s3cret",
            Url = "github.com",
        });
        var fake = new FakeClipboardAccess();
        using var service = new ClipboardService(fake, () => new UserConfig { ClipboardClearSeconds = 30 });
        using var __ = main;
        try
        {
            var launcher = new FakeUrlLauncher();
            var viewModel = new QuickAccessViewModel(main, service, launcher);
            var hidden = false;
            viewModel.HideRequested += () => hidden = true;

            await viewModel.CopyPasswordAndOpenCommand.ExecuteAsync(viewModel.FilteredEntries[0]);

            Assert.Equal("s3cret", fake.Text);
            Assert.Equal(["https://github.com"], launcher.Opened);
            Assert.True(hidden);
        }
        finally
        {
            vault.Dispose();
        }
    }

    [Fact]
    public Task QuickAccessWindowCanBeConstructed() => Headless.Run(() =>
    {
        var (vault, main) = CreateMain(
            new PasswordEntry { Title = "GitHub", Username = "alice", Url = "https://github.com" },
            new PasswordEntry { Title = "Email", Username = "carol" });
        try
        {
            var viewModel = new QuickAccessViewModel(main);
            var window = new QuickAccessWindow { DataContext = viewModel };

            Assert.NotNull(window);
            var search = window.FindControl<AtomUI.Desktop.Controls.LineEdit>("SearchBox");
            Assert.NotNull(search);
            Assert.NotNull(search!.InnerLeftContent);
        }
        finally
        {
            vault.Dispose();
            main.Dispose();
        }
    });

    [Fact]
    public void HiddenOverlayDefersRefilteringUntilActivated()
    {
        var (vault, main) = CreateMain(
            new PasswordEntry { Title = "GitHub", Username = "alice" });
        using var _ = vault;
        using var __ = main;
        var viewModel = new QuickAccessViewModel(main) { SearchText = "git" };

        // Prewarmed but not shown: changes only mark the filter dirty.
        viewModel.SetActive(false);
        main.Entries.Add(new PasswordEntry { Title = "GitLab", Username = "bob" });
        Assert.Single(viewModel.FilteredEntries);

        viewModel.SetActive(true);
        Assert.Equal(2, viewModel.FilteredEntries.Count);
    }

    private sealed class FakeUrlLauncher : IUrlLauncher
    {
        public List<string?> Opened { get; } = [];

        public bool IsSupported(string? url)
            => url?.StartsWith("https://", StringComparison.OrdinalIgnoreCase) == true;

        public bool TryOpen(string? url)
        {
            if (!IsSupported(url))
            {
                return false;
            }

            Opened.Add(url);
            return true;
        }
    }
}
