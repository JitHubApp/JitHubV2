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
using static JitHub.WinUI.Automation.Windows.WindowSizing;

namespace JitHub.WinUI.Automation.Probes.Shell;

/// <summary>
/// Drives the shell-repo-click probe.
/// </summary>
internal static class ShellRepoClickProbe
{
    internal static void RunShellRepoClickProbe(CaptureOptions options)
    {
        using var app = LaunchApplication(options.AppPath, "--page=shell", "--theme=dark");
        using var automation = new UIA3Automation();
        try
        {
            Window window = GetReadyWindow(app, automation, "shell-repo-click probe");
            ResizeWindow(window, 1366, 900);
            AutomationElement repositoryList = WaitForElement("ShellRepositoryList", () => window.FindFirstDescendant(cf => cf.ByAutomationId("ShellRepositoryList")), TimeSpan.FromSeconds(10));
            AutomationElement repoButton = WaitForElement(
                "first repository",
                () => repositoryList.FindFirstDescendant(cf => cf.ByControlType(ControlType.Button)),
                TimeSpan.FromSeconds(10));
            repoButton.Click();
            WaitForElement("RepoDetailBranchPicker", () => window.FindFirstDescendant(cf => cf.ByAutomationId("RepoDetailBranchPicker")), TimeSpan.FromSeconds(14));
            AutomationElement codeRoot = WaitForElement(
                "RepoCodePageRoot",
                () => window.FindFirstDescendant(cf => cf.ByAutomationId("RepoCodePageRoot")),
                TimeSpan.FromSeconds(14));
            AssertProbe(IsVisible(codeRoot), "Repository navigation did not expose the Code workspace root.");
            AssertNamedAutomationElement(window, "RepoDetailBranchPicker", ControlType.Button);
            AssertNamedAutomationElement(window, "RepoDetailWatchButton", ControlType.Button);
            AssertNamedAutomationElement(window, "RepoDetailStarButton", ControlType.Button);
            AssertNamedAutomationElement(window, "RepoDetailForkButton", ControlType.Button);
            AssertNamedAutomationElement(window, "RepoCodeFileFilter", ControlType.Edit);
            AssertNamedAutomationElement(window, "RepoCodeFileTree", ControlType.Group);

            AutomationElement fileTree = window.FindFirstDescendant(cf => cf.ByAutomationId("RepoCodeFileTree"))!;
            AutomationElement treeItem = WaitForElement(
                "realized repository tree item",
                () => fileTree.FindAllDescendants(cf => cf.ByControlType(ControlType.TreeItem))
                    .FirstOrDefault(element =>
                        IsVisible(element) &&
                        element.AutomationId.StartsWith("RepoCodeTreeItem_", StringComparison.Ordinal) &&
                        (element.Name.EndsWith(", file", StringComparison.Ordinal) ||
                         element.Name.EndsWith(", folder", StringComparison.Ordinal))),
                TimeSpan.FromSeconds(14));
            AssertProbe(!string.IsNullOrWhiteSpace(treeItem.Name), "Repository tree item did not expose a meaningful accessible name.");
            treeItem.Focus();
            Keyboard.Press(VirtualKeyShort.ENTER);
            Thread.Sleep(500);

            AutomationElement breadcrumbSegment = WaitForElement(
                "realized repository breadcrumb segment",
                () => window.FindAllDescendants(cf => cf.ByControlType(ControlType.Button))
                    .FirstOrDefault(element =>
                        IsVisible(element) &&
                        element.AutomationId.StartsWith("RepoCodeBreadcrumbSegment_", StringComparison.Ordinal)),
                TimeSpan.FromSeconds(10));
            AssertProbe(
                breadcrumbSegment.Name.StartsWith("Open ", StringComparison.Ordinal),
                "Repository breadcrumb segment did not expose its navigation action.");
            AssertProbe(breadcrumbSegment.Patterns.Invoke.IsSupported, "Repository breadcrumb segment did not expose the Invoke pattern.");
            breadcrumbSegment.Focus();
            Keyboard.Press(VirtualKeyShort.ENTER);
            WaitUntil(
                "breadcrumb keyboard invocation keeps Code workspace active",
                () => IsVisible(window.FindFirstDescendant(cf => cf.ByAutomationId("RepoCodePageRoot"))),
                TimeSpan.FromSeconds(5));

            AssertNamedAutomationElement(window, "RepoCodeBackButton", ControlType.Button);
            AssertNamedAutomationElement(window, "RepoCodeForwardButton", ControlType.Button);
            AssertNamedAutomationElement(window, "RepoCodeCopyPathButton", ControlType.Button);
            AssertNamedAutomationElement(window, "RepoCodeCopyRawUrlButton", ControlType.Button);
            AssertNamedAutomationElement(window, "RepoCodeOpenOnGitHubButton", ControlType.Button);
            CaptureWindow(window, Path.Combine(options.OutputDirectory, "shell-repo-click.png"));
            ResizeWindow(window, 640, 600);
            AssertNamedAutomationElement(window, "RepoDetailCompactCommandsButton", ControlType.Button);
            CaptureWindow(window, Path.Combine(options.OutputDirectory, "repo-code-accessibility-compact.png"));
        }
        finally
        {
            TryClose(app);
            KillExistingApplicationInstances(options.AppPath);
        }
    }
}
