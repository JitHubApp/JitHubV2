using System.Diagnostics;
using System.Drawing;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using JitHub.Performance.Budgets;
using static JitHub.WinUI.PerformanceGate.Elements.RouteElements;
using static JitHub.WinUI.PerformanceGate.Native.NativePointerInput;
using static JitHub.WinUI.PerformanceGate.Native.NativeWindowMethods;
using static JitHub.WinUI.PerformanceGate.Traversal.TraversalTargets;

namespace JitHub.WinUI.PerformanceGate.Traversal;

/// <summary>
/// Prepares a native pointer activation of a traversal target: foreground activation, click point and visible bounds.
/// </summary>
internal static class TraversalPointerActivation
{
    internal static Func<long> PrepareTraversalActivation(
        ProductPerformanceRouteDefinition route,
        AutomationElement element,
        string expectedIdentity,
        AutomationElement selectionHost,
        IntPtr appWindowHandle)
    {
        ActivateWindowForPointerInput(appWindowHandle);

        AutomationElement? label = null;
        if (element.ControlType == ControlType.TreeItem ||
            route.Id is "repo_issues" or "repo_pull_requests")
        {
            label = element
                .FindAllDescendants()
                .Where(static descendant => descendant.ControlType == ControlType.Text)
                .FirstOrDefault(IsVisible);
            if (label is null && element.ControlType == ControlType.TreeItem)
            {
                throw new InvalidOperationException(
                    "The cached tree traversal target did not expose a visible label.");
            }
        }

        Point initialTarget = ResolveTraversalClickPoint(
            element,
            label,
            selectionHost,
            appWindowHandle);
        SendNativePointerMove(initialTarget);
        Thread.Sleep(75);
        return () =>
        {
            ActivateWindowForPointerInput(appWindowHandle);
            AutomationElement currentElement = FindTraversalElementByIdentity(
                selectionHost,
                expectedIdentity) ?? element;
            AutomationElement? currentLabel =
                currentElement.ControlType == ControlType.TreeItem ||
                route.Id is "repo_issues" or "repo_pull_requests"
                ? currentElement
                    .FindAllDescendants()
                    .Where(static descendant => descendant.ControlType == ControlType.Text)
                    .FirstOrDefault(IsVisible)
                : null;
            Point currentTarget = ResolveTraversalClickPoint(
                currentElement,
                currentLabel,
                selectionHost,
                appWindowHandle);
            SendNativePointerMove(currentTarget);
            // Re-resolving a virtualized row can move the pointer to a newly realized
            // container. Let WinUI finish that hover transition before timing button-down;
            // cached-selection latency begins with the user's click, not cursor travel.
            Thread.Sleep(75);
            long activationStartedTimestamp = Stopwatch.GetTimestamp();
            SendNativeClick();
            return activationStartedTimestamp;
        };
    }

    private static Point ResolveTraversalClickPoint(
        AutomationElement element,
        AutomationElement? label,
        AutomationElement selectionHost,
        IntPtr appWindowHandle)
    {
        Rectangle bounds = GetVisibleActivationBounds(
            label ?? element,
            selectionHost,
            GetWindowBounds(appWindowHandle));
        if (label is not null)
        {
            return new Point(
                bounds.Left + bounds.Width / 2,
                bounds.Top + bounds.Height / 2);
        }

        // List rows can contain legitimate nested actions such as author links.
        // Activate a neutral point near the leading edge and vertically center it
        // inside the portion actually clipped by the list viewport. A row can be
        // reported on-screen while its top edge is hidden beneath the list header.
        int horizontalInset = Math.Min(12, Math.Max(2, bounds.Width / 4));
        return element.ControlType is ControlType.ListItem or ControlType.DataItem
            ? new Point(bounds.Left + horizontalInset, bounds.Top + bounds.Height / 2)
            : new Point(bounds.Left + bounds.Width / 2, bounds.Top + bounds.Height / 2);
    }

    internal static void ActivateWindowForPointerInput(IntPtr windowHandle)
    {
        if (windowHandle == IntPtr.Zero)
        {
            throw new InvalidOperationException(
                "JitHub did not expose a native window handle for cached traversal input.");
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
            _ = ShowWindow(windowHandle, 5);
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

        Stopwatch timeout = Stopwatch.StartNew();
        while (timeout.Elapsed < TimeSpan.FromSeconds(2))
        {
            if (GetAncestor(GetForegroundWindow(), 2) == GetAncestor(windowHandle, 2))
            {
                return;
            }

            Thread.Sleep(10);
        }

        throw new InvalidOperationException(
            "JitHub could not acquire foreground input before cached traversal.");
    }

    private static Rectangle GetVisibleActivationBounds(
        AutomationElement element,
        AutomationElement selectionHost,
        Rectangle appWindowBounds)
    {
        Rectangle elementBounds = element.BoundingRectangle;
        Rectangle hostBounds = selectionHost.BoundingRectangle;
        Rectangle visibleBounds = Rectangle.Intersect(
            Rectangle.Intersect(elementBounds, hostBounds),
            appWindowBounds);
        if (visibleBounds.Width <= 1 || visibleBounds.Height <= 1)
        {
            throw new InvalidOperationException(
                "The cached traversal target is outside the JitHub selection viewport.");
        }

        return visibleBounds;
    }

    internal static Rectangle GetWindowBounds(IntPtr windowHandle)
    {
        if (!GetWindowRect(windowHandle, out NativeRect bounds))
        {
            throw new InvalidOperationException(
                "JitHub did not expose valid native bounds for cached traversal input.");
        }

        return Rectangle.FromLTRB(bounds.Left, bounds.Top, bounds.Right, bounds.Bottom);
    }
}
