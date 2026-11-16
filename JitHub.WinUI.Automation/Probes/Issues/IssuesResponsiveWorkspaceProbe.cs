using System.Drawing;
using FlaUI.Core.AutomationElements;
using FlaUI.UIA3;
using JitHub.WinUI.Automation.Options;
using JitHub.WinUI.Automation.Windows;
using static JitHub.WinUI.Automation.Applications.ApplicationProcessCleanup;
using static JitHub.WinUI.Automation.Applications.ProbeApplicationLauncher;
using static JitHub.WinUI.Automation.Conditions.ProbeConditions;
using static JitHub.WinUI.Automation.Elements.ElementGeometry;
using static JitHub.WinUI.Automation.Elements.ElementQueries;
using static JitHub.WinUI.Automation.Screenshots.WindowCapture;
using static JitHub.WinUI.Automation.Windows.ReadyWindows;
using static JitHub.WinUI.Automation.Windows.WindowSizing;
using static JitHub.WinUI.Automation.Workspaces.AdaptivePaneButtons;
using static JitHub.WinUI.Automation.Workspaces.AdaptiveWorkspaceDrawers;
using static JitHub.WinUI.Automation.Workspaces.WorkItemListSelection;

namespace JitHub.WinUI.Automation.Probes.Issues;

/// <summary>
/// Drives the issues-responsive-workspace probe.
/// </summary>
internal static class IssuesResponsiveWorkspaceProbe
{
    // This method stays whole because, for both My Issues and Repo Issues, the narrowing width sweep compares each
    // size's rail, list and inspector visibility with the values from the previous size to enforce collapse priority,
    // so those carried-over flags and the drawer checks share one loop.
    internal static void RunIssuesResponsiveWorkspaceProbe(CaptureOptions options)
    {
        (string Page, string Prefix, string ListId, string DetailId, string InspectorId)[] targets =
        [
            ("my-issues", "MyIssues", "MyIssuesList", "MyIssuesDetailTitle", "MyIssuesInspectorTitle"),
            ("repo-issues", "RepoIssues", "RepoIssuesList", "RepoIssuesDetailTitle", "RepoIssuesInspectorTitle")
        ];

        (int Width, int Height)[] sizes =
        [
            (1600, 1000),
            (1366, 900),
            (1180, 800),
            (900, 700),
            (760, 650),
            (640, 600)
        ];
        foreach ((string page, string prefix, string listId, string detailId, string inspectorId) in targets)
        {
            using var app = page == "repo-issues"
                ? LaunchApplication(options.AppPath, $"--page={page}", "--theme=dark", $"--repo={options.RepositoryFullName}")
                : LaunchApplication(options.AppPath, $"--page={page}", "--theme=dark");
            using var automation = new UIA3Automation();
            try
            {
                Window window = GetReadyWindow(app, automation, $"issues-responsive-workspace {page}");
                bool? previousRailInline = null;
                bool? previousLeadingInline = null;
                bool? previousTrailingInline = null;
                foreach ((int width, int height) in sizes)
                {
                    Rectangle resizedBounds = ResizeWindow(window, width, height);
                    int actualWidth = resizedBounds.Width;
                    string viewportLabel = GetResponsiveViewportLabel(width, height, resizedBounds);
                    WaitForElement(detailId, () => window.FindFirstDescendant(cf => cf.ByAutomationId(detailId)), TimeSpan.FromSeconds(12));
                    AssertProbe(IsVisible(window.FindFirstDescendant(cf => cf.ByAutomationId(detailId))), $"{page}: primary detail was not visible at {viewportLabel}.");

                    bool railInline = IsVisible(window.FindFirstDescendant(cf => cf.ByAutomationId("ShellNav_home")));
                    bool leadingInline = IsVisible(window.FindFirstDescendant(cf => cf.ByAutomationId(listId)));
                    bool trailingInline = window.FindAllDescendants(cf => cf.ByAutomationId(inspectorId)).Any(IsVisible);
                    AssertProbe(
                        previousRailInline is not false || !railInline,
                        $"{page}: narrowing to {viewportLabel} made the shell rail reappear.");
                    AssertProbe(
                        previousLeadingInline is not false || !leadingInline,
                        $"{page}: narrowing to {viewportLabel} made the leading pane reappear.");
                    AssertProbe(
                        previousTrailingInline is not false || !trailingInline,
                        $"{page}: narrowing to {viewportLabel} made the inspector reappear.");
                    AssertProbe(
                        !trailingInline || (railInline && leadingInline),
                        $"{page}: inspector remained inline after an earlier-priority pane collapsed at {viewportLabel}.");
                    AssertProbe(
                        leadingInline || !railInline,
                        $"{page}: leading pane collapsed before the app rail at {viewportLabel}.");
                    previousRailInline = railInline;
                    previousLeadingInline = leadingInline;
                    previousTrailingInline = trailingInline;

                    if (actualWidth >= AutomationResponsiveLayout.ShellRailCollapseWidth && leadingInline)
                    {
                        AutomationElement inlineList = WaitForElement(
                            listId,
                            () => FindCurrentVisibleByAutomationId(window, listId),
                            TimeSpan.FromSeconds(8));
                        ExerciseIssueListScrollSelection(
                            window,
                            inlineList,
                            options.OutputDirectory,
                            $"{page}-responsive");
                    }

                    AutomationElement? leadingButton = FindAdaptivePaneButton(window, prefix, leading: true);
                    if (IsVisible(leadingButton))
                    {
                        ExerciseAdaptiveWorkspaceDrawers(window, prefix, listId, inspectorId);
                    }

                    if (actualWidth is >= 756 and <= 764)
                    {
                        AutomationElement detail = WaitForElement(
                            detailId,
                            () => FindCurrentVisibleByAutomationId(window, detailId),
                            TimeSpan.FromSeconds(8));
                        AssertProbe(IsInsideWindowBounds(detail, window), $"{page}: detail title was clipped at 760px.");
                        AssertProbe(
                            detail.BoundingRectangle.Width >= 180,
                            $"{page}: detail title retained only {detail.BoundingRectangle.Width:0.0}px at 760px.");

                        string primaryActionId = page == "repo-issues"
                            ? "RepoIssuesCompactActionOverflowButton"
                            : "MyIssuesOpenInRepositoryButton";
                        AutomationElement? primaryAction = FindCurrentVisibleByAutomationId(window, primaryActionId);
                        if (IsVisible(primaryAction))
                        {
                            AssertProbe(IsInsideWindowBounds(primaryAction!, window), $"{page}: primary detail action was clipped at 760px.");
                        }
                    }
                    else
                    {
                        if (leadingInline)
                        {
                            AssertProbe(
                                IsVisible(window.FindFirstDescendant(cf => cf.ByAutomationId(listId))),
                                $"{page}: leading list was not inline at {viewportLabel}.");
                        }

                        if (!IsVisible(leadingButton))
                        {
                            AutomationElement? trailingButton = FindAdaptivePaneButton(window, prefix, leading: false);
                            if (IsVisible(trailingButton))
                            {
                                ExerciseAdaptiveWorkspaceDrawers(
                                    window,
                                    prefix,
                                    listId,
                                    inspectorId,
                                    exerciseLeading: false);
                            }
                        }
                    }

                    CaptureWindow(window, Path.Combine(options.OutputDirectory, $"{page}-adaptive-{viewportLabel}.png"));
                }
            }
            finally
            {
                TryClose(app);
                KillExistingApplicationInstances(options.AppPath);
            }
        }
    }
}
