using System.Runtime.InteropServices;
using System.Text;

namespace JitHub.WinUI.Automation.Native;

internal static partial class NativeMethods
{
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool OpenClipboard(IntPtr hWndNewOwner);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool CloseClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool IsClipboardFormatAvailable(uint format);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr GetClipboardData(uint uFormat);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool EmptyClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetClipboardData(uint uFormat, IntPtr hMem);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalAlloc(uint uFlags, UIntPtr dwBytes);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalFree(IntPtr hMem);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalLock(IntPtr hMem);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalUnlock(IntPtr hMem);

    internal static void SetClipboardText(string text)
    {
        if (!OpenClipboard(IntPtr.Zero))
        {
            return;
        }

        IntPtr handle = IntPtr.Zero;
        try
        {
            _ = EmptyClipboard();
            byte[] bytes = Encoding.Unicode.GetBytes((text ?? string.Empty) + '\0');
            handle = GlobalAlloc(GmemMoveable, (UIntPtr)bytes.Length);
            if (handle == IntPtr.Zero)
            {
                return;
            }

            IntPtr locked = GlobalLock(handle);
            if (locked == IntPtr.Zero)
            {
                return;
            }

            try
            {
                Marshal.Copy(bytes, 0, locked, bytes.Length);
            }
            finally
            {
                _ = GlobalUnlock(handle);
            }

            if (SetClipboardData(CfUnicodeText, handle) != IntPtr.Zero)
            {
                handle = IntPtr.Zero;
            }
        }
        finally
        {
            _ = CloseClipboard();
            if (handle != IntPtr.Zero)
            {
                _ = GlobalFree(handle);
            }
        }
    }

    internal static string GetClipboardText()
    {
        if (!OpenClipboard(IntPtr.Zero))
        {
            return string.Empty;
        }

        try
        {
            if (!IsClipboardFormatAvailable(CfUnicodeText))
            {
                return string.Empty;
            }

            IntPtr handle = GetClipboardData(CfUnicodeText);
            if (handle == IntPtr.Zero)
            {
                return string.Empty;
            }

            IntPtr locked = GlobalLock(handle);
            if (locked == IntPtr.Zero)
            {
                return string.Empty;
            }

            try
            {
                return Marshal.PtrToStringUni(locked) ?? string.Empty;
            }
            finally
            {
                _ = GlobalUnlock(handle);
            }
        }
        finally
        {
            _ = CloseClipboard();
        }
    }
}
