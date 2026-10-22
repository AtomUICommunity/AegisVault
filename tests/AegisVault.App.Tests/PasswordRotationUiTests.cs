using AegisVault.Core.Models;
using AegisVault.Core.Services;
using AegisVault.App.ViewModels;
using Xunit;

namespace AegisVault.App.Tests;

/// <summary>
/// ROT-1a/b UI: the detail pane shows how old the password is, lists superseded
/// values, and never shows them unless the entry actually has history.
/// </summary>
public sealed class PasswordRotationUiTests : IDisposable
{
    private static readonly byte[] Password = "rotation-ui-password"u8.ToArray();

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

    public PasswordRotationUiTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "aegis-rotation-ui", Guid.NewGuid().ToString("N"));
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

    private VaultService CreateVault() => VaultService.CreateNew(_vaultPath, Password, FastOptions);

    [Fact]
    public void EntryWithoutHistoryShowsNoHistorySection()
    {
        using var vault = CreateVault();
        var entry = vault.AddEntry(new PasswordEntry { Title = "Mail", Password = "p" });
        using var viewModel = new MainViewModel(vault);
        viewModel.SelectedEntry = entry;

        Assert.False(viewModel.HasPasswordHistory);
        Assert.Empty(viewModel.PasswordHistory);
    }

    [Fact]
    public void RotatingAPasswordSurfacesTheOldValueInTheUi()
    {
        using var vault = CreateVault();
        var entry = vault.AddEntry(new PasswordEntry { Title = "Mail", Password = "first" });
        using var viewModel = new MainViewModel(vault);

        Assert.True(vault.UpdateEntry(entry with { Password = "second" }));
        viewModel.SelectedEntry = vault.Entries[0];

        Assert.True(viewModel.HasPasswordHistory);
        var item = Assert.Single(viewModel.PasswordHistory);
        Assert.Equal("first", item.Password);
    }

    [Fact]
    public void PasswordAgeIsDerivedFromUpdatedAt()
    {
        var clock = new ManualClock();
        using var vault = VaultService.CreateNew(_vaultPath, Password, FastOptions, clock);
        var entry = vault.AddEntry(new PasswordEntry { Title = "Mail", Password = "p" });
        using var viewModel = new MainViewModel(vault, timeProvider: clock);

        viewModel.SelectedEntry = entry;
        Assert.Equal(Localization.Loc.T("Main_PasswordAgeToday"), viewModel.PasswordAgeDisplay);

        clock.Advance(TimeSpan.FromDays(3));
        viewModel.SelectedEntry = null;
        viewModel.SelectedEntry = entry;

        Assert.Equal(Localization.Loc.Format("Main_PasswordAgeDays", 3), viewModel.PasswordAgeDisplay);
    }

    private sealed class ManualClock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 10, 22, 12, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan delta) => _now = _now.Add(delta);
    }
}
