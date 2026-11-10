using System.Drawing;
using System.Runtime.InteropServices;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Input;
using FlaUI.Core.WindowsAPI;
using JitHub.WinUI.Automation.Native;
using static JitHub.WinUI.Automation.Conditions.ProbeConditions;
using static JitHub.WinUI.Automation.Elements.ElementGeometry;
using static JitHub.WinUI.Automation.Elements.ElementQueries;
using static JitHub.WinUI.Automation.Interaction.KeyboardInput;
using static JitHub.WinUI.Automation.Workspaces.AdaptivePaneButtons;

namespace JitHub.WinUI.Automation.Workspaces;

/// <summary>
/// Opens, dismisses and checks the adaptive leading and trailing drawers of the responsive workspaces.
/// </summary>
internal static class AdaptiveWorkspaceDrawers
{
    // This method stays whole because the leading and trailing drawer passes follow the same sequence (record opener
    // bounds, open, check bounds and focus containment, close by aligned button or Escape, confirm focus returned to
    // the opener, then Escape and light-dismiss) and keeping them side by side shows that the two passes match.
    internal static void ExerciseAdaptiveWorkspaceDrawers(
        Window window,
        string prefix,
        string listId,
        string inspectorId,
        bool exerciseLeading = true,
        bool exerciseTrailing = true,
        bool requireAlignedCloseControls = true)
    {
        if (exerciseLeading)
        {
            AutomationElement leadingButton = WaitForElement(
                $"{prefix}LeadingPaneButton",
                () => FindAdaptivePaneButton(window, prefix, leading: true),
                TimeSpan.FromSeconds(8));
            AssertProbe(IsVisible(leadingButton), $"{prefix}: leading pane button was not visible.");
            var leadingButtonBounds = leadingButton.BoundingRectangle;
            FocusForKeyboardActivation(window, leadingButton);
            leadingButton.Click();
            WaitForElement($"{prefix}LeftDrawer", () =>
            {
                AutomationElement? drawer = window.FindFirstDescendant(cf => cf.ByAutomationId($"{prefix}LeftDrawer"));
                return IsVisible(drawer) ? drawer : null;
            }, TimeSpan.FromSeconds(8));
            WaitForElement(listId, () =>
            {
                AutomationElement? list = window.FindFirstDescendant(cf => cf.ByAutomationId(listId));
                return IsVisible(list) ? list : null;
            }, TimeSpan.FromSeconds(8));
            Thread.Sleep(320);
            AssertDrawerInsideWorkspace(window, prefix, leading: true);
            AssertDrawerKeyboardFocusContained(window, $"{prefix}LeftDrawer", $"{prefix} leading drawer");
            AutomationElement? leadingCloseButton = FindAdaptivePaneCloseButton(window, prefix, leading: true);
            if (requireAlignedCloseControls)
            {
                leadingCloseButton = WaitForElement(
                    $"{prefix} leading pane close button",
                    () => FindAdaptivePaneCloseButton(window, prefix, leading: true),
                    TimeSpan.FromSeconds(5));
                AssertProbe(IsVisible(leadingCloseButton), $"{prefix}: leading drawer did not expose its in-panel close control.");
                AssertElementBoundsAligned(leadingButtonBounds, leadingCloseButton, $"{prefix} leading pane toggle");
                leadingCloseButton.Click();
            }
            else
            {
                Keyboard.Press(VirtualKeyShort.ESCAPE);
            }

            WaitUntil(
                $"{prefix} leading drawer closes",
                () => !IsVisible(window.FindFirstDescendant(cf => cf.ByAutomationId($"{prefix}LeftDrawer"))),
                TimeSpan.FromSeconds(5));
            AssertPaneFocusReturned(window, prefix, leading: true, $"{prefix} leading close button");

            ExerciseDrawerDismissal(window, prefix, listId, leading: true, useEscape: true);
            ExerciseDrawerDismissal(window, prefix, listId, leading: true, useEscape: false);
        }

        if (exerciseTrailing)
        {
            AutomationElement trailingButton = WaitForElement(
                $"{prefix}TrailingPaneButton",
                () => FindAdaptivePaneButton(window, prefix, leading: false),
                TimeSpan.FromSeconds(8));
            AssertProbe(IsVisible(trailingButton), $"{prefix}: inspector pane button was not visible.");
            var trailingButtonBounds = trailingButton.BoundingRectangle;
            FocusForKeyboardActivation(window, trailingButton);
            trailingButton.Click();
            WaitForElement($"{prefix}RightDrawer", () =>
            {
                AutomationElement? drawer = window.FindFirstDescendant(cf => cf.ByAutomationId($"{prefix}RightDrawer"));
                return IsVisible(drawer) ? drawer : null;
            }, TimeSpan.FromSeconds(8));
            Thread.Sleep(320);
            AssertDrawerInsideWorkspace(window, prefix, leading: false);
            AssertDrawerKeyboardFocusContained(window, $"{prefix}RightDrawer", $"{prefix} inspector drawer");
            AutomationElement? trailingCloseButton = FindAdaptivePaneCloseButton(window, prefix, leading: false);
            if (requireAlignedCloseControls)
            {
                trailingCloseButton = WaitForElement(
                    $"{prefix} inspector pane close button",
                    () => FindAdaptivePaneCloseButton(window, prefix, leading: false),
                    TimeSpan.FromSeconds(5));
                AssertProbe(IsVisible(trailingCloseButton), $"{prefix}: inspector drawer did not expose its in-panel close control.");
                AssertElementBoundsAligned(trailingButtonBounds, trailingCloseButton, $"{prefix} inspector pane toggle");
                trailingCloseButton.Click();
            }
            else
            {
                Keyboard.Press(VirtualKeyShort.ESCAPE);
            }

            WaitUntil(
                $"{prefix} inspector drawer closes",
                () => !IsVisible(window.FindFirstDescendant(cf => cf.ByAutomationId($"{prefix}RightDrawer"))),
                TimeSpan.FromSeconds(5));
            AssertPaneFocusReturned(window, prefix, leading: false, $"{prefix} inspector close button");

            ExerciseDrawerDismissal(window, prefix, inspectorId, leading: false, useEscape: true);
            ExerciseDrawerDismissal(window, prefix, inspectorId, leading: false, useEscape: false);
        }
    }

    private static void ExerciseDrawerDismissal(
        Window window,
        string prefix,
        string paneContentId,
        bool leading,
        bool useEscape)
    {
        AutomationElement opener = WaitForElement(
            $"{prefix} {(leading ? "leading" : "inspector")} pane opener",
            () => FindAdaptivePaneButton(window, prefix, leading),
            TimeSpan.FromSeconds(8));
        FocusForKeyboardActivation(window, opener);
        opener.Click();
        string drawerId = $"{prefix}{(leading ? "Left" : "Right")}Drawer";
        AutomationElement drawer = WaitForElement(
            drawerId,
            () =>
            {
                AutomationElement? candidate = FindCurrentVisibleByAutomationId(window, drawerId);
                return IsVisible(candidate) ? candidate : null;
            },
            TimeSpan.FromSeconds(8));
        try
        {
            WaitForElement(
                paneContentId,
                () => FindCurrentVisibleByAutomationId(window, paneContentId) ??
                    (!leading ? FindAdaptivePaneCloseButton(window, prefix, leading: false) : null),
                TimeSpan.FromSeconds(8));
        }
        catch
        {
            PrintVisibleAutomationIds(
                window,
                $"{prefix}-{(leading ? "leading" : "trailing")}-drawer-content-missing");
            throw;
        }
        Thread.Sleep(240);
        WaitForDrawerSettled(window, prefix, leading);

        if (useEscape)
        {
            Keyboard.Press(VirtualKeyShort.ESCAPE);
        }
        else
        {
            AutomationElement workspace = WaitForElement(
                $"{prefix}AdaptiveWorkspace",
                () => FindCurrentVisibleByAutomationId(window, $"{prefix}AdaptiveWorkspace"),
                TimeSpan.FromSeconds(5));
            Rectangle bounds = workspace.BoundingRectangle;
            Rectangle drawerBounds = FindCurrentVisibleByAutomationId(window, drawerId)!.BoundingRectangle;
            Rectangle visibleWorkspace = Rectangle.Intersect(bounds, window.BoundingRectangle);
            int outsideStart = leading
                ? Math.Max(drawerBounds.Right, visibleWorkspace.Left)
                : visibleWorkspace.Left;
            int outsideEnd = leading
                ? visibleWorkspace.Right
                : Math.Min(drawerBounds.Left, visibleWorkspace.Right);
            AssertProbe(
                outsideEnd - outsideStart >= 24,
                $"{prefix} did not leave a visible light-dismiss target outside {drawerId}. workspace={visibleWorkspace}; drawer={drawerBounds}.");
            int x = outsideStart + ((outsideEnd - outsideStart) / 2);
            int y = visibleWorkspace.Top + (visibleWorkspace.Height / 2);
            Mouse.Click(new System.Drawing.Point(x, y));
        }

        WaitUntil(
            $"{prefix} drawer closes by {(useEscape ? "Escape" : "light dismiss")}",
            () => !IsVisible(FindCurrentVisibleByAutomationId(window, drawerId)),
            TimeSpan.FromSeconds(5));
        AssertPaneFocusReturned(
            window,
            prefix,
            leading,
            $"{prefix} {(leading ? "leading" : "inspector")} {(useEscape ? "Escape" : "light dismiss")}");
    }

    internal static void WaitForDrawerSettled(Window window, string prefix, bool leading)
    {
        string workspaceId = $"{prefix}AdaptiveWorkspace";
        string drawerId = $"{prefix}{(leading ? "Left" : "Right")}Drawer";
        bool settled = WaitUntilAvailable(
            () =>
            {
                AutomationElement? workspace = FindCurrentVisibleByAutomationId(window, workspaceId);
                AutomationElement? drawer = FindCurrentVisibleByAutomationId(window, drawerId);
                if (workspace is null || drawer is null)
                {
                    return false;
                }

                Rectangle workspaceBounds = workspace.BoundingRectangle;
                Rectangle drawerBounds = drawer.BoundingRectangle;
                return leading
                    ? Math.Abs(drawerBounds.Left - workspaceBounds.Left) <= 2
                    : Math.Abs(drawerBounds.Right - workspaceBounds.Right) <= 2;
            },
            TimeSpan.FromSeconds(5));
        AssertProbe(settled, $"{prefix} {(leading ? "leading" : "inspector")} drawer did not settle at its workspace edge.");
    }

    internal static void AssertPaneFocusReturned(Window window, string prefix, bool leading, string context)
    {
        AutomationElement opener = WaitForElement(
            $"{prefix} pane opener after dismiss",
            () => FindAdaptivePaneButton(window, prefix, leading),
            TimeSpan.FromSeconds(5));
        bool returned = WaitUntilAvailable(
            () =>
            {
                AutomationElement focused = window.Automation.FocusedElement();
                string focusedId = GetAutomationId(focused);
                string openerId = GetAutomationId(opener);
                return !string.IsNullOrWhiteSpace(openerId) && string.Equals(focusedId, openerId, StringComparison.Ordinal);
            },
            TimeSpan.FromSeconds(3));
        AssertProbe(returned, $"{context} did not restore keyboard focus to the pane opener.");
    }

    // This method stays whole because its local closures share the window handle and the last element focused inside
    // the drawer, so foreground-loss recovery can restore focus there while ten forward Tab and six Shift+Tab steps
    // each assert that focus stayed inside the currently realized drawer.
    internal static void AssertDrawerKeyboardFocusContained(Window window, string drawerAutomationId, string context)
    {
        IntPtr windowHandle = new(window.Properties.NativeWindowHandle.ValueOrDefault);
        AutomationElement? lastContainedFocus = null;
        AutomationElement? CurrentDrawer() => FindCurrentVisibleByAutomationId(window, drawerAutomationId);

        bool FocusIsInsideCurrentDrawer()
        {
            try
            {
                AutomationElement? currentDrawer = CurrentDrawer();
                return currentDrawer is not null &&
                    IsInsideElementBounds(window.Automation.FocusedElement(), currentDrawer, tolerance: 2);
            }
            catch (COMException)
            {
                return false;
            }
        }

        void RecoverFromExternalForegroundLoss()
        {
            if (NativeMethods.IsForegroundWindow(windowHandle))
            {
                return;
            }

            NativeMethods.ActivateForKeyboard(windowHandle);
            if (lastContainedFocus is not null)
            {
                try
                {
                    lastContainedFocus.FocusNative();
                }
                catch (COMException)
                {
                    lastContainedFocus = null;
                }
            }

            _ = WaitUntilAvailable(
                () => NativeMethods.IsForegroundWindow(windowHandle) && FocusIsInsideCurrentDrawer(),
                TimeSpan.FromMilliseconds(750));
        }

        string Describe(AutomationElement element)
        {
            try
            {
                return $"{element.ControlType}:'{GetAutomationId(element)}'/{GetElementName(element)}" +
                    $"@{element.BoundingRectangle}; focused={element.Properties.HasKeyboardFocus.ValueOrDefault}; " +
                    $"focusable={element.Properties.IsKeyboardFocusable.ValueOrDefault}";
            }
            catch (COMException)
            {
                return "stale UIA provider";
            }
        }

        _ = WaitForElement(
            drawerAutomationId,
            CurrentDrawer,
            TimeSpan.FromSeconds(5));
        bool receivedFocus = WaitUntilAvailable(FocusIsInsideCurrentDrawer, TimeSpan.FromSeconds(3));
        if (!receivedFocus)
        {
            AutomationElement focused = window.Automation.FocusedElement();
            AutomationElement? currentDrawer = CurrentDrawer();
            string drawerControls = currentDrawer is null
                ? "drawer provider unavailable"
                : string.Join(
                    "; ",
                    currentDrawer.FindAllDescendants()
                        .Where(element =>
                            element.ControlType == ControlType.Button ||
                            element.ControlType == ControlType.Edit ||
                            element.ControlType == ControlType.Tree ||
                            element.ControlType == ControlType.TreeItem)
                        .Take(12)
                        .Select(Describe));
            AssertProbe(
                false,
                $"{context} did not receive keyboard focus; focus remained on {Describe(focused)}; " +
                $"drawer={((currentDrawer is null) ? "unavailable" : Describe(currentDrawer))}; controls=[{drawerControls}].");
        }

        lastContainedFocus = window.Automation.FocusedElement();

        List<string> forwardFocusTrace = [];
        for (int index = 0; index < 10; index++)
        {
            RecoverFromExternalForegroundLoss();
            AutomationElement focused = window.Automation.FocusedElement();
            if (!FocusIsInsideCurrentDrawer())
            {
                _ = WaitUntilAvailable(FocusIsInsideCurrentDrawer, TimeSpan.FromMilliseconds(500));
                focused = window.Automation.FocusedElement();
            }
            forwardFocusTrace.Add($"{index + 1}:{Describe(focused)}");
            AssertProbe(
                FocusIsInsideCurrentDrawer(),
                $"{context}: Tab transition {index + 1} escaped the open drawer to {Describe(focused)}. " +
                $"Focus trace: {string.Join(" -> ", forwardFocusTrace)}");
            lastContainedFocus = focused;
            Keyboard.Press(VirtualKeyShort.TAB);
            Thread.Sleep(70);
        }

        for (int index = 0; index < 6; index++)
        {
            RecoverFromExternalForegroundLoss();
            AutomationElement focused = window.Automation.FocusedElement();
            if (!FocusIsInsideCurrentDrawer())
            {
                _ = WaitUntilAvailable(FocusIsInsideCurrentDrawer, TimeSpan.FromMilliseconds(500));
                focused = window.Automation.FocusedElement();
            }
            AssertProbe(
                FocusIsInsideCurrentDrawer(),
                $"{context}: reverse Tab transition {index + 1} escaped the open drawer to {Describe(focused)}.");
            lastContainedFocus = focused;
            using (Keyboard.Pressing(VirtualKeyShort.LSHIFT))
            {
                Keyboard.Press(VirtualKeyShort.TAB);
            }
            Thread.Sleep(70);
        }
    }

    internal static void AssertDrawerInsideWorkspace(Window window, string prefix, bool leading)
    {
        string workspaceId = $"{prefix}AdaptiveWorkspace";
        string drawerId = leading ? $"{prefix}LeftDrawer" : $"{prefix}RightDrawer";
        AutomationElement? workspace = window.FindFirstDescendant(cf => cf.ByAutomationId(workspaceId));
        AutomationElement? drawer = window.FindFirstDescendant(cf => cf.ByAutomationId(drawerId));
        AssertProbe(IsVisible(workspace), $"{prefix}: adaptive workspace was not visible.");
        AssertProbe(IsVisible(drawer), $"{prefix}: expected {drawerId} to be visible after opening the pane.");

        AssertProbe(
            IsInsideElementBounds(drawer!, workspace!, tolerance: 1.5),
            $"{prefix}: {(leading ? "leading" : "inspector")} drawer rendered outside the workspace bounds.");
    }
}
