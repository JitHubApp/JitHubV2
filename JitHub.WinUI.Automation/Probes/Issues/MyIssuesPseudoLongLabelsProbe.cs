using FlaUI.Core.AutomationElements;
using FlaUI.UIA3;
using JitHub.WinUI.Automation.Options;
using static JitHub.WinUI.Automation.Applications.ApplicationProcessCleanup;
using static JitHub.WinUI.Automation.Applications.ProbeApplicationLauncher;
using static JitHub.WinUI.Automation.Conditions.ProbeConditions;
using static JitHub.WinUI.Automation.Elements.ElementQueries;
using static JitHub.WinUI.Automation.Screenshots.WindowCapture;
using static JitHub.WinUI.Automation.Windows.ReadyWindows;
using static JitHub.WinUI.Automation.Windows.WindowSizing;

namespace JitHub.WinUI.Automation.Probes.Issues;

/// <summary>
/// Drives the my-issues-pseudo-long-labels probe.
/// </summary>
internal static class MyIssuesPseudoLongLabelsProbe
{
    // This method stays whole because it is one ordered pseudo-long-label script: confirm the wide view switched to
    // compact pickers, expand the scope picker to see the full option text, then shrink to 760x650 and open the list
    // drawer if needed to confirm the picker is still visible.
    internal static void RunMyIssuesPseudoLongLabelsProbe(CaptureOptions options)
    {
        using var app = LaunchApplication(
            options.AppPath,
            "--page=my-issues",
            "--scenario=my-issues-pseudo-long-labels",
            "--theme=dark");
        using var automation = new UIA3Automation();
        try
        {
            Window window = GetReadyWindow(app, automation, "my-issues pseudo-long-label probe");
            ResizeWindow(window, 1366, 900);
            AutomationElement scopePicker;
            try
            {
                scopePicker = WaitForElement(
                    "MyIssuesScopeCompactPicker for pseudo-long labels",
                    () =>
                    {
                        AutomationElement? picker = window.FindFirstDescendant(cf => cf.ByAutomationId("MyIssuesScopeCompactPicker"));
                        return IsVisible(picker) ? picker : null;
                    },
                    TimeSpan.FromSeconds(10));
            }
            catch
            {
                PrintVisibleAutomationIds(window, "my-issues-pseudo-label-picker-missing");
                CaptureWindow(window, Path.Combine(options.OutputDirectory, "my-issues-page-pseudo-label-picker-missing.png"));
                throw;
            }
            WaitForElement(
                "MyIssuesStateCompactPicker for pseudo-long labels",
                () =>
                {
                    AutomationElement? picker = window.FindFirstDescendant(cf => cf.ByAutomationId("MyIssuesStateCompactPicker"));
                    return IsVisible(picker) ? picker : null;
                },
                TimeSpan.FromSeconds(5));
            AssertProbe(
                !IsVisible(window.FindFirstDescendant(cf => cf.ByAutomationId("MyIssuesScopeSegmented"))),
                "Pseudo-long My Issues labels did not switch away from the segmented control.");
            scopePicker.AsComboBox().Expand();
            WaitForElement(
                "full pseudo-long My Issues scope option",
                () =>
                {
                    AutomationElement? option = automation.GetDesktop().FindFirstDescendant(
                        cf => cf.ByText("Assigned to the authenticated account"));
                    return IsVisible(option) ? option : null;
                },
                TimeSpan.FromSeconds(5));
            CaptureWindowWithPopups(window, Path.Combine(options.OutputDirectory, "my-issues-page-pseudo-long-picker-open.png"));
            scopePicker.AsComboBox().Collapse();
            CaptureWindow(window, Path.Combine(options.OutputDirectory, "my-issues-page-pseudo-long-wide.png"));

            ResizeWindow(window, 760, 650);
            AutomationElement? compactPicker = window.FindFirstDescendant(cf => cf.ByAutomationId("MyIssuesScopeCompactPicker"));
            if (!IsVisible(compactPicker))
            {
                AutomationElement opener = new[] { "MyIssuesOpenListPaneButton", "MyIssuesLeadingPaneButton" }
                    .Select(id => window.FindFirstDescendant(cf => cf.ByAutomationId(id)))
                    .FirstOrDefault(IsVisible)
                    ?? throw new InvalidOperationException("My Issues list drawer opener was not visible for pseudo-long labels.");
                opener.Click();
            }

            WaitForElement(
                "visible pseudo-long My Issues picker in compact drawer",
                () =>
                {
                    AutomationElement? picker = window.FindFirstDescendant(cf => cf.ByAutomationId("MyIssuesScopeCompactPicker"));
                    return IsVisible(picker) ? picker : null;
                },
                TimeSpan.FromSeconds(5));
            CaptureWindow(window, Path.Combine(options.OutputDirectory, "my-issues-page-pseudo-long-compact.png"));
        }
        finally
        {
            TryClose(app);
            KillExistingApplicationInstances(options.AppPath);
        }
    }
}
