using System.Drawing;
using System.Runtime.InteropServices;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Input;
using FlaUI.Core.WindowsAPI;
using FlaUI.UIA3;
using JitHub.WinUI.Automation.Native;
using static JitHub.WinUI.Automation.Conditions.ProbeConditions;
using static JitHub.WinUI.Automation.Elements.ElementGeometry;
using static JitHub.WinUI.Automation.Elements.ElementQueries;
using static JitHub.WinUI.Automation.Interaction.ElementActions;
using static JitHub.WinUI.Automation.Interaction.MouseInput;
using static JitHub.WinUI.Automation.Interaction.ScrollActions;
using static JitHub.WinUI.Automation.Probes.Commits.CommitDiffNavigation;
using static JitHub.WinUI.Automation.Probes.RepoCode.RepoCodeNavigation;
using static JitHub.WinUI.Automation.Screenshots.WindowCapture;

namespace JitHub.WinUI.Automation.Probes.Website;

/// <summary>
/// Stages each website showcase surface (navigation, open panes, tooltips and presentation state) before it is captured.
/// </summary>
internal static class WebsiteShowcaseSurfaces
{
    // This method stays whole because it is the single dispatch table that maps each website showcase surface id to its
    // staging steps, such as scrolling Home, opening the pull request conversation, selecting code or CSV fixtures,
    // showing the commit diff, or opening the Gist editor, so every capture id is listed in one place.
    internal static void PrepareWebsiteShowcaseSurface(Window window, string id, string outputDirectory)
    {
        switch (id)
        {
            case "home-workspace":
            {
                AutomationElement scrollHost = WaitForElement(
                    "website-showcase Home scroll host",
                    () => FindCurrentVisibleByAutomationId(window, "DashboardMainRailScrollViewer"),
                    TimeSpan.FromSeconds(12));
                if (scrollHost.Patterns.Scroll.IsSupported)
                {
                    scrollHost.Patterns.Scroll.Pattern.SetScrollPercent(
                        FlaUI.Core.Patterns.ScrollPatternConstants.NoScroll,
                        0);
                }
                WaitForElement(
                    "website-showcase expanded Home header",
                    () => FindCurrentVisibleByAutomationId(window, "DashboardCustomizeButton"),
                    TimeSpan.FromSeconds(8));
                WaitForElement(
                    "website-showcase Home overview affordance",
                    () => FindCurrentVisibleByAutomationId(window, "DashboardSideRail") ??
                        FindCurrentVisibleByAutomationId(window, "DashboardOverviewDrawerButton"),
                    TimeSpan.FromSeconds(12));
                break;
            }
            case "pull-request-conversation":
            {
                EnsureWebsitePullRequestDetailVisible(window);
                AutomationElement conversation = WaitForElement(
                    "website-showcase pull request conversation",
                    () => FindCurrentVisibleByAutomationId(window, "RepoPullRequestsCommentsList"),
                    TimeSpan.FromSeconds(18));
                if (conversation.Patterns.Scroll.IsSupported)
                {
                    conversation.Patterns.Scroll.Pattern.SetScrollPercent(
                        FlaUI.Core.Patterns.ScrollPatternConstants.NoScroll,
                        10);
                }
                CloseVisiblePane(window, "RepoPullRequestsCloseInspectorPaneButton");
                break;
            }
            case "code-editor":
                ExpandWebsiteRepoCodeFolder(window, "src");
                SelectRepoCodeFixtureFile(window, "src/App.cs", "App.cs, file");
                WaitForElement(
                    "website-showcase native code editor",
                    () => window.FindAllDescendants(cf => cf.ByAutomationId("RepoCodeEditor"))
                        .FirstOrDefault(element => IsVisible(element) && element.Patterns.Value.IsSupported),
                    TimeSpan.FromSeconds(18));
                break;
            case "csv-table":
                SelectRepoCodeFixtureFile(window, "data.csv", "data.csv, file");
                WaitForElement(
                    "website-showcase rich CSV table",
                    () => window.FindAllDescendants()
                        .FirstOrDefault(element =>
                            IsVisible(element) &&
                            (string.Equals(GetAutomationId(element), "CsvPreviewDataGrid", StringComparison.Ordinal) ||
                             string.Equals(GetAutomationId(element), "CsvPreviewDataTable", StringComparison.Ordinal)) &&
                            element.Patterns.Grid.IsSupported &&
                            element.Patterns.Grid.Pattern.RowCount.Value == 7),
                    TimeSpan.FromSeconds(18));
                break;
            case "commit-diff":
            {
                EnsureCommitDetailVisible(window);
                AutomationElement diffSelector = WaitForElement(
                    "website-showcase commit Diff section",
                    () => FindCurrentVisibleByAutomationId(window, "RepoCommitsSection_Diff") ??
                        FindCurrentVisibleByAutomationId(window, "RepoCommitsShySection_Diff"),
                    TimeSpan.FromSeconds(10));
                if (diffSelector.Patterns.SelectionItem.IsSupported && !diffSelector.Patterns.SelectionItem.Pattern.IsSelected.Value)
                {
                    diffSelector.Patterns.SelectionItem.Pattern.Select();
                }
                AutomationElement diffRows = WaitForElement(
                    "website-showcase commit diff rows",
                    () => FindCurrentVisibleByAutomationId(window, "CommitDiffViewerRowsScrollViewer"),
                    TimeSpan.FromSeconds(18));
                WaitUntil(
                    "website-showcase rendered commit diff text",
                    () => FindVisibleDiffTextElements(diffRows).Length >= 2,
                    TimeSpan.FromSeconds(18));
                if (diffRows.Patterns.Scroll.IsSupported)
                {
                    diffRows.Patterns.Scroll.Pattern.SetScrollPercent(
                        FlaUI.Core.Patterns.ScrollPatternConstants.NoScroll,
                        0);
                }
                CloseVisiblePane(window, "RepoCommitsCloseInspectorPaneButton");
                break;
            }
            case "stars-library":
            {
                AutomationElement list = WaitForElement(
                    "website-showcase Stars list",
                    () => FindCurrentVisibleByAutomationId(window, "StarsList"),
                    TimeSpan.FromSeconds(15));
                WaitForElement(
                    "website-showcase first Star",
                    () => list.FindFirstDescendant(cf => cf.ByControlType(ControlType.ListItem)),
                    TimeSpan.FromSeconds(12));
                break;
            }
            case "gists-editor":
            {
                if (!IsVisible(FindCurrentVisibleByAutomationId(window, "GistsDetailTitle")))
                {
                    AutomationElement? list = FindCurrentVisibleByAutomationId(window, "GistsList");
                    if (list is null)
                    {
                        AutomationElement libraryButton = WaitForElement(
                            "website-showcase Gists library button",
                            () => FindCurrentVisibleByAutomationId(window, "GistsLeadingPaneButton"),
                            TimeSpan.FromSeconds(10));
                        InvokeOrClick(libraryButton);
                        list = WaitForElement(
                            "website-showcase Gists list drawer",
                            () => FindCurrentVisibleByAutomationId(window, "GistsList"),
                            TimeSpan.FromSeconds(10));
                    }

                    AutomationElement firstGist = WaitForElement(
                        "website-showcase first Gist",
                        () => list.FindFirstDescendant(cf => cf.ByControlType(ControlType.ListItem)),
                        TimeSpan.FromSeconds(12));
                    InvokeOrClick(firstGist);
                    WaitForElement(
                        "website-showcase Gist detail",
                        () => FindCurrentVisibleByAutomationId(window, "GistsDetailTitle"),
                        TimeSpan.FromSeconds(12));
                }

                AutomationElement editButton = WaitForElement(
                    "website-showcase Edit Gist button",
                    () => FindCurrentVisibleByAutomationId(window, "GistsEdit"),
                    TimeSpan.FromSeconds(10));
                InvokeOrClick(editButton);
                AutomationElement editorDialog = WaitForElement(
                    "website-showcase Gist editor dialog",
                    () => FindCurrentVisibleByAutomationId(window, "GistEditorDialog"),
                    TimeSpan.FromSeconds(12));
                AutomationElement contentEditor = WaitForElement(
                    "website-showcase Gist content editor",
                    () => editorDialog.FindFirstDescendant(cf => cf.ByAutomationId("GistEditorContent")),
                    TimeSpan.FromSeconds(10));
                bool contentEditorVisible = WaitUntilAvailable(
                    () => FindCurrentVisibleByAutomationId(window, "GistEditorDialog") is { } liveDialog &&
                        liveDialog.FindFirstDescendant(cf => cf.ByAutomationId("GistEditorContent")) is { } liveEditor &&
                        IsVisible(liveEditor) &&
                        liveEditor.Patterns.Value.IsSupported &&
                        (liveEditor.Patterns.Value.Pattern.Value.Value ?? string.Empty).Contains("# Release checklist", StringComparison.Ordinal) &&
                        Rectangle.Intersect(GetClippedAutomationBounds(liveEditor), window.BoundingRectangle) is { Width: > 240, Height: > 80 },
                    TimeSpan.FromSeconds(6));
                if (!contentEditorVisible)
                {
                    CaptureWindowWithPopups(window, Path.Combine(outputDirectory, "gists-editor-scroll-debug.png"));
                    AutomationElement liveEditor = FindCurrentVisibleByAutomationId(window, "GistEditorDialog")?
                        .FindFirstDescendant(cf => cf.ByAutomationId("GistEditorContent")) ?? contentEditor;
                    throw new InvalidOperationException(
                        "website-showcase could not expose a useful Gist content editor viewport. " +
                        $"Editor={liveEditor.BoundingRectangle}; clipped={GetClippedAutomationBounds(liveEditor)}; " +
                        $"window={window.BoundingRectangle}; ancestors=[{DescribeScrollAncestors(liveEditor)}].");
                }
                break;
            }
            case "profile-overview":
            {
                AutomationElement overview = WaitForElement(
                    "website-showcase Profile overview",
                    () => FindCurrentVisibleByAutomationId(window, "ProfileOverviewScrollViewer"),
                    TimeSpan.FromSeconds(18));
                WaitForElement(
                    "website-showcase Profile identity",
                    () => FindCurrentVisibleByAutomationId(window, "ProfileDisplayName") ??
                        FindCurrentVisibleByAutomationId(window, "ProfileCompactIdentityDetailsButton"),
                    TimeSpan.FromSeconds(12));
                WaitForElement(
                    "website-showcase contribution graph",
                    () => FindCurrentVisibleByAutomationId(window, "ProfileContributionGraph"),
                    TimeSpan.FromSeconds(18));
                if (overview.Patterns.Scroll.IsSupported &&
                    overview.Patterns.Scroll.Pattern.VerticallyScrollable.ValueOrDefault)
                {
                    overview.Patterns.Scroll.Pattern.SetScrollPercent(
                        FlaUI.Core.Patterns.ScrollPatternConstants.NoScroll,
                        0);
                }
                break;
            }
            default:
                throw new InvalidOperationException($"Unknown website showcase surface '{id}'.");
        }
    }

    internal static void PrepareWebsiteShowcasePresentation(Window window, UIA3Automation automation)
    {
        AutomationElement[] dismissButtons = window
            .FindAllDescendants(cf => cf.ByAutomationId("CloseButton").And(cf.ByControlType(ControlType.Button)))
            .Where(IsVisible)
            .ToArray();
        foreach (AutomationElement button in dismissButtons)
        {
            InvokeOrClick(button);
        }

        if (dismissButtons.Length > 0)
        {
            WaitUntil(
                "website-showcase transient status closes",
                () => window
                    .FindAllDescendants(cf => cf.ByAutomationId("CloseButton").And(cf.ByControlType(ControlType.Button)))
                    .All(button => !IsVisible(button)),
                TimeSpan.FromSeconds(5));
        }

        PrepareWebsiteShowcaseTooltips(window, automation);
    }

    internal static void PrepareWebsiteShowcaseTooltips(Window window, UIA3Automation automation)
    {
        NativeMethods.MoveCursorPhysical(new Point(1, 1));
        Thread.Sleep(80);
        NativeMethods.MoveCursorPhysical(new Point(2, 2));
        SendMouseInput(
            new Point(2, 2),
            MouseEventFlags.MOUSEEVENTF_MOVE | MouseEventFlags.MOUSEEVENTF_MOVE_NOCOALESCE);
        Thread.Sleep(1200);
        Rectangle windowBounds = window.BoundingRectangle;
        int appProcessId = window.Properties.ProcessId.ValueOrDefault;
        bool hasVisibleTooltip = automation.GetDesktop()
            .FindAllDescendants(cf => cf.ByControlType(ControlType.ToolTip))
            .Any(element => IsVisibleTooltipOverWindow(element, windowBounds, appProcessId));
        if (hasVisibleTooltip)
        {
            Keyboard.Press(VirtualKeyShort.ESCAPE);
            Thread.Sleep(120);
        }

        bool tooltipsClosed = WaitUntilAvailable(
            () => automation.GetDesktop()
                .FindAllDescendants(cf => cf.ByControlType(ControlType.ToolTip))
                .All(element => !IsVisibleTooltipOverWindow(element, windowBounds, appProcessId)),
            TimeSpan.FromSeconds(3));
        if (!tooltipsClosed)
        {
            string tooltipDetails = string.Join(
                " | ",
                automation.GetDesktop()
                    .FindAllDescendants(cf => cf.ByControlType(ControlType.ToolTip))
                    .Where(element => IsVisibleTooltipOverWindow(element, windowBounds, appProcessId))
                    .Select(element =>
                        $"name='{GetElementName(element)}',pid={element.Properties.ProcessId.ValueOrDefault}," +
                        $"hwnd={element.Properties.NativeWindowHandle.ValueOrDefault}," +
                        $"windowPattern={element.Patterns.Window.IsSupported},bounds={element.BoundingRectangle}"));
            throw new InvalidOperationException(
                $"website-showcase could not dismiss a tooltip overlapping the app window; " +
                $"cursor={NativeMethods.GetCursorPositionPhysical()}: {tooltipDetails}");
        }
        Thread.Sleep(250);
    }

    private static bool IsVisibleTooltipOverWindow(AutomationElement element, Rectangle windowBounds, int appProcessId)
    {
        try
        {
            if (element.Properties.ProcessId.ValueOrDefault != appProcessId)
            {
                return false;
            }

            Rectangle intersection = Rectangle.Intersect(element.BoundingRectangle, windowBounds);
            return IsVisible(element) && intersection.Width > 1 && intersection.Height > 1;
        }
        catch (COMException)
        {
            return false;
        }
    }

    private static void EnsureWebsitePullRequestDetailVisible(Window window)
    {
        if (IsVisible(FindCurrentVisibleByAutomationId(window, "RepoPullRequestsDetailTitle")))
        {
            return;
        }

        AutomationElement list = WaitForElement(
            "website-showcase pull request list",
            () => FindCurrentVisibleByAutomationId(window, "RepoPullRequestsList"),
            TimeSpan.FromSeconds(15));
        AutomationElement firstPullRequest = WaitForElement(
            "website-showcase first pull request",
            () => list.FindFirstDescendant(cf => cf.ByControlType(ControlType.ListItem)),
            TimeSpan.FromSeconds(12));
        InvokeOrClick(firstPullRequest);
        WaitForElement(
            "website-showcase pull request detail",
            () => FindCurrentVisibleByAutomationId(window, "RepoPullRequestsDetailTitle"),
            TimeSpan.FromSeconds(15));
    }

    private static void ExpandWebsiteRepoCodeFolder(Window window, string path)
    {
        AutomationElement? folder = window.FindAllDescendants(cf => cf.ByControlType(ControlType.TreeItem))
            .FirstOrDefault(element =>
                IsVisible(element) &&
                string.Equals(element.Properties.ItemStatus.ValueOrDefault, $"path:{path}", StringComparison.Ordinal));
        if (folder is null)
        {
            AutomationElement opener = WaitForElement(
                "website-showcase repository file-tree opener",
                () => window.FindAllDescendants(cf => cf.ByAutomationId("RepoCodeOpenFileTreeButton"))
                    .FirstOrDefault(IsVisible),
                TimeSpan.FromSeconds(8));
            OpenRepoCodeFileTreeDrawer(window, opener);
            AutomationElement drawer = WaitForElement(
                "website-showcase repository file-tree drawer",
                () => window.FindAllDescendants(cf => cf.ByAutomationId("RepoCodeLeftDrawer"))
                    .FirstOrDefault(IsVisible),
                TimeSpan.FromSeconds(8));
            folder = WaitForElement(
                $"website-showcase repository folder {path}",
                () => drawer.FindAllDescendants(cf => cf.ByControlType(ControlType.TreeItem))
                    .FirstOrDefault(element =>
                        IsVisible(element) &&
                        string.Equals(element.Properties.ItemStatus.ValueOrDefault, $"path:{path}", StringComparison.Ordinal)),
                TimeSpan.FromSeconds(12));
        }

        if (folder.Patterns.ExpandCollapse.IsSupported &&
            folder.Patterns.ExpandCollapse.Pattern.ExpandCollapseState.Value != ExpandCollapseState.Expanded)
        {
            folder.Patterns.ExpandCollapse.Pattern.Expand();
            Thread.Sleep(450);
        }
    }

    private static void CloseVisiblePane(Window window, string closeAutomationId)
    {
        AutomationElement? close = FindCurrentVisibleByAutomationId(window, closeAutomationId);
        if (IsVisible(close))
        {
            InvokeOrClick(close!);
            Thread.Sleep(350);
        }
    }
}
