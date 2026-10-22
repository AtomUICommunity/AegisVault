using System.Reflection;
using System.Text.Json;
using AegisVault.Core.Models;
using AegisVault.Core.Storage;
using Xunit;

namespace AegisVault.Core.Tests.Storage;

public sealed class ModelMigrationsTests
{
    [Fact]
    public void MinimalLegacyEntryPayloadDeserializesWithNullCollections()
    {
        // Characterization of the source-generated context: missing collection
        // properties come back as null (unlike the reflection serializer).
        var entry = JsonSerializer.Deserialize(
            """{"Title":"Legacy"}""",
            VaultJsonContext.Default.PasswordEntry);

        Assert.NotNull(entry);
        Assert.Null(entry!.Tags);
        Assert.Null(entry.Urls);
        Assert.Null(entry.CustomFields);
    }

    [Fact]
    public void EntryToStringDoesNotLeakSecrets()
    {
        var entry = new PasswordEntry
        {
            Title = "GitHub",
            Username = "octocat",
            Password = "s3cret-value",
            TotpSecret = "JBSWY3DPEHPK3PXP",
            Phone = "13800000000",
            Email = "ops@example.com",
            AppId = "cli_app",
            Secret = "client-secret-value",
            ApiKey = "api-key-value",
        };

        var text = entry.ToString();

        Assert.Contains("GitHub", text);
        Assert.DoesNotContain("s3cret-value", text);
        Assert.DoesNotContain("JBSWY3DPEHPK3PXP", text);
        Assert.DoesNotContain("octocat", text);
        Assert.DoesNotContain("13800000000", text);
        Assert.DoesNotContain("ops@example.com", text);
        Assert.DoesNotContain("cli_app", text);
        Assert.DoesNotContain("client-secret-value", text);
        Assert.DoesNotContain("api-key-value", text);
    }

    [Fact]
    public void UpgradeFromV1FillsEveryCollectionAndKeepsData()
    {
        var legacy = JsonSerializer.Deserialize(
            """{"Title":"Legacy","Url":"https://example.com","Tags":["work"]}""",
            VaultJsonContext.Default.PasswordEntry)!;

        var upgraded = ModelMigrations.UpgradeEntry(legacy, 1);

        Assert.Equal("Legacy", upgraded.Title);
        Assert.Equal("https://example.com", upgraded.Url);
        Assert.Equal(["work"], upgraded.Tags);
        Assert.Empty(upgraded.Urls);
        Assert.Empty(upgraded.CustomFields);
    }

    [Fact]
    public void UpgradeFillsEveryCollectionPropertyDeclaredOnTheModel()
    {
        // Guards future model changes: any new collection/object member must be
        // covered by ModelMigrations.Normalize, otherwise this test fails.
        var minimal = JsonSerializer.Deserialize(
            """{"Title":"Minimal"}""",
            VaultJsonContext.Default.PasswordEntry)!;

        var upgraded = ModelMigrations.UpgradeEntry(minimal, 1);

        foreach (var property in typeof(PasswordEntry).GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            var type = property.PropertyType;
            var isCollection = type.IsArray ||
                (type.IsGenericType &&
                 type.GetGenericTypeDefinition() is { } definition &&
                 (definition == typeof(List<>) ||
                  definition == typeof(Dictionary<,>) ||
                  definition == typeof(IReadOnlyList<>) ||
                  definition == typeof(IEnumerable<>)));

            if (isCollection)
            {
                Assert.True(
                    property.GetValue(upgraded) is not null,
                    $"PasswordEntry.{property.Name} is null after upgrade; extend ModelMigrations.Normalize.");
            }
        }
    }

    [Fact]
    public void UpgradeFromV2LeavesNewCredentialFieldsEmpty()
    {
        // Payloads written before v3 have no Phone/AppId/Secret/ApiKey keys.
        var legacy = JsonSerializer.Deserialize(
            """{"Title":"Legacy","Username":"u","Password":"p","Urls":["https://example.com"],"CustomFields":{"K":"V"}}""",
            VaultJsonContext.Default.PasswordEntry)!;

        var upgraded = ModelMigrations.UpgradeEntry(legacy, 2);

        Assert.Equal("u", upgraded.Username);
        Assert.Equal("p", upgraded.Password);
        Assert.Equal(["https://example.com"], upgraded.Urls);
        Assert.Equal("V", upgraded.CustomFields["K"]);
        Assert.Equal(string.Empty, upgraded.Phone);
        Assert.Equal(string.Empty, upgraded.Email);
        Assert.Equal(string.Empty, upgraded.AppId);
        Assert.Equal(string.Empty, upgraded.Secret);
        Assert.Equal(string.Empty, upgraded.ApiKey);
    }

    [Fact]
    public void RoundTripsCredentialFieldsThroughJson()
    {
        var entry = new PasswordEntry
        {
            Title = "API",
            Phone = "13800000000",
            Email = "ops@example.com",
            AppId = "cli_123",
            Secret = "shh",
            ApiKey = "key-1",
        };

        var json = JsonSerializer.Serialize(entry, VaultJsonContext.Default.PasswordEntry);
        var loaded = ModelMigrations.UpgradeEntry(
            JsonSerializer.Deserialize(json, VaultJsonContext.Default.PasswordEntry)!,
            EntryRepository.EntryFormatVersion);

        Assert.Equal("13800000000", loaded.Phone);
        Assert.Equal("ops@example.com", loaded.Email);
        Assert.Equal("cli_123", loaded.AppId);
        Assert.Equal("shh", loaded.Secret);
        Assert.Equal("key-1", loaded.ApiKey);
    }

    [Fact]
    public void UpgradeRejectsVersionZero()
    {
        var entry = new PasswordEntry { Title = "x" };
        Assert.Throws<InvalidDataException>(() => ModelMigrations.UpgradeEntry(entry, 0));
    }

    [Fact]
    public void UpgradeFromV4LeavesRecycleBinAndRecentUnset()
    {
        // Payloads written before v5 have no DeletedAt/LastOpenedAt keys, so they
        // must come back as "live" and "never opened" without touching the data.
        var legacy = JsonSerializer.Deserialize(
            """{"Title":"Legacy","Email":"ops@example.com"}""",
            VaultJsonContext.Default.PasswordEntry)!;

        var upgraded = ModelMigrations.UpgradeEntry(legacy, 4);

        Assert.Null(upgraded.DeletedAt);
        Assert.Null(upgraded.LastOpenedAt);
        Assert.Equal("ops@example.com", upgraded.Email);
    }

    [Fact]
    public void RoundTripsRecycleBinAndRecentTimestampsThroughJson()
    {
        var deletedAt = new DateTimeOffset(2026, 9, 25, 10, 30, 0, TimeSpan.Zero);
        var lastOpenedAt = new DateTimeOffset(2026, 9, 24, 8, 0, 0, TimeSpan.Zero);
        var entry = new PasswordEntry
        {
            Title = "Recycled",
            DeletedAt = deletedAt,
            LastOpenedAt = lastOpenedAt,
        };

        var json = JsonSerializer.Serialize(entry, VaultJsonContext.Default.PasswordEntry);
        var loaded = ModelMigrations.UpgradeEntry(
            JsonSerializer.Deserialize(json, VaultJsonContext.Default.PasswordEntry)!,
            EntryRepository.EntryFormatVersion);

        Assert.Equal(deletedAt, loaded.DeletedAt);
        Assert.Equal(lastOpenedAt, loaded.LastOpenedAt);
    }

    [Fact]
    public void NormalizeTreatsDefaultTimestampsAsUnset()
    {
        // 0001-01-01 is never written by the application; normalize it to null.
        var entry = new PasswordEntry
        {
            Title = "x",
            DeletedAt = default(DateTimeOffset),
            LastOpenedAt = default(DateTimeOffset),
        };

        var normalized = ModelMigrations.Normalize(entry);

        Assert.Null(normalized.DeletedAt);
        Assert.Null(normalized.LastOpenedAt);
    }

    [Fact]
    public void UserConfigNormalizationRestoresMissingGenerator()
    {
        var config = JsonSerializer.Deserialize("{}", VaultJsonContext.Default.UserConfig)!;
        Assert.Null(config.Generator);

        var normalized = ModelMigrations.Normalize(config);

        Assert.NotNull(normalized.Generator);
        Assert.Equal(20, normalized.Generator.Length);
    }

    [Fact]
    public void CategoryNormalizationRestoresMissingName()
    {
        var categories = JsonSerializer.Deserialize(
            """[{"Id":"6f9619ff-8b86-d011-b42d-00cf4fc964ff"}]""",
            VaultJsonContext.Default.ListCategory)!;
        var category = Assert.Single(categories);
        Assert.Null(category.Name);

        var normalized = ModelMigrations.Normalize(category);

        Assert.Equal(string.Empty, normalized.Name);
        Assert.Equal(Guid.Parse("6f9619ff-8b86-d011-b42d-00cf4fc964ff"), normalized.Id);
    }

    [Fact]
    public void CategoryTreeNormalizationKeepsDanglingAndSelfParentedNodesAsRoots()
    {
        var rootId = Guid.NewGuid();
        var danglingId = Guid.NewGuid();
        var selfId = Guid.NewGuid();
        var categories = ModelMigrations.NormalizeCategoryTree(
        [
            new Category { Id = rootId, Name = "Work" },
            new Category { Id = danglingId, Name = "Orphan", ParentId = Guid.NewGuid() },
            new Category { Id = selfId, Name = "Self", ParentId = selfId },
        ]);

        Assert.Equal(3, categories.Count);
        Assert.All(categories, category => Assert.Null(category.ParentId));
    }

    [Fact]
    public void CategoryTreeNormalizationBreaksCyclesWithoutLosingCategories()
    {
        var firstId = Guid.NewGuid();
        var secondId = Guid.NewGuid();
        var categories = ModelMigrations.NormalizeCategoryTree(
        [
            new Category { Id = firstId, Name = "First", ParentId = secondId },
            new Category { Id = secondId, Name = "Second", ParentId = firstId },
        ]);

        Assert.Equal(2, categories.Count);
        Assert.All(categories, category => Assert.Null(category.ParentId));
    }

    [Fact]
    public void CategoryTreeNormalizationCapsTheDepth()
    {
        var ids = Enumerable.Range(0, ModelMigrations.MaxCategoryDepth + 2).Select(_ => Guid.NewGuid()).ToArray();
        var categories = new List<Category>();
        for (var index = 0; index < ids.Length; index++)
        {
            categories.Add(new Category
            {
                Id = ids[index],
                Name = $"Level{index + 1}",
                ParentId = index == 0 ? null : ids[index - 1],
            });
        }

        var normalized = ModelMigrations.NormalizeCategoryTree(categories);

        var deepest = normalized.Single(category => category.Id == ids[^1]);
        var expectedParent = normalized.Single(category => category.Id == ids[ModelMigrations.MaxCategoryDepth - 2]);
        Assert.Equal(expectedParent.Id, deepest.ParentId);

        // Every node stays within the cap even when the chain was far deeper.
        var byId = normalized.ToDictionary(category => category.Id);
        foreach (var category in normalized)
        {
            var depth = 1;
            var parentId = category.ParentId;
            while (parentId is { } id && byId.TryGetValue(id, out var parent))
            {
                depth++;
                parentId = parent.ParentId;
            }

            Assert.True(depth <= ModelMigrations.MaxCategoryDepth, $"{category.Name} sits at depth {depth}.");
        }
    }

    [Fact]
    public void CategoryTreeNormalizationDropsDuplicateIds()
    {
        var id = Guid.NewGuid();
        var categories = ModelMigrations.NormalizeCategoryTree(
        [
            new Category { Id = id, Name = "First" },
            new Category { Id = id, Name = "Second" },
        ]);

        Assert.Single(categories);
        Assert.Equal("First", categories[0].Name);
    }

    [Fact]
    public void CategoryTreeNormalizationRenamesDuplicateSiblingNames()
    {
        var parentId = Guid.NewGuid();
        var otherParentId = Guid.NewGuid();
        var firstChildId = Guid.NewGuid();
        var clashingChildId = Guid.NewGuid();
        var unrelatedChildId = Guid.NewGuid();

        var categories = ModelMigrations.NormalizeCategoryTree(
        [
            new Category { Id = parentId, Name = "Work" },
            new Category { Id = otherParentId, Name = "Home" },
            new Category { Id = firstChildId, Name = "Shared", ParentId = parentId },
            new Category { Id = clashingChildId, Name = "shared", ParentId = parentId },
            new Category { Id = unrelatedChildId, Name = "Shared", ParentId = otherParentId },
        ]);

        // Nothing is dropped: the clash is renamed, and case-insensitive.
        Assert.Equal(5, categories.Count);
        Assert.Equal("Shared", categories.Single(category => category.Id == firstChildId).Name);
        Assert.Equal("shared (2)", categories.Single(category => category.Id == clashingChildId).Name);

        // The same name under a different parent is not a conflict.
        Assert.Equal("Shared", categories.Single(category => category.Id == unrelatedChildId).Name);
    }

    [Fact]
    public void UpgradeFromV5LeavesPasswordHistoryEmpty()
    {
        var entry = ModelMigrations.UpgradeEntry(new PasswordEntry { Title = "Mail" }, fromVersion: 5);

        Assert.NotNull(entry.PasswordHistory);
        Assert.Empty(entry.PasswordHistory!);
    }

    [Fact]
    public void NormalizeTrimsPasswordHistoryToTheCapAndOrdersItNewestFirst()
    {
        var now = DateTimeOffset.Parse("2026-10-22T12:00:00Z");
        var history = Enumerable.Range(0, 15)
            .Select(index => new PasswordHistoryItem
            {
                Password = "old-" + index,
                ChangedAt = now.AddDays(-index),
            })
            .ToList();

        var entry = ModelMigrations.Normalize(new PasswordEntry { Title = "Mail", PasswordHistory = history });

        Assert.Equal(PasswordEntry.MaximumPasswordHistory, entry.PasswordHistory!.Count);
        Assert.Equal("old-0", entry.PasswordHistory[0].Password);
        Assert.Equal("old-9", entry.PasswordHistory[^1].Password);
    }

    [Fact]
    public void NormalizeDropsBlankAndUnstampedHistoryItems()
    {
        var entry = ModelMigrations.Normalize(new PasswordEntry
        {
            Title = "Mail",
            PasswordHistory =
            [
                new PasswordHistoryItem { Password = "", ChangedAt = DateTimeOffset.UtcNow },
                new PasswordHistoryItem { Password = "kept", ChangedAt = DateTimeOffset.UtcNow },
                new PasswordHistoryItem { Password = "no timestamp", ChangedAt = default },
            ],
        });

        Assert.Equal(["kept"], entry.PasswordHistory!.Select(item => item.Password));
    }

    [Fact]
    public void PasswordHistoryRoundTripsThroughTheSourceGeneratedJson()
    {
        var changedAt = DateTimeOffset.Parse("2026-10-22T12:00:00Z");
        var entry = new PasswordEntry
        {
            Title = "Mail",
            Password = "new",
            PasswordHistory = [new PasswordHistoryItem { Password = "old", ChangedAt = changedAt }],
        };

        var json = JsonSerializer.Serialize(entry, VaultJsonContext.Default.PasswordEntry);
        var restored = JsonSerializer.Deserialize(json, VaultJsonContext.Default.PasswordEntry)!;

        Assert.NotNull(restored.PasswordHistory);
        Assert.Equal("old", Assert.Single(restored.PasswordHistory!).Password);
        Assert.Equal(changedAt, restored.PasswordHistory![0].ChangedAt);
    }

    [Fact]
    public void ToStringStillRedactsTheHistory()
    {
        var entry = new PasswordEntry
        {
            Title = "Mail",
            Password = "current-secret",
            PasswordHistory = [new PasswordHistoryItem { Password = "historic-secret", ChangedAt = DateTimeOffset.UtcNow }],
        };

        var text = entry.ToString();

        Assert.DoesNotContain("current-secret", text);
        Assert.DoesNotContain("historic-secret", text);
    }
}
