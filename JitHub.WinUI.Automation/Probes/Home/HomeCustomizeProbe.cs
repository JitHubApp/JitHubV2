using FlaUI.Core.AutomationElements;
using FlaUI.Core.Input;
using FlaUI.UIA3;
using JitHub.WinUI.Automation.Options;
using static JitHub.WinUI.Automation.Applications.ApplicationProcessCleanup;
using static JitHub.WinUI.Automation.Applications.ProbeApplicationLauncher;
using static JitHub.WinUI.Automation.Conditions.ProbeConditions;
using static JitHub.WinUI.Automation.Elements.ElementGeometry;
using static JitHub.WinUI.Automation.Elements.ElementQueries;
using static JitHub.WinUI.Automation.Interaction.ElementActions;
using static JitHub.WinUI.Automation.Screenshots.WindowCapture;
using static JitHub.WinUI.Automation.Windows.ReadyWindows;
using static JitHub.WinUI.Automation.Windows.WindowSizing;

namespace JitHub.WinUI.Automation.Probes.Home;

/// <summary>
/// Drives the home-customize probe.
/// </summary>
internal static class HomeCustomizeProbe
{
    internal static void RunHomeCustomizeProbe(CaptureOptions options)
    {
        using var app = LaunchApplication(options.AppPath, "--page=shell", "--theme=dark");
        using var automation = new UIA3Automation();
        try
        {
            Window window = GetReadyWindow(app, automation, "home-customize probe");
            ResizeWindow(window, 1366, 900);
            AutomationElement customizeButton = WaitForElement("DashboardCustomizeButton", () => window.FindFirstDescendant(cf => cf.ByAutomationId("DashboardCustomizeButton")), TimeSpan.FromSeconds(8));
            var customizeBounds = customizeButton.BoundingRectangle;
            Mouse.Click(new System.Drawing.Point(
                (int)Math.Round(customizeBounds.X + customizeBounds.Width / 2d),
                (int)Math.Round(customizeBounds.Y + customizeBounds.Height / 2d)));
            AutomationElement dialog = WaitForElement("DashboardCustomizeSaveButton", () =>
            {
                AutomationElement? element = FindElementInWindowOrDialog(window, automation, "DashboardCustomizeSaveButton");
                return IsVisible(element) ? element : null;
            }, TimeSpan.FromSeconds(5));
            AutomationElement dialogRoot = WaitForElement("DashboardCustomizeDialog", () =>
            {
                AutomationElement? element = FindElementInWindowOrDialog(window, automation, "DashboardCustomizeDialog");
                return IsVisible(element) ? element : null;
            }, TimeSpan.FromSeconds(5));
            AssertProbe(IsInsideWindowBounds(dialogRoot, window), "Customize dialog escaped the app window.");
            AssertProbe(IsHorizontallyCentered(dialogRoot, window, 44), "Customize dialog was not centered in the app window.");
            Thread.Sleep(500);
            CaptureWindowWithPopups(window, Path.Combine(options.OutputDirectory, "home-customize-open.png"));

            AutomationElement reset = WaitForElement("DashboardCustomizeResetButton", () => FindElementInWindowOrDialog(window, automation, "DashboardCustomizeResetButton"), TimeSpan.FromSeconds(5));
            InvokeOrClick(reset);
            Thread.Sleep(250);
            InvokeOrClick(dialog);
            WaitUntil("customize dialog closes", () => !IsVisible(FindElementInWindowOrDialog(window, automation, "DashboardCustomizeSaveButton")), TimeSpan.FromSeconds(5));

            for (int cycle = 0; cycle < 3; cycle++)
            {
                customizeButton = WaitForElement(
                    $"DashboardCustomizeButton rapid cycle {cycle + 1}",
                    () => window.FindFirstDescendant(cf => cf.ByAutomationId("DashboardCustomizeButton")),
                    TimeSpan.FromSeconds(5));
                InvokeOrClick(customizeButton);
                AutomationElement cancel = WaitForElement(
                    $"DashboardCustomizeCancelButton rapid cycle {cycle + 1}",
                    () => FindElementInWindowOrDialog(window, automation, "DashboardCustomizeCancelButton"),
                    TimeSpan.FromSeconds(5));
                AssertProbe(IsVisible(cancel), $"Customize dialog did not open on rapid cycle {cycle + 1}.");
                InvokeOrClick(cancel);
                WaitUntil(
                    $"customize dialog closes on rapid cycle {cycle + 1}",
                    () => !IsVisible(FindElementInWindowOrDialog(window, automation, "DashboardCustomizeCancelButton")),
                    TimeSpan.FromSeconds(5));
            }

            CaptureWindow(window, Path.Combine(options.OutputDirectory, "home-customize-saved.png"));
        }
        finally
        {
            TryClose(app);
            KillExistingApplicationInstances(options.AppPath);
        }
    }
}
