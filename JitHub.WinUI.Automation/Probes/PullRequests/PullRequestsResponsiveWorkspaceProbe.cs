using System.Drawing;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.UIA3;
using JitHub.WinUI.Automation.Options;
using static JitHub.WinUI.Automation.Applications.ApplicationProcessCleanup;
using static JitHub.WinUI.Automation.Applications.ProbeApplicationLauncher;
using static JitHub.WinUI.Automation.Conditions.ProbeConditions;
using static JitHub.WinUI.Automation.Elements.ElementQueries;
using static JitHub.WinUI.Automation.Interaction.ElementActions;
using static JitHub.WinUI.Automation.Screenshots.WindowCapture;
using static JitHub.WinUI.Automation.Windows.ReadyWindows;
using static JitHub.WinUI.Automation.Windows.WindowSizing;
using static JitHub.WinUI.Automation.Workspaces.AdaptivePaneButtons;
using static JitHub.WinUI.Automation.Workspaces.AdaptiveWorkspaceDrawers;
using static JitHub.WinUI.Automation.Workspaces.WorkItemListSelection;

namespace JitHub.WinUI.Automation.Probes.PullRequests;

/// <summary>
/// Drives the pull-requests-responsive-workspace probe, including the shy header section.
/// </summary>
internal static class PullRequestsResponsiveWorkspaceProbe
{
    // This method stays whole because it runs one pull request workspace through each snap width, checking the detail
    // header, compact commands and list drawers, then returns to wide layout for scroll selection and walks every shy-
    // header section, all on the same selected pull request.
    internal static void RunPullRequestsResponsiveWorkspaceProbe(CaptureOptions options)
    {
        (int Width, int Height)[] sizes =
        [
            (1366, 900),
            (1180, 800),
            (900, 700),
            (760, 650),
            (640, 600)
        ];

        using var app = LaunchApplication(
            options.AppPath,
            "--page=repo-pulls",
            "--scenario=pr-shy-header",
            "--theme=dark",
            $"--repo={options.RepositoryFullName}");
        using var automation = new UIA3Automation();
        try
        {
            Window window = GetReadyWindow(app, automation, "pull-requests-responsive-workspace probe");
            foreach ((int width, int height) in sizes)
            {
                Rectangle resizedBounds = ResizeWindow(window, width, height);
                string viewportLabel = GetResponsiveViewportLabel(width, height, resizedBounds);
                try
                {
                    WaitForElement(
                        "RepoPullRequestsDetailTitle",
                        () => window.FindFirstDescendant(cf => cf.ByAutomationId("RepoPullRequestsDetailTitle")),
                        TimeSpan.FromSeconds(14));
                }
                catch
                {
                    string failurePath = Path.Combine(options.OutputDirectory, $"pull-requests-launch-timeout-{viewportLabel}.png");
                    CaptureWindow(window, failurePath);
                    string[] visibleTexts = window
                        .FindAllDescendants(cf => cf.ByControlType(ControlType.Text))
                        .Where(IsVisible)
                        .Select(GetElementName)
                        .Where(name => !string.IsNullOrWhiteSpace(name))
                        .Take(40)
                        .ToArray();
                    Console.WriteLine($"pull-requests-responsive-workspace timeout screenshot={failurePath}");
                    Console.WriteLine($"visible text: {string.Join(" | ", visibleTexts)}");
                    throw;
                }
                AssertProbe(
                    IsVisible(window.FindFirstDescendant(cf => cf.ByAutomationId("RepoPullRequestsDetailTitle"))),
                    $"pull-requests: primary detail was not visible at {viewportLabel}.");
                AssertProbe(
                    window.FindFirstDescendant(cf => cf.ByAutomationId("WorkspaceTabs")) is null,
                    "Pull requests exposed workspace tabs.");

                bool compactWorkspace = IsVisible(FindCurrentVisibleByAutomationId(
                    window,
                    "RepoPullRequestsSectionComboBox"));
                AssertProbe(
                    IsVisible(FindCurrentVisibleByAutomationId(
                        window,
                        compactWorkspace ? "RepoPullRequestsSectionComboBox" : "RepoPullRequestsSectionSegmented")),
                    $"pull-requests: expected {(compactWorkspace ? "compact" : "expanded")} detail header was not visible at {viewportLabel}.");

                AutomationElement? compactRepositoryCommands =
                    FindCurrentVisibleByAutomationId(window, "RepoDetailCompactCommandsButton");
                if (IsVisible(compactRepositoryCommands))
                {
                    AssertProbe(
                        IsVisible(FindCurrentVisibleByAutomationId(window, "RepoDetailIdentity")),
                        $"pull-requests: compact repository identity was not visible at {viewportLabel}.");
                }

                AutomationElement? leadingButton = FindAdaptivePaneButton(window, "RepoPullRequests", leading: true);
                if (IsVisible(leadingButton))
                {
                    ExerciseAdaptiveWorkspaceDrawers(window, "RepoPullRequests", "RepoPullRequestsList", "RepoPullRequestsInspector");
                }
                else
                {
                    AssertProbe(
                        IsVisible(window.FindFirstDescendant(cf => cf.ByAutomationId("RepoPullRequestsList"))),
                        $"pull-requests: leading list was not inline at {viewportLabel}.");
                }

                CaptureWindow(window, Path.Combine(options.OutputDirectory, $"pull-requests-adaptive-{viewportLabel}.png"));
            }

            ResizeWindow(window, 1366, 900);
            Thread.Sleep(350);
            AutomationElement list = WaitForElement(
                "RepoPullRequestsList",
                () => window.FindFirstDescendant(cf => cf.ByAutomationId("RepoPullRequestsList")),
                TimeSpan.FromSeconds(8));
            ExerciseIssueListScrollSelection(window, list, options.OutputDirectory, "pull-requests-page");

            (string SelectorId, string ContentId, string Slug)[] sections =
            [
                ("RepoPullRequestsSection_Conversation", "RepoPullRequestsCommentsList", "conversation"),
                ("RepoPullRequestsSection_Files", "CommitDiffViewerRowsScrollViewer", "files"),
                ("RepoPullRequestsSection_Commits", "RepoPullRequestsCommitsList", "commits"),
                ("RepoPullRequestsSection_Reviews", "RepoPullRequestsReviewsList", "reviews"),
                ("RepoPullRequestsSection_Timeline", "RepoPullRequestsTimelineList", "timeline")
            ];
            foreach ((string selectorId, string contentId, string slug) in sections)
            {
                ExercisePullRequestShyHeaderSection(
                    window,
                    selectorId,
                    contentId,
                    slug,
                    options.OutputDirectory);
            }
        }
        finally
        {
            TryClose(app);
            KillExistingApplicationInstances(options.AppPath);
        }
    }

    private static void ExercisePullRequestShyHeaderSection(
        Window window,
        string selectorAutomationId,
        string contentAutomationId,
        string slug,
        string outputDirectory)
    {
        AutomationElement selector = WaitForElement(
            selectorAutomationId,
            () => FindCurrentVisibleByAutomationId(window, selectorAutomationId),
            TimeSpan.FromSeconds(8));
        InvokeOrClick(selector);

        AutomationElement scrollHost = WaitForElement(
            contentAutomationId,
            () => FindCurrentVisibleByAutomationId(window, contentAutomationId),
            TimeSpan.FromSeconds(12));
        WaitUntil(
            $"{slug} section vertical scroll contract",
            () => scrollHost.Patterns.Scroll.IsSupported &&
                scrollHost.Patterns.Scroll.Pattern.VerticallyScrollable.ValueOrDefault,
            TimeSpan.FromSeconds(12));

        var scroll = scrollHost.Patterns.Scroll.Pattern;
        scroll.SetScrollPercent(FlaUI.Core.Patterns.ScrollPatternConstants.NoScroll, 0);
        WaitForPullRequestHeaderState(window, shy: false, $"{slug} section top restoration");
        Thread.Sleep(300);
        CaptureWindow(window, Path.Combine(outputDirectory, $"pull-requests-section-{slug}.png"));

        scroll.SetScrollPercent(FlaUI.Core.Patterns.ScrollPatternConstants.NoScroll, 85);
        WaitForPullRequestHeaderState(window, shy: true, $"{slug} section shy header");
        Thread.Sleep(300);
        CaptureWindow(window, Path.Combine(outputDirectory, $"pull-requests-section-{slug}-shy.png"));

        scroll.SetScrollPercent(FlaUI.Core.Patterns.ScrollPatternConstants.NoScroll, 45);
        WaitForPullRequestHeaderState(window, shy: false, $"{slug} section upward reveal");
        AssertProbe(
            scroll.VerticalScrollPercent.ValueOrDefault > 1,
            $"{slug} section only restored its expanded header at the top instead of after upward travel.");
        Thread.Sleep(300);
        CaptureWindow(window, Path.Combine(outputDirectory, $"pull-requests-section-{slug}-revealed.png"));

        scroll.SetScrollPercent(FlaUI.Core.Patterns.ScrollPatternConstants.NoScroll, 65);
        WaitForPullRequestHeaderState(window, shy: true, $"{slug} section downward re-hide");
        Thread.Sleep(300);
        CaptureWindow(window, Path.Combine(outputDirectory, $"pull-requests-section-{slug}-rehidden.png"));

        scroll.SetScrollPercent(FlaUI.Core.Patterns.ScrollPatternConstants.NoScroll, 0);
        WaitForPullRequestHeaderState(window, shy: false, $"{slug} section final top restoration");
    }

    private static void WaitForPullRequestHeaderState(Window window, bool shy, string context)
    {
        string visibleHeaderId = shy ? "RepoPullRequestsShySectionComboBox" : "RepoPullRequestsSectionSegmented";
        string hiddenHeaderId = shy ? "RepoPullRequestsSectionSegmented" : "RepoPullRequestsShySectionComboBox";
        WaitUntil(
            context,
            () => IsVisible(FindCurrentVisibleByAutomationId(window, visibleHeaderId)) &&
                !IsVisible(FindCurrentVisibleByAutomationId(window, hiddenHeaderId)),
            TimeSpan.FromSeconds(5));
    }
}
