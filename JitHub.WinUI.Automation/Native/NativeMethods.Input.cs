using System.Drawing;
using System.Runtime.InteropServices;
using FlaUI.Core.WindowsAPI;

namespace JitHub.WinUI.Automation.Native;

internal static partial class NativeMethods
{
    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        internal int X;
        internal int Y;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetCursorPos(int x, int y);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out NativePoint point);

    internal static void MoveCursorPhysical(Point point)
    {
        _ = SetCursorPos(point.X, point.Y);
    }

    internal static Point GetCursorPositionPhysical() =>
        GetCursorPos(out NativePoint point)
            ? new Point(point.X, point.Y)
            : Point.Empty;

    [DllImport("user32.dll")]
    private static extern IntPtr SetFocus(IntPtr windowHandle);

    [DllImport("user32.dll")]
    private static extern void keybd_event(byte virtualKey, byte scanCode, uint flags, nuint extraInfo);

    internal static void SendKey(IntPtr windowHandle, VirtualKeyShort key)
    {
        if (windowHandle == IntPtr.Zero)
        {
            throw new InvalidOperationException("JitHub did not expose a native window handle for directed keyboard input.");
        }

        IntPtr foregroundWindow = GetForegroundWindow();
        uint currentThread = GetCurrentThreadId();
        uint targetThread = GetWindowThreadProcessId(windowHandle, out _);
        uint foregroundThread = foregroundWindow == IntPtr.Zero
            ? 0
            : GetWindowThreadProcessId(foregroundWindow, out _);
        bool attachedTarget = targetThread != 0 && targetThread != currentThread &&
            AttachThreadInput(currentThread, targetThread, attach: true);
        bool attachedForeground = foregroundThread != 0 &&
            foregroundThread != currentThread &&
            foregroundThread != targetThread &&
            AttachThreadInput(currentThread, foregroundThread, attach: true);
        try
        {
            _ = ShowWindow(windowHandle, SwRestore);
            _ = BringWindowToTop(windowHandle);
            _ = SetForegroundWindow(windowHandle);
            _ = SetFocus(windowHandle);
            byte virtualKey = unchecked((byte)(ushort)key);
            keybd_event(virtualKey, 0, 0, 0);
            keybd_event(virtualKey, 0, KeyEventKeyUp, 0);
        }
        finally
        {
            if (attachedForeground)
            {
                _ = AttachThreadInput(currentThread, foregroundThread, attach: false);
            }

            if (attachedTarget)
            {
                _ = AttachThreadInput(currentThread, targetThread, attach: false);
            }
        }
    }

    internal static void ActivateForKeyboard(IntPtr windowHandle)
    {
        if (windowHandle == IntPtr.Zero)
        {
            throw new InvalidOperationException("JitHub did not expose a native window handle for keyboard activation.");
        }

        IntPtr foregroundWindow = GetForegroundWindow();
        uint currentThread = GetCurrentThreadId();
        uint targetThread = GetWindowThreadProcessId(windowHandle, out _);
        uint foregroundThread = foregroundWindow == IntPtr.Zero
            ? 0
            : GetWindowThreadProcessId(foregroundWindow, out _);
        bool attachedTarget = targetThread != 0 && targetThread != currentThread &&
            AttachThreadInput(currentThread, targetThread, attach: true);
        bool attachedForeground = foregroundThread != 0 &&
            foregroundThread != currentThread &&
            foregroundThread != targetThread &&
            AttachThreadInput(currentThread, foregroundThread, attach: true);
        try
        {
            _ = ShowWindow(windowHandle, SwRestore);
            _ = BringWindowToTop(windowHandle);
            _ = SetForegroundWindow(windowHandle);
        }
        finally
        {
            if (attachedForeground)
            {
                _ = AttachThreadInput(currentThread, foregroundThread, attach: false);
            }

            if (attachedTarget)
            {
                _ = AttachThreadInput(currentThread, targetThread, attach: false);
            }
        }
    }
}
