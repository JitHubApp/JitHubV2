using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.UIA3;
using JitHub.WinUI.Automation.Options;
using static JitHub.WinUI.Automation.Applications.ApplicationProcessCleanup;
using static JitHub.WinUI.Automation.Applications.ProbeApplicationLauncher;
using static JitHub.WinUI.Automation.Conditions.ProbeConditions;
using static JitHub.WinUI.Automation.Elements.ElementQueries;
using static JitHub.WinUI.Automation.Interaction.ElementActions;
using static JitHub.WinUI.Automation.Probes.Stars.StarsCategories;
using static JitHub.WinUI.Automation.Screenshots.WindowCapture;
using static JitHub.WinUI.Automation.Windows.ReadyWindows;
using static JitHub.WinUI.Automation.Windows.WindowSizing;

namespace JitHub.WinUI.Automation.Probes.Stars;

/// <summary>
/// Drives the stars-selection-mode probe.
/// </summary>
internal static class StarsSelectionModeProbe
{
    // This method stays whole because it repeats Stars selection mode enter, check a row, and exit three times at wide
    // width, then repeats it once at 640 wide with Cancel, asserting each time that the bulk toolbar appears and the
    // Stars page stays open.
    internal static void RunStarsSelectionModeProbe(CaptureOptions options)
    {
        KillExistingApplicationInstances(options.AppPath);
        using var app = LaunchApplication(options.AppPath, "--page=stars", "--theme=dark");
        using var automation = new UIA3Automation();
        try
        {
            Window window = GetReadyWindow(app, automation, "stars-selection-mode probe");
            ResizeWindow(window, 1366, 900);
            AutomationElement starsRoot = WaitForElement(
                "StarsSearch",
                () => window.FindFirstDescendant(cf => cf.ByAutomationId("StarsSearch")),
                TimeSpan.FromSeconds(10));
            AutomationElement starsList = WaitForElement(
                "StarsList",
                () => window.FindFirstDescendant(cf => cf.ByAutomationId("StarsList")),
                TimeSpan.FromSeconds(10));
            AutomationElement selection = WaitForElement(
                "StarsSelectionMode",
                () => window.FindFirstDescendant(cf => cf.ByAutomationId("StarsSelectionMode")),
                TimeSpan.FromSeconds(10));

            WaitForElement(
                "first Stars row",
                () => starsList.FindFirstDescendant(cf => cf.ByControlType(ControlType.ListItem)),
                TimeSpan.FromSeconds(12));

            for (int pass = 0; pass < 3; pass++)
            {
                InvokeOrClick(selection);
                AutomationElement row = WaitForElement(
                    $"first Stars row for selection pass {pass + 1}",
                    () => starsList.FindFirstDescendant(cf => cf.ByControlType(ControlType.ListItem)),
                    TimeSpan.FromSeconds(5));
                AssertStarsRepositoryAutomationIdentity(row);
                AutomationElement checkBox = WaitForElement(
                    $"Stars row checkbox for pass {pass + 1}",
                    () => row.FindFirstDescendant(cf => cf.ByControlType(ControlType.CheckBox)),
                    TimeSpan.FromSeconds(5));
                AssertProbe(IsVisible(checkBox), $"Stars did not expose a native row checkbox in selection pass {pass + 1}.");
                InvokeOrClick(checkBox);
                WaitForElement(
                    $"Stars bulk toolbar for pass {pass + 1}",
                    () => window.FindFirstDescendant(cf => cf.ByAutomationId("StarsBulkCategory")),
                    TimeSpan.FromSeconds(5));
                if (pass == 0)
                {
                    CaptureWindow(window, Path.Combine(options.OutputDirectory, "stars-selection-checkbox.png"));
                }

                InvokeOrClick(selection);
                WaitUntil(
                    $"Stars selection mode dismissal pass {pass + 1}",
                    () => !IsVisible(window.FindFirstDescendant(cf => cf.ByAutomationId("StarsBulkCategory"))),
                    TimeSpan.FromSeconds(5));
                AssertProbe(IsVisible(starsRoot), $"Stars closed during selection mode pass {pass + 1}.");
            }

            ResizeWindow(window, 640, 600);
            selection = WaitForElement(
                "StarsSelectionMode at compact width",
                () => window.FindFirstDescendant(cf => cf.ByAutomationId("StarsSelectionMode")),
                TimeSpan.FromSeconds(5));
            InvokeOrClick(selection);
            AutomationElement compactRow = WaitForElement(
                "first Stars row for compact selection",
                () => starsList.FindFirstDescendant(cf => cf.ByControlType(ControlType.ListItem)),
                TimeSpan.FromSeconds(5));
            AutomationElement compactCheckBox = WaitForElement(
                "Stars row checkbox at compact width",
                () => compactRow.FindFirstDescendant(cf => cf.ByControlType(ControlType.CheckBox)),
                TimeSpan.FromSeconds(5));
            InvokeOrClick(compactCheckBox);
            WaitForElement(
                "Stars compact selection toolbar",
                () => window.FindFirstDescendant(cf => cf.ByAutomationId("StarsBulkCategory")),
                TimeSpan.FromSeconds(5));
            CaptureWindow(window, Path.Combine(options.OutputDirectory, "stars-selection-checkbox-compact.png"));
            InvokeOrClick(WaitForElement(
                "StarsCancelSelection",
                () => window.FindFirstDescendant(cf => cf.ByAutomationId("StarsCancelSelection")),
                TimeSpan.FromSeconds(5)));
            WaitUntil(
                "Stars cancel selection dismissal",
                () => !IsVisible(window.FindFirstDescendant(cf => cf.ByAutomationId("StarsBulkCategory"))),
                TimeSpan.FromSeconds(5));
            AssertProbe(IsVisible(starsRoot), "Stars closed while canceling selection mode.");
            CaptureWindow(window, Path.Combine(options.OutputDirectory, "stars-selection-mode-regression.png"));
        }
        finally
        {
            TryClose(app);
            KillExistingApplicationInstances(options.AppPath);
        }
    }
}
