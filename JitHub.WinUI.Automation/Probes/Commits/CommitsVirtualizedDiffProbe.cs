using System.Diagnostics;
using System.Drawing;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Input;
using FlaUI.Core.Tools;
using FlaUI.UIA3;
using JitHub.WinUI.Automation.Native;
using JitHub.WinUI.Automation.Options;
using static JitHub.WinUI.Automation.Applications.ApplicationProcessCleanup;
using static JitHub.WinUI.Automation.Applications.ProbeApplicationLauncher;
using static JitHub.WinUI.Automation.Conditions.ProbeConditions;
using static JitHub.WinUI.Automation.Elements.ElementQueries;
using static JitHub.WinUI.Automation.Interaction.ElementActions;
using static JitHub.WinUI.Automation.Interaction.KeyboardInput;
using static JitHub.WinUI.Automation.Probes.Commits.CommitDiffNavigation;
using static JitHub.WinUI.Automation.Screenshots.WindowCapture;
using static JitHub.WinUI.Automation.Windows.ReadyWindows;
using static JitHub.WinUI.Automation.Windows.WindowSizing;

namespace JitHub.WinUI.Automation.Probes.Commits;

/// <summary>
/// Drives the commits-virtualized-diff probe, including multi-row diff selection.
/// </summary>
internal static class CommitsVirtualizedDiffProbe
{
    // This method stays whole because it is one ordered script on the commit diff: apply and clear the file filter,
    // search and step to the next match, scroll and check multi-row selection, switch to Compare, then resize to 900
    // and 640 widths, with each step running on the diff state the previous step left.
    internal static void RunCommitsVirtualizedDiffProbe(CaptureOptions options)
    {
        KillExistingApplicationInstances(options.AppPath);
        using var app = LaunchApplication(
            options.AppPath,
            "--page=repo-commits",
            "--theme=dark",
            $"--repo={options.RepositoryFullName}");
        using var automation = new UIA3Automation();
        try
        {
            Window window = GetReadyWindow(app, automation, "commits-virtualized-diff probe");
            ResizeWindow(window, 1366, 900);
            EnsureCommitDetailVisible(window);
            EnsureCommitDiffVisible(window);
            AssertCommitDiffViewerContracts(window, "commits-virtualized-diff initial");

            AutomationElement filterBox = WaitForElement(
                "RepoCommitsDiffFileFilterBox",
                () => window.FindFirstDescendant(cf => cf.ByAutomationId("RepoCommitsDiffFileFilterBox")),
                TimeSpan.FromSeconds(8));
            SetTextBoxText(filterBox, "zz-no-file-match");
            WaitUntil(
                "diff file filter no-results row appears",
                () => IsVisible(window.FindFirstDescendant(cf => cf.ByText("No files match the current filter."))),
                TimeSpan.FromSeconds(4));
            SetTextBoxText(filterBox, string.Empty);
            Thread.Sleep(600);
            AssertCommitDiffViewerContracts(window, "commits-virtualized-diff after filter clear");

            AutomationElement searchBox = WaitForElement(
                "RepoCommitsDiffSearchBox",
                () => window.FindFirstDescendant(cf => cf.ByAutomationId("RepoCommitsDiffSearchBox")),
                TimeSpan.FromSeconds(8));
            SetTextBoxText(searchBox, "a");
            Thread.Sleep(700);
            AutomationElement nextButton = WaitForElement(
                "RepoCommitsNextDiffMatchButton",
                () => window.FindFirstDescendant(cf => cf.ByAutomationId("RepoCommitsNextDiffMatchButton")),
                TimeSpan.FromSeconds(8));
            if (nextButton.Patterns.Invoke.IsSupported || nextButton.Properties.IsEnabled.ValueOrDefault)
            {
                InvokeOrClick(nextButton);
                Thread.Sleep(350);
            }

            AutomationElement diffRows = WaitForElement(
                "CommitDiffViewerRowsScrollViewer",
                () => window.FindFirstDescendant(cf => cf.ByAutomationId("CommitDiffViewerRowsScrollViewer")),
                TimeSpan.FromSeconds(8));
            Mouse.MoveTo(diffRows.BoundingRectangle.Center());
            Mouse.Scroll(-4);
            Thread.Sleep(350);
            AssertCommitDiffMultiRowSelection(window, automation, diffRows, options.OutputDirectory);
            CaptureWindow(window, Path.Combine(options.OutputDirectory, "commits-virtualized-diff-wide.png"));

            AutomationElement compareSection = WaitForElement(
                "RepoCommitsSection_Compare",
                () => window.FindFirstDescendant(cf => cf.ByAutomationId("RepoCommitsSection_Compare")),
                TimeSpan.FromSeconds(8));
            InvokeOrClick(compareSection);
            Thread.Sleep(600);
            AutomationElement compareButton = WaitForElement(
                "RepoCommitsCompareButton",
                () => window.FindFirstDescendant(cf => cf.ByAutomationId("RepoCommitsCompareButton")),
                TimeSpan.FromSeconds(8));
            InvokeOrClick(compareButton);
            Thread.Sleep(1200);
            AssertProbe(
                IsVisible(window.FindFirstDescendant(cf => cf.ByAutomationId("RepoCommitsCompareDiffSearchBox"))) &&
                IsVisible(window.FindFirstDescendant(cf => cf.ByAutomationId("CommitDiffViewerRowsScrollViewer"))),
                "Compare diff section did not expose the virtualized wrapped diff surface.");
            AssertCommitDiffViewerContracts(window, "commits-virtualized-diff compare");

            foreach ((int width, int height) in new[] { (900, 700), (640, 600) })
            {
                Rectangle resizedBounds = ResizeWindow(window, width, height);
                string viewportLabel = GetResponsiveViewportLabel(width, height, resizedBounds);
                Thread.Sleep(600);
                AssertProbe(
                    IsVisible(window.FindFirstDescendant(cf => cf.ByAutomationId("RepoCommitsCompareDiffSearchBox"))) ||
                    IsVisible(window.FindFirstDescendant(cf => cf.ByAutomationId("RepoCommitsDiffViewer"))),
                    $"Virtualized diff viewer was not visible at {viewportLabel}.");
                AssertCommitDiffViewerContracts(window, $"commits-virtualized-diff {viewportLabel}");
                CaptureWindow(window, Path.Combine(options.OutputDirectory, $"commits-virtualized-diff-{viewportLabel}.png"));
            }
        }
        finally
        {
            TryClose(app);
            KillExistingApplicationInstances(options.AppPath);
        }
    }

    // This method stays whole because it is one pointer gesture sequence on the diff viewer: drag-select several rows,
    // right-click and copy to prove the selection survives, then drag the scrollbar and copy again to prove scrollbar
    // drags never create a text selection, sharing one clipboard sentinel protocol.
    private static void AssertCommitDiffMultiRowSelection(Window window, UIA3Automation automation, AutomationElement diffRows, string outputDirectory)
    {
        AutomationElement[] textElements = FindVisibleDiffTextElements(diffRows);
        AssertProbe(textElements.Length >= 2, "Commit diff did not expose at least two visible text rows for selection.");

        var startBounds = textElements[0].BoundingRectangle;
        var endBounds = textElements[Math.Min(textElements.Length - 1, 4)].BoundingRectangle;
        Point start = new(
            (int)Math.Round(startBounds.X + Math.Min(24, Math.Max(6, startBounds.Width / 4d))),
            (int)Math.Round(startBounds.Y + Math.Max(4, startBounds.Height / 2d)));
        Point end = new(
            (int)Math.Round(endBounds.X + Math.Min(endBounds.Width - 4, Math.Max(28, endBounds.Width * 0.7))),
            (int)Math.Round(endBounds.Y + Math.Max(4, endBounds.Height / 2d)));

        AutomationElement diffSearch = WaitForElement(
            "commit diff search focus anchor",
            () => FindCurrentVisibleByAutomationId(window, "RepoCommitsDiffSearchBox"),
            TimeSpan.FromSeconds(5));
        FocusForKeyboardActivation(window, diffSearch);
        Thread.Sleep(150);
        Mouse.MoveTo(start);
        Thread.Sleep(100);
        Mouse.Down(MouseButton.Left);
        for (int step = 1; step <= 8; step++)
        {
            double ratio = step / 8d;
            Point intermediate = new(
                (int)Math.Round(start.X + ((end.X - start.X) * ratio)),
                (int)Math.Round(start.Y + ((end.Y - start.Y) * ratio)));
            Mouse.MoveTo(intermediate);
            Thread.Sleep(90);
        }

        Mouse.Up(MouseButton.Left);
        Thread.Sleep(300);
        CaptureWindow(window, Path.Combine(outputDirectory, "commits-virtualized-diff-selection-debug.png"));

        NativeMethods.SetClipboardText("__jithub_context_menu_copy_pending__");
        Mouse.RightClick(new System.Drawing.Point(start.X + 4, start.Y));
        int appProcessId = window.Properties.ProcessId.ValueOrDefault;
        AutomationElement contextCopy = WaitForElement(
            "Commit diff copy context menu item",
            () => automation.GetDesktop()
                .FindAllDescendants(cf => cf.ByControlType(ControlType.MenuItem))
                .FirstOrDefault(item =>
                    item.Properties.ProcessId.ValueOrDefault == appProcessId &&
                    IsVisible(item) &&
                    string.Equals(GetElementName(item), "Copy selected diff text", StringComparison.Ordinal)),
            TimeSpan.FromSeconds(3));
        InvokeOrClick(contextCopy);
        string contextClipboard = string.Empty;
        Stopwatch stopwatch = Stopwatch.StartNew();
        do
        {
            contextClipboard = NativeMethods.GetClipboardText();
            if (contextClipboard.Trim().Length > 0 &&
                contextClipboard.Contains('\n', StringComparison.Ordinal))
            {
                break;
            }

            Thread.Sleep(100);
        }
        while (stopwatch.Elapsed < TimeSpan.FromSeconds(3));

        int contextCopiedLineCount = contextClipboard.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length;
        Console.WriteLine(
            $"commit-diff-context-copy copiedLength={contextClipboard.Length} lines={contextCopiedLineCount} preview='{TruncateForLog(contextClipboard.ReplaceLineEndings("\\n"), 160)}'");
        CaptureWindow(window, Path.Combine(outputDirectory, "commits-virtualized-diff-context-copy.png"));
        AssertProbe(
            contextCopiedLineCount >= 2,
            "Right-clicking a selected diff range cleared the selection before the context menu copy command.");
        AssertProbe(
            !contextClipboard.Contains("Show more", StringComparison.OrdinalIgnoreCase),
            "Commit diff selection copied legacy show-more UI text instead of diff text.");

        Mouse.Click(start);
        Thread.Sleep(250);
        const string scrollbarSentinel = "__jithub_scrollbar_drag_should_not_select__";
        NativeMethods.SetClipboardText(scrollbarSentinel);
        var diffBounds = diffRows.BoundingRectangle;
        Point scrollStart = new(
            (int)Math.Round((double)diffBounds.Right - 8),
            (int)Math.Round((double)diffBounds.Top + Math.Min(120d, (double)diffBounds.Height * 0.25)));
        Point scrollEnd = new(
            scrollStart.X,
            (int)Math.Round(Math.Min((double)diffBounds.Bottom - 24, scrollStart.Y + Math.Max(80d, (double)diffBounds.Height * 0.28))));
        Mouse.MoveTo(scrollStart);
        Thread.Sleep(100);
        Mouse.Down(MouseButton.Left);
        for (int step = 1; step <= 6; step++)
        {
            double ratio = step / 6d;
            Mouse.MoveTo(new System.Drawing.Point(
                scrollStart.X,
                (int)Math.Round(scrollStart.Y + ((scrollEnd.Y - scrollStart.Y) * ratio))));
            Thread.Sleep(80);
        }

        Mouse.Up(MouseButton.Left);
        Thread.Sleep(250);
        AutomationElement scrollbarCopyRow = FindVisibleDiffTextElements(diffRows).First();
        var scrollbarCopyBounds = scrollbarCopyRow.BoundingRectangle;
        NativeMethods.SetClipboardText(scrollbarSentinel);
        Mouse.RightClick(new System.Drawing.Point(
            (int)Math.Round(scrollbarCopyBounds.X + Math.Min(24, Math.Max(6, scrollbarCopyBounds.Width / 4d))),
            (int)Math.Round(scrollbarCopyBounds.Y + Math.Max(4, scrollbarCopyBounds.Height / 2d))));
        AutomationElement scrollbarContextCopy = WaitForElement(
            "Commit diff scrollbar isolation copy menu item",
            () => automation.GetDesktop()
                .FindAllDescendants(cf => cf.ByControlType(ControlType.MenuItem))
                .FirstOrDefault(item =>
                    item.Properties.ProcessId.ValueOrDefault == appProcessId &&
                    IsVisible(item) &&
                    string.Equals(GetElementName(item), "Copy selected diff text", StringComparison.Ordinal)),
            TimeSpan.FromSeconds(3));
        InvokeOrClick(scrollbarContextCopy);
        string afterScrollbarDrag = string.Empty;
        stopwatch.Restart();
        do
        {
            afterScrollbarDrag = NativeMethods.GetClipboardText();
            if (!string.Equals(afterScrollbarDrag, scrollbarSentinel, StringComparison.Ordinal))
            {
                break;
            }

            Thread.Sleep(100);
        }
        while (stopwatch.Elapsed < TimeSpan.FromSeconds(3));

        AssertProbe(
            afterScrollbarDrag.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length <= 1,
            "Dragging the commit diff scrollbar created a multi-row diff text selection.");
    }

    private static string TruncateForLog(string value, int maxLength)
    {
        if (string.IsNullOrEmpty(value) || value.Length <= maxLength)
        {
            return value ?? string.Empty;
        }

        return value[..maxLength] + "...";
    }
}
