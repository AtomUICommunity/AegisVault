using Avalonia.Controls;
using Avalonia.Interactivity;

namespace AegisVault.App.Views;

/// <summary>What the user chose in the close-confirmation dialog.</summary>
public enum ClosePromptResult
{
    /// <summary>Keep the window open (also the result when dismissed via Esc/titlebar).</summary>
    Cancel,

    /// <summary>Hide to the tray.</summary>
    Minimize,

    /// <summary>Exit the application.</summary>
    Exit,
}

/// <summary>
/// Asks what to do when the main window is closed: exit, minimize to tray, or
/// cancel. Also carries the user's "don't ask again" choice.
/// </summary>
public partial class CloseConfirmWindow : Window
{
    public CloseConfirmWindow()
    {
        InitializeComponent();
    }

    public CloseConfirmWindow(string title, string message)
        : this()
    {
        Title = title;
        TitleBar.Title = title;
        MessageText.Text = message;
    }

    /// <summary>True when the user ticked "don't ask again".</summary>
    public bool DontAskAgain { get; private set; }

    private void OnExitClicked(object? sender, RoutedEventArgs e) => Finish(ClosePromptResult.Exit);

    private void OnMinimizeClicked(object? sender, RoutedEventArgs e) => Finish(ClosePromptResult.Minimize);

    private void OnCancelClicked(object? sender, RoutedEventArgs e) => Finish(ClosePromptResult.Cancel);

    private void Finish(ClosePromptResult result)
    {
        DontAskAgain = DontAskCheckBox.IsChecked == true;
        Close(result);
    }
}
