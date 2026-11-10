using System.Runtime.InteropServices;
using FlaUI.Core.AutomationElements;
using static JitHub.WinUI.Automation.Elements.ElementQueries;
using static JitHub.WinUI.Automation.Interaction.ElementActions;

namespace JitHub.WinUI.Automation.Workspaces;

/// <summary>
/// Finds the open and close buttons of the adaptive workspace panes.
/// </summary>
internal static class AdaptivePaneButtons
{
    internal static AutomationElement? FindAdaptivePaneCloseButton(Window window, string prefix, bool leading)
    {
        string[] ids = prefix switch
        {
            "MyIssues" => leading
                ? ["MyIssuesCloseListPaneButton"]
                : ["MyIssuesCloseInspectorPaneButton"],
            "MyPullRequests" => leading
                ? ["MyPullRequestsCloseListPaneButton"]
                : ["MyPullRequestsCloseInspectorPaneButton"],
            "RepoIssues" => leading
                ? ["RepoIssuesCloseListPaneButton"]
                : ["RepoIssuesCloseInspectorPaneButton"],
            "RepoPullRequests" => leading
                ? ["RepoPullRequestsCloseListPaneButton"]
                : ["RepoPullRequestsCloseInspectorPaneButton"],
            "RepoCommits" => leading
                ? ["RepoCommitsCloseListPaneButton"]
                : ["RepoCommitsCloseInspectorPaneButton"],
            "RepoCode" => leading
                ? ["RepoCodeCloseFileTreeButton"]
                : [],
            _ => []
        };

        foreach (string id in ids)
        {
            AutomationElement? element = window.FindFirstDescendant(cf => cf.ByAutomationId(id));
            if (IsVisible(element))
            {
                return element;
            }
        }

        return null;
    }

    internal static AutomationElement? FindAdaptivePaneButton(Window window, string prefix, bool leading)
    {
        string[] ids = prefix switch
        {
            "MyIssues" => leading
                ? ["MyIssuesOpenListPaneButton", "MyIssuesLeadingPaneButton"]
                : ["MyIssuesOpenInspectorPaneButton", "MyIssuesTrailingPaneButton"],
            "MyPullRequests" => leading
                ? ["MyPullRequestsOpenListPaneButton", "MyPullRequestsLeadingPaneButton"]
                : ["MyPullRequestsOpenInspectorPaneButton", "MyPullRequestsTrailingPaneButton"],
            "RepoIssues" => leading
                ? ["RepoIssuesOpenListPaneButton", "RepoDetailOpenIssueListPaneButton", "RepoIssuesLeadingPaneButton"]
                : ["RepoIssuesOpenInspectorPaneButton", "RepoIssuesCompactOpenInspectorPaneButton", "RepoDetailOpenIssueInspectorPaneButton", "RepoIssuesTrailingPaneButton"],
            "RepoPullRequests" => leading
                ? ["RepoPullRequestsOpenListPaneButton", "RepoPullRequestsLeadingPaneButton"]
                : ["RepoPullRequestsOpenInspectorPaneButton", "RepoPullRequestsTrailingPaneButton"],
            "RepoCommits" => leading
                ? ["RepoCommitsOpenListPaneButton", "RepoCommitsLeadingPaneButton"]
                : ["RepoCommitsOpenInspectorPaneButton", "RepoCommitsTrailingPaneButton"],
            "RepoCode" => leading
                ? ["RepoCodeOpenFileTreeButton", "RepoCodeLeadingPaneButton"]
                : [],
            _ => leading
                ? [$"{prefix}LeadingPaneButton"]
                : [$"{prefix}TrailingPaneButton"]
        };

        foreach (string id in ids)
        {
            AutomationElement? element = FindCurrentVisibleByAutomationId(window, id);
            if (IsVisible(element))
            {
                return element;
            }

            AutomationElement? currentProvider = window
                .FindAllDescendants(cf => cf.ByAutomationId(id))
                .LastOrDefault();
            if (currentProvider is null)
            {
                continue;
            }

            try
            {
                RevealForInteraction(currentProvider, id);
                element = FindCurrentVisibleByAutomationId(window, id);
                if (IsVisible(element))
                {
                    return element;
                }
            }
            catch (COMException)
            {
                // A responsive reparent can invalidate the old provider; retry the next stable ID.
            }
        }

        return null;
    }
}
