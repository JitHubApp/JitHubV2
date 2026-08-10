using System;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using Xunit;

namespace JitHub.WinUI.Tests.Services;

public sealed class PredictivePrefetchLifecycleContractTests
{
    [Theory]
    [InlineData("RepoIssuePageViewModel.cs", "PrefetchIssue", "ScheduleIssuePrefetch")]
    [InlineData("RepoPullRequestPageViewModel.cs", "PrefetchPullRequest", "_pullRequestNavigationCache.SchedulePrefetch")]
    [InlineData("RepoCommitsPageViewModel.cs", "PrefetchCommit", "ScheduleTrackedPrefetch")]
    public void DetailWorkspace_UsesLatestWinsIntentScheduling(
        string viewModelFile,
        string prefetchMethod,
        string scheduleGateway)
    {
        string source = ReadProductFile("ViewModels", "Pages", viewModelFile);

        Assert.Contains("LatestWinsPrefetchScheduler _hoverPrefetch", source, StringComparison.Ordinal);
        Assert.Contains($"public void {prefetchMethod}", source, StringComparison.Ordinal);
        Assert.Contains("_hoverPrefetch.Schedule(", source, StringComparison.Ordinal);
        Assert.Contains(scheduleGateway, source, StringComparison.Ordinal);
        Assert.Contains("public void CancelPredictivePrefetches()", source, StringComparison.Ordinal);
        Assert.Contains("_hoverPrefetch.Cancel();", source, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("RepoIssuePage", "IssuesList_ContainerContentChanging", "IssueListItemContainer_GotFocus")]
    [InlineData("RepoPullRequestPage", "PullRequestsList_ContainerContentChanging", "PullRequestListItemContainer_GotFocus")]
    [InlineData("RepoCommitsPage", "CommitsList_ContainerContentChanging", "CommitListItemContainer_GotFocus")]
    public void DetailWorkspace_RouteDepartureAndFocusableContainersOwnPrediction(
        string pageName,
        string containerChangingHandler,
        string focusHandler)
    {
        string codeBehind = ReadProductFile("Views", "Pages", pageName + ".xaml.cs");
        string xaml = ReadProductFile("Views", "Pages", pageName + ".xaml");
        if (string.Equals(pageName, "RepoIssuePage", StringComparison.Ordinal))
        {
            codeBehind += Environment.NewLine + ReadProductFile(
                "Views",
                "Controls",
                "Issue",
                "RepoIssueListPane.xaml.cs");
            xaml = ReadProductFile("Views", "Controls", "Issue", "RepoIssueListPane.xaml");
        }
        XDocument document = XDocument.Parse(xaml);

        Assert.Contains("protected override void OnNavigatedFrom", codeBehind, StringComparison.Ordinal);
        Assert.Contains("ViewModel.CancelPredictivePrefetches();", codeBehind, StringComparison.Ordinal);
        Assert.Contains($"private void {containerChangingHandler}", codeBehind, StringComparison.Ordinal);
        Assert.Contains($"container.GotFocus -= {focusHandler};", codeBehind, StringComparison.Ordinal);
        Assert.Contains($"container.GotFocus += {focusHandler};", codeBehind, StringComparison.Ordinal);
        Assert.Contains("if (args.InRecycleQueue)", codeBehind, StringComparison.Ordinal);
        Assert.Contains($"private void {focusHandler}", codeBehind, StringComparison.Ordinal);
        Assert.Contains("sender is ListViewItem { Content:", codeBehind, StringComparison.Ordinal);
        Assert.DoesNotContain(
            document.Descendants().Where(element => element.Name.LocalName == "Grid"),
            element => element.Attributes().Any(attribute => attribute.Name.LocalName == "GotFocus"));
        Assert.Contains(
            document.Descendants().Where(element => element.Name.LocalName == "ListView"),
            element => element.Attributes().Any(attribute =>
                attribute.Name.LocalName == "ContainerContentChanging" &&
                string.Equals(attribute.Value, containerChangingHandler, StringComparison.Ordinal)));
        Assert.Contains("PointerEntered=", xaml, StringComparison.Ordinal);
    }

    private static string ReadProductFile(params string[] relativeSegments)
    {
        string root = FindRepositoryRoot();
        string path = Path.Combine([root, "JitHub.WinUI", .. relativeSegments]);
        return File.ReadAllText(path);
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? current = new(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (Directory.Exists(Path.Combine(current.FullName, "JitHub.WinUI")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the JitHub repository root.");
    }
}
