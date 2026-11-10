using FlaUI.Core.AutomationElements;
using FlaUI.Core.Input;
using FlaUI.Core.WindowsAPI;
using FlaUI.UIA3;
using JitHub.WinUI.Automation.Options;
using static JitHub.WinUI.Automation.Applications.ApplicationProcessCleanup;
using static JitHub.WinUI.Automation.Applications.ProbeApplicationLauncher;
using static JitHub.WinUI.Automation.Conditions.ProbeConditions;
using static JitHub.WinUI.Automation.Elements.ElementGeometry;
using static JitHub.WinUI.Automation.Screenshots.WindowCapture;
using static JitHub.WinUI.Automation.Shell.ShellSearch;
using static JitHub.WinUI.Automation.Windows.ReadyWindows;
using static JitHub.WinUI.Automation.Windows.WindowSizing;

namespace JitHub.WinUI.Automation.Probes.Shell;

/// <summary>
/// Drives the shell-search-states probe.
/// </summary>
internal static class ShellSearchStatesProbe
{
    internal static void RunShellSearchStatesProbe(CaptureOptions options)
    {
        using var app = LaunchApplication(options.AppPath, "--page=shell", "--theme=dark");
        using var automation = new UIA3Automation();
        try
        {
            Window window = GetReadyWindow(app, automation, "shell-search-states probe");
            ResizeWindow(window, 1366, 900);
            AutomationElement commandSearch = WaitForElement(
                "ShellSearchTextBox",
                () => FindShellSearchTextBox(window),
                TimeSpan.FromSeconds(5));

            CaptureWindow(window, Path.Combine(options.OutputDirectory, "shell-search-states-normal.png"));
            Mouse.MoveTo(CenterPoint(commandSearch, window));
            Thread.Sleep(350);
            CaptureWindow(window, Path.Combine(options.OutputDirectory, "shell-search-states-hover.png"));
            commandSearch.Click();
            WaitUntil("command search gets focus", () => IsSearchActuallyFocused(automation, commandSearch), TimeSpan.FromSeconds(5));
            CaptureWindow(window, Path.Combine(options.OutputDirectory, "shell-search-states-focused.png"));

            Keyboard.Press(VirtualKeyShort.ESCAPE);
            Thread.Sleep(250);
            AutomationElement repoFilter = WaitForElement(
                "ShellRepoFilter_Public",
                () => window.FindFirstDescendant(cf => cf.ByAutomationId("ShellRepoFilter_Public")),
                TimeSpan.FromSeconds(5));
            Mouse.MoveTo(CenterPoint(repoFilter, window));
            Thread.Sleep(350);
            CaptureWindow(window, Path.Combine(options.OutputDirectory, "shell-search-states-repo-filter-hover.png"));
            AutomationElement privateFilter = WaitForElement(
                "ShellRepoFilter_Private",
                () => window.FindFirstDescendant(cf => cf.ByAutomationId("ShellRepoFilter_Private")),
                TimeSpan.FromSeconds(5));
            privateFilter.Click();
            Thread.Sleep(350);
            CaptureWindow(window, Path.Combine(options.OutputDirectory, "shell-search-states-repo-filter-selected.png"));
        }
        finally
        {
            TryClose(app);
            KillExistingApplicationInstances(options.AppPath);
        }
    }
}
