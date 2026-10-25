using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.VisualTree;
using AegisVault.App.ViewModels;
using AegisVault.App.Views;
using Xunit;

namespace AegisVault.App.Tests;

/// <summary>
/// Verifies the round trip a user actually performs: drag a row, close the app,
/// open it again, and see the same order.
/// </summary>
public sealed class CategoryOrderPersistenceTests
{
    [Fact]
    public Task TheDraggedOrderSurvivesAReopen() => Headless.Run(() =>
    {
        var directory = Path.Combine(Path.GetTempPath(), "aegis-order", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "vault.aegis");
        var password = "master password"u8.ToArray();
        var options = new Core.Services.VaultOptions
        {
            Kdf = new Core.Models.KdfParameters
            {
                Algorithm = Core.Models.KdfParameters.AlgorithmArgon2id,
                Iterations = 3,
                MemoryBytes = 8L * 1024 * 1024,
            },
        };

        try
        {
            using (var vault = Core.Services.VaultService.CreateNew(path, password, options))
            {
                vault.AddCategory("Alpha");
                vault.AddCategory("Beta");
                vault.AddCategory("Gamma");

                var viewModel = new MainViewModel(vault);
                var window = new MainWindow { DataContext = viewModel };
                window.Show();
                Avalonia.Threading.Dispatcher.UIThread.RunJobs();

                Border Row(string name) => window.GetVisualDescendants().OfType<Border>()
                    .First(candidate => candidate.Classes.Contains("categoryRowLabel")
                                        && candidate.DataContext is CategoryNavNode node
                                        && node.Item.DisplayName == name);

                var source = Row("Gamma");
                var target = Row("Alpha");
                var start = source.TranslatePoint(new Point(source.Bounds.Width / 2, source.Bounds.Height / 2), window)!.Value;
                var drop = target.TranslatePoint(new Point(target.Bounds.Width / 2, 3), window)!.Value;

                window.MouseDown(start, MouseButton.Left);
                window.MouseMove(new Point(start.X + 8, start.Y + 8));
                Avalonia.Threading.Dispatcher.UIThread.RunJobs();
                window.MouseMove(drop);
                Avalonia.Threading.Dispatcher.UIThread.RunJobs();
                window.MouseUp(drop, MouseButton.Left);
                Avalonia.Threading.Dispatcher.UIThread.RunJobs();

                Assert.Equal(["Gamma", "Alpha", "Beta"], vault.Categories
                    .OrderBy(category => category.SortOrder)
                    .Select(category => category.Name));
                window.Close();
            }

            using (var reopened = Core.Services.VaultService.Open(path))
            {
                Assert.Equal(Core.Services.VaultUnlockStatus.Success, reopened.Unlock(password));
                Assert.Equal(["Gamma", "Alpha", "Beta"], reopened.Categories
                    .OrderBy(category => category.SortOrder)
                    .Select(category => category.Name));
                Assert.Equal([1, 2, 3], reopened.Categories
                    .Select(category => category.SortOrder)
                    .Order());
            }
        }
        finally
        {
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    });
}
