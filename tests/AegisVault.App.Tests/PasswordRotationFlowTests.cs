using AegisVault.Core.Models;
using AegisVault.Core.Services;
using AegisVault.App.ViewModels;
using Xunit;

namespace AegisVault.App.Tests;

/// <summary>
/// Regression: editing an entry and changing its password must (a) record the old
/// value in the vault, (b) refresh the detail pane immediately, and (c) survive an
/// unrelated later edit made from a stale UI record.
/// </summary>
public sealed class PasswordRotationFlowTests : IDisposable
{
    private static readonly byte[] Password = "rotation-flow"u8.ToArray();
    private static readonly VaultOptions Fast = new()
    {
        Kdf = new KdfParameters { Algorithm = KdfParameters.AlgorithmArgon2id, Iterations = 3, MemoryBytes = 8L * 1024 * 1024 },
    };

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "aegis-rot-flow", Guid.NewGuid().ToString("N"));

    public PasswordRotationFlowTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private (VaultService Vault, MainViewModel ViewModel) Create()
    {
        var vault = VaultService.CreateNew(Path.Combine(_dir, Guid.NewGuid().ToString("N") + ".aegis"), Password, Fast);
        vault.AddEntry(new PasswordEntry { Title = "Mail", Password = "old-pw" });
        var viewModel = new MainViewModel(vault);
        viewModel.SelectedEntry = vault.Entries[0];
        return (vault, viewModel);
    }

    [Fact]
    public void EditingThePasswordRecordsHistoryAndShowsItImmediately()
    {
        var (vault, viewModel) = Create();
        using var _ = vault;
        using var __ = viewModel;

        Assert.False(viewModel.HasPasswordHistory);

        viewModel.BeginEditCommand.Execute(null);
        viewModel.EditPassword = "new-pw";
        viewModel.SaveEntryCommand.Execute(null);

        var stored = vault.Entries[0];
        Assert.Equal("new-pw", stored.Password);
        Assert.Equal("old-pw", Assert.Single(stored.PasswordHistory ?? []).Password);

        // The detail pane must reflect the new history without reselecting.
        Assert.True(viewModel.HasPasswordHistory, "history should be visible right after saving");
        Assert.Equal("old-pw", Assert.Single(viewModel.PasswordHistory).Password);
    }

    [Fact]
    public void SavingWithoutChangingThePasswordAddsNoHistory()
    {
        var (vault, viewModel) = Create();
        using var _ = vault;
        using var __ = viewModel;

        viewModel.BeginEditCommand.Execute(null);
        viewModel.EditNotes = "just a note";
        viewModel.SaveEntryCommand.Execute(null);

        Assert.Empty(vault.Entries[0].PasswordHistory ?? []);
        Assert.False(viewModel.HasPasswordHistory);
    }

    [Fact]
    public void UnrelatedEditAfterRotationKeepsTheRecordedHistory()
    {
        var (vault, viewModel) = Create();
        using var _ = vault;
        using var __ = viewModel;

        viewModel.BeginEditCommand.Execute(null);
        viewModel.EditPassword = "new-pw";
        viewModel.SaveEntryCommand.Execute(null);

        // Favourite toggling sends a record that predates the rotation append.
        viewModel.ToggleFavoriteCommand.Execute(null);

        var stored = vault.Entries[0];
        Assert.True(stored.IsFavorite);
        Assert.Equal("old-pw", Assert.Single(stored.PasswordHistory ?? []).Password);
    }

    [Fact]
    public void RotationSurvivesAReopen()
    {
        var vaultPath = Path.Combine(_dir, "reopen.aegis");
        using (var vault = VaultService.CreateNew(vaultPath, Password, Fast))
        {
            vault.AddEntry(new PasswordEntry { Title = "Mail", Password = "old-pw" });
            var viewModel = new MainViewModel(vault);
            viewModel.SelectedEntry = vault.Entries[0];
            viewModel.BeginEditCommand.Execute(null);
            viewModel.EditPassword = "new-pw";
            viewModel.SaveEntryCommand.Execute(null);
            viewModel.Dispose();
        }

        using var reopened = VaultService.Open(vaultPath);
        Assert.Equal(VaultUnlockStatus.Success, reopened.Unlock(Password));
        Assert.Equal("old-pw", Assert.Single(reopened.Entries[0].PasswordHistory ?? []).Password);
    }
}
