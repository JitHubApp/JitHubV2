using System.Drawing;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Input;
using FlaUI.Core.WindowsAPI;
using FlaUI.UIA3;
using JitHub.WinUI.Automation.Options;
using static JitHub.WinUI.Automation.Applications.ApplicationProcessCleanup;
using static JitHub.WinUI.Automation.Applications.ProbeApplicationLauncher;
using static JitHub.WinUI.Automation.Conditions.ProbeConditions;
using static JitHub.WinUI.Automation.Elements.ElementGeometry;
using static JitHub.WinUI.Automation.Elements.ElementQueries;
using static JitHub.WinUI.Automation.Interaction.ElementActions;
using static JitHub.WinUI.Automation.Probes.PullRequests.MyPullRequestsPseudoLongLabelsProbe;
using static JitHub.WinUI.Automation.Screenshots.WindowCapture;
using static JitHub.WinUI.Automation.Windows.ReadyWindows;
using static JitHub.WinUI.Automation.Windows.WindowSizing;
using static JitHub.WinUI.Automation.Workspaces.AdaptiveWorkspaceDrawers;
using static JitHub.WinUI.Automation.Workspaces.WorkItemMarkdown;

namespace JitHub.WinUI.Automation.Probes.PullRequests;

/// <summary>
/// Drives the my-pull-requests-page probe.
/// </summary>
internal static class MyPullRequestsPageProbe
{
    // This method stays whole because it drives one My Pull Requests instance, or an attached process, from row hover
    // and selection through Markdown copy, five responsive sizes, Commits-to-Reviews keyboard section traversal and
    // compact drawers, and skips teardown and the long-label follow-up only when attached.
    internal static void RunMyPullRequestsPageProbe(CaptureOptions options)
    {
        bool isAttached = !string.IsNullOrWhiteSpace(options.AttachProcess);
        using var app = isAttached
            ? CreateProbeApplication(options)
            : LaunchApplication(options.AppPath, "--page=my-pull-requests", "--theme=dark");
        using var automation = new UIA3Automation();
        try
        {
            Window window = GetReadyWindow(app, automation, "my-pull-requests-page probe");
            ResizeWindow(window, 1366, 900);
            AutomationElement list;
            try
            {
                list = WaitForElement(
                    "MyPullRequestsList",
                    () => FindCurrentVisibleByAutomationId(window, "MyPullRequestsList"),
                    TimeSpan.FromSeconds(20));
            }
            catch
            {
                CaptureWindow(window, Path.Combine(options.OutputDirectory, "my-pull-requests-page-missing-list.png"));
                PrintVisibleAutomationIds(window, "my-pull-requests-page-missing-list");
                throw;
            }
            AutomationElement pullRequestToSelect = WaitForElement(
                "unselected My Pull Requests row",
                () => list.FindAllDescendants(cf => cf.ByControlType(ControlType.ListItem))
                    .Where(IsVisible)
                    .Skip(1)
                    .FirstOrDefault()
                    ?? list.FindAllDescendants(cf => cf.ByControlType(ControlType.ListItem)).FirstOrDefault(IsVisible),
                TimeSpan.FromSeconds(10));
            AutomationElement stableRowIdentity = WaitForElement(
                "stable My Pull Requests row identity",
                () => pullRequestToSelect.FindAllDescendants()
                    .FirstOrDefault(element => IsVisible(element) && GetAutomationId(element).StartsWith("MyWorkItem_", StringComparison.Ordinal)),
                TimeSpan.FromSeconds(5));
            AssertProbe(
                !string.IsNullOrWhiteSpace(GetElementName(stableRowIdentity)),
                "My Pull Requests row did not expose a stable accessible name.");

            Mouse.MoveTo(CenterPoint(pullRequestToSelect, window));
            Thread.Sleep(300);
            CaptureWindow(window, Path.Combine(options.OutputDirectory, "my-pull-requests-page-row-hover.png"));
            pullRequestToSelect.Click();
            WaitForElement(
                "MyPullRequestsDetailTitle",
                () => FindCurrentVisibleByAutomationId(window, "MyPullRequestsDetailTitle"),
                TimeSpan.FromSeconds(10));
            ExerciseMyWorkItemMarkdownCopy(
                window,
                automation,
                "MarkdownHost_Conversation_MyPullRequestsBody",
                "This preview item represents",
                "My Pull Requests body");

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
                string viewportLabel = GetResponsiveViewportLabel(width, height, resizedBounds);
                AutomationElement workspace = WaitForElement(
                    $"MyPullRequestsAdaptiveWorkspace at {viewportLabel}",
                    () => FindCurrentVisibleByAutomationId(window, "MyPullRequestsAdaptiveWorkspace"),
                    TimeSpan.FromSeconds(8));
                AssertProbe(IsVisible(workspace), $"My Pull Requests workspace was not visible at {viewportLabel}.");
                CaptureWindow(window, Path.Combine(options.OutputDirectory, $"my-pull-requests-page-{viewportLabel}.png"));
            }

            ResizeWindow(window, 1180, 800);
            AutomationElement commitsSection = WaitForElement(
                "MyPullRequestsSection_Commits",
                () => FindCurrentVisibleByAutomationId(window, "MyPullRequestsSection_Commits"),
                TimeSpan.FromSeconds(5));
            InvokeOrClick(commitsSection);
            WaitForElement(
                "MyPullRequestsCommitsList",
                () =>
                {
                    return FindCurrentVisibleByAutomationId(window, "MyPullRequestsCommitsList");
                },
                TimeSpan.FromSeconds(8));
            commitsSection.Focus();
            Keyboard.Press(VirtualKeyShort.RIGHT);
            WaitForElement(
                "MyPullRequestsReviewsList after keyboard section traversal",
                () =>
                {
                    return FindCurrentVisibleByAutomationId(window, "MyPullRequestsReviewsList");
                },
                TimeSpan.FromSeconds(8));

            ResizeWindow(window, 760, 650);
            AssertProbe(
                !IsVisible(FindCurrentVisibleByAutomationId(window, "MyPullRequestsInspector")),
                "My Pull Requests inspector stayed inline in compact layout.");
            ExerciseAdaptiveWorkspaceDrawers(
                window,
                "MyPullRequests",
                "MyPullRequestsList",
                "MyPullRequestsInspector",
                requireAlignedCloseControls: false);
            CaptureWindow(window, Path.Combine(options.OutputDirectory, "my-pull-requests-page-keyboard-and-drawers.png"));
        }
        finally
        {
            if (!isAttached)
            {
                TryClose(app);
                KillExistingApplicationInstances(options.AppPath);
            }
        }

        if (!isAttached)
        {
            RunMyPullRequestsPseudoLongLabelsProbe(options);
        }
    }
}
