using AegisVault.Core.Models;
using AegisVault.Core.Services;
using Xunit;

namespace AegisVault.Core.Tests.Services;

public sealed class AppPreferencesStoreTests : IDisposable
{
    private readonly string _directory;

    public AppPreferencesStoreTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "aegis-tests", Guid.NewGuid().ToString("N"));
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

    [Fact]
    public void RoundTripsPreferences()
    {
        var path = Path.Combine(_directory, "app.json");
        var store = new AppPreferencesStore(path);

        store.Save(new AppPreferences
        {
            LastVaultPath = Path.Combine(_directory, "vault.aegis"),
            Theme = "dark",
            Language = "en",
        });

        var loaded = new AppPreferencesStore(path).Load();

        Assert.Equal(Path.Combine(_directory, "vault.aegis"), loaded.LastVaultPath);
        Assert.Equal("dark", loaded.Theme);
        Assert.Equal("en", loaded.Language);
    }

    [Fact]
    public void RoundTripsFloatingBallPlacement()
    {
        var path = Path.Combine(_directory, "app.json");

        new AppPreferencesStore(path).Save(new AppPreferences
        {
            ShowFloatingBall = true,
            BallPosition = "120,340",
            BallDockedSide = "left",
        });

        var loaded = new AppPreferencesStore(path).Load();

        Assert.True(loaded.ShowFloatingBall);
        Assert.Equal("120,340", loaded.BallPosition);
        Assert.Equal("left", loaded.BallDockedSide);
    }

    [Fact]
    public void LegacyPreferencesWithoutBallFieldsLoadAsUnplaced()
    {
        var path = Path.Combine(_directory, "legacy.json");
        File.WriteAllText(path, """{"Theme":"dark","ShowFloatingBall":true}""");

        var loaded = new AppPreferencesStore(path).Load();

        Assert.Equal("dark", loaded.Theme);
        Assert.True(loaded.ShowFloatingBall);
        Assert.Null(loaded.BallPosition);
        Assert.Null(loaded.BallDockedSide);
    }

    [Fact]
    public void MissingFileReturnsDefaults()
    {
        var store = new AppPreferencesStore(Path.Combine(_directory, "missing.json"));

        var loaded = store.Load();

        Assert.Null(loaded.LastVaultPath);
        Assert.Equal("system", loaded.Theme);
        Assert.Equal("system", loaded.Language);
        Assert.Null(loaded.BallPosition);
        Assert.Null(loaded.BallDockedSide);
    }

    [Fact]
    public void CorruptFileReturnsDefaults()
    {
        var path = Path.Combine(_directory, "app.json");
        File.WriteAllText(path, "{ not valid json !!");

        var loaded = new AppPreferencesStore(path).Load();

        Assert.Null(loaded.LastVaultPath);
        Assert.Equal("system", loaded.Theme);
    }

    [Fact]
    public void SaveCreatesDirectory()
    {
        var path = Path.Combine(_directory, "nested", "deeper", "app.json");

        new AppPreferencesStore(path).Save(new AppPreferences { LastVaultPath = "x" });

        Assert.True(File.Exists(path));
    }

    [Fact]
    public void FileContainsNoSecrets()
    {
        var path = Path.Combine(_directory, "app.json");
        new AppPreferencesStore(path).Save(new AppPreferences { LastVaultPath = @"C:\v\vault.aegis" });

        var content = File.ReadAllText(path);

        Assert.Contains("lastVaultPath", content, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("password", content, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("dek", content, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RecentVaultsRoundTripInOrder()
    {
        var path = Path.Combine(_directory, "app.json");

        new AppPreferencesStore(path).Save(new AppPreferences
        {
            RecentVaultPaths = [@"C:\a\work.aegis", @"C:\b\personal.aegis"],
        });

        var loaded = new AppPreferencesStore(path).Load();

        Assert.Equal([@"C:\a\work.aegis", @"C:\b\personal.aegis"], loaded.RecentVaultPaths);
    }

    [Fact]
    public void LegacyPreferencesWithoutRecentVaultsLoadAsEmptyList()
    {
        var path = Path.Combine(_directory, "legacy.json");
        File.WriteAllText(path, """{"Theme":"dark","LastVaultPath":"C:\\v\\vault.aegis"}""");

        var loaded = new AppPreferencesStore(path).Load();

        Assert.Equal("dark", loaded.Theme);
        Assert.Equal(@"C:\v\vault.aegis", loaded.LastVaultPath);
        Assert.NotNull(loaded.RecentVaultPaths);
        Assert.Empty(loaded.RecentVaultPaths!);
    }

    [Fact]
    public void RecentVaultsAreNormalisedOnLoad()
    {
        var path = Path.Combine(_directory, "app.json");
        File.WriteAllText(path, """
            {"RecentVaultPaths":["  C:\\a\\work.aegis  ","","c:\\A\\WORK.aegis","C:\\b\\x.aegis","C:\\c\\y.aegis","C:\\d\\z.aegis"]}
            """);

        var loaded = new AppPreferencesStore(path).Load();

        Assert.Equal([@"C:\a\work.aegis", @"C:\b\x.aegis", @"C:\c\y.aegis"], loaded.RecentVaultPaths);
    }

    [Fact]
    public void StartupGuideDismissalRoundTrips()
    {
        var path = Path.Combine(_directory, "app.json");

        new AppPreferencesStore(path).Save(new AppPreferences { StartupGuideDismissed = true });

        Assert.True(new AppPreferencesStore(path).Load().StartupGuideDismissed);
        Assert.False(new AppPreferencesStore(Path.Combine(_directory, "other.json")).Load().StartupGuideDismissed);
    }

    [Fact]
    public void CloseActionRoundTripsKnownValues()
    {
        var path = Path.Combine(_directory, "app.json");

        new AppPreferencesStore(path).Save(new AppPreferences { CloseAction = CloseActions.Exit });
        Assert.Equal(CloseActions.Exit, new AppPreferencesStore(path).Load().CloseAction);

        new AppPreferencesStore(path).Save(new AppPreferences { CloseAction = CloseActions.Minimize });
        Assert.Equal(CloseActions.Minimize, new AppPreferencesStore(path).Load().CloseAction);
    }

    [Fact]
    public void UnknownOrMissingCloseActionLoadsAsAsk()
    {
        var legacy = Path.Combine(_directory, "legacy.json");
        File.WriteAllText(legacy, """{"Theme":"dark"}""");
        Assert.Equal(CloseActions.Ask, new AppPreferencesStore(legacy).Load().CloseAction);

        var bogus = Path.Combine(_directory, "bogus.json");
        File.WriteAllText(bogus, """{"CloseAction":"shutdown"}""");
        Assert.Equal(CloseActions.Ask, new AppPreferencesStore(bogus).Load().CloseAction);
    }
}
