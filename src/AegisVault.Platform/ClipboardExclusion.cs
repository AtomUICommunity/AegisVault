using System.Runtime.InteropServices;

namespace AegisVault.Platform;

/// <summary>
/// Marks the current clipboard content as sensitive on Windows so the
/// clipboard history (Win+V), the cloud clipboard and clipboard-monitor
/// processing skip it. Must be called while the copying process still owns
/// the clipboard. Best effort: unsupported platforms and failures return false.
/// </summary>
public static class ClipboardExclusion
{
    private const uint GmemMoveable = 0x0002;
    private const uint GmemZeroInit = 0x0040;
    private const uint CfUnicodeText = 13;

    public static bool IsSupported => OperatingSystem.IsWindows();

    /// <summary>
    /// Puts <paramref name="text"/> on the clipboard together with the exclusion
    /// markers, in a single clipboard transaction.
    /// <para>
    /// This exists because writing the text through the UI framework and then adding
    /// the markers afterwards leaves a window - one await continuation wide - in which
    /// the clipboard holds the secret with no "do not record" flags. Anything sampling
    /// the clipboard in that window (Windows clipboard history, cloud clipboard) can
    /// capture the password, and the 30-second auto-clear cannot reach those copies
    /// afterwards. Holding the clipboard open for the whole sequence means no other
    /// process can read it until every format is in place.
    /// </para>
    /// </summary>
    /// <returns>
    /// False when the platform is not Windows or the clipboard was unavailable; the
    /// caller should fall back to its normal write path.
    /// </returns>
    public static bool TrySetSecretText(nint hwnd, string text)
    {
        if (!IsSupported || hwnd == 0)
        {
            return false;
        }

        try
        {
            if (!OpenClipboard(hwnd))
            {
                return false;
            }

            try
            {
                if (!EmptyClipboard())
                {
                    return false;
                }

                // Mark first: even if writing the text fails afterwards, an empty
                // clipboard carrying the flags cannot leak anything.
                var marked =
                    TrySetDword("CanIncludeInClipboardHistory", 0) &
                    TrySetDword("CanUploadToCloudClipboard", 0) &
                    TrySetDword("ExcludeClipboardContentFromMonitorProcessing", 1);

                var written = TrySetUnicodeText(text);
                return marked && written;
            }
            finally
            {
                CloseClipboard();
            }
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// Adds the exclusion marker formats to the clipboard. <paramref name="hwnd"/>
    /// is the window that takes clipboard ownership for the call.
    /// </summary>
    public static bool TryMarkCurrent(nint hwnd)
    {
        if (!IsSupported || hwnd == 0)
        {
            return false;
        }

        try
        {
            if (!OpenClipboard(hwnd))
            {
                return false;
            }

            try
            {
                var history = TrySetDword("CanIncludeInClipboardHistory", 0);
                var cloud = TrySetDword("CanUploadToCloudClipboard", 0);
                var monitor = TrySetDword("ExcludeClipboardContentFromMonitorProcessing", 1);
                return history && cloud && monitor;
            }
            finally
            {
                CloseClipboard();
            }
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static bool TrySetUnicodeText(string text)
    {
        // CF_UNICODETEXT is UTF-16LE with a terminating NUL, which the byte count must
        // include; without it other applications read past the end of the buffer.
        var bytes = (text.Length + 1) * sizeof(char);
        var memory = GlobalAlloc(GmemMoveable | GmemZeroInit, (nuint)bytes);
        if (memory == 0)
        {
            return false;
        }

        var pointer = GlobalLock(memory);
        if (pointer == 0)
        {
            GlobalFree(memory);
            return false;
        }

        try
        {
            // Copy the UTF-16 code units verbatim; the trailing NUL is already there
            // because the block was zero-initialised.
            unsafe
            {
                fixed (char* source = text)
                {
                    Buffer.MemoryCopy(source, (void*)pointer, bytes, text.Length * sizeof(char));
                }
            }
        }
        finally
        {
            GlobalUnlock(memory);
        }

        if (SetClipboardData(CfUnicodeText, memory) == 0)
        {
            GlobalFree(memory);
            return false;
        }

        // Ownership moved to the clipboard, which frees the block.
        return true;
    }

    private static bool TrySetDword(string formatName, int value)
    {
        var format = RegisterClipboardFormat(formatName);
        if (format == 0)
        {
            return false;
        }

        var memory = GlobalAlloc(GmemMoveable | GmemZeroInit, 4);
        if (memory == 0)
        {
            return false;
        }

        var pointer = GlobalLock(memory);
        if (pointer == 0)
        {
            GlobalFree(memory);
            return false;
        }

        Marshal.WriteInt32(pointer, value);
        GlobalUnlock(memory);

        if (SetClipboardData(format, memory) == 0)
        {
            GlobalFree(memory);
            return false;
        }

        // Ownership moved to the clipboard, which frees the block.
        return true;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenClipboard(nint hWndNewOwner);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EmptyClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint SetClipboardData(uint uFormat, nint hMem);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint RegisterClipboardFormat(string lpszFormat);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint GlobalAlloc(uint uFlags, nuint dwBytes);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint GlobalLock(nint hMem);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalUnlock(nint hMem);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint GlobalFree(nint hMem);
}
