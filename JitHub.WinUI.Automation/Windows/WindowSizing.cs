using System.Drawing;
using FlaUI.Core.AutomationElements;
using JitHub.WinUI.Automation.Native;
using static JitHub.WinUI.Automation.Conditions.ProbeConditions;
using static JitHub.WinUI.Automation.Windows.WindowActivation;

namespace JitHub.WinUI.Automation.Windows;

/// <summary>
/// Resizes the app window to physical or logical sizes and labels responsive viewports.
/// </summary>
internal static class WindowSizing
{
    internal static Rectangle ResizeWindow(Window window, int width, int height, bool reactivate = true)
    {
        Rectangle workArea = NativeMethods.GetWorkArea();
        int targetWidth = workArea.Width > 0 ? Math.Min(width, workArea.Width) : width;
        int targetHeight = workArea.Height > 0 ? Math.Min(height, workArea.Height) : height;
        if (targetWidth != width || targetHeight != height)
        {
            Console.WriteLine(
                $"Requested {width}x{height}; using {targetWidth}x{targetHeight} to fit the current Windows work area.");
        }

        IntPtr windowHandle = GetNativeWindowHandle(window);
        Rectangle initialBounds = NativeMethods.GetWindowBounds(windowHandle);
        bool alreadySized =
            Math.Abs(initialBounds.Width - targetWidth) <= 4 &&
            Math.Abs(initialBounds.Height - targetHeight) <= 4;
        if (!alreadySized)
        {
            NativeMethods.ResizeWindow(windowHandle, targetWidth, targetHeight);
        }
        if (reactivate)
        {
            TryActivateWindow(window);
        }
        try
        {
            WaitUntil(
                $"window to resize to {targetWidth}x{targetHeight}",
                () =>
                {
                    Rectangle bounds = NativeMethods.GetWindowBounds(windowHandle);
                    bool reachedTarget =
                        Math.Abs(bounds.Width - targetWidth) <= 4 &&
                        Math.Abs(bounds.Height - targetHeight) <= 4;
                    if (!reachedTarget)
                    {
                        // MainWindow applies its persisted/default launch size after
                        // the readiness signal. Reapply the requested test viewport
                        // until that one-time startup sizing has settled.
                        NativeMethods.ResizeWindow(windowHandle, targetWidth, targetHeight);
                    }

                    return reachedTarget;
                },
                TimeSpan.FromSeconds(5));
        }
        catch (InvalidOperationException exception)
        {
            Rectangle actual = NativeMethods.GetWindowBounds(windowHandle);
            throw new InvalidOperationException(
                $"Timed out resizing the JitHub window to {targetWidth}x{targetHeight}; actual native bounds were {actual}.",
                exception);
        }
        Thread.Sleep(600);
        Rectangle settledBounds = NativeMethods.GetWindowBounds(windowHandle);
        Console.WriteLine(
            $"Viewport requested={width}x{height}; actual={settledBounds.Width}x{settledBounds.Height}.");
        return settledBounds;
    }

    internal static Rectangle ResizeLogicalWindow(Window window, int logicalWidth, int logicalHeight)
    {
        uint dpi = NativeMethods.GetWindowDpi(GetNativeWindowHandle(window));
        double scale = dpi / 96d;
        int physicalWidth = Math.Max(1, (int)Math.Round(logicalWidth * scale));
        int physicalHeight = Math.Max(1, (int)Math.Round(logicalHeight * scale));
        Rectangle physicalBounds = ResizeWindow(window, physicalWidth, physicalHeight);
        int actualLogicalWidth = (int)Math.Round(physicalBounds.Width / scale);
        int actualLogicalHeight = (int)Math.Round(physicalBounds.Height / scale);
        Console.WriteLine(
            $"Logical viewport requested={logicalWidth}x{logicalHeight}; " +
            $"actual={actualLogicalWidth}x{actualLogicalHeight}; dpi={dpi}.");
        return physicalBounds;
    }

    internal static string GetResponsiveViewportLabel(int requestedWidth, int requestedHeight, Rectangle actualBounds)
    {
        string actual = $"{actualBounds.Width}x{actualBounds.Height}";
        bool constrained =
            Math.Abs(actualBounds.Width - requestedWidth) > 4 ||
            Math.Abs(actualBounds.Height - requestedHeight) > 4;
        return constrained
            ? $"{actual}-requested-{requestedWidth}x{requestedHeight}"
            : actual;
    }
}
