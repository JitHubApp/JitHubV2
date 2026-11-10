using System.Drawing;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Input;
using FlaUI.UIA3;
using JitHub.WinUI.Automation.Options;
using JitHub.WinUI.Automation.Windows;
using static JitHub.WinUI.Automation.Applications.ApplicationProcessCleanup;
using static JitHub.WinUI.Automation.Applications.ProbeApplicationLauncher;
using static JitHub.WinUI.Automation.Conditions.ProbeConditions;
using static JitHub.WinUI.Automation.Elements.ElementGeometry;
using static JitHub.WinUI.Automation.Elements.ElementQueries;
using static JitHub.WinUI.Automation.Interaction.ElementActions;
using static JitHub.WinUI.Automation.Probes.Commits.CommitDiffNavigation;
using static JitHub.WinUI.Automation.Screenshots.WindowCapture;
using static JitHub.WinUI.Automation.Windows.ReadyWindows;
using static JitHub.WinUI.Automation.Windows.WindowSizing;
using static JitHub.WinUI.Automation.Workspaces.AdaptivePaneButtons;
using static JitHub.WinUI.Automation.Workspaces.AdaptiveWorkspaceDrawers;
using static JitHub.WinUI.Automation.Workspaces.WorkItemListSelection;

namespace JitHub.WinUI.Automation.Probes.Commits;

/// <summary>
/// Drives the commits-responsive-workspace probe.
/// </summary>
internal static class CommitsResponsiveWorkspaceProbe
{
    // This method stays whole because it runs one repo-commits instance through five window sizes, checking the inline
    // inspector, diff viewer contracts and adaptive drawers, then returns to 1366px for list scroll selection and the
    // Comments section, so every check sees the layout the prior resize produced.
    internal static void RunCommitsResponsiveWorkspaceProbe(CaptureOptions options)
    {
        (int Width, int Height)[] sizes =
        [
            (1366, 900),
            (1180, 800),
            (900, 700),
            (760, 650),
            (640, 600)
        ];

        KillExistingApplicationInstances(options.AppPath);
        using var app = LaunchApplication(
            options.AppPath,
            "--page=repo-commits",
            "--theme=dark",
            $"--repo={options.RepositoryFullName}");
        using var automation = new UIA3Automation();
        try
        {
            Window window = GetReadyWindow(app, automation, "commits-responsive-workspace probe");
            foreach ((int width, int height) in sizes)
            {
                Rectangle resizedBounds = ResizeWindow(window, width, height);
                int actualWidth = resizedBounds.Width;
                string viewportLabel = GetResponsiveViewportLabel(width, height, resizedBounds);
                try
                {
                    WaitForElement(
                        "RepoCommitsAdaptiveWorkspace",
                        () => window.FindFirstDescendant(cf => cf.ByAutomationId("RepoCommitsAdaptiveWorkspace")),
                        TimeSpan.FromSeconds(14));
                    EnsureCommitDetailVisible(window);
                }
                catch
                {
                    string failurePath = Path.Combine(options.OutputDirectory, $"commits-launch-timeout-{viewportLabel}.png");
                    CaptureWindow(window, failurePath);
                    PrintVisibleAutomationIds(window, "commits-responsive-workspace");
                    Console.WriteLine($"commits-responsive-workspace timeout screenshot={failurePath}");
                    throw;
                }

                AssertProbe(
                    IsVisible(window.FindFirstDescendant(cf => cf.ByAutomationId("RepoCommitsDetailTitle"))),
                    $"commits: primary detail was not visible at {viewportLabel}.");
                if (actualWidth >= AutomationResponsiveLayout.ShellRailCollapseWidth)
                {
                    AutomationElement? workspace = window.FindFirstDescendant(
                        cf => cf.ByAutomationId("RepoCommitsAdaptiveWorkspace"));
                    AutomationElement? inspector = window.FindFirstDescendant(
                        cf => cf.ByAutomationId("RepoCommitsInspectorHost"));
                    AutomationElement? inspectorButton = window.FindFirstDescendant(
                        cf => cf.ByAutomationId("RepoCommitsOpenInspectorPaneButton"));
                    Console.WriteLine(
                        $"commits-responsive-layout window={viewportLabel}; " +
                        $"workspace={workspace?.BoundingRectangle}; " +
                        $"inspector={inspector?.BoundingRectangle}; " +
                        $"inspector-visible={IsVisible(inspector)}; " +
                        $"opener={inspectorButton?.BoundingRectangle}; " +
                        $"opener-visible={IsVisible(inspectorButton)}");
                    AssertProbe(
                        IsVisible(inspector),
                        "commits: inspector was not inline at the normal 1366px wide desktop size.");
                    AssertProbe(
                        !IsVisible(inspectorButton),
                        "commits: inspector drawer opener remained visible while the inspector should be inline.");
                }
                bool isDiffViewerVisible = IsVisible(window.FindFirstDescendant(cf => cf.ByAutomationId("RepoCommitsDiffViewer"))) ||
                    IsCommitDiffTextVisible(window);
                if (!isDiffViewerVisible)
                {
                    string failurePath = Path.Combine(options.OutputDirectory, $"commits-diff-missing-{viewportLabel}.png");
                    CaptureWindow(window, failurePath);
                    PrintVisibleAutomationIds(window, "commits-diff-missing");
                    Console.WriteLine($"commits-responsive-workspace diff missing screenshot={failurePath}");
                }
                AssertProbe(isDiffViewerVisible, $"commits: diff viewer was not visible at {viewportLabel}.");
                AssertCommitDiffViewerContracts(window, $"commits-responsive-workspace {viewportLabel}");
                AssertProbe(
                    window.FindFirstDescendant(cf => cf.ByAutomationId("WorkspaceTabs")) is null,
                    "Commits exposed workspace tabs.");

                AutomationElement? leadingButton = FindAdaptivePaneButton(window, "RepoCommits", leading: true);
                if (IsVisible(leadingButton))
                {
                    ExerciseAdaptiveWorkspaceDrawers(window, "RepoCommits", "RepoCommitsList", "RepoCommitsInspectorHost");
                }
                else
                {
                    AssertProbe(
                        IsVisible(window.FindFirstDescendant(cf => cf.ByAutomationId("RepoCommitsList"))),
                        $"commits: leading list was not inline at {viewportLabel}.");
                }

                CaptureWindow(window, Path.Combine(options.OutputDirectory, $"commits-adaptive-{viewportLabel}.png"));
            }

            ResizeWindow(window, 1366, 900);
            Thread.Sleep(350);
            EnsureCommitDetailVisible(window);
            AutomationElement list = WaitForElement(
                "RepoCommitsList",
                () => window.FindFirstDescendant(cf => cf.ByAutomationId("RepoCommitsList")),
                TimeSpan.FromSeconds(8));
            ExerciseIssueListScrollSelection(window, list, options.OutputDirectory, "commits-page");

            AutomationElement commentsSection = WaitForElement(
                "RepoCommitsSection_Comments",
                () => window.FindFirstDescendant(cf => cf.ByAutomationId("RepoCommitsSection_Comments")),
                TimeSpan.FromSeconds(8));
            InvokeOrClick(commentsSection);
            try
            {
                AutomationElement commentButton = WaitForElement(
                    "RepoCommitsCommentButton",
                    () => window.FindFirstDescendant(cf => cf.ByAutomationId("RepoCommitsCommentButton")),
                    TimeSpan.FromSeconds(8));
                AutomationElement commentsViewport = WaitForElement(
                    "RepoCommitsCommentsViewport",
                    () => window.FindFirstDescendant(cf => cf.ByAutomationId("RepoCommitsCommentsViewport")),
                    TimeSpan.FromSeconds(8));
                for (int attempt = 0; attempt < 6 && !IsVisible(commentButton); attempt++)
                {
                    Mouse.MoveTo(CenterPoint(commentsViewport, window));
                    Mouse.Scroll(-5);
                    Thread.Sleep(180);
                }
                AssertProbe(
                    IsVisible(commentButton),
                    "Commit comment action could not be reached in the comments viewport.");
            }
            catch
            {
                CaptureWindow(
                    window,
                    Path.Combine(options.OutputDirectory, "commits-section-comments-missing.png"));
                throw;
            }
            CaptureWindow(window, Path.Combine(options.OutputDirectory, "commits-section-comments.png"));
        }
        finally
        {
            TryClose(app);
            KillExistingApplicationInstances(options.AppPath);
        }
    }

    private static bool IsCommitDiffTextVisible(Window window)
    {
        return window
            .FindAllDescendants(cf => cf.ByControlType(ControlType.Text))
            .Where(IsVisible)
            .Select(GetElementName)
            .Any(text =>
                text.Contains("@@", StringComparison.Ordinal) ||
                text.Contains("texture_mtl.mm", StringComparison.OrdinalIgnoreCase) ||
                text.Contains("_texture", StringComparison.OrdinalIgnoreCase));
    }
}
