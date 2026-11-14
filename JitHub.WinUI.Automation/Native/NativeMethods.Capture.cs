using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text;

namespace JitHub.WinUI.Automation.Native;

internal static partial class NativeMethods
{
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PrintWindow(IntPtr hWnd, IntPtr hdcBlt, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hWnd, StringBuilder className, int maximumCount);

    internal static Bitmap CaptureWindowSurface(IntPtr windowHandle)
    {
        const uint PwRenderFullContent = 0x00000002;
        Rectangle windowBounds = GetWindowBounds(windowHandle);
        Rectangle visibleBounds = GetPhysicalWindowBounds(windowHandle);
        if (windowBounds.IsEmpty || visibleBounds.IsEmpty)
        {
            throw new InvalidOperationException("Could not read JitHub window bounds for window-only capture.");
        }

        using var fullWindow = new Bitmap(
            Math.Max(1, windowBounds.Width),
            Math.Max(1, windowBounds.Height),
            PixelFormat.Format32bppArgb);
        using (Graphics graphics = Graphics.FromImage(fullWindow))
        {
            IntPtr deviceContext = graphics.GetHdc();
            try
            {
                if (!PrintWindow(windowHandle, deviceContext, PwRenderFullContent))
                {
                    throw new System.ComponentModel.Win32Exception(
                        Marshal.GetLastWin32Error(),
                        "Windows could not render the JitHub surface for screenshot capture.");
                }
            }
            finally
            {
                graphics.ReleaseHdc(deviceContext);
            }
        }

        Rectangle crop = Rectangle.Intersect(
            new Rectangle(
                visibleBounds.Left - windowBounds.Left,
                visibleBounds.Top - windowBounds.Top,
                visibleBounds.Width,
                visibleBounds.Height),
            new Rectangle(Point.Empty, fullWindow.Size));
        if (crop.Width <= 0 || crop.Height <= 0)
        {
            throw new InvalidOperationException("The DWM window surface does not intersect the rendered JitHub window.");
        }

        return fullWindow.Clone(crop, PixelFormat.Format32bppArgb);
    }

    // This method stays whole because it is one capture precondition check: it rejects overlapping system dialogs and
    // foreign foreground windows, then walks the z-order above JitHub, hiding known overlays and restoring them if any
    // other window blocks the capture bounds.
    internal static IReadOnlyList<IntPtr> PrepareExclusiveScreenCapture(
        IntPtr mainWindowHandle,
        int expectedProcessId,
        Rectangle captureBounds)
    {
        if (mainWindowHandle == IntPtr.Zero)
        {
            throw new InvalidOperationException("JitHub did not expose a native window handle for popup capture.");
        }

        IntPtr rootWindowHandle = GetAncestor(mainWindowHandle, GaRoot);
        if (rootWindowHandle != IntPtr.Zero)
        {
            mainWindowHandle = rootWindowHandle;
        }

        IntPtr blockingSystemDialog = FindBlockingSystemDialog(captureBounds);
        if (blockingSystemDialog != IntPtr.Zero)
        {
            _ = GetWindowThreadProcessId(blockingSystemDialog, out uint blockingProcessId);
            throw new InvalidOperationException(
                $"Refusing screenshot capture because a Windows system dialog from process {blockingProcessId} overlaps JitHub.");
        }

        IntPtr foreground = GetForegroundWindow();
        _ = GetWindowThreadProcessId(foreground, out uint foregroundProcessId);
        if (foreground == IntPtr.Zero)
        {
            throw new InvalidOperationException(
                "Refusing popup capture because Windows did not expose a foreground window.");
        }

        if (foregroundProcessId != (uint)expectedProcessId && IsWindowVisible(foreground))
        {
            throw new InvalidOperationException(
                $"Refusing popup capture because the foreground window belongs to process {foregroundProcessId}, not JitHub process {expectedProcessId}.");
        }

        List<IntPtr> hiddenOverlays = [];
        for (IntPtr candidate = GetWindow(mainWindowHandle, GwHwndPrev);
             candidate != IntPtr.Zero;
             candidate = GetWindow(candidate, GwHwndPrev))
        {
            if (!IsWindowVisible(candidate) || !GetWindowRect(candidate, out NativeRect rect))
            {
                continue;
            }

            Rectangle candidateBounds = Rectangle.FromLTRB(rect.Left, rect.Top, rect.Right, rect.Bottom);
            if (!captureBounds.IntersectsWith(candidateBounds))
            {
                continue;
            }

            _ = GetWindowThreadProcessId(candidate, out uint candidateProcessId);
            if (candidateProcessId != (uint)expectedProcessId)
            {
                if (IsWindowsShellSurface((int)candidateProcessId, candidate))
                {
                    continue;
                }

                if (IsKnownCaptureOverlay((int)candidateProcessId, candidate))
                {
                    _ = ShowWindow(candidate, SwHide);
                    hiddenOverlays.Add(candidate);
                    continue;
                }

                RestoreCaptureOverlays(hiddenOverlays);
                throw new InvalidOperationException(
                    $"Refusing popup capture because process {candidateProcessId} ({GetWindowClassName(candidate)}) overlaps JitHub above the app window.");
            }
        }

        if (hiddenOverlays.Count > 0)
        {
            Thread.Sleep(60);
        }

        return hiddenOverlays;
    }

    private static IntPtr FindBlockingSystemDialog(Rectangle captureBounds)
    {
        IntPtr blockingWindow = IntPtr.Zero;
        _ = EnumWindows(
            (candidate, parameter) =>
            {
                _ = parameter;
                if (GetWindowClassName(candidate) is not (
                        "Shell_SystemDialog" or
                        "Shell_SystemDialogProxy" or
                        "Shell_SystemDim"))
                {
                    return true;
                }

                if (!GetWindowRect(candidate, out NativeRect rect))
                {
                    blockingWindow = candidate;
                    return false;
                }

                Rectangle bounds = Rectangle.FromLTRB(rect.Left, rect.Top, rect.Right, rect.Bottom);
                if (!bounds.IsEmpty && !captureBounds.IntersectsWith(bounds))
                {
                    return true;
                }

                blockingWindow = candidate;
                return false;
            },
            IntPtr.Zero);
        return blockingWindow;
    }

    internal static void RestoreCaptureOverlays(IReadOnlyList<IntPtr> windowHandles)
    {
        foreach (IntPtr windowHandle in windowHandles)
        {
            _ = ShowWindow(windowHandle, SwShowNoActivate);
        }
    }

    private static bool IsKnownCaptureOverlay(int processId, IntPtr windowHandle)
    {
        try
        {
            using Process process = Process.GetProcessById(processId);
            if (string.Equals(process.ProcessName, "codex-computer-use", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(process.MainWindowTitle, "Codex Computer Use Cursor Overlay", StringComparison.Ordinal))
            {
                return true;
            }

            return string.Equals(process.ProcessName, "DDPM.Subagent.User", StringComparison.OrdinalIgnoreCase) &&
                GetWindowClassName(windowHandle).StartsWith(
                    "HwndWrapper[DDPM.Subagent.User;",
                    StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }

    private static bool IsWindowsShellSurface(int processId, IntPtr windowHandle)
    {
        try
        {
            using Process process = Process.GetProcessById(processId);
            if (!string.Equals(process.ProcessName, "explorer", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            return GetWindowClassName(windowHandle) is
                "Shell_TrayWnd" or "Shell_SecondaryTrayWnd" or "Progman" or "WorkerW" or
                "ThumbnailDeviceHelperWnd" or "ProxyModalWindow";
        }
        catch
        {
            return false;
        }
    }

    private static string GetWindowClassName(IntPtr windowHandle)
    {
        var className = new StringBuilder(128);
        _ = GetClassName(windowHandle, className, className.Capacity);
        return className.ToString();
    }
}
