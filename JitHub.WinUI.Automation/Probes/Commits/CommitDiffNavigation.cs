using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using static JitHub.WinUI.Automation.Conditions.ProbeConditions;
using static JitHub.WinUI.Automation.Elements.ElementQueries;
using static JitHub.WinUI.Automation.Interaction.ElementActions;
using static JitHub.WinUI.Automation.Workspaces.AdaptivePaneButtons;

namespace JitHub.WinUI.Automation.Probes.Commits;

/// <summary>
/// Opens the commit detail and diff panes and checks the diff viewer contracts the commit probes share.
/// </summary>
internal static class CommitDiffNavigation
{
    internal static void EnsureCommitDiffVisible(Window window)
    {
        if (IsVisible(window.FindFirstDescendant(cf => cf.ByAutomationId("RepoCommitsDiffViewer"))))
        {
            return;
        }

        AutomationElement? list = window.FindFirstDescendant(cf => cf.ByAutomationId("RepoCommitsList"));
        if (IsVisible(list))
        {
            AutomationElement? row = list?.FindFirstDescendant(cf => cf.ByControlType(ControlType.ListItem));
            if (row is not null)
            {
                InvokeOrClick(row);
                Thread.Sleep(900);
            }
        }

            WaitForElement(
                "CommitDiffViewerRowsScrollViewer",
                () => window.FindFirstDescendant(cf => cf.ByAutomationId("CommitDiffViewerRowsScrollViewer")),
                TimeSpan.FromSeconds(14));
    }

    internal static void AssertCommitDiffViewerContracts(Window window, string context)
    {
        AssertProbe(
            IsVisible(window.FindFirstDescendant(cf => cf.ByAutomationId("CommitDiffViewerRowsScrollViewer"))),
            $"{context}: virtualized diff row ScrollViewer was not visible.");
        AssertProbe(
            !IsVisible(window.FindFirstDescendant(cf => cf.ByAutomationId("RepoCommitsDiffModeComboBox"))),
            $"{context}: split/unified mode selector is still visible.");
        AssertProbe(
            window.FindFirstDescendant(cf => cf.ByText("Show more files")) is null &&
            window.FindFirstDescendant(cf => cf.ByText("Show more lines")) is null,
            $"{context}: legacy show-more diff controls are still present.");
    }

    internal static AutomationElement[] FindVisibleDiffTextElements(AutomationElement diffRows)
    {
        var rowBounds = diffRows.BoundingRectangle;
        return diffRows
            .FindAllDescendants(cf => cf.ByControlType(ControlType.Text))
            .Where(IsVisible)
            .Where(element =>
            {
                var bounds = element.BoundingRectangle;
                return bounds.Height > 8 &&
                    bounds.Width > 24 &&
                    bounds.X >= rowBounds.X &&
                    bounds.Y >= rowBounds.Y &&
                    bounds.X < rowBounds.X + rowBounds.Width &&
                    bounds.Y < rowBounds.Y + rowBounds.Height;
            })
            .Where(element =>
            {
                string name = GetElementName(element);
                return !string.IsNullOrWhiteSpace(name) &&
                    name.Trim().Length > 3 &&
                    !name.Trim().All(char.IsDigit) &&
                    !string.Equals(name.Trim(), "+", StringComparison.Ordinal) &&
                    !string.Equals(name.Trim(), "-", StringComparison.Ordinal) &&
                    !string.Equals(name.Trim(), "@", StringComparison.Ordinal) &&
                    !name.Contains("No files match", StringComparison.OrdinalIgnoreCase);
            })
            .OrderBy(element => element.BoundingRectangle.Y)
            .ThenBy(element => element.BoundingRectangle.X)
            .Take(8)
            .ToArray();
    }

    internal static void EnsureCommitDetailVisible(Window window)
    {
        AutomationElement? detail = FindCurrentVisibleByAutomationId(window, "RepoCommitsDetailTitle");
        if (IsVisible(detail) ||
            IsVisible(FindCurrentVisibleByAutomationId(window, "RepoCommitsDetailShyHeader")) ||
            IsVisible(FindCurrentVisibleByAutomationId(window, "RepoCommitsDiffViewer")))
        {
            return;
        }

        AutomationElement? list = FindCurrentVisibleByAutomationId(window, "RepoCommitsList");
        if (!IsVisible(list))
        {
            AutomationElement? leadingButton = FindAdaptivePaneButton(window, "RepoCommits", leading: true);
            if (IsVisible(leadingButton))
            {
                InvokeOrClick(leadingButton!);
                Thread.Sleep(450);
                list = FindCurrentVisibleByAutomationId(window, "RepoCommitsList");
            }
        }

        AssertProbe(IsVisible(list), "RepoCommitsList was not available for commit selection.");
        AutomationElement firstRow = WaitForElement(
            "first visible commit row",
            () => list!.FindAllDescendants(cf => cf.ByControlType(ControlType.ListItem)).FirstOrDefault(IsVisible),
            TimeSpan.FromSeconds(12));
        SelectAutomationItem(firstRow, "first commit row");
        Thread.Sleep(650);

        WaitForElement(
            "RepoCommitsDetailTitle",
            () => FindCurrentVisibleByAutomationId(window, "RepoCommitsDetailTitle"),
            TimeSpan.FromSeconds(12));
    }
}
