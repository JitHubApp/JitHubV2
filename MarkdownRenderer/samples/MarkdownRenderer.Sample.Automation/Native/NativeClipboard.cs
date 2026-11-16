namespace MarkdownRenderer.Sample.Automation.Native;

/// <summary>
/// Reads and writes Unicode clipboard text so copy probes can detect a real clipboard change.
/// </summary>
internal static class NativeClipboard
{
    private const uint ClipboardFormatUnicodeText = 13;
    private const uint GlobalMemoryMoveable = 0x0002;
    private const uint GlobalMemoryZeroInit = 0x0040;

    internal static void SetClipboardText(string text)
    {
        WithOpenClipboard(() =>
        {
            if (!EmptyClipboard())
                throw new System.ComponentModel.Win32Exception(System.Runtime.InteropServices.Marshal.GetLastWin32Error());

            int byteCount = checked((text.Length + 1) * 2);
            IntPtr handle = GlobalAlloc(GlobalMemoryMoveable | GlobalMemoryZeroInit, (UIntPtr)byteCount);
            if (handle == IntPtr.Zero)
                throw new System.ComponentModel.Win32Exception(System.Runtime.InteropServices.Marshal.GetLastWin32Error());

            bool transferred = false;
            try
            {
                IntPtr locked = GlobalLock(handle);
                if (locked == IntPtr.Zero)
                    throw new System.ComponentModel.Win32Exception(System.Runtime.InteropServices.Marshal.GetLastWin32Error());
                try
                {
                    if (text.Length > 0)
                        System.Runtime.InteropServices.Marshal.Copy(text.ToCharArray(), 0, locked, text.Length);
                    System.Runtime.InteropServices.Marshal.WriteInt16(locked, text.Length * 2, 0);
                }
                finally
                {
                    GlobalUnlock(handle);
                }

                if (SetClipboardData(ClipboardFormatUnicodeText, handle) == IntPtr.Zero)
                    throw new System.ComponentModel.Win32Exception(System.Runtime.InteropServices.Marshal.GetLastWin32Error());
                transferred = true;
            }
            finally
            {
                if (!transferred)
                    GlobalFree(handle);
            }
        });
    }

    internal static string GetClipboardText()
    {
        return WithOpenClipboard(() =>
        {
            IntPtr handle = GetClipboardData(ClipboardFormatUnicodeText);
            if (handle == IntPtr.Zero)
                return string.Empty;

            IntPtr locked = GlobalLock(handle);
            if (locked == IntPtr.Zero)
                return string.Empty;
            try
            {
                return System.Runtime.InteropServices.Marshal.PtrToStringUni(locked) ?? string.Empty;
            }
            finally
            {
                GlobalUnlock(handle);
            }
        });
    }

    private static void WithOpenClipboard(Action action) => WithOpenClipboard<object?>(() =>
    {
        action();
        return null;
    });

    private static T WithOpenClipboard<T>(Func<T> action)
    {
        Exception? last = null;
        for (int attempt = 0; attempt < 20; attempt++)
        {
            if (OpenClipboard(IntPtr.Zero))
            {
                try { return action(); }
                finally { CloseClipboard(); }
            }

            last = new System.ComponentModel.Win32Exception(System.Runtime.InteropServices.Marshal.GetLastWin32Error());
            Thread.Sleep(50);
        }

        throw new InvalidOperationException("Could not open clipboard for automation probe", last);
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    private static extern bool OpenClipboard(IntPtr newOwner);

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    private static extern bool CloseClipboard();

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    private static extern bool EmptyClipboard();

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr GetClipboardData(uint format);

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetClipboardData(uint format, IntPtr handle);

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalAlloc(uint flags, UIntPtr bytes);

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalLock(IntPtr handle);

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalUnlock(IntPtr handle);

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalFree(IntPtr handle);
}
