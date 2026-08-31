using System;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using Xunit;

namespace JitHub.WinUI.Tests.Views;

public sealed class PullRequestResponsiveActionContractTests
{
    [Fact]
    public void PullRequestActionsHaveWideAndCompactNativeSurfacesWithDistinctNames()
    {
        XDocument xaml = XDocument.Load(SourcePath(
            "JitHub.WinUI",
            "Views",
            "Pages",
            "RepoPullRequestPage.xaml"));
        string[] requiredIds =
        [
            "RepoPullRequestsInlineActions",
            "RepoPullRequestsToggleStateButton",
            "RepoPullRequestsSubmitReviewButton",
            "RepoPullRequestsMergeButton",
            "RepoPullRequestsCompactActionsButton",
            "RepoPullRequestsCompactToggleStateAction",
            "RepoPullRequestsCompactSubmitReviewAction",
            "RepoPullRequestsCompactMergeCommitAction",
            "RepoPullRequestsCompactSquashMergeAction",
            "RepoPullRequestsCompactRebaseMergeAction"
        ];

        foreach (string id in requiredIds)
        {
            Assert.Contains(xaml.Descendants(), element =>
                (string?)element.Attribute("AutomationProperties.AutomationId") == id ||
                (string?)element.Attribute(XName.Get("Name", "http://schemas.microsoft.com/winfx/2006/xaml")) == id);
        }

        foreach (string id in requiredIds.Where(id => id.StartsWith(
                     "RepoPullRequestsCompact",
                     StringComparison.Ordinal)))
        {
            XElement[] actions = xaml.Descendants().Where(element =>
                (string?)element.Attribute("AutomationProperties.AutomationId") == id).ToArray();
            Assert.True(actions.Length > 0);
            Assert.All(actions, action => Assert.False(string.IsNullOrWhiteSpace(
                (string?)action.Attribute("AutomationProperties.Name"))));
        }
    }

    [Fact]
    public void PageSwitchesToCompactOverflowForEveryNonWideWorkspaceMode()
    {
        string source = File.ReadAllText(SourcePath(
            "JitHub.WinUI",
            "Views",
            "Pages",
            "RepoPullRequestPage.xaml.cs"));

        Assert.Contains("bool useCompactActionOverflow =", source, StringComparison.Ordinal);
        Assert.Contains(
            "PullRequestsWorkspace.State is { Mode: not AdaptiveWorkspaceMode.Wide }",
            source,
            StringComparison.Ordinal);
        Assert.Contains("RepoPullRequestsInlineActions.Visibility", source, StringComparison.Ordinal);
        Assert.Contains("RepoPullRequestsCompactActionsButton.Visibility", source, StringComparison.Ordinal);
        Assert.Contains("RepoPullRequestsShyActionsButton", File.ReadAllText(SourcePath(
            "JitHub.WinUI",
            "Views",
            "Pages",
            "RepoPullRequestPage.xaml")), StringComparison.Ordinal);
        Assert.Contains("RepoPullRequestsSubmitReviewDialog", source, StringComparison.Ordinal);
    }

    [Fact]
    public void PullRequestXamlUsesWinUiGridSizingSyntax()
    {
        string source = File.ReadAllText(SourcePath(
            "JitHub.WinUI",
            "Views",
            "Pages",
            "RepoPullRequestPage.xaml"));

        Assert.DoesNotContain("MinMax(", source, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PullRequestListDrawerPaddingAlignsItsToggleWithTheDetailHeader()
    {
        XDocument xaml = XDocument.Load(SourcePath(
            "JitHub.WinUI",
            "Views",
            "Pages",
            "RepoPullRequestPage.xaml"));
        XElement listHost = Assert.Single(xaml.Descendants(), element =>
            (string?)element.Attribute("AutomationProperties.AutomationId") == "RepoPullRequestsListHost");

        Assert.Equal("{ThemeResource AppPadding10}", (string?)listHost.Attribute("Padding"));

        XElement inspector = Assert.Single(xaml.Descendants(), element =>
            (string?)element.Attribute("AutomationProperties.AutomationId") == "RepoPullRequestsInspector");
        Assert.Equal("{ThemeResource AppPadding16_6_10_16}", (string?)inspector.Attribute("Padding"));
    }

    [Fact]
    public void PullRequestResponsiveProbeExercisesShyHeaderAcrossEverySection()
    {
        string automation = File.ReadAllText(SourcePath(
            "JitHub.WinUI.Automation",
            "Program.cs"));
        string queryService = File.ReadAllText(SourcePath(
            "JitHub.WinUI",
            "Services",
            "PullRequests",
            "GitHubPullRequestQueryService.cs"));

        Assert.Contains("--scenario=pr-shy-header", automation, StringComparison.Ordinal);
        Assert.Contains("ExercisePullRequestShyHeaderSection", automation, StringComparison.Ordinal);
        Assert.Contains("section upward reveal", automation, StringComparison.Ordinal);
        Assert.Contains("section downward re-hide", automation, StringComparison.Ordinal);
        foreach (string section in new[] { "Conversation", "Files", "Commits", "Reviews", "Timeline" })
        {
            Assert.Contains($"RepoPullRequestsSection_{section}", automation, StringComparison.Ordinal);
        }

        Assert.Contains("IsShyHeaderAutomationScenario", queryService, StringComparison.Ordinal);
        Assert.Contains("Enumerable.Range(1, 160)", queryService, StringComparison.Ordinal);
        Assert.Contains("Enumerable.Range(1, 24)", queryService, StringComparison.Ordinal);
    }

    private static string SourcePath(params string[] segments)
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Directory.Build.props")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return Path.Combine([directory!.FullName, .. segments]);
    }
}
