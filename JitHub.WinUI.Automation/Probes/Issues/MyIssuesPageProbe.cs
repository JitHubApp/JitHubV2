using System.Drawing;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Input;
using FlaUI.UIA3;
using JitHub.WinUI.Automation.Options;
using static JitHub.WinUI.Automation.Applications.ApplicationProcessCleanup;
using static JitHub.WinUI.Automation.Applications.ProbeApplicationLauncher;
using static JitHub.WinUI.Automation.Conditions.ProbeConditions;
using static JitHub.WinUI.Automation.Elements.ElementGeometry;
using static JitHub.WinUI.Automation.Elements.ElementQueries;
using static JitHub.WinUI.Automation.Probes.Issues.MyIssuesPseudoLongLabelsProbe;
using static JitHub.WinUI.Automation.Screenshots.WindowCapture;
using static JitHub.WinUI.Automation.Windows.ReadyWindows;
using static JitHub.WinUI.Automation.Windows.WindowSizing;
using static JitHub.WinUI.Automation.Workspaces.AdaptiveWorkspaceDrawers;
using static JitHub.WinUI.Automation.Workspaces.WorkItemListSelection;
using static JitHub.WinUI.Automation.Workspaces.WorkItemMarkdown;

namespace JitHub.WinUI.Automation.Probes.Issues;

/// <summary>
/// Drives the my-issues-page probe.
/// </summary>
internal static class MyIssuesPageProbe
{
    // This method stays whole because it walks one My Issues session in order: select a row and verify detail, Markdown
    // and copy, sweep the responsive viewports, test scroll selection, exercise compact drawers, then apply Created and
    // Closed filters, with each step relying on the selected issue and window size left before.
    internal static void RunMyIssuesPageProbe(CaptureOptions options)
    {
        bool isAttached = !string.IsNullOrWhiteSpace(options.AttachProcess);
        using var app = isAttached
            ? CreateProbeApplication(options)
            : LaunchApplication(options.AppPath, "--page=my-issues", "--theme=dark");
        using var automation = new UIA3Automation();
        try
        {
            Window window = GetReadyWindow(app, automation, "my-issues-page probe");
            ResizeWindow(window, 1366, 900);
            AutomationElement list;
            try
            {
                list = WaitForElement(
                    "MyIssuesList",
                    () => FindCurrentVisibleByAutomationId(window, "MyIssuesList"),
                    TimeSpan.FromSeconds(10));
            }
            catch
            {
                PrintVisibleAutomationIds(window, "my-issues-list-missing");
                CaptureWindow(window, Path.Combine(options.OutputDirectory, "my-issues-page-list-missing.png"));
                throw;
            }
            AutomationElement issueToSelect = WaitForElement(
                "unselected issue row",
                () => list.FindAllDescendants(cf => cf.ByControlType(ControlType.ListItem))
                    .Where(IsVisible)
                    .Skip(1)
                    .FirstOrDefault()
                    ?? list.FindAllDescendants(cf => cf.ByControlType(ControlType.ListItem)).FirstOrDefault(IsVisible),
                TimeSpan.FromSeconds(10));
            AutomationElement stableRowIdentity = WaitForElement(
                "stable My Issues row identity",
                () => issueToSelect.FindAllDescendants()
                    .FirstOrDefault(element => IsVisible(element) && GetAutomationId(element).StartsWith("MyWorkItem_", StringComparison.Ordinal)),
                TimeSpan.FromSeconds(5));
            AssertProbe(!string.IsNullOrWhiteSpace(GetElementName(stableRowIdentity)), "My Issues row did not expose a stable accessible name.");
            Mouse.MoveTo(CenterPoint(issueToSelect, window));
            Thread.Sleep(350);
            CaptureWindow(window, Path.Combine(options.OutputDirectory, "my-issues-page-row-hover.png"));
            issueToSelect.Click();
            Thread.Sleep(900);
            WaitForElement(
                "MyIssuesDetailTitle",
                () => FindCurrentVisibleByAutomationId(window, "MyIssuesDetailTitle"),
                TimeSpan.FromSeconds(10));
            AutomationElement? inspector = FindCurrentVisibleByAutomationId(window, "MyIssuesInspector");
            Console.WriteLine(IsVisible(inspector)
                ? "my-issues-page: inspector visible in desktop layout"
                : "my-issues-page: inspector collapsed in desktop layout");
            WaitForElement(
                "MyIssuesOpenInRepositoryButton",
                () => FindCurrentVisibleByAutomationId(window, "MyIssuesOpenInRepositoryButton"),
                TimeSpan.FromSeconds(8));
            AssertMarkdownTextVisible(window, "This preview item represents", "The cached detail path");
            ExerciseMyWorkItemMarkdownCopy(
                window,
                automation,
                "MarkdownHost_Conversation_MyIssuesBody",
                "This preview item represents",
                "My Issues body");
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
                    $"MyIssuesAdaptiveWorkspace at {viewportLabel}",
                    () => FindCurrentVisibleByAutomationId(window, "MyIssuesAdaptiveWorkspace"),
                    TimeSpan.FromSeconds(8));
                AssertProbe(IsVisible(workspace), $"My Issues workspace was not visible at {viewportLabel}.");
                CaptureWindow(window, Path.Combine(options.OutputDirectory, $"my-issues-page-{viewportLabel}.png"));
            }

            ResizeWindow(window, 1366, 900);
            ExerciseIssueListScrollSelection(window, list, options.OutputDirectory, "my-issues-page");

            ResizeWindow(window, 760, 650);
            WaitForElement(
                "MyIssuesDetailTitle",
                () => FindCurrentVisibleByAutomationId(window, "MyIssuesDetailTitle"),
                TimeSpan.FromSeconds(8));
            AssertProbe(!IsVisible(FindCurrentVisibleByAutomationId(window, "MyIssuesInspector")), "My Issues inspector stayed visible in compact layout.");
            ExerciseAdaptiveWorkspaceDrawers(window, "MyIssues", "MyIssuesList", "MyIssuesInspector");
            CaptureWindow(window, Path.Combine(options.OutputDirectory, "my-issues-page-compact.png"));

            ResizeWindow(window, 1366, 900);

            AutomationElement createdFilter = WaitForElement(
                "MyIssuesFilter_Created",
                () => FindCurrentVisibleByAutomationId(window, "MyIssuesFilter_Created"),
                TimeSpan.FromSeconds(5));
            createdFilter.Click();
            Thread.Sleep(500);
            AutomationElement closedFilter = WaitForElement(
                "MyIssuesState_Closed",
                () => FindCurrentVisibleByAutomationId(window, "MyIssuesState_Closed"),
                TimeSpan.FromSeconds(5));
            closedFilter.Click();
            Thread.Sleep(900);
            CaptureWindow(window, Path.Combine(options.OutputDirectory, "my-issues-page-filters.png"));
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
            RunMyIssuesPseudoLongLabelsProbe(options);
        }
    }
}
