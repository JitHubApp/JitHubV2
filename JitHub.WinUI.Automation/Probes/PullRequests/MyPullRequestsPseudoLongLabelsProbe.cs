using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Input;
using FlaUI.Core.WindowsAPI;
using FlaUI.UIA3;
using JitHub.WinUI.Automation.Options;
using static JitHub.WinUI.Automation.Applications.ApplicationProcessCleanup;
using static JitHub.WinUI.Automation.Applications.ProbeApplicationLauncher;
using static JitHub.WinUI.Automation.Conditions.ProbeConditions;
using static JitHub.WinUI.Automation.Elements.ElementQueries;
using static JitHub.WinUI.Automation.Screenshots.WindowCapture;
using static JitHub.WinUI.Automation.Windows.ReadyWindows;
using static JitHub.WinUI.Automation.Windows.WindowActivation;
using static JitHub.WinUI.Automation.Windows.WindowSizing;

namespace JitHub.WinUI.Automation.Probes.PullRequests;

/// <summary>
/// Drives the my-pull-requests-pseudo-long-labels probe.
/// </summary>
internal static class MyPullRequestsPseudoLongLabelsProbe
{
    // This method stays whole because it drives one compact My Pull Requests picker with pseudo-long labels: open the
    // list drawer if needed, focus the picker, then press Down and Up and verify the full selected label each time,
    // with each key press depending on the focus left before.
    internal static void RunMyPullRequestsPseudoLongLabelsProbe(CaptureOptions options)
    {
        using var app = LaunchApplication(
            options.AppPath,
            "--page=my-pull-requests",
            "--scenario=my-pull-requests-pseudo-long-labels",
            "--theme=dark");
        using var automation = new UIA3Automation();
        try
        {
            Window window = GetReadyWindow(app, automation, "my-pull-requests pseudo-long-label probe");
            ResizeWindow(window, 640, 600);
            AutomationElement? compactPicker = FindCurrentVisibleByAutomationId(window, "MyPullRequestsStateCompactPicker");
            if (!IsVisible(compactPicker))
            {
                AutomationElement opener = new[] { "MyPullRequestsOpenListPaneButton", "MyPullRequestsLeadingPaneButton" }
                    .Select(id => FindCurrentVisibleByAutomationId(window, id))
                    .FirstOrDefault(IsVisible)
                    ?? throw new InvalidOperationException("My Pull Requests list drawer opener was not visible for pseudo-long labels.");
                opener.Click();
            }
            try
            {
                compactPicker = WaitForElement(
                    "visible My Pull Requests pseudo-long picker in compact drawer",
                    () => FindCurrentVisibleByAutomationId(window, "MyPullRequestsStateCompactPicker"),
                    TimeSpan.FromSeconds(5));
            }
            catch
            {
                PrintVisibleAutomationIds(window, "my-pull-requests-pseudo-long-picker-missing");
                CaptureWindow(window, Path.Combine(options.OutputDirectory, "my-pull-requests-page-pseudo-long-picker-missing.png"));
                throw;
            }
            TryActivateWindow(window);
            Thread.Sleep(600);
            compactPicker.FocusNative();
            Thread.Sleep(150);
            CaptureWindow(
                window,
                Path.Combine(options.OutputDirectory, "my-pull-requests-page-pseudo-long-initial.png"));
            Func<string> selectedStateName = () =>
            {
                AutomationElement? currentPicker = FindCurrentVisibleByAutomationId(
                    window,
                    "MyPullRequestsStateCompactPicker");
                if (currentPicker is null)
                {
                    return string.Empty;
                }

                AutomationElement? selected = currentPicker.AsComboBox().SelectedItem;
                if (selected is null)
                {
                    return string.Empty;
                }

                return selected.FindAllDescendants(cf => cf.ByControlType(ControlType.Text))
                    .Where(IsVisible)
                    .Select(GetElementName)
                    .FirstOrDefault(static text => !string.IsNullOrWhiteSpace(text))
                    ?? GetElementName(selected);
            };
            AssertProbe(
                selectedStateName().Contains("Currently open pull requests involving", StringComparison.Ordinal),
                "My Pull Requests compact picker did not expose its full pseudo-long selected label.");
            compactPicker.FocusNative();
            Keyboard.Press(VirtualKeyShort.DOWN);
            WaitUntil(
                "pseudo-long My Pull Requests closed selection",
                () => selectedStateName().Contains("Previously closed pull requests involving", StringComparison.Ordinal),
                TimeSpan.FromSeconds(5));
            CaptureWindow(window, Path.Combine(options.OutputDirectory, "my-pull-requests-page-pseudo-long-closed.png"));
            compactPicker = WaitForElement(
                "current My Pull Requests pseudo-long picker",
                () => FindCurrentVisibleByAutomationId(window, "MyPullRequestsStateCompactPicker"),
                TimeSpan.FromSeconds(5));
            compactPicker.FocusNative();
            Keyboard.Press(VirtualKeyShort.UP);
            WaitUntil(
                "pseudo-long My Pull Requests open selection",
                () => selectedStateName().Contains("Currently open pull requests involving", StringComparison.Ordinal),
                TimeSpan.FromSeconds(5));
            CaptureWindow(window, Path.Combine(options.OutputDirectory, "my-pull-requests-page-pseudo-long-640x600.png"));
        }
        finally
        {
            TryClose(app);
            KillExistingApplicationInstances(options.AppPath);
        }
    }
}
