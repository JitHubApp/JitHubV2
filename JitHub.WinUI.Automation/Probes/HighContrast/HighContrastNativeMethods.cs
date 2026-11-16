using System.Runtime.InteropServices;

namespace JitHub.WinUI.Automation.Probes.HighContrast;

/// <summary>
/// The Win32 calls and structures the high contrast probe uses for the system setting, DPI, window bounds and window capture.
/// </summary>
internal static class HighContrastNativeMethods
{
    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "SystemParametersInfoW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SystemParametersInfoW(
        uint action,
        uint parameter,
        ref NativeHighContrast highContrast,
        uint updateFlags);

    [DllImport("user32.dll")]
    internal static extern IntPtr SetThreadDpiAwarenessContext(IntPtr dpiContext);

    [DllImport("user32.dll")]
    internal static extern uint GetSysColor(int index);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetWindowRect(IntPtr windowHandle, out NativeRect bounds);

    [DllImport("user32.dll")]
    internal static extern uint GetDpiForWindow(IntPtr windowHandle);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool PrintWindow(IntPtr windowHandle, IntPtr deviceContext, uint flags);

    [DllImport("dwmapi.dll")]
    internal static extern int DwmGetWindowAttribute(
        IntPtr windowHandle,
        int attribute,
        out NativeRect value,
        int valueSize);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetForegroundWindow(IntPtr windowHandle);

    [DllImport("user32.dll")]
    internal static extern IntPtr GetForegroundWindow();

    [StructLayout(LayoutKind.Sequential)]
    internal struct NativeHighContrast
    {
        public uint Size;
        public uint Flags;
        public IntPtr DefaultScheme;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }
}
