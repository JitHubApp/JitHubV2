using System.Diagnostics;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Input;
using FlaUI.Core.Tools;
using FlaUI.UIA3;
using JitHub.WinUI.Automation.Options;
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

namespace JitHub.WinUI.Automation.Probes.Commits;

/// <summary>
/// Drives the commits-performance probe and reads the commit performance snapshot the app publishes.
/// </summary>
internal static class CommitsPerformanceProbe
{
    // This method stays whole because it measures one large-commit session against fixed budgets in sequence: selection
    // input latency, first diff rows, diff search indexing, scroll frame rate and dispatcher stalls, then working set,
    // and each measurement reads monitor snapshots taken relative to the previous step.
    internal static void RunCommitsPerformanceProbe(CaptureOptions options)
    {
        const double selectionInputBudgetMilliseconds = 50;
        const double firstDiffRowsBudgetMilliseconds = 750;
        const double searchIndexBudgetMilliseconds = 500;
        const double dispatcherStallBudgetMilliseconds = 50;
        const double minimumScrollFramesPerSecond = 30;
        const long workingSetBudgetBytes = 768L * 1024 * 1024;

        KillExistingApplicationInstances(options.AppPath);
        using var app = LaunchApplication(
            options.AppPath,
            "--page=repo-commits",
            "--theme=dark",
            "--large-commit",
            $"--repo={options.RepositoryFullName}");
        using var automation = new UIA3Automation();
        try
        {
            Window window = GetReadyWindow(app, automation, "commits-performance probe");
            ResizeWindow(window, 1366, 900);
            AutomationElement workspace = WaitForElement(
                "RepoCommitsAdaptiveWorkspace performance monitor",
                () => window.FindFirstDescendant(cf => cf.ByAutomationId("RepoCommitsAdaptiveWorkspace")),
                TimeSpan.FromSeconds(14));
            EnsureCommitDetailVisible(window);
            EnsureCommitDiffVisible(window);

            AutomationElement list = WaitForElement(
                "RepoCommitsList performance fixture",
                () => window.FindFirstDescendant(cf => cf.ByAutomationId("RepoCommitsList")),
                TimeSpan.FromSeconds(8));
            AutomationElement[] rows = list.FindAllDescendants(cf => cf.ByControlType(ControlType.ListItem))
                .Where(IsVisible)
                .Take(3)
                .ToArray();
            AssertProbe(rows.Length >= 2, "commits-performance: fixture did not expose a neighboring commit.");

            Mouse.MoveTo(CenterPoint(rows[1], window));
            Thread.Sleep(100);
            Stopwatch firstRows = Stopwatch.StartNew();
            Stopwatch input = Stopwatch.StartNew();
            Mouse.Click();
            input.Stop();
            AssertProbe(
                input.Elapsed.TotalMilliseconds <= selectionInputBudgetMilliseconds,
                $"commits-performance: selection input took {input.Elapsed.TotalMilliseconds:F1} ms " +
                $"(budget {selectionInputBudgetMilliseconds:F0} ms).");

            CommitPerformanceSnapshot selectionSnapshot = WaitForCommitPerformanceSnapshot(
                workspace,
                snapshot => snapshot.FirstDiffMilliseconds >= 0 && snapshot.RenderCount > 0,
                TimeSpan.FromSeconds(8));
            AutomationElement performanceDiffRows = WaitForElement(
                "large commit diff viewport",
                () => window.FindFirstDescendant(cf => cf.ByAutomationId("CommitDiffViewerRowsScrollViewer")),
                TimeSpan.FromSeconds(8));
            WaitUntil(
                "large commit first realized diff row",
                () => FindVisibleDiffTextElements(performanceDiffRows).Length > 0,
                TimeSpan.FromSeconds(8));
            firstRows.Stop();

            AssertProbe(
                selectionSnapshot.FirstDiffMilliseconds <= firstDiffRowsBudgetMilliseconds &&
                firstRows.Elapsed.TotalMilliseconds <= firstDiffRowsBudgetMilliseconds,
                $"commits-performance: first diff rows took monitor={selectionSnapshot.FirstDiffMilliseconds:F1} ms, " +
                $"visible={firstRows.Elapsed.TotalMilliseconds:F1} ms (budget {firstDiffRowsBudgetMilliseconds:F0} ms).");
            AssertProbe(
                selectionSnapshot.DispatcherMaxGapMilliseconds <= dispatcherStallBudgetMilliseconds,
                $"commits-performance: UI dispatcher stalled for {selectionSnapshot.DispatcherMaxGapMilliseconds:F1} ms " +
                $"(budget {dispatcherStallBudgetMilliseconds:F0} ms).");

            AutomationElement searchBox = WaitForElement(
                "RepoCommitsDiffSearchBox performance fixture",
                () => window.FindFirstDescendant(cf => cf.ByAutomationId("RepoCommitsDiffSearchBox")),
                TimeSpan.FromSeconds(8));
            SetTextBoxText(searchBox, "PERF_TARGET_35_119");
            CommitPerformanceSnapshot searchSnapshot = WaitForCommitPerformanceSnapshot(
                workspace,
                snapshot => snapshot.SearchMilliseconds >= 0,
                TimeSpan.FromSeconds(5));
            AssertProbe(
                searchSnapshot.SearchMilliseconds <= searchIndexBudgetMilliseconds,
                $"commits-performance: diff search indexing took {searchSnapshot.SearchMilliseconds:F1} ms " +
                $"(budget {searchIndexBudgetMilliseconds:F0} ms).");
            AssertProbe(
                searchSnapshot.DispatcherMaxGapMilliseconds <= dispatcherStallBudgetMilliseconds,
                $"commits-performance: search blocked the dispatcher for {searchSnapshot.DispatcherMaxGapMilliseconds:F1} ms " +
                $"(budget {dispatcherStallBudgetMilliseconds:F0} ms).");
            SetTextBoxText(searchBox, string.Empty);

            AutomationElement diffRows = WaitForElement(
                "CommitDiffViewerRowsScrollViewer performance fixture",
                () => window.FindFirstDescendant(cf => cf.ByAutomationId("CommitDiffViewerRowsScrollViewer")),
                TimeSpan.FromSeconds(8));
            Mouse.MoveTo(diffRows.BoundingRectangle.Center());
            CommitPerformanceSnapshot beforeScroll = ReadCommitPerformanceSnapshot(workspace);
            Stopwatch scroll = Stopwatch.StartNew();
            while (scroll.Elapsed < TimeSpan.FromSeconds(1.2))
            {
                Mouse.Scroll(-1);
                Thread.Sleep(20);
            }
            scroll.Stop();
            Thread.Sleep(250);
            CommitPerformanceSnapshot afterScroll = ReadCommitPerformanceSnapshot(workspace);
            double scrollFramesPerSecond = Math.Max(0, afterScroll.RenderCount - beforeScroll.RenderCount) /
                Math.Max(0.001, scroll.Elapsed.TotalSeconds);
            AssertProbe(
                scrollFramesPerSecond >= minimumScrollFramesPerSecond,
                $"commits-performance: scroll rendered {scrollFramesPerSecond:F1} frames/s " +
                $"(budget {minimumScrollFramesPerSecond:F0} frames/s).");
            AssertProbe(
                afterScroll.DispatcherMaxGapMilliseconds <= dispatcherStallBudgetMilliseconds,
                $"commits-performance: scrolling stalled the dispatcher for {afterScroll.DispatcherMaxGapMilliseconds:F1} ms " +
                $"(budget {dispatcherStallBudgetMilliseconds:F0} ms).");

            using Process process = Process.GetProcessById(app.ProcessId);
            process.Refresh();
            long workingSetBytes = process.WorkingSet64;
            AssertProbe(
                workingSetBytes <= workingSetBudgetBytes,
                $"commits-performance: working set {workingSetBytes / (1024 * 1024):N0} MiB exceeded " +
                $"{workingSetBudgetBytes / (1024 * 1024):N0} MiB.");

            CaptureWindow(window, Path.Combine(options.OutputDirectory, "commits-performance-large-fixture.png"));
            Console.WriteLine(
                $"commits-performance probe: input={input.Elapsed.TotalMilliseconds:F1}ms; " +
                $"first-visible={firstRows.Elapsed.TotalMilliseconds:F1}ms; " +
                $"search={searchSnapshot.SearchMilliseconds:F1}ms; " +
                $"dispatcher-max={afterScroll.DispatcherMaxGapMilliseconds:F1}ms; " +
                $"scroll-fps={scrollFramesPerSecond:F1}; working-set={workingSetBytes / (1024 * 1024):N0}MiB; " +
                $"output={options.OutputDirectory}");
        }
        finally
        {
            TryClose(app);
            KillExistingApplicationInstances(options.AppPath);
        }
    }

    private static CommitPerformanceSnapshot WaitForCommitPerformanceSnapshot(
        AutomationElement workspace,
        Func<CommitPerformanceSnapshot, bool> predicate,
        TimeSpan timeout)
    {
        CommitPerformanceSnapshot snapshot = default;
        WaitUntil(
            "commit performance counters",
            () => CommitPerformanceSnapshot.TryParse(
                    workspace.Properties.ItemStatus.ValueOrDefault,
                    out snapshot) &&
                predicate(snapshot),
            timeout);
        return snapshot;
    }

    private static CommitPerformanceSnapshot ReadCommitPerformanceSnapshot(AutomationElement workspace)
    {
        string? value = workspace.Properties.ItemStatus.ValueOrDefault;
        AssertProbe(
            CommitPerformanceSnapshot.TryParse(value, out CommitPerformanceSnapshot snapshot),
            $"Commit performance counters were malformed: '{value}'.");
        return snapshot;
    }
}
