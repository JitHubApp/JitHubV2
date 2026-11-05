using System.Runtime.InteropServices;
using static MarkdownRenderer.PerformanceHarness.DesktopVisibilityNativeMethods;

namespace MarkdownRenderer.PerformanceHarness;

/// <summary>
/// Reads one benchmark window's desktop state: foreground ownership,
/// visibility, minimized and cloaked state, work-area containment and
/// occlusion by windows above it in z-order. It keeps only geometry and
/// flags; it never reads titles, content or pixels.
/// </summary>
internal sealed class TargetWindowVisibilityProbe
{
    private readonly IntPtr _targetWindow;
    private readonly uint _targetProcessId;

    public TargetWindowVisibilityProbe(IntPtr targetWindow)
    {
        _targetWindow = targetWindow;
        _ = GetWindowThreadProcessId(targetWindow, out _targetProcessId);
    }

    public MeasurementVisibilitySample Capture(string source, long elapsedTicks)
    {
        bool captureSucceeded = false;
        bool targetWasForeground = false;
        bool targetWasVisible = false;
        bool targetWasMinimized = false;
        bool targetWasCloaked = false;
        bool targetWasWithinWorkArea = false;
        bool targetWasUnoccluded = false;
        try
        {
            IntPtr foregroundWindow = GetForegroundWindow();
            targetWasForeground = IsTargetForeground(foregroundWindow);
            targetWasVisible = IsWindowVisible(_targetWindow);
            targetWasMinimized = IsIconic(_targetWindow);
            int cloakResult = DwmGetWindowAttribute(
                _targetWindow,
                DwmwaCloaked,
                out uint cloaked,
                sizeof(uint));
            if (cloakResult < 0)
                throw new InvalidOperationException("DWM could not report whether the benchmark window is cloaked.");
            targetWasCloaked = cloaked != 0;

            if (!TryGetVisibleFrameBounds(_targetWindow, out NativeRect targetRect))
                throw new InvalidOperationException("DWM could not report the benchmark window's visible frame bounds.");
            IntPtr monitor = MonitorFromWindow(_targetWindow, MonitorDefaultToNearest);
            var monitorInfo = new NativeMonitorInfo { Size = Marshal.SizeOf<NativeMonitorInfo>() };
            if (monitor == IntPtr.Zero || !GetMonitorInfo(monitor, ref monitorInfo))
                throw new InvalidOperationException("Windows could not report the benchmark monitor work area.");

            NativeRect workArea = monitorInfo.WorkArea;
            targetWasWithinWorkArea = targetRect.Left >= workArea.Left &&
                targetRect.Top >= workArea.Top &&
                targetRect.Right <= workArea.Right &&
                targetRect.Bottom <= workArea.Bottom;
            targetWasUnoccluded = !IsOccludedByWindowAbove(targetRect);
            captureSucceeded = true;
        }
        catch
        {
            // A failed capture is reported through CaptureSucceeded; the
            // monitor counts it and release evidence fails closed.
            captureSucceeded = false;
        }

        return new MeasurementVisibilitySample
        {
            ElapsedTicks = elapsedTicks,
            Source = source,
            CaptureSucceeded = captureSucceeded,
            TargetWasForeground = targetWasForeground,
            TargetWasVisible = targetWasVisible,
            TargetWasMinimized = targetWasMinimized,
            TargetWasCloaked = targetWasCloaked,
            TargetWasWithinWorkArea = targetWasWithinWorkArea,
            TargetWasUnoccluded = targetWasUnoccluded,
        };
    }

    public bool IsTargetForeground(IntPtr foregroundWindow)
    {
        if (foregroundWindow == IntPtr.Zero)
            return false;

        _ = GetWindowThreadProcessId(foregroundWindow, out uint foregroundProcessId);
        if (foregroundProcessId != _targetProcessId)
            return false;

        IntPtr targetRootOwner = GetAncestor(_targetWindow, GetAncestorRootOwner);
        IntPtr foregroundRootOwner = GetAncestor(foregroundWindow, GetAncestorRootOwner);
        return targetRootOwner != IntPtr.Zero && foregroundRootOwner == targetRootOwner;
    }

    public bool MayOccludeTarget(IntPtr candidate)
    {
        if (candidate == IntPtr.Zero || candidate == _targetWindow ||
            !IsWindowVisible(candidate) || IsIconic(candidate))
        {
            return false;
        }

        if (!TryGetVisibleFrameBounds(_targetWindow, out NativeRect targetRect) ||
            !TryGetVisibleFrameBounds(candidate, out NativeRect candidateRect))
        {
            return true;
        }
        if (!Intersects(targetRect, candidateRect))
            return false;

        return IsAboveTargetInZOrder(candidate);
    }

    private bool IsOccludedByWindowAbove(NativeRect targetRect)
    {
        bool foundTarget = false;
        bool occluded = false;
        bool inspectionFailed = false;
        _ = EnumWindows((window, _) =>
        {
            if (window == _targetWindow)
            {
                foundTarget = true;
                return false;
            }

            if (!IsWindowVisible(window) || IsIconic(window))
                return true;

            if (!TryGetVisibleFrameBounds(window, out NativeRect windowRect))
            {
                inspectionFailed = true;
                return false;
            }

            if (Intersects(targetRect, windowRect) && !IsCloaked(window))
            {
                occluded = true;
                return false;
            }

            return true;
        }, IntPtr.Zero);

        // If the target is absent from the desktop z-order, we cannot prove
        // that the application owns a visible interactive surface.
        return !foundTarget || occluded || inspectionFailed;
    }

    private bool IsAboveTargetInZOrder(IntPtr candidate)
    {
        bool candidateBeforeTarget = false;
        _ = EnumWindows((window, _) =>
        {
            if (window == _targetWindow)
                return false;
            if (window == candidate)
            {
                candidateBeforeTarget = true;
                return false;
            }
            return true;
        }, IntPtr.Zero);
        return candidateBeforeTarget;
    }

    private static bool IsCloaked(IntPtr window)
        => DwmGetWindowAttribute(window, DwmwaCloaked, out uint cloaked, sizeof(uint)) >= 0 &&
           cloaked != 0;

    private static bool TryGetVisibleFrameBounds(IntPtr window, out NativeRect rect)
    {
        // GetWindowRect includes invisible resize borders. DWM's extended
        // frame bounds describe the rendered edge in physical desktop pixels,
        // which keeps the work-area and occlusion tests correct across DPI.
        return DwmGetWindowAttribute(
            window,
            DwmwaExtendedFrameBounds,
            out rect,
            Marshal.SizeOf<NativeRect>()) >= 0;
    }

    private static bool Intersects(NativeRect first, NativeRect second)
        => first.Left < second.Right && first.Right > second.Left &&
           first.Top < second.Bottom && first.Bottom > second.Top;
}
