using System.Drawing;
using FlaUI.Core.AutomationElements;
using FlaUI.UIA3;
using JitHub.WinUI.Automation.Options;
using JitHub.WinUI.Automation.Windows;
using static JitHub.WinUI.Automation.Applications.ApplicationProcessCleanup;
using static JitHub.WinUI.Automation.Applications.ProbeApplicationLauncher;
using static JitHub.WinUI.Automation.Conditions.ProbeConditions;
using static JitHub.WinUI.Automation.Elements.ElementGeometry;
using static JitHub.WinUI.Automation.Elements.ElementQueries;
using static JitHub.WinUI.Automation.Interaction.ElementActions;
using static JitHub.WinUI.Automation.Screenshots.WindowCapture;
using static JitHub.WinUI.Automation.Windows.ReadyWindows;
using static JitHub.WinUI.Automation.Windows.WindowSizing;

namespace JitHub.WinUI.Automation.Probes.Shell;

/// <summary>
/// Drives the shell-responsive probe.
/// </summary>
internal static class ShellResponsiveProbe
{
    // This method stays whole because it first collapses the wide shell rail by user action and proves that choice
    // survives a resize, then expands it again before the width sweep, whose wide-width assertions depend on that
    // expanded rail and whose compact branch toggles the drawer.
    internal static void RunShellResponsiveProbe(CaptureOptions options)
    {
        using var app = LaunchApplication(options.AppPath, "--page=shell", "--theme=dark");
        using var automation = new UIA3Automation();
        try
        {
            Window window = GetReadyWindow(app, automation, "shell-responsive probe");
            Rectangle initialWideBounds = ResizeWindow(window, 1366, 900);
            if (initialWideBounds.Width >= AutomationResponsiveLayout.ShellRailCollapseWidth)
            {
                WaitForElement(
                    "DashboardCustomizeButton",
                    () => window.FindFirstDescendant(cf => cf.ByAutomationId("DashboardCustomizeButton")),
                    TimeSpan.FromSeconds(8));
                AutomationElement wideRailButton = WaitForElement(
                    "wide ShellRailDrawerButton",
                    () =>
                    {
                        AutomationElement? button = window.FindFirstDescendant(
                            cf => cf.ByAutomationId("ShellRailDrawerButton"));
                        return IsVisible(button) ? button : null;
                    },
                    TimeSpan.FromSeconds(5));
                WaitUntil(
                    "wide shell rail starts inline",
                    () => IsVisible(window.FindFirstDescendant(cf => cf.ByAutomationId("ShellRail"))),
                    TimeSpan.FromSeconds(5));

                InvokeOrClick(wideRailButton);
                WaitUntil(
                    "user collapses wide shell rail",
                    () => !IsVisible(window.FindFirstDescendant(cf => cf.ByAutomationId("ShellRail"))),
                    TimeSpan.FromSeconds(5));
                CaptureWindow(
                    window,
                    Path.Combine(options.OutputDirectory, "shell-responsive-user-collapsed-wide.png"));

                ResizeWindow(window, 900, 700);
                ResizeWindow(window, 1366, 900);
                WaitUntil(
                    "user-collapsed shell rail remains collapsed after resize",
                    () => !IsVisible(window.FindFirstDescendant(cf => cf.ByAutomationId("ShellRail"))),
                    TimeSpan.FromSeconds(5));
                CaptureWindow(
                    window,
                    Path.Combine(options.OutputDirectory, "shell-responsive-user-collapse-persisted.png"));

                wideRailButton = WaitForElement(
                    "wide ShellRailDrawerButton after resize",
                    () =>
                    {
                        AutomationElement? button = window.FindFirstDescendant(
                            cf => cf.ByAutomationId("ShellRailDrawerButton"));
                        return IsVisible(button) ? button : null;
                    },
                    TimeSpan.FromSeconds(5));
                InvokeOrClick(wideRailButton);
                WaitUntil(
                    "user expands wide shell rail",
                    () => IsVisible(window.FindFirstDescendant(cf => cf.ByAutomationId("ShellRail"))),
                    TimeSpan.FromSeconds(5));
            }

            (int Width, int Height)[] sizes =
            [
                (1366, 900),
                (1180, 800),
                (900, 700),
                (760, 650),
                (640, 600)
            ];

            foreach ((int width, int height) in sizes)
            {
                Rectangle resizedBounds = ResizeWindow(window, width, height);
                int actualWidth = resizedBounds.Width;
                string viewportLabel = GetResponsiveViewportLabel(width, height, resizedBounds);
                WaitForElement("DashboardCustomizeButton", () => window.FindFirstDescendant(cf => cf.ByAutomationId("DashboardCustomizeButton")), TimeSpan.FromSeconds(8));
                if (actualWidth >= AutomationResponsiveLayout.ShellRailCollapseWidth)
                {
                    AssertProbe(window.FindFirstDescendant(cf => cf.ByAutomationId("ShellNav_home")) is not null, "Combined shell nav was not present.");
                    AssertProbe(window.FindFirstDescendant(cf => cf.ByAutomationId("ShellRepoFilter_Public")) is not null, "Combined shell repository filter was not present.");
                    AssertProbe(IsVisible(window.FindFirstDescendant(cf => cf.ByAutomationId("ShellRailDrawerButton"))), "Wide shell did not expose the persistent navigation toggle.");
                    AssertProbe(IsVisible(window.FindFirstDescendant(cf => cf.ByAutomationId("ShellRail"))), "Wide shell rail did not return inline after the user expanded it.");
                }
                else
                {
                    AutomationElement railButton = WaitForElement("ShellRailDrawerButton", () => window.FindFirstDescendant(cf => cf.ByAutomationId("ShellRailDrawerButton")), TimeSpan.FromSeconds(5));
                    AssertProbe(IsVisible(railButton), "Compact shell did not expose the navigation drawer button.");
                    AssertProbe(!IsVisible(window.FindFirstDescendant(cf => cf.ByAutomationId("ShellRail"))), "Shell rail stayed visible at compact width.");
                    AutomationElement? existingDrawer = window.FindFirstDescendant(cf => cf.ByAutomationId("ShellRailDrawer"));
                    if (!IsVisible(existingDrawer))
                    {
                        InvokeOrClick(railButton);
                    }

                    WaitForElement("ShellRailDrawer", () =>
                    {
                        AutomationElement? drawer = window.FindFirstDescendant(cf => cf.ByAutomationId("ShellRailDrawer"));
                        return IsVisible(drawer) ? drawer : null;
                    }, TimeSpan.FromSeconds(5));
                    WaitForElement($"Shell navigation reachable at {actualWidth}px", () =>
                    {
                        AutomationElement? item = window.FindFirstDescendant(cf => cf.ByAutomationId("ShellNav_home"));
                        return IsVisible(item) && IsInsideWindowBounds(item!, window) ? item : null;
                    }, TimeSpan.FromSeconds(5));
                    WaitForElement($"Repository filters reachable at {actualWidth}px", () =>
                    {
                        AutomationElement? item = window.FindFirstDescendant(cf => cf.ByAutomationId("ShellRepoFilter_Public"));
                        return IsVisible(item) && IsInsideWindowBounds(item!, window) ? item : null;
                    }, TimeSpan.FromSeconds(5));
                    Thread.Sleep(150);
                    InvokeOrClick(railButton);
                    Thread.Sleep(80);
                    InvokeOrClick(railButton);
                    WaitForElement("ShellRailDrawer reopens", () =>
                    {
                        AutomationElement? drawer = window.FindFirstDescendant(cf => cf.ByAutomationId("ShellRailDrawer"));
                        return IsVisible(drawer) ? drawer : null;
                    }, TimeSpan.FromSeconds(5));
                    Thread.Sleep(350);
                }

                AssertProbe(window.FindFirstDescendant(cf => cf.ByAutomationId("ShellSearchTextBox")) is not null, "Command search was not present.");
                AssertProbe(window.FindFirstDescendant(cf => cf.ByAutomationId("WorkspaceTabs")) is null, "Visible workspace tabs still exist in the shell.");
                string path = Path.Combine(options.OutputDirectory, $"shell-responsive-{viewportLabel}.png");
                CaptureWindow(window, path);
                Console.WriteLine($"shell-responsive: captured {path}");
            }
        }
        finally
        {
            TryClose(app);
            KillExistingApplicationInstances(options.AppPath);
        }
    }
}
