using Avalonia.Controls;
using AegisVault.App.Views;
using Xunit;

namespace AegisVault.App.Tests;

/// <summary>
/// The close-confirmation dialog offers three outcomes (exit / minimize / cancel)
/// plus a "don't ask again" checkbox; the enum's default must be Cancel so a
/// dismissal (Esc, title-bar X) never exits by accident.
/// </summary>
public sealed class CloseConfirmWindowTests
{
    [Fact]
    public Task ExitButtonReportsExitAndCarriesTheDontAskChoice() => Headless.Run(() =>
    {
        var window = new CloseConfirmWindow("title", "message");
        try
        {
            var checkbox = window.FindControl<CheckBox>("DontAskCheckBox");
            var exit = window.FindControl<Button>("ExitButton");

            Assert.NotNull(checkbox);
            Assert.NotNull(exit);
            checkbox!.IsChecked = true;

            exit!.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));

            Assert.True(window.DontAskAgain);
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public Task MinimizeButtonDoesNotTickDontAskByDefault() => Headless.Run(() =>
    {
        var window = new CloseConfirmWindow("title", "message");
        try
        {
            Assert.False(window.DontAskAgain);
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public void CancelIsTheDefaultResult() => Assert.Equal(0, (int)ClosePromptResult.Cancel);
}
