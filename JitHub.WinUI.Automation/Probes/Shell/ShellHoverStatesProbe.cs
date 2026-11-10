using FlaUI.Core.AutomationElements;
using FlaUI.Core.Input;
using FlaUI.UIA3;
using JitHub.WinUI.Automation.Options;
using static JitHub.WinUI.Automation.Applications.ApplicationProcessCleanup;
using static JitHub.WinUI.Automation.Applications.ProbeApplicationLauncher;
using static JitHub.WinUI.Automation.Conditions.ProbeConditions;
using static JitHub.WinUI.Automation.Elements.ElementGeometry;
using static JitHub.WinUI.Automation.Elements.ElementQueries;
using static JitHub.WinUI.Automation.Screenshots.CaptureFiles;
using static JitHub.WinUI.Automation.Screenshots.WindowCapture;
using static JitHub.WinUI.Automation.Windows.ReadyWindows;
using static JitHub.WinUI.Automation.Windows.WindowSizing;

namespace JitHub.WinUI.Automation.Probes.Shell;

/// <summary>
/// Drives the shell-hover-states probe.
/// </summary>
internal static class ShellHoverStatesProbe
{
    internal static void RunShellHoverStatesProbe(CaptureOptions options)
    {
        using var app = LaunchApplication(options.AppPath, "--page=shell", "--theme=dark");
        using var automation = new UIA3Automation();
        try
        {
            Window window = GetReadyWindow(app, automation, "shell-hover-states probe");
            ResizeWindow(window, 1600, 960);
            string[] ids =
            [
                "ShellBackButton",
                "ShellForwardButton",
                "ShellNav_home",
                "ShellNav_issues",
                "ShellNav_pull-requests",
                "ShellNav_notifications",
                "ShellNav_stars",
                "ShellNav_gists",
                "ShellNav_explore",
                "ShellSearchSubmitButton",
                "ShellNewRepositoryButton",
                "ShellSettingsTopButton",
                "ShellProfileTopButton",
                "ShellSearchTextBox",
                "ShellRepoFilter_Public",
                "ShellRepoFilter_Private",
                "ShellRepoFilter_Forked",
                "DashboardCustomizeButton"
            ];

            CaptureWindow(window, Path.Combine(options.OutputDirectory, "shell-hover-states-before.png"));
            foreach (string id in ids)
            {
                AutomationElement element = WaitForElement(
                    id,
                    () =>
                    {
                        AutomationElement? candidate = window.FindFirstDescendant(cf => cf.ByAutomationId(id));
                        return IsVisible(candidate) ? candidate : null;
                    },
                    TimeSpan.FromSeconds(8));
                AssertProbe(
                    !string.IsNullOrWhiteSpace(element.Name),
                    $"shell-hover-states: {id} does not expose an accessible name.");

                Mouse.MoveTo(CenterPoint(element, window));
                Thread.Sleep(250);
                string path = Path.Combine(options.OutputDirectory, $"shell-hover-states-{SanitizeFileName(id)}.png");
                CaptureWindow(window, path);
                Console.WriteLine($"shell-hover-states: {id}");
            }

            ResizeWindow(window, 760, 650);
            AutomationElement drawerButton = WaitForElement(
                "ShellRailDrawerButton",
                () =>
                {
                    AutomationElement? candidate = window.FindFirstDescendant(
                        cf => cf.ByAutomationId("ShellRailDrawerButton"));
                    return IsVisible(candidate) ? candidate : null;
                },
                TimeSpan.FromSeconds(8));
            AssertProbe(
                !string.IsNullOrWhiteSpace(drawerButton.Name),
                "shell-hover-states: ShellRailDrawerButton does not expose an accessible name.");
            Mouse.MoveTo(CenterPoint(drawerButton, window));
            Thread.Sleep(250);
            CaptureWindow(
                window,
                Path.Combine(options.OutputDirectory, "shell-hover-states-ShellRailDrawerButton.png"));
            Console.WriteLine("shell-hover-states: ShellRailDrawerButton");
        }
        finally
        {
            TryClose(app);
            KillExistingApplicationInstances(options.AppPath);
        }
    }
}
