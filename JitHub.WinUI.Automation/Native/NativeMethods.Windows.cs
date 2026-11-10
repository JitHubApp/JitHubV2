using System.Drawing;
using System.Runtime.InteropServices;

namespace JitHub.WinUI.Automation.Native;

internal static partial class NativeMethods
{
    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(
        IntPtr windowHandle,
        int attribute,
        out NativeRect attributeValue,
        int attributeSize);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool MoveWindow(IntPtr hWnd, int x, int y, int width, int height, bool repaint);

    internal static bool IsOwnedTopLevelWindow(IntPtr candidateWindow, IntPtr ownerWindow)
    {
        if (candidateWindow == IntPtr.Zero || ownerWindow == IntPtr.Zero)
        {
            return false;
        }

        IntPtr candidateRoot = GetAncestor(candidateWindow, GaRoot);
        IntPtr ownerRoot = GetAncestor(ownerWindow, GaRoot);
        return candidateRoot == candidateWindow &&
            ownerRoot != IntPtr.Zero &&
            GetWindow(candidateWindow, GwOwner) == ownerRoot;
    }

    internal static bool TryFindLargestOwnedTopLevelWindow(
        IntPtr ownerWindow,
        out IntPtr ownedWindow)
    {
        ownedWindow = IntPtr.Zero;
        if (ownerWindow == IntPtr.Zero)
        {
            return false;
        }

        IntPtr selectedWindow = IntPtr.Zero;
        long selectedArea = -1;
        bool enumerationCompleted = EnumWindows(
            (candidate, parameter) =>
            {
                _ = parameter;
                if (!IsWindowVisible(candidate) ||
                    !IsOwnedTopLevelWindow(candidate, ownerWindow))
                {
                    return true;
                }

                long area = 0;
                if (GetWindowRect(candidate, out NativeRect bounds))
                {
                    long width = Math.Max(0L, (long)bounds.Right - bounds.Left);
                    long height = Math.Max(0L, (long)bounds.Bottom - bounds.Top);
                    area = width * height;
                }

                if (selectedWindow == IntPtr.Zero || area > selectedArea)
                {
                    selectedWindow = candidate;
                    selectedArea = area;
                }

                return true;
            },
            IntPtr.Zero);

        ownedWindow = selectedWindow;
        return enumerationCompleted && selectedWindow != IntPtr.Zero;
    }

    internal static bool IsForegroundWindow(IntPtr windowHandle)
    {
        IntPtr foregroundRoot = GetAncestor(GetForegroundWindow(), GaRoot);
        IntPtr expectedRoot = GetAncestor(windowHandle, GaRoot);
        return foregroundRoot != IntPtr.Zero && foregroundRoot == expectedRoot;
    }

    internal static bool HasInvisibleForeignForegroundWindow(IntPtr windowHandle)
    {
        IntPtr foreground = GetForegroundWindow();
        if (foreground == IntPtr.Zero || IsWindowVisible(foreground))
        {
            return false;
        }

        _ = GetWindowThreadProcessId(foreground, out uint foregroundProcessId);
        _ = GetWindowThreadProcessId(windowHandle, out uint targetProcessId);
        return foregroundProcessId != 0 && foregroundProcessId != targetProcessId;
    }

    internal static void ResizeWindow(IntPtr windowHandle, int width, int height)
    {
        if (windowHandle == IntPtr.Zero)
        {
            throw new InvalidOperationException("JitHub did not expose a native window handle for resizing.");
        }

        // A prior interrupted automation run can leave Windows restoring the
        // next top-level window as maximized. MoveWindow reports success in
        // that state but the visible bounds remain maximized, so normalize the
        // native state before applying deterministic responsive dimensions.
        _ = ShowWindow(windowHandle, SwRestore);
        if (!MoveWindow(windowHandle, 0, 0, width, height, repaint: true))
        {
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "Could not resize the JitHub window.");
        }
    }

    internal static Rectangle GetWindowBounds(IntPtr windowHandle)
    {
        if (windowHandle == IntPtr.Zero || !GetWindowRect(windowHandle, out NativeRect rect))
        {
            return Rectangle.Empty;
        }

        return Rectangle.FromLTRB(rect.Left, rect.Top, rect.Right, rect.Bottom);
    }

    internal static Rectangle GetPhysicalWindowBounds(IntPtr windowHandle)
    {
        const int DwmwaExtendedFrameBounds = 9;
        if (windowHandle != IntPtr.Zero &&
            DwmGetWindowAttribute(
                windowHandle,
                DwmwaExtendedFrameBounds,
                out NativeRect rect,
                Marshal.SizeOf<NativeRect>()) == 0)
        {
            return Rectangle.FromLTRB(rect.Left, rect.Top, rect.Right, rect.Bottom);
        }

        Rectangle fallback = GetWindowBounds(windowHandle);
        if (fallback.IsEmpty)
        {
            throw new InvalidOperationException("Could not read the native JitHub window bounds for screenshot capture.");
        }

        return fallback;
    }
}
