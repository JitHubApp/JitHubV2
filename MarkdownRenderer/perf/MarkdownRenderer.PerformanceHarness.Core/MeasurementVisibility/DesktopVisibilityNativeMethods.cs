using System.Runtime.InteropServices;

namespace MarkdownRenderer.PerformanceHarness.Core.MeasurementVisibility;

/// <summary>
/// Win32 and DWM declarations the desktop visibility monitor and its window
/// probe call. The declarations only read window geometry, visibility and
/// z-order, and install out-of-context WinEvent hooks.
/// </summary>
internal static class DesktopVisibilityNativeMethods
{
    internal const uint EventSystemForeground = 0x0003;
    internal const uint EventSystemDesktopSwitch = 0x0020;
    internal const uint EventObjectShow = 0x8002;
    internal const uint EventObjectReorder = 0x8004;
    internal const uint EventObjectLocationChange = 0x800B;
    internal const int ObjectIdWindow = 0;
    internal const int ChildIdSelf = 0;
    internal const uint WineventOutOfContext = 0;
    internal const uint WmQuit = 0x0012;
    internal const uint PeekMessageNoRemove = 0;
    internal const uint DwmwaCloaked = 14;
    internal const uint DwmwaExtendedFrameBounds = 9;
    internal const uint MonitorDefaultToNearest = 2;
    internal const uint GetAncestorRootOwner = 3;

    internal delegate bool EnumWindowsProc(IntPtr window, IntPtr parameter);

    [StructLayout(LayoutKind.Sequential)]
    internal struct NativeMessage
    {
        public IntPtr Window;
        public uint Message;
        public UIntPtr WParam;
        public IntPtr LParam;
        public uint Time;
        public int X;
        public int Y;
        public uint Private;
    }

    internal delegate void WinEventDelegate(
        IntPtr hook,
        uint eventType,
        IntPtr hwnd,
        int objectId,
        int childId,
        uint eventThread,
        uint eventTime);

    [StructLayout(LayoutKind.Sequential)]
    internal struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct NativeMonitorInfo
    {
        public int Size;
        public NativeRect MonitorArea;
        public NativeRect WorkArea;
        public uint Flags;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool EnumWindows(EnumWindowsProc callback, IntPtr parameter);

    [DllImport("user32.dll")]
    internal static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool IsWindowVisible(IntPtr window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool IsIconic(IntPtr window);

    [DllImport("user32.dll")]
    internal static extern IntPtr MonitorFromWindow(IntPtr window, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetMonitorInfo(IntPtr monitor, ref NativeMonitorInfo info);

    [DllImport("user32.dll")]
    internal static extern IntPtr GetAncestor(IntPtr window, uint flags);

    [DllImport("user32.dll")]
    internal static extern IntPtr SetWinEventHook(
        uint eventMin,
        uint eventMax,
        IntPtr eventHookModule,
        WinEventDelegate callback,
        uint processId,
        uint threadId,
        uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool PeekMessage(
        out NativeMessage message,
        IntPtr window,
        uint minimumMessage,
        uint maximumMessage,
        uint removeMessage);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern int GetMessage(
        out NativeMessage message,
        IntPtr window,
        uint minimumMessage,
        uint maximumMessage);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool TranslateMessage(in NativeMessage message);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    internal static extern IntPtr DispatchMessage(in NativeMessage message);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool PostThreadMessage(
        uint threadId,
        uint message,
        UIntPtr wParam,
        IntPtr lParam);

    [DllImport("kernel32.dll")]
    internal static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool UnhookWinEvent(IntPtr hook);

    [DllImport("dwmapi.dll")]
    internal static extern int DwmGetWindowAttribute(
        IntPtr window,
        uint attribute,
        out uint value,
        int valueSize);

    [DllImport("dwmapi.dll", EntryPoint = "DwmGetWindowAttribute")]
    internal static extern int DwmGetWindowAttribute(
        IntPtr window,
        uint attribute,
        out NativeRect value,
        int valueSize);
}
