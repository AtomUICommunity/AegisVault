using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AegisVault.Core.Crypto;
using AegisVault.Core.Models;
using AegisVault.Core.Services;
using AegisVault.Core.Storage;
using Xunit;

namespace AegisVault.Core.Tests.Services;

public sealed class CategoryTests : IDisposable
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

    public CategoryTests()
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
    public void CategoryCrudPersistsAcrossReopen()
    {
        var path = Path.Combine(_directory, "vault.aegis");
        Guid categoryId;

        using (var vault = VaultService.CreateNew(path, Password, FastOptions))
        {
            var category = vault.AddCategory("Work");
            categoryId = category.Id;

            var entry = vault.AddEntry(new PasswordEntry { Title = "GitHub", CategoryId = categoryId });
            Assert.Equal(categoryId, entry.CategoryId);

            Assert.Single(vault.Categories);
            Assert.True(vault.RenameCategory(categoryId, "Job"));
            Assert.Equal("Job", vault.Categories[0].Name);
        }

        using (var vault = VaultService.Open(path))
        {
            Assert.Equal(VaultUnlockStatus.Success, vault.Unlock(Password));
            Assert.Equal("Job", vault.Categories.Single().Name);
            Assert.Equal(categoryId, vault.Entries.Single().CategoryId);

            Assert.True(vault.DeleteCategory(categoryId));
            Assert.Empty(vault.Categories);
            Assert.Null(vault.Entries.Single().CategoryId);
        }
    }

    [Fact]
    public void ColorsPersistAndNormalizeAcrossReopen()
    {
        var path = Path.Combine(_directory, "colors.aegis");
        Guid paletteId;
        Guid customId;

        using (var vault = VaultService.CreateNew(path, Password, FastOptions))
        {
            paletteId = vault.AddCategory("Palette", "blue").Id;
            customId = vault.AddCategory("Custom", "#3b82f6").Id;
            vault.AddCategory("Unset");

            // Case/format canonicalization on the way in.
            Assert.True(vault.SetCategoryColor(paletteId, "  PURPLE "));
            Assert.True(vault.SetCategoryColor(customId, "#abc"));
        }

        using (var vault = VaultService.Open(path))
        {
            Assert.Equal(VaultUnlockStatus.Success, vault.Unlock(Password));

            Assert.Equal("purple", vault.Categories.Single(c => c.Id == paletteId).Color);
            Assert.Equal("#AABBCC", vault.Categories.Single(c => c.Id == customId).Color);
            Assert.Equal(string.Empty, vault.Categories.Single(c => c.Name == "Unset").Color);
        }
    }

    [Fact]
    public void UnknownColorsAndMissingIdsAreIgnored()
    {
        using var vault = VaultService.CreateNew(Path.Combine(_directory, "invalid.aegis"), Password, FastOptions);
        var category = vault.AddCategory("Work", "not-a-color");

        Assert.Equal(string.Empty, category.Color);

        Assert.True(vault.SetCategoryColor(category.Id, "#12345"));
        Assert.Equal(string.Empty, vault.Categories.Single().Color);

        Assert.False(vault.SetCategoryColor(Guid.NewGuid(), "red"));
    }

    [Fact]
    public void LegacyCategoriesPayloadIsReadAndRewrittenWithCurrentAad()
    {
        // Build the vault the way the first release wrote it: a bare category
        // list encrypted with the v1 AAD (no colour, no hierarchy).
        var path = Path.Combine(_directory, "legacy-categories.aegis");
        var categoryId = Guid.NewGuid();
        var legacyJson = JsonSerializer.SerializeToUtf8Bytes(new[]
        {
            new { Id = categoryId, Name = "Legacy", CreatedAt = DateTimeOffset.UtcNow },
        });

        var dek = WriteVaultWithCategoriesBlob(path, legacyJson, 1);
        try
        {
            using (var vault = VaultService.Open(path))
            {
                Assert.Equal(VaultUnlockStatus.Success, vault.Unlock(Password));
                var category = Assert.Single(vault.Categories);
                Assert.Equal("Legacy", category.Name);
                Assert.Equal(string.Empty, category.Color);
                Assert.Null(category.ParentId);
                Assert.False(vault.CategoriesUnreadable);
            }

            // The unlock rewrites the blob as the current envelope.
            var rewritten = ReadCategoriesBlob(path, dek, CategoriesPayload.CurrentVersion);
            var text = Encoding.UTF8.GetString(rewritten);
            Assert.Contains($"\"Version\":{CategoriesPayload.CurrentVersion}", text);
            Assert.Contains("\"Color\"", text);
            Assert.Contains("\"ParentId\"", text);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(dek);
        }
    }

    [Fact]
    public void V2BareListIsReadAndRewrittenAsTheVersionedEnvelope()
    {
        var path = Path.Combine(_directory, "v2-categories.aegis");
        var legacyJson = JsonSerializer.SerializeToUtf8Bytes(new[]
        {
            new Category { Id = Guid.NewGuid(), Name = "Work", Color = "blue" },
        });

        var dek = WriteVaultWithCategoriesBlob(path, legacyJson, 2);
        try
        {
            using var vault = VaultService.Open(path);
            Assert.Equal(VaultUnlockStatus.Success, vault.Unlock(Password));
            Assert.Equal("blue", vault.Categories.Single().Color);

            var rewritten = Encoding.UTF8.GetString(ReadCategoriesBlob(path, dek, CategoriesPayload.CurrentVersion));
            Assert.Contains($"\"Version\":{CategoriesPayload.CurrentVersion}", rewritten);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(dek);
        }
    }

    [Fact]
    public void CategoryTreeRoundTripsThroughThePayload()
    {
        var path = Path.Combine(_directory, "tree.aegis");
        var rootId = Guid.NewGuid();
        var childId = Guid.NewGuid();
        var payload = new CategoriesPayload
        {
            Categories =
            [
                new Category { Id = rootId, Name = "Work", Color = "green" },
                new Category { Id = childId, Name = "Servers", ParentId = rootId },
            ],
        };
        var json = JsonSerializer.SerializeToUtf8Bytes(payload, VaultJsonContext.Default.CategoriesPayload);

        var dek = WriteVaultWithCategoriesBlob(path, json, CategoriesPayload.CurrentVersion);
        try
        {
            using var vault = VaultService.Open(path);
            Assert.Equal(VaultUnlockStatus.Success, vault.Unlock(Password));

            Assert.Equal(rootId, vault.Categories.Single(c => c.Id == childId).ParentId);
            Assert.Null(vault.Categories.Single(c => c.Id == rootId).ParentId);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(dek);
        }
    }

    [Fact]
    public void V3PayloadIsUpgradedToAnOrderThatMatchesTheOldAlphabeticalList()
    {
        // A v0.5.2 vault: the envelope has a version and a tree, but no SortOrder.
        // Upgrading must not reshuffle the sidebar, so the alphabetical order those
        // releases displayed is frozen into the data.
        var path = Path.Combine(_directory, "v3-order.aegis");
        var rootId = Guid.NewGuid();
        var zebraId = Guid.NewGuid();
        var alphaId = Guid.NewGuid();
        var childBetaId = Guid.NewGuid();
        var childAardvarkId = Guid.NewGuid();
        var payload = new CategoriesPayload
        {
            Version = 3,
            Categories =
            [
                new Category { Id = zebraId, Name = "Zebra" },
                new Category { Id = alphaId, Name = "Alpha" },
                new Category { Id = rootId, Name = "Work" },
                new Category { Id = childBetaId, Name = "Beta", ParentId = rootId },
                new Category { Id = childAardvarkId, Name = "Aardvark", ParentId = rootId },
            ],
        };
        var json = JsonSerializer.SerializeToUtf8Bytes(payload, VaultJsonContext.Default.CategoriesPayload);

        var dek = WriteVaultWithCategoriesBlob(path, json, 3);
        try
        {
            using (var vault = VaultService.Open(path))
            {
                Assert.Equal(VaultUnlockStatus.Success, vault.Unlock(Password));

                // Roots: Alpha, Work, Zebra — the order v0.5.2 rendered.
                Assert.Equal(
                    ["Alpha", "Work", "Zebra"],
                    vault.Categories
                        .Where(category => category.ParentId is null)
                        .OrderBy(category => category.SortOrder)
                        .Select(category => category.Name));
                Assert.Equal([1, 2, 3], vault.Categories
                    .Where(category => category.ParentId is null)
                    .Select(category => category.SortOrder)
                    .Order());

                // Children: Aardvark, Beta, numbered independently of the roots.
                Assert.Equal(
                    ["Aardvark", "Beta"],
                    vault.Categories
                        .Where(category => category.ParentId == rootId)
                        .OrderBy(category => category.SortOrder)
                        .Select(category => category.Name));
                Assert.Equal(
                    [1, 2],
                    vault.Categories
                        .Where(category => category.ParentId == rootId)
                        .Select(category => category.SortOrder)
                        .Order());
            }

            // The upgrade is a one-shot rewrite: the blob now declares v4 and
            // carries SortOrder, so the next unlock normalizes nothing.
            var rewritten = ReadCategoriesBlob(path, dek, CategoriesPayload.CurrentVersion);
            var text = Encoding.UTF8.GetString(rewritten);
            Assert.Contains($"\"Version\":{CategoriesPayload.CurrentVersion}", text);
            Assert.Contains("\"SortOrder\"", text);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(dek);
        }
    }

    [Fact]
    public void NewerCategoriesPayloadIsReportedAsUnsupportedVersion()
    {
        // A future release writes the same envelope shape with a higher version.
        var path = Path.Combine(_directory, "newer-categories.aegis");
        var payload = new CategoriesPayload
        {
            Version = CategoriesPayload.CurrentVersion + 1,
            Categories = [new Category { Name = "FromTheFuture" }],
        };
        var json = JsonSerializer.SerializeToUtf8Bytes(payload, VaultJsonContext.Default.CategoriesPayload);

        var dek = WriteVaultWithCategoriesBlob(path, json, CategoriesPayload.CurrentVersion);
        try
        {
            using var vault = VaultService.Open(path);
            Assert.Equal(VaultUnlockStatus.UnsupportedVersion, vault.Unlock(Password));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(dek);
        }
    }

    [Fact]
    public void UnreadableCategoriesBlobUnlocksWithAWarning()
    {
        var path = Path.Combine(_directory, "unreadable-categories.aegis");
        var json = JsonSerializer.SerializeToUtf8Bytes(new[] { new Category { Name = "Hidden" } });
        var dek = WriteVaultWithCategoriesBlob(path, json, 9);
        try
        {
            using var vault = VaultService.Open(path);
            Assert.Equal(VaultUnlockStatus.Success, vault.Unlock(Password));

            Assert.Empty(vault.Categories);
            Assert.True(vault.CategoriesUnreadable);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(dek);
        }
    }

    /// <summary>
    /// Creates a vault whose categories blob is encrypted under
    /// <c>AegisVault|settings|categories|v{version}</c> and returns the raw DEK.
    /// </summary>
    private static byte[] WriteVaultWithCategoriesBlob(string path, byte[] json, int version)
    {
        var kdf = FastOptions.Kdf!;
        var salt = RandomNumberGenerator.GetBytes(kdf.SaltSize);
        using var kek = KeyDerivationFactory.Create(kdf.Algorithm).DeriveKey(Password, salt, kdf);
        var dek = RandomNumberGenerator.GetBytes(KeyEnvelope.DekSize);

        using var database = VaultDatabase.OpenOrCreate(path);
        var now = DateTimeOffset.UtcNow;
        database.WriteMeta(new VaultHeader
        {
            FormatVersion = VaultHeader.CurrentFormatVersion,
            Kdf = kdf,
            Salt = salt,
            WrappedDek = KeyEnvelope.Wrap(
                kek.ReadOnlySpan,
                dek,
                VaultHeader.ComputeAssociatedData(VaultHeader.CurrentFormatVersion, kdf, salt)),
            CreatedAt = now,
            UpdatedAt = now,
        });

        var (nonce, ciphertext, tag) = AesGcmCipher.Encrypt(
            dek,
            json,
            Encoding.UTF8.GetBytes($"AegisVault|settings|categories|v{version}"));
        database.WriteSetting("categories", nonce, ciphertext, tag);
        return dek;
    }

    private static byte[] ReadCategoriesBlob(string path, byte[] dek, int version)
    {
        using var database = VaultDatabase.OpenOrCreate(path);
        var stored = database.ReadSetting("categories");
        Assert.NotNull(stored);
        return AesGcmCipher.Decrypt(
            dek,
            stored!.Nonce,
            stored.Ciphertext,
            stored.Tag,
            Encoding.UTF8.GetBytes($"AegisVault|settings|categories|v{version}"));
    }

    [Fact]
    public void RejectsEmptyAndDuplicateNames()
    {
        using var vault = VaultService.CreateNew(Path.Combine(_directory, "names.aegis"), Password, FastOptions);
        vault.AddCategory("Work");

        Assert.Throws<CategoryValidationException>(() => vault.AddCategory("   "));
        Assert.Throws<CategoryValidationException>(() => vault.AddCategory("work"));

        var personal = vault.AddCategory("Personal");
        Assert.Throws<CategoryValidationException>(() => vault.RenameCategory(personal.Id, "WORK"));

        // Renaming to its own name is allowed.
        Assert.True(vault.RenameCategory(personal.Id, "Personal"));
    }

    [Fact]
    public void LegacyVaultHasNoCategories()
    {
        var path = Path.Combine(_directory, "legacy.aegis");
        using (var vault = VaultService.CreateNew(path, Password, FastOptions))
        {
            vault.AddEntry(new PasswordEntry { Title = "A" });
        }

        using var reopened = VaultService.Open(path);
        Assert.Equal(VaultUnlockStatus.Success, reopened.Unlock(Password));
        Assert.Empty(reopened.Categories);
        Assert.Null(reopened.Entries.Single().CategoryId);
    }

    [Fact]
    public void LockClearsCategories()
    {
        using var vault = VaultService.CreateNew(Path.Combine(_directory, "lock.aegis"), Password, FastOptions);
        vault.AddCategory("Work");

        vault.Lock();

        Assert.Empty(vault.Categories);
    }

    [Fact]
    public void SiblingNamesAreUniquePerParentAndMayRepeatAcrossParents()
    {
        using var vault = VaultService.CreateNew(Path.Combine(_directory, "siblings.aegis"), Password, FastOptions);
        var work = vault.AddCategory("Work");
        var home = vault.AddCategory("Home");

        vault.AddCategory("Servers", parentId: work.Id);
        Assert.Throws<CategoryValidationException>(() => vault.AddCategory("servers", parentId: work.Id));

        // Same name under a different parent is allowed.
        var homeServers = vault.AddCategory("Servers", parentId: home.Id);
        Assert.Equal(home.Id, homeServers.ParentId);

        // Top-level names stay unique against other top-level names only.
        Assert.Throws<CategoryValidationException>(() => vault.AddCategory("work"));
        vault.AddCategory("Work", parentId: work.Id);
    }

    [Fact]
    public void CategoryTreeSurvivesReopen()
    {
        var path = Path.Combine(_directory, "tree-persist.aegis");
        Guid parentId;
        Guid childId;

        using (var vault = VaultService.CreateNew(path, Password, FastOptions))
        {
            parentId = vault.AddCategory("Work", "green").Id;
            childId = vault.AddCategory("Servers", parentId: parentId).Id;
        }

        using (var vault = VaultService.Open(path))
        {
            Assert.Equal(VaultUnlockStatus.Success, vault.Unlock(Password));
            Assert.Equal(parentId, vault.Categories.Single(category => category.Id == childId).ParentId);
            Assert.Null(vault.Categories.Single(category => category.Id == parentId).ParentId);
        }
    }

    [Fact]
    public void GetCategorySubtreeReturnsTheWholeBranch()
    {
        using var vault = VaultService.CreateNew(Path.Combine(_directory, "subtree.aegis"), Password, FastOptions);
        var root = vault.AddCategory("Work");
        var child = vault.AddCategory("Servers", parentId: root.Id);
        var grandChild = vault.AddCategory("Production", parentId: child.Id);
        var other = vault.AddCategory("Home");

        var subtree = vault.GetCategorySubtree(root.Id);

        Assert.Equal(3, subtree.Count);
        Assert.Contains(root.Id, subtree);
        Assert.Contains(child.Id, subtree);
        Assert.Contains(grandChild.Id, subtree);
        Assert.DoesNotContain(other.Id, subtree);
    }

    [Fact]
    public void MoveCategoryReparentsAndRejectsInvalidTargets()
    {
        using var vault = VaultService.CreateNew(Path.Combine(_directory, "move.aegis"), Password, FastOptions);
        var work = vault.AddCategory("Work");
        var home = vault.AddCategory("Home");
        var servers = vault.AddCategory("Servers", parentId: work.Id);
        var production = vault.AddCategory("Production", parentId: servers.Id);

        Assert.True(vault.MoveCategory(servers.Id, home.Id));
        Assert.Equal(home.Id, vault.Categories.Single(c => c.Id == servers.Id).ParentId);
        Assert.Equal(servers.Id, vault.Categories.Single(c => c.Id == production.Id).ParentId);

        // Into itself, into its own subtree, or under a missing parent.
        Assert.Throws<CategoryValidationException>(() => vault.MoveCategory(servers.Id, servers.Id));
        Assert.Throws<CategoryValidationException>(() => vault.MoveCategory(home.Id, production.Id));
        var missing = Assert.Throws<CategoryValidationException>(() => vault.MoveCategory(servers.Id, Guid.NewGuid()));
        Assert.Equal(CategoryValidationError.ParentMissing, missing.Error);

        // Lifting back to the top level.
        Assert.True(vault.MoveCategory(production.Id, null));
        Assert.Null(vault.Categories.Single(c => c.Id == production.Id).ParentId);
    }

    [Fact]
    public void MoveCategoryRefusesToExceedTheDepthCap()
    {
        using var vault = VaultService.CreateNew(Path.Combine(_directory, "depth.aegis"), Password, FastOptions);
        var branch = vault.AddCategory("Branch");
        vault.AddCategory("Leaf", parentId: branch.Id);

        var level1 = vault.AddCategory("L1");
        var level2 = vault.AddCategory("L2", parentId: level1.Id);
        var level3 = vault.AddCategory("L3", parentId: level2.Id);

        // L3 already sits at depth 3 while the branch is two levels tall, so
        // nesting it there would need five levels.
        var tooDeep = Assert.Throws<CategoryValidationException>(() => vault.MoveCategory(branch.Id, level3.Id));
        Assert.Equal(CategoryValidationError.DepthExceeded, tooDeep.Error);
    }

    [Fact]
    public void DeleteCategoryPromotesChildrenToItsOwnParent()
    {
        using var vault = VaultService.CreateNew(Path.Combine(_directory, "delete-promote.aegis"), Password, FastOptions);
        var root = vault.AddCategory("Work");
        var middle = vault.AddCategory("Servers", parentId: root.Id);
        var leaf = vault.AddCategory("Production", parentId: middle.Id);
        var entry = vault.AddEntry(new PasswordEntry { Title = "Host", CategoryId = middle.Id });

        var result = vault.DeleteCategory(middle.Id, CategoryDeleteMode.PromoteChildren);

        Assert.True(result.Removed);
        Assert.Empty(result.Renamed);
        Assert.Equal(root.Id, vault.Categories.Single(c => c.Id == leaf.Id).ParentId);
        Assert.Null(vault.Entries.Single(e => e.Id == entry.Id).CategoryId);
    }

    [Fact]
    public void DeleteCategoryCanDenyOrCascade()
    {
        using var vault = VaultService.CreateNew(Path.Combine(_directory, "delete-modes.aegis"), Password, FastOptions);
        var root = vault.AddCategory("Work");
        var child = vault.AddCategory("Servers", parentId: root.Id);
        var grandChild = vault.AddCategory("Production", parentId: child.Id);

        Assert.Throws<InvalidOperationException>(() => vault.DeleteCategory(root.Id, CategoryDeleteMode.Deny));
        Assert.Equal(3, vault.Categories.Count);

        Assert.True(vault.DeleteCategory(root.Id, CategoryDeleteMode.Cascade).Removed);
        Assert.Empty(vault.Categories);
        Assert.DoesNotContain(vault.Categories, category => category.Id == grandChild.Id);
    }

    [Fact]
    public void MergeCategoriesReassignsEntriesAndChildren()
    {
        using var vault = VaultService.CreateNew(Path.Combine(_directory, "merge.aegis"), Password, FastOptions);
        var source = vault.AddCategory("Servers");
        var target = vault.AddCategory("Work");
        var child = vault.AddCategory("Production", parentId: source.Id);
        var entry = vault.AddEntry(new PasswordEntry { Title = "Host", CategoryId = source.Id });

        Assert.True(vault.MergeCategories(source.Id, target.Id));

        Assert.DoesNotContain(vault.Categories, category => category.Id == source.Id);
        Assert.Equal(target.Id, vault.Categories.Single(c => c.Id == child.Id).ParentId);
        Assert.Equal(target.Id, vault.Entries.Single(e => e.Id == entry.Id).CategoryId);

        Assert.Throws<CategoryValidationException>(() => vault.MergeCategories(target.Id, target.Id));
        Assert.False(vault.MergeCategories(Guid.NewGuid(), target.Id));
    }

    [Fact]
    public void MoveCategoryRejectsASiblingNameCollision()
    {
        using var vault = VaultService.CreateNew(Path.Combine(_directory, "move-name.aegis"), Password, FastOptions);
        var left = vault.AddCategory("Left");
        var right = vault.AddCategory("Right");
        var shared = vault.AddCategory("Shared", parentId: left.Id);
        var otherShared = vault.AddCategory("Shared", parentId: right.Id);

        // Staying under the same parent is always legal.
        Assert.True(vault.MoveCategory(shared.Id, left.Id));

        var collision = Assert.Throws<CategoryValidationException>(() => vault.MoveCategory(shared.Id, right.Id));
        Assert.Equal(CategoryValidationError.NameDuplicate, collision.Error);
        Assert.Equal(left.Id, vault.Categories.Single(category => category.Id == shared.Id).ParentId);

        // The comparison is case-insensitive, exactly like create/rename.
        Assert.True(vault.RenameCategory(otherShared.Id, "sHaReD"));
        Assert.Throws<CategoryValidationException>(() => vault.MoveCategory(shared.Id, right.Id));

        Assert.True(vault.RenameCategory(otherShared.Id, "Other"));
        Assert.True(vault.MoveCategory(shared.Id, right.Id));
        Assert.Equal(right.Id, vault.Categories.Single(category => category.Id == shared.Id).ParentId);
    }

    [Fact]
    public void MergeCategoriesRejectsNameCollisionsAndDepthOverflow()
    {
        using var vault = VaultService.CreateNew(Path.Combine(_directory, "merge-guards.aegis"), Password, FastOptions);
        var source = vault.AddCategory("Servers");
        var target = vault.AddCategory("Work");
        var sourceChild = vault.AddCategory("Production", parentId: source.Id);
        var targetChild = vault.AddCategory("Production", parentId: target.Id);

        var collision = Assert.Throws<CategoryValidationException>(() => vault.MergeCategories(source.Id, target.Id));
        Assert.Equal(CategoryValidationError.NameDuplicate, collision.Error);

        // A rejected merge must not leave a half-applied tree behind.
        Assert.Contains(vault.Categories, category => category.Id == source.Id);
        Assert.Equal(source.Id, vault.Categories.Single(category => category.Id == sourceChild.Id).ParentId);
        Assert.Equal(target.Id, vault.Categories.Single(category => category.Id == targetChild.Id).ParentId);

        // Renaming the clash away makes the same merge legal.
        Assert.True(vault.RenameCategory(targetChild.Id, "Legacy"));
        Assert.True(vault.MergeCategories(source.Id, target.Id));
        Assert.Equal(target.Id, vault.Categories.Single(category => category.Id == sourceChild.Id).ParentId);
    }

    [Fact]
    public void MergeCategoriesRefusesToExceedTheDepthCap()
    {
        using var vault = VaultService.CreateNew(Path.Combine(_directory, "merge-depth.aegis"), Password, FastOptions);
        var source = vault.AddCategory("Servers");
        vault.AddCategory("Production", parentId: source.Id);

        var level1 = vault.AddCategory("L1");
        var level2 = vault.AddCategory("L2", parentId: level1.Id);
        var level3 = vault.AddCategory("L3", parentId: level2.Id);
        var level4 = vault.AddCategory("L4", parentId: level3.Id);

        // L4 already sits at the cap, so adopting the source's child needs five.
        var overflow = Assert.Throws<CategoryValidationException>(() => vault.MergeCategories(source.Id, level4.Id));
        Assert.Equal(CategoryValidationError.DepthExceeded, overflow.Error);
        Assert.Contains(vault.Categories, category => category.Id == source.Id);
    }

    [Fact]
    public void DeleteCategoryRenamesPromotedChildrenInsteadOfDuplicatingNames()
    {
        var path = Path.Combine(_directory, "delete-rename.aegis");
        Guid renamedId;
        Guid rootId;

        using (var vault = VaultService.CreateNew(path, Password, FastOptions))
        {
            var root = vault.AddCategory("Work");
            var parent = vault.AddCategory("Legacy", parentId: root.Id);
            var child = vault.AddCategory("Servers", parentId: parent.Id);
            vault.AddCategory("Servers", parentId: root.Id);

            var result = vault.DeleteCategory(parent.Id, CategoryDeleteMode.PromoteChildren);

            Assert.True(result.Removed);
            var rename = Assert.Single(result.Renamed);
            Assert.Equal(child.Id, rename.Id);
            Assert.Equal("Servers", rename.From);
            Assert.Equal("Servers (2)", rename.To);

            var promoted = vault.Categories.Single(category => category.Id == child.Id);
            Assert.Equal(root.Id, promoted.ParentId);
            Assert.Equal("Servers (2)", promoted.Name);

            renamedId = child.Id;
            rootId = root.Id;
        }

        using (var vault = VaultService.Open(path))
        {
            Assert.Equal(VaultUnlockStatus.Success, vault.Unlock(Password));
            var promoted = vault.Categories.Single(category => category.Id == renamedId);
            Assert.Equal(rootId, promoted.ParentId);
            Assert.Equal("Servers (2)", promoted.Name);
        }
    }

    [Fact]
    public void ReorderCategoryPersistsTheSiblingOrder()
    {
        var path = Path.Combine(_directory, "reorder.aegis");
        Guid gammaId;

        using (var vault = VaultService.CreateNew(path, Password, FastOptions))
        {
            vault.AddCategory("Alpha");
            vault.AddCategory("Beta");
            gammaId = vault.AddCategory("Gamma").Id;

            var alpha = vault.Categories.Single(category => category.Name == "Alpha");
            Assert.True(vault.ReorderCategory(gammaId, alpha.Id));

            Assert.Equal(
                ["Gamma", "Alpha", "Beta"],
                vault.Categories.OrderBy(category => category.SortOrder).Select(category => category.Name));
            Assert.Equal([1, 2, 3], vault.Categories.Select(category => category.SortOrder).Order());
        }

        using (var vault = VaultService.Open(path))
        {
            Assert.Equal(VaultUnlockStatus.Success, vault.Unlock(Password));
            Assert.Equal(
                ["Gamma", "Alpha", "Beta"],
                vault.Categories.OrderBy(category => category.SortOrder).Select(category => category.Name));
        }
    }

    [Fact]
    public void ReorderCategoryRejectsATargetUnderAnotherParent()
    {
        using var vault = VaultService.CreateNew(
            Path.Combine(_directory, "reorder-cross.aegis"), Password, FastOptions);

        var left = vault.AddCategory("Left");
        var right = vault.AddCategory("Right");
        var child = vault.AddCategory("Child", parentId: left.Id);

        Assert.False(vault.ReorderCategory(child.Id, right.Id));
        Assert.False(vault.ReorderCategory(child.Id, child.Id));
        Assert.Equal(left.Id, vault.Categories.Single(category => category.Id == child.Id).ParentId);
    }

    [Fact]
    public void MoveCategoryAppendsToTheEndOfTheNewSiblingGroup()
    {
        using var vault = VaultService.CreateNew(
            Path.Combine(_directory, "move-order.aegis"), Password, FastOptions);

        var destination = vault.AddCategory("Destination");
        vault.AddCategory("Existing", parentId: destination.Id);
        var moving = vault.AddCategory("Moving");

        Assert.True(vault.MoveCategory(moving.Id, destination.Id));

        var children = vault.Categories.Where(category => category.ParentId == destination.Id).ToList();
        Assert.Equal(["Existing", "Moving"], children.OrderBy(category => category.SortOrder).Select(category => category.Name));
        Assert.Equal([1, 2], children.OrderBy(category => category.SortOrder).Select(category => category.SortOrder));
    }

    [Fact]
    public void DeletingACategoryKeepsTheSiblingOrderDense()
    {
        using var vault = VaultService.CreateNew(
            Path.Combine(_directory, "delete-order.aegis"), Password, FastOptions);

        vault.AddCategory("Alpha");
        var beta = vault.AddCategory("Beta");
        vault.AddCategory("Gamma");

        Assert.True(vault.DeleteCategory(beta.Id));

        Assert.Equal(["Alpha", "Gamma"], vault.Categories.OrderBy(category => category.SortOrder).Select(category => category.Name));
        Assert.Equal([1, 2], vault.Categories.Select(category => category.SortOrder).Order());
    }
}
