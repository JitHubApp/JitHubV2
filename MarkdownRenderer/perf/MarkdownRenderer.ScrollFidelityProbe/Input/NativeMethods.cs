using System.Runtime.InteropServices;

namespace MarkdownRenderer.ScrollFidelityProbe.Input;

/// <summary>Win32 window geometry and input injection used by the probe.</summary>
internal static partial class NativeMethods
{
    internal const uint InputMouse = 0;
    internal const uint MouseEventWheel = 0x0800;
    internal const int WheelDelta = 120;
    private const int DwmExtendedFrameBounds = 9;

    [StructLayout(LayoutKind.Sequential)]
    internal struct Point
    {
        internal int X;
        internal int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct NativeRect
    {
        internal int Left;
        internal int Top;
        internal int Right;
        internal int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MouseInput
    {
        internal int Dx;
        internal int Dy;
        internal int MouseData;
        internal uint Flags;
        internal uint Time;
        internal nint ExtraInfo;
    }

    /// <summary>The 64-bit INPUT layout: the union starts after 8 bytes.</summary>
    [StructLayout(LayoutKind.Explicit, Size = 40)]
    internal struct InputRecord
    {
        [FieldOffset(0)]
        internal uint Type;

        [FieldOffset(8)]
        internal MouseInput Mouse;
    }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool ClientToScreen(nint window, ref Point point);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetCursorPos(int x, int y);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetForegroundWindow(nint window);

    [LibraryImport("user32.dll", SetLastError = true)]
    internal static unsafe partial uint SendInput(uint count, InputRecord* inputs, int size);

    [LibraryImport("dwmapi.dll")]
    private static unsafe partial int DwmGetWindowAttribute(nint window, int attribute, void* value, int size);

    internal static unsafe NativeRect GetExtendedFrameBounds(nint window)
    {
        NativeRect rect;
        Marshal.ThrowExceptionForHR(DwmGetWindowAttribute(window, DwmExtendedFrameBounds, &rect, sizeof(NativeRect)));
        return rect;
    }

    internal static unsafe bool SendWheel(int delta)
    {
        InputRecord input = default;
        input.Type = InputMouse;
        input.Mouse.Flags = MouseEventWheel;
        input.Mouse.MouseData = delta;
        return SendInput(1, &input, sizeof(InputRecord)) == 1;
    }
}
