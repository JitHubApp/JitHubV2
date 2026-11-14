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
using static JitHub.WinUI.Automation.Screenshots.WindowCapture;
using static JitHub.WinUI.Automation.Windows.ReadyWindows;
using static JitHub.WinUI.Automation.Windows.WindowSizing;
using static JitHub.WinUI.Automation.Workspaces.AdaptiveWorkspaceDrawers;
using static JitHub.WinUI.Automation.Workspaces.WorkItemListSelection;
using static JitHub.WinUI.Automation.Workspaces.WorkItemMarkdown;

namespace JitHub.WinUI.Automation.Probes.Issues;

/// <summary>
/// Drives the repo-issues-page probe: permissions, compact action overflow, filters and the comment editor.
/// </summary>
internal static class RepoIssuesPageProbe
{
    // This method stays whole because it is one repo-issues session that moves from 1600px hover, selection, Markdown,
    // permission, scroll and filter checks to compact drawer checks at 760px and 640px, and each layout assertion
    // depends on the selected issue and the width left by the step before.
    internal static void RunRepoIssuesPageProbe(CaptureOptions options)
    {
        using var app = LaunchApplication(
            options.AppPath,
            "--page=repo-issues",
            "--theme=dark",
            $"--repo={options.RepositoryFullName}");
        using var automation = new UIA3Automation();
        try
        {
            Window window = GetReadyWindow(app, automation, "repo-issues-page probe");
            ResizeWindow(window, 1600, 1000);

            CaptureWindow(window, Path.Combine(options.OutputDirectory, "repo-issues-page-launch.png"));

            AutomationElement list;
            try
            {
                list = WaitForElement(
                    "RepoIssuesList",
                    () => window.FindFirstDescendant(cf => cf.ByAutomationId("RepoIssuesList")),
                    TimeSpan.FromSeconds(18));
            }
            catch
            {
                CaptureWindow(window, Path.Combine(options.OutputDirectory, "repo-issues-page-missing-list.png"));
                PrintVisibleAutomationIds(window, "repo-issues-page");
                throw;
            }
            AutomationElement detailTitle = WaitForElement(
                "RepoIssuesDetailTitle",
                () => window.FindFirstDescendant(cf => cf.ByAutomationId("RepoIssuesDetailTitle")),
                TimeSpan.FromSeconds(18));
            AutomationElement? firstIssue = list.FindFirstDescendant(cf => cf.ByControlType(ControlType.ListItem));
            if (firstIssue is not null)
            {
                Mouse.MoveTo(CenterPoint(firstIssue, window));
                Thread.Sleep(350);
                CaptureWindow(window, Path.Combine(options.OutputDirectory, "repo-issues-page-row-hover.png"));
                firstIssue.Click();
                Thread.Sleep(900);
            }

            AssertProbe(IsVisible(detailTitle), "Repository issue detail title was not visible.");
            AssertMarkdownTextVisible(
                window,
                "This public preview issue demonstrates cached, responsive repository issue navigation.",
                "The cached issue detail stays visible while its discussion refreshes.");
            AssertRepoIssuePermissionSurface(window, automation, options.OutputDirectory);
            CaptureWindow(window, Path.Combine(options.OutputDirectory, "repo-issues-page-wide.png"));
            ExerciseIssueListScrollSelection(window, list, options.OutputDirectory, "repo-issues-page");
            ExerciseRepoIssueFiltersAndCommentEditor(window, options.OutputDirectory);

            ResizeWindow(window, 760, 650);
            WaitForElement(
                "RepoIssuesDetailTitle compact",
                () => window.FindFirstDescendant(cf => cf.ByAutomationId("RepoIssuesDetailTitle")),
                TimeSpan.FromSeconds(8));
            AssertProbe(!IsVisible(window.FindFirstDescendant(cf => cf.ByAutomationId("RepoIssuesInspector"))), "Repository issue inspector stayed visible in compact layout.");
            CaptureWindow(window, Path.Combine(options.OutputDirectory, "repo-issues-page-compact-before-drawers.png"));
            AssertProbe(!IsVisible(window.FindFirstDescendant(cf => cf.ByAutomationId("RepoIssuesList"))), "Repository issue list stayed inline below its coordinated compact breakpoint.");
            AssertRepoIssueCompactActionOverflow(window, automation);
            ExerciseAdaptiveWorkspaceDrawers(
                window,
                "RepoIssues",
                "RepoIssuesList",
                "RepoIssuesInspector");
            ResizeWindow(window, 640, 600);
            WaitForElement(
                "RepoIssuesDetailTitle narrow",
                () => window.FindFirstDescendant(cf => cf.ByAutomationId("RepoIssuesDetailTitle")),
                TimeSpan.FromSeconds(8));
            AssertProbe(!IsVisible(window.FindFirstDescendant(cf => cf.ByAutomationId("RepoIssuesList"))), "Repository issue list stayed inline at 640px.");
            ExerciseAdaptiveWorkspaceDrawers(window, "RepoIssues", "RepoIssuesList", "RepoIssuesInspector");
            CaptureWindow(window, Path.Combine(options.OutputDirectory, "repo-issues-page-compact.png"));
        }
        finally
        {
            TryClose(app);
            KillExistingApplicationInstances(options.AppPath);
        }
    }

    private static void AssertRepoIssuePermissionSurface(Window window, UIA3Automation automation, string outputDirectory)
    {
        bool openedInspectorDrawer = !IsVisible(
            FindCurrentVisibleByAutomationId(window, "RepoIssuesInspectorMetadataButton"));
        if (openedInspectorDrawer)
        {
            AutomationElement inspectorOpener = WaitForElement(
                "repository issue inspector opener",
                () => FindCurrentVisibleByAutomationId(window, "RepoIssuesOpenInspectorPaneButton") ??
                      FindCurrentVisibleByAutomationId(window, "RepoIssuesCompactOpenInspectorPaneButton"),
                TimeSpan.FromSeconds(8));
            InvokeOrClick(inspectorOpener);
            WaitForElement(
                "RepoIssuesInspectorMetadataButton in drawer",
                () => FindCurrentVisibleByAutomationId(window, "RepoIssuesInspectorMetadataButton"),
                TimeSpan.FromSeconds(8));
        }

        string[] actionIds =
        [
            "RepoIssuesNewIssueButton",
            "RepoIssuesEditButton",
            "RepoIssuesToggleStateButton",
            "RepoIssuesInspectorMetadataButton",
            "RepoIssuesOpenCommentButton"
        ];

        foreach (string actionId in actionIds)
        {
            AutomationElement action = WaitForElement(
                actionId,
                () => FindCurrentVisibleByAutomationId(window, actionId),
                TimeSpan.FromSeconds(8));
            AssertProbe(
                !action.IsEnabled,
                $"Repository Issues write action '{actionId}' was enabled for the read-only public preview viewer.");
        }

        if (openedInspectorDrawer)
        {
            AutomationElement closeInspector = WaitForElement(
                "RepoIssuesCloseInspectorPaneButton",
                () => FindCurrentVisibleByAutomationId(window, "RepoIssuesCloseInspectorPaneButton"),
                TimeSpan.FromSeconds(8));
            InvokeOrClick(closeInspector);
            WaitUntil(
                "repository issue inspector drawer to close",
                () => !IsVisible(FindCurrentVisibleByAutomationId(window, "RepoIssuesInspectorMetadataButton")),
                TimeSpan.FromSeconds(8));
        }

        AutomationElement edit = FindCurrentVisibleByAutomationId(window, "RepoIssuesEditButton")!;
        Mouse.MoveTo(CenterPoint(edit, window));
        Thread.Sleep(250);
        CaptureWindow(window, Path.Combine(outputDirectory, "repo-issues-page-action-hover.png"));

        AutomationElement bodyInteractions = WaitForElement(
            "RepoIssuesBodyInteractionBar",
            () => FindCurrentVisibleByAutomationId(window, "RepoIssuesBodyInteractionBar"),
            TimeSpan.FromSeconds(8));
        AssertProbe(
            string.Equals(bodyInteractions.Name, "Issue body actions", StringComparison.Ordinal),
            "Repository Issues body did not expose the shared interaction surface.");
        AssertProbe(
            FindCurrentVisibleByAutomationId(window, "CommentAddReactionButton") is null,
            "Repository Issues exposed an enabled reaction picker for the read-only public preview viewer.");
        Console.WriteLine("repo-issues: public preview exposes issue content while every write capability remains disabled.");
        _ = automation;
    }

    private static void AssertRepoIssueCompactActionOverflow(Window window, UIA3Automation automation)
    {
        AutomationElement overflow = WaitForElement(
            "RepoIssuesCompactActionOverflowButton",
            () => FindCurrentVisibleByAutomationId(window, "RepoIssuesCompactActionOverflowButton"),
            TimeSpan.FromSeconds(8));
        AssertProbe(
            string.Equals(overflow.Name, "More issue actions", StringComparison.Ordinal),
            $"Compact issue overflow exposed the unexpected name '{overflow.Name}'.");

        InvokeOrClick(overflow);
        string[] menuActionIds =
        [
            "RepoIssuesCompactEditAction",
            "RepoIssuesCompactMetadataAction",
            "RepoIssuesCompactToggleStateAction"
        ];
        foreach (string actionId in menuActionIds)
        {
            WaitForElement(
                actionId,
                () => automation.GetDesktop().FindFirstDescendant(cf => cf.ByAutomationId(actionId)),
                TimeSpan.FromSeconds(5));
        }

        Keyboard.Press(VirtualKeyShort.ESCAPE);
        Thread.Sleep(150);
    }

    // This method stays whole because it runs the repository issues toolbar in one pass: open the comment editor and
    // preview Markdown when commenting is allowed, then search, change scope, sort and direction and restore them,
    // toggle Closed and Open, and capture the final filter state.
    private static void ExerciseRepoIssueFiltersAndCommentEditor(Window window, string outputDirectory)
    {
        AutomationElement launcher = WaitForElement(
            "RepoIssuesOpenCommentButton",
            () => window.FindFirstDescendant(cf => cf.ByAutomationId("RepoIssuesOpenCommentButton")),
            TimeSpan.FromSeconds(8));
        if (!launcher.IsEnabled)
        {
            CaptureWindow(window, Path.Combine(outputDirectory, "repo-issues-page-comment-read-only.png"));
        }
        else
        {
            InvokeOrClick(launcher);
            AutomationElement editor = WaitForElement(
                "RepoIssuesCommentBox_Editor",
                () => window.FindFirstDescendant(cf => cf.ByAutomationId("RepoIssuesCommentBox_Editor")),
                TimeSpan.FromSeconds(8));
            AutomationElement mode = WaitForElement(
                "RepoIssuesCommentBox_Mode",
                () => window.FindFirstDescendant(cf => cf.ByAutomationId("RepoIssuesCommentBox_Mode")),
                TimeSpan.FromSeconds(5));
            AutomationElement preview = WaitForElement(
                "Repo Issues comment Preview",
                () => mode.FindFirstDescendant(cf => cf.ByAutomationId("RepoIssuesCommentBox_Mode_Preview")),
                TimeSpan.FromSeconds(5));
            AutomationElement commentButton = WaitForElement(
                "RepoIssuesCommentButton",
                () => window.FindFirstDescendant(cf => cf.ByAutomationId("RepoIssuesCommentButton")),
                TimeSpan.FromSeconds(5));
            AssertProbe(IsVisible(commentButton), "Issue comment command was not visible with the comment editor.");
            AssertProbe(editor.IsEnabled, "Enabled issue comment launcher opened a disabled editor.");
            SetTextBoxText(editor, "**automation comment preview**");
            InvokeOrClick(preview);
            WaitForElement(
                "RepoIssuesCommentBox_Preview",
                () => window.FindFirstDescendant(cf => cf.ByAutomationId("RepoIssuesCommentBox_Preview")),
                TimeSpan.FromSeconds(5));
            CaptureWindow(window, Path.Combine(outputDirectory, "repo-issues-page-comment-preview.png"));
        }

        AutomationElement search = WaitForElement(
            "RepoIssuesSearchBox",
            () => window.FindFirstDescendant(cf => cf.ByAutomationId("RepoIssuesSearchBox")),
            TimeSpan.FromSeconds(5));
        SetTextBoxText(search, "test");
        Thread.Sleep(450);
        SetTextBoxText(search, string.Empty);

        AutomationElement scope = WaitForElement(
            "RepoIssuesScopeComboBox",
            () => window.FindFirstDescendant(cf => cf.ByAutomationId("RepoIssuesScopeComboBox")),
            TimeSpan.FromSeconds(5));
        AutomationElement sort = WaitForElement(
            "RepoIssuesSortComboBox",
            () => window.FindFirstDescendant(cf => cf.ByAutomationId("RepoIssuesSortComboBox")),
            TimeSpan.FromSeconds(5));
        AutomationElement direction = WaitForElement(
            "RepoIssuesDirectionComboBox",
            () => window.FindFirstDescendant(cf => cf.ByAutomationId("RepoIssuesDirectionComboBox")),
            TimeSpan.FromSeconds(5));
        scope.AsComboBox().Select("Mine");
        sort.AsComboBox().Select("Created");
        direction.AsComboBox().Select("Oldest first");
        Thread.Sleep(500);
        scope.AsComboBox().Select("All");
        sort.AsComboBox().Select("Updated");
        direction.AsComboBox().Select("Newest first");

        AutomationElement closed = WaitForElement(
            "RepoIssuesState_Closed",
            () => window.FindFirstDescendant(cf => cf.ByAutomationId("RepoIssuesState_Closed")),
            TimeSpan.FromSeconds(5));
        AutomationElement open = WaitForElement(
            "RepoIssuesState_Open",
            () => window.FindFirstDescendant(cf => cf.ByAutomationId("RepoIssuesState_Open")),
            TimeSpan.FromSeconds(5));
        InvokeOrClick(closed);
        Thread.Sleep(350);
        InvokeOrClick(open);
        Thread.Sleep(450);
        CaptureWindow(window, Path.Combine(outputDirectory, "repo-issues-page-filters.png"));
    }
}
