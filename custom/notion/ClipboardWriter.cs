using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace LiveCaptionsNotion.Native;

internal static class ClipboardWriter
{
    // Publish Unicode text directly: no delayed OLE rendering/flush race with clipboard readers.
    // Call on the window's dispatcher; delays yield rather than blocking the UI.
    internal static async Task SetTextAsync(string text, IntPtr owner, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (owner == IntPtr.Zero) throw new ArgumentException("A clipboard owner window is required.", nameof(owner));
        for (int attempt = 0; ; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (OpenClipboard(owner)) break;
            int error = Marshal.GetLastWin32Error();
            if (attempt >= 25) throw new Win32Exception(error, "Clipboard remained busy; text was not copied.");
            await Task.Delay(60, cancellationToken);
        }

        IntPtr memory = IntPtr.Zero;
        try
        {
            byte[] bytes = Encoding.Unicode.GetBytes(text + '\0');
            memory = GlobalAlloc(0x0002, (UIntPtr)bytes.Length);
            if (memory == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
            IntPtr address = GlobalLock(memory);
            if (address == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
            try { Marshal.Copy(bytes, 0, address, bytes.Length); }
            finally { GlobalUnlock(memory); }
            if (!EmptyClipboard()) throw new Win32Exception(Marshal.GetLastWin32Error());
            if (SetClipboardData(13, memory) == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
            memory = IntPtr.Zero; // Windows now owns this allocation.
        }
        finally
        {
            if (memory != IntPtr.Zero) GlobalFree(memory);
            CloseClipboard();
        }
    }

    [DllImport("user32.dll", SetLastError = true)] private static extern bool OpenClipboard(IntPtr owner);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool EmptyClipboard();
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetClipboardData(uint format, IntPtr memory);
    [DllImport("user32.dll")] private static extern bool CloseClipboard();
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr GlobalAlloc(uint flags, UIntPtr bytes);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr GlobalLock(IntPtr memory);
    [DllImport("kernel32.dll")] private static extern bool GlobalUnlock(IntPtr memory);
    [DllImport("kernel32.dll")] private static extern IntPtr GlobalFree(IntPtr memory);
}
