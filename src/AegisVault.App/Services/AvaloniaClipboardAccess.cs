using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using AegisVault.Platform;

namespace AegisVault.App.Services;

/// <summary>Clipboard access bound to the currently active top-level window.</summary>
public sealed class AvaloniaClipboardAccess : IClipboardAccess
{
    private readonly Func<TopLevel?> _topLevelProvider;

    public AvaloniaClipboardAccess(Func<TopLevel?> topLevelProvider)
    {
        _topLevelProvider = topLevelProvider;
    }

    public async Task SetTextAsync(string text)
    {
        var handle = _topLevelProvider()?.TryGetPlatformHandle()?.Handle ?? 0;

        // Preferred path on Windows: one clipboard transaction that writes the text and
        // the "do not record" markers together. Writing through the framework first
        // left a window where the secret sat on the clipboard without the flags, which
        // the clipboard history could capture permanently.
        if (ClipboardExclusion.TrySetSecretText(handle, text))
        {
            return;
        }

        // Fallback (non-Windows, or the clipboard was busy): write through Avalonia and
        // mark afterwards. The mark can fail silently here, which is why the primary
        // path above exists.
        if (GetClipboard() is not { } clipboard)
        {
            return;
        }

        var transfer = new DataTransfer();
        transfer.Add(DataTransferItem.CreateText(text));
        await clipboard.SetDataAsync(transfer);

        ClipboardExclusion.TryMarkCurrent(handle);
    }

    public async Task<string?> GetTextAsync()
    {
        if (GetClipboard() is not { } clipboard)
        {
            return null;
        }

        var transfer = await clipboard.TryGetDataAsync();
        if (transfer is null)
        {
            return null;
        }

        try
        {
            return await transfer.TryGetTextAsync();
        }
        finally
        {
            transfer.Dispose();
        }
    }

    public Task ClearAsync()
        => GetClipboard()?.ClearAsync() ?? Task.CompletedTask;

    private IClipboard? GetClipboard() => _topLevelProvider()?.Clipboard;
}
