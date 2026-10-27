using System.Linq;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.VisualTree;
using AegisVault.App.ViewModels;
using AegisVault.App.Views;
using Xunit;
using Xunit.Abstractions;

namespace AegisVault.App.Tests;

/// <summary>
/// Every place that lists categories (move-to, merge-into, and the entry editor's combo)
/// must expose the hierarchy, otherwise a nested category is indistinguishable from a root.
/// </summary>
public sealed class CategoryChoiceDepthTests
{
    private readonly ITestOutputHelper _out;

    public CategoryChoiceDepthTests(ITestOutputHelper output) => _out = output;

    private static (MainViewModel Vm, Core.Services.VaultService Vault, string Dir) Create()
    {
        var dir = Path.Combine(Path.GetTempPath(), "aegis-depths", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var vault = Core.Services.VaultService.CreateNew(
            Path.Combine(dir, "v.aegis"),
            "master password"u8.ToArray(),
            new Core.Services.VaultOptions
            {
                Kdf = new Core.Models.KdfParameters
                {
                    Algorithm = Core.Models.KdfParameters.AlgorithmArgon2id,
                    Iterations = 3,
                    MemoryBytes = 8L * 1024 * 1024,
                },
            });

        var work = vault.AddCategory("Work");
        var infra = vault.AddCategory("Infra", parentId: work.Id);
        vault.AddCategory("Database", parentId: infra.Id);
        vault.AddCategory("Mail");

        return (new MainViewModel(vault), vault, dir);
    }

    [Fact]
    public Task MergeChoicesCarryDepthAndExcludeTheSourceSubtree() => Headless.Run(() =>
    {
        var (vm, vault, dir) = Create();
        using var _ = vault;
        try
        {
            var workId = vault.Categories.Single(c => c.Name == "Work").Id;
            var choices = vm.BuildMergeChoices(workId);

            foreach (var c in choices)
            {
                _out.WriteLine($"'{c.Name}' depth={c.Depth}");
                Assert.Equal(c.Name.TrimStart(), c.Name);
            }

            // The source and everything under it must be absent.
            Assert.DoesNotContain(choices, c => c.Name is "Work" or "Infra" or "Database");

            // A sibling root stays, at depth 1.
            var mail = Assert.Single(choices, c => c.Name == "Mail");
            Assert.Equal(1, mail.Depth);
        }
        finally { TryDelete(dir); }
    });

    [Fact]
    public Task MergeChoicesKeepNestedDepths() => Headless.Run(() =>
    {
        var (vm, vault, dir) = Create();
        using var _ = vault;
        try
        {
            // Merging Mail leaves the Work branch intact, so its depths must survive.
            var mailId = vault.Categories.Single(c => c.Name == "Mail").Id;
            var choices = vm.BuildMergeChoices(mailId);
            var byName = choices.ToDictionary(c => c.Name, c => c.Depth);

            _out.WriteLine(string.Join(", ", choices.Select(c => $"{c.Name}={c.Depth}")));
            Assert.Equal(1, byName["Work"]);
            Assert.Equal(2, byName["Infra"]);
            Assert.Equal(3, byName["Database"]);
        }
        finally { TryDelete(dir); }
    });

    [Fact]
    public Task EntryEditorCategoryPickerIsTreeOrderedWithDepths() => Headless.Run(() =>
    {
        var (vm, vault, dir) = Create();
        using var _ = vault;
        try
        {
            var choices = vm.CategoryChoices.ToList();
            foreach (var c in choices)
            {
                _out.WriteLine($"'{c.Name}' depth={c.Depth}");
            }

            // First entry is the "no category" placeholder at depth 0.
            Assert.Equal(0, choices[0].Depth);

            var byName = choices.ToDictionary(c => c.Name, c => c.Depth);
            Assert.Equal(1, byName["Work"]);
            Assert.Equal(2, byName["Infra"]);
            Assert.Equal(3, byName["Database"]);
            Assert.Equal(1, byName["Mail"]);

            // Tree order: a child directly follows its parent, not alphabetical.
            var index = choices.FindIndex(c => c.Name == "Work");
            Assert.Equal("Infra", choices[index + 1].Name);
        }
        finally { TryDelete(dir); }
    });

    private static void TryDelete(string dir)
    {
        try { Directory.Delete(dir, true); } catch (IOException) { }
    }
}
