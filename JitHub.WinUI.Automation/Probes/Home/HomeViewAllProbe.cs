using System.Drawing;
using FlaUI.Core.AutomationElements;
using FlaUI.UIA3;
using JitHub.WinUI.Automation.Options;
using static JitHub.WinUI.Automation.Applications.ApplicationProcessCleanup;
using static JitHub.WinUI.Automation.Applications.ProbeApplicationLauncher;
using static JitHub.WinUI.Automation.Conditions.ProbeConditions;
using static JitHub.WinUI.Automation.Elements.ElementQueries;
using static JitHub.WinUI.Automation.Interaction.ElementActions;
using static JitHub.WinUI.Automation.Screenshots.CaptureFiles;
using static JitHub.WinUI.Automation.Screenshots.WindowCapture;
using static JitHub.WinUI.Automation.Windows.ReadyWindows;
using static JitHub.WinUI.Automation.Windows.WindowSizing;

namespace JitHub.WinUI.Automation.Probes.Home;

/// <summary>
/// Drives the home-view-all probe.
/// </summary>
internal static class HomeViewAllProbe
{
    // This method stays whole because each wide and compact pass launches a fresh app, opens the Overview drawer when
    // the widget lives there, checks that the false View all actions stay hidden, then invokes the real one and
    // verifies its destination on the same window handle.
    internal static void RunHomeViewAllProbe(CaptureOptions options)
    {
        (string ViewAllId, string WideDestinationId, string CompactDestinationId, bool IsSideWidget)[] viewAllRoutes =
        [
            ("DashboardWidgetViewAll_notifications", "NotificationsList", "NotificationsList", true)
        ];
        string[] unavailableFalseRoutes =
        [
            "DashboardWidgetViewAll_recent_activity",
            "DashboardWidgetViewAll_repositories",
            "DashboardWidgetViewAll_overview",
            "DashboardWidgetViewAll_recommended_repositories"
        ];

        (int Width, int Height, string Label)[] sizes = [(1366, 900, "wide"), (640, 600, "compact")];
        foreach ((int width, int height, string label) in sizes)
        {
            foreach ((string viewAllId, string wideDestinationId, string compactDestinationId, bool isSideWidget) in viewAllRoutes)
            {
                using var app = LaunchApplication(options.AppPath, "--page=shell", "--theme=dark");
                using var automation = new UIA3Automation();
                try
                {
                    Window window = GetReadyWindow(app, automation, $"home-view-all probe {label} {viewAllId}");
                    Rectangle resizedBounds = ResizeWindow(window, width, height);
                    string actualLayout = resizedBounds.Width < 1040 ? "compact" : "wide";
                    AssertProbe(
                        string.Equals(label, actualLayout, StringComparison.Ordinal),
                        $"Home layout was {actualLayout} at native width {resizedBounds.Width}, but the probe expected {label}.");
                    IntPtr mainWindowHandle = new(window.Properties.NativeWindowHandle.ValueOrDefault);
                    int expectedProcessId = app.ProcessId;
                    if (actualLayout == "compact" && isSideWidget)
                    {
                        AutomationElement drawerButton = WaitForElement(
                            "DashboardOverviewDrawerButton",
                            () => window.FindFirstDescendant(cf => cf.ByAutomationId("DashboardOverviewDrawerButton")),
                            TimeSpan.FromSeconds(8));
                        InvokeOrClick(drawerButton);
                        WaitForElement(
                            "DashboardSideDrawerCloseButton",
                            () =>
                            {
                                AutomationElement? close = window.FindFirstDescendant(cf => cf.ByAutomationId("DashboardSideDrawerCloseButton"));
                                return IsVisible(close) ? close : null;
                            },
                            TimeSpan.FromSeconds(8));
                    }

                    foreach (string falseRoute in unavailableFalseRoutes)
                    {
                        AssertProbe(
                            !IsVisible(window.FindFirstDescendant(cf => cf.ByAutomationId(falseRoute))),
                            $"Home exposed the semantically false action {falseRoute} at {label} width.");
                    }

                    AutomationElement button = WaitForElement(viewAllId, () =>
                    {
                        AutomationElement[] matches = window.FindAllDescendants(cf => cf.ByAutomationId(viewAllId));
                        return matches.FirstOrDefault(IsVisible)
                            ?? matches.FirstOrDefault(element => element.Patterns.ScrollItem.IsSupported)
                            ?? matches.FirstOrDefault();
                    }, TimeSpan.FromSeconds(10));
                    RevealForInteraction(button, $"{label} {viewAllId}");
                    Console.WriteLine($"Invoking {label} Home widget action {viewAllId} at {button.BoundingRectangle}.");
                    button.Click();
                    Thread.Sleep(900);
                    window = GetReadyWindowByHandle(
                        automation,
                        mainWindowHandle,
                        expectedProcessId,
                        $"home-view-all destination {label} {viewAllId}");
                    Console.WriteLine($"Destination {viewAllId} window bounds: {window.BoundingRectangle} handle={window.Properties.NativeWindowHandle.ValueOrDefault} process={window.Properties.ProcessId.ValueOrDefault}.");
                    CaptureWindow(window, Path.Combine(options.OutputDirectory, $"home-view-all-{label}-{SanitizeFileName(viewAllId)}.png"));
                    string destinationId = actualLayout == "compact" ? compactDestinationId : wideDestinationId;
                    WaitForElement(destinationId, () => window.FindFirstDescendant(cf => cf.ByAutomationId(destinationId)), TimeSpan.FromSeconds(8));
                }
                finally
                {
                    TryClose(app);
                    KillExistingApplicationInstances(options.AppPath);
                }
            }
        }
    }
}
