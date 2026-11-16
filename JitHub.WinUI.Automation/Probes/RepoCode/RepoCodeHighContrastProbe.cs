using FlaUI.Core.AutomationElements;
using FlaUI.UIA3;
using JitHub.WinUI.Automation.Options;
using static JitHub.WinUI.Automation.Applications.ApplicationProcessCleanup;
using static JitHub.WinUI.Automation.Applications.ProbeApplicationLauncher;
using static JitHub.WinUI.Automation.Conditions.ProbeConditions;
using static JitHub.WinUI.Automation.Elements.ElementQueries;
using static JitHub.WinUI.Automation.Interaction.ElementActions;
using static JitHub.WinUI.Automation.Probes.RepoCode.RepoCodeNavigation;
using static JitHub.WinUI.Automation.Screenshots.WindowCapture;
using static JitHub.WinUI.Automation.Windows.ReadyWindows;
using static JitHub.WinUI.Automation.Windows.WindowSizing;

namespace JitHub.WinUI.Automation.Probes.RepoCode;

/// <summary>
/// Drives the repo-code-high-contrast probe.
/// </summary>
internal static class RepoCodeHighContrastProbe
{
    internal static void RunRepoCodeHighContrastProbe(CaptureOptions options)
    {
        KillExistingApplicationInstances(options.AppPath);
        using var app = LaunchApplication(
            options.AppPath,
            "--page=repo-code",
            "--scenario=repo-code-high-contrast",
            "--theme=dark",
            "--high-contrast",
            $"--repo={options.RepositoryFullName}");
        using var automation = new UIA3Automation();
        try
        {
            Window window = GetReadyWindow(app, automation, "repo-code-high-contrast probe");
            ResizeWindow(window, 1366, 900);
            AutomationElement sourceFile = FindMandatoryRepoCodeSourceFile(window);
            InvokeOrClick(sourceFile);
            AutomationElement editor = WaitForElementWithItemStatus(
                window,
                "RepoCodeEditor",
                "Source loaded",
                TimeSpan.FromSeconds(5));
            AssertProbe(
                string.Equals(
                    editor.Properties.HelpText.ValueOrDefault,
                    "High contrast editor colors active",
                    StringComparison.Ordinal),
                "repo-code-high-contrast: native editor did not publish its system-color treatment.");
            AssertProbe(IsVisible(editor), "repo-code-high-contrast: editor was not visible at wide width.");
            CaptureWindow(window, Path.Combine(options.OutputDirectory, "repo-code-high-contrast-1366x900.png"));

            ResizeWindow(window, 760, 650);
            Thread.Sleep(350);
            editor = WaitForElementWithItemStatus(
                window,
                "RepoCodeEditor",
                "Source loaded",
                TimeSpan.FromSeconds(5));
            AssertProbe(IsVisible(editor), "repo-code-high-contrast: editor was not visible at compact width.");
            AssertProbe(
                string.Equals(
                    editor.Properties.HelpText.ValueOrDefault,
                    "High contrast editor colors active",
                    StringComparison.Ordinal),
                "repo-code-high-contrast: editor lost high contrast treatment after resize.");
            CaptureWindow(window, Path.Combine(options.OutputDirectory, "repo-code-high-contrast-760x650.png"));

            Console.WriteLine(
                $"repo-code-high-contrast probe: system-color editor treatment passed at wide and compact widths; output={options.OutputDirectory}");
        }
        finally
        {
            TryClose(app);
            KillExistingApplicationInstances(options.AppPath);
        }
    }
}
