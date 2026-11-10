using FlaUI.Core.AutomationElements;
using FlaUI.Core.Input;
using FlaUI.Core.WindowsAPI;
using FlaUI.UIA3;
using JitHub.WinUI.Automation.Options;
using static JitHub.WinUI.Automation.Applications.ApplicationProcessCleanup;
using static JitHub.WinUI.Automation.Applications.ProbeApplicationLauncher;
using static JitHub.WinUI.Automation.Conditions.ProbeConditions;
using static JitHub.WinUI.Automation.Elements.ElementQueries;
using static JitHub.WinUI.Automation.Elements.FocusQueries;
using static JitHub.WinUI.Automation.Interaction.ElementActions;
using static JitHub.WinUI.Automation.Interaction.KeyboardInput;
using static JitHub.WinUI.Automation.Screenshots.WindowCapture;
using static JitHub.WinUI.Automation.Windows.ReadyWindows;
using static JitHub.WinUI.Automation.Windows.WindowSizing;

namespace JitHub.WinUI.Automation.Probes.Home;

/// <summary>
/// Drives the home-widget-board probe.
/// </summary>
internal static class HomeWidgetBoardProbe
{
    // This method stays whole because it is one focus-tracking script on the compact Home overview drawer: open it,
    // check close-button focus and Tab/Shift+Tab wrapping, close with Escape, then light dismiss, then rapid reopen
    // cycles, with each step asserting focus returns to the opener the previous step left.
    internal static void RunHomeWidgetBoardProbe(CaptureOptions options)
    {
        bool isAttached = !string.IsNullOrWhiteSpace(options.AttachProcess);
        using var app = isAttached
            ? CreateProbeApplication(options)
            : LaunchApplication(options.AppPath, "--page=shell", "--theme=dark");
        using var automation = new UIA3Automation();
        try
        {
            Window window = GetReadyWindow(app, automation, "home-widget-board probe");
            ResizeWindow(window, 1366, 900);
            WaitForElement("DashboardCustomizeButton", () => window.FindFirstDescendant(cf => cf.ByAutomationId("DashboardCustomizeButton")), TimeSpan.FromSeconds(8));
            AssertProbe(IsVisible(window.FindFirstDescendant(cf => cf.ByAutomationId("DashboardSideRailScrollViewer"))), "Home side rail content was not visible in wide layout.");
            AssertProbe(!IsVisible(window.FindFirstDescendant(cf => cf.ByAutomationId("DashboardWidgetViewAll_overview"))), "Home exposed a semantically false Overview View all action.");
            AssertProbe(window.FindFirstDescendant(cf => cf.ByAutomationId("WorkspaceTabs")) is null, "Home or shell exposed a TabView.");
            CaptureWindow(window, Path.Combine(options.OutputDirectory, "home-widget-board-wide.png"));

            ResizeWindow(window, 760, 650);
            Thread.Sleep(350);
            AutomationElement drawerButton = WaitForElement("DashboardOverviewDrawerButton", () => window.FindFirstDescendant(cf => cf.ByAutomationId("DashboardOverviewDrawerButton")), TimeSpan.FromSeconds(8));
            AssertProbe(IsVisible(drawerButton), "Compact Home did not expose the Overview drawer button.");
            AssertProbe(!IsVisible(window.FindFirstDescendant(cf => cf.ByAutomationId("DashboardSideRailScrollViewer"))), "Home side rail content stayed visible at compact width.");
            if (drawerButton.Patterns.Invoke.IsSupported)
            {
                drawerButton.Patterns.Invoke.Pattern.Invoke();
            }
            else
            {
                drawerButton.Click();
            }
            AutomationElement closeButton = WaitForElement("DashboardSideDrawerCloseButton", () =>
            {
                AutomationElement? close = window.FindFirstDescendant(cf => cf.ByAutomationId("DashboardSideDrawerCloseButton"));
                return IsVisible(close) ? close : null;
            }, TimeSpan.FromSeconds(8));
            FocusForKeyboardActivation(window, closeButton);

            WaitUntil(
                "Home overview drawer focuses its close button",
                () => string.Equals(
                    GetFocusedAutomationId(automation),
                    "DashboardSideDrawerCloseButton",
                    StringComparison.Ordinal),
                TimeSpan.FromSeconds(5));
            CaptureWindow(window, Path.Combine(options.OutputDirectory, "home-widget-board-drawer-close-focused.png"));

            AutomationElement drawerPanel = WaitForElement(
                "DashboardSideDrawerPanel",
                () => window.FindFirstDescendant(cf => cf.ByAutomationId("DashboardSideDrawerPanel")),
                TimeSpan.FromSeconds(5));
            System.Drawing.Rectangle drawerBounds = drawerPanel.BoundingRectangle;
            FocusForKeyboardActivation(window, closeButton);

            using (Keyboard.Pressing(VirtualKeyShort.SHIFT))
            {
                Keyboard.Press(VirtualKeyShort.TAB);
            }
            Thread.Sleep(250);
            Console.WriteLine($"Home drawer raw focus after Shift+Tab: {GetFocusedAutomationDescription(automation)}; " +
                $"inside={IsFocusedElementWithin(automation, drawerPanel)}");
            WaitUntil(
                "Shift+Tab wraps to the last Home overview drawer control",
                () => IsFocusedElementWithin(automation, drawerPanel),
                TimeSpan.FromSeconds(5));
            Console.WriteLine($"Home drawer focus after Shift+Tab: {GetFocusedAutomationDescription(automation)}");

            Keyboard.Press(VirtualKeyShort.TAB);
            Thread.Sleep(250);
            Console.WriteLine($"Home drawer focus after wrap Tab: {GetFocusedAutomationDescription(automation)}");
            WaitUntil(
                "Tab wraps to the Home overview drawer close button",
                () => string.Equals(
                    GetFocusedAutomationId(automation),
                    "DashboardSideDrawerCloseButton",
                    StringComparison.Ordinal),
                TimeSpan.FromSeconds(5));

            Keyboard.Press(VirtualKeyShort.ESCAPE);
            WaitUntil(
                "Escape closes the Home overview drawer",
                () => !IsVisible(window.FindFirstDescendant(cf => cf.ByAutomationId("DashboardSideDrawerCloseButton"))),
                TimeSpan.FromSeconds(5));
            WaitUntil(
                "Escape restores exact Home overview opener focus",
                () => string.Equals(
                    GetFocusedAutomationId(automation),
                    "DashboardOverviewDrawerButton",
                    StringComparison.Ordinal),
                TimeSpan.FromSeconds(5));
            CaptureWindow(window, Path.Combine(options.OutputDirectory, "home-widget-board-drawer-escape-focus-restored.png"));

            drawerButton = WaitForElement(
                "DashboardOverviewDrawerButton after Escape",
                () => window.FindFirstDescendant(cf => cf.ByAutomationId("DashboardOverviewDrawerButton")),
                TimeSpan.FromSeconds(5));
            InvokeOrClick(drawerButton);
            closeButton = WaitForElement("DashboardSideDrawerCloseButton before light dismiss", () =>
            {
                AutomationElement? close = window.FindFirstDescendant(cf => cf.ByAutomationId("DashboardSideDrawerCloseButton"));
                return IsVisible(close) ? close : null;
            }, TimeSpan.FromSeconds(8));
            WaitUntil(
                "Home overview drawer refocuses close before light dismiss",
                () => string.Equals(
                    GetFocusedAutomationId(automation),
                    "DashboardSideDrawerCloseButton",
                    StringComparison.Ordinal),
                TimeSpan.FromSeconds(5));
            drawerPanel = WaitForElement(
                "DashboardSideDrawerPanel before light dismiss",
                () => window.FindFirstDescendant(cf => cf.ByAutomationId("DashboardSideDrawerPanel")),
                TimeSpan.FromSeconds(5));
            drawerBounds = drawerPanel.BoundingRectangle;
            System.Drawing.Rectangle windowBounds = window.BoundingRectangle;
            int dismissX = Math.Max(windowBounds.Left + 8, drawerBounds.Left - 24);
            int dismissY = Math.Clamp(
                drawerBounds.Top + Math.Max(24, drawerBounds.Height / 2),
                windowBounds.Top + 8,
                windowBounds.Bottom - 8);
            Mouse.Click(new System.Drawing.Point(dismissX, dismissY));
            WaitUntil(
                "outside click light-dismisses the Home overview drawer",
                () => !IsVisible(window.FindFirstDescendant(cf => cf.ByAutomationId("DashboardSideDrawerCloseButton"))),
                TimeSpan.FromSeconds(5));
            WaitUntil(
                "light dismiss restores exact Home overview opener focus",
                () => string.Equals(
                    GetFocusedAutomationId(automation),
                    "DashboardOverviewDrawerButton",
                    StringComparison.Ordinal),
                TimeSpan.FromSeconds(5));
            CaptureWindow(window, Path.Combine(options.OutputDirectory, "home-widget-board-drawer-light-dismiss-focus-restored.png"));

            drawerButton = WaitForElement(
                "DashboardOverviewDrawerButton before rapid cycles",
                () => window.FindFirstDescendant(cf => cf.ByAutomationId("DashboardOverviewDrawerButton")),
                TimeSpan.FromSeconds(5));
            InvokeOrClick(drawerButton);
            closeButton = WaitForElement("DashboardSideDrawerCloseButton before rapid cycles", () =>
            {
                AutomationElement? close = window.FindFirstDescendant(cf => cf.ByAutomationId("DashboardSideDrawerCloseButton"));
                return IsVisible(close) ? close : null;
            }, TimeSpan.FromSeconds(8));
            for (int cycle = 0; cycle < 5; cycle++)
            {
                InvokeOrClick(closeButton);
                WaitUntil(
                    $"Dashboard side drawer closes on rapid cycle {cycle + 1}",
                    () => !IsVisible(window.FindFirstDescendant(cf => cf.ByAutomationId("DashboardSideDrawerCloseButton"))),
                    TimeSpan.FromSeconds(5));
                InvokeOrClick(drawerButton);
                closeButton = WaitForElement($"DashboardSideDrawerCloseButton rapid reopen {cycle + 1}", () =>
                {
                    AutomationElement? close = window.FindFirstDescendant(cf => cf.ByAutomationId("DashboardSideDrawerCloseButton"));
                    return IsVisible(close) ? close : null;
                }, TimeSpan.FromSeconds(8));
            }
            Thread.Sleep(350);
            AutomationElement visibleOverviewMetric = WaitForElement(
                "visible Overview metric after repeated drawer cycles",
                () => window.FindAllDescendants(cf => cf.ByAutomationId("DashboardOverviewMetricRepositories"))
                    .FirstOrDefault(IsVisible),
                TimeSpan.FromSeconds(5));
            AssertProbe(
                IsVisible(visibleOverviewMetric),
                "Repeated Home drawer cycles reopened below the Overview metrics.");
            CaptureWindow(window, Path.Combine(options.OutputDirectory, "home-widget-board-compact-drawer.png"));
        }
        finally
        {
            if (!isAttached)
            {
                TryClose(app);
                KillExistingApplicationInstances(options.AppPath);
            }
        }
    }
}
