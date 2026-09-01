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
        bool ownsNavigationLoad = string.Equals(pageName, "RepoIssuePage", StringComparison.Ordinal) ||
            string.Equals(pageName, "RepoPullRequestPage", StringComparison.Ordinal);
        string cancellationCall = ownsNavigationLoad
            ? "ViewModel.CancelNavigationWork();"
            : "ViewModel.CancelPredictivePrefetches();";
        Assert.Contains(cancellationCall, codeBehind, StringComparison.Ordinal);
        if (string.Equals(pageName, "RepoIssuePage", StringComparison.Ordinal))
        {
            string viewModel = ReadProductFile("ViewModels", "Pages", "RepoIssuePageViewModel.cs");
            Assert.Contains("public void CancelNavigationWork()", viewModel, StringComparison.Ordinal);
            Assert.Contains("CancelActiveListLoad();", viewModel, StringComparison.Ordinal);
            Assert.Contains("CancelActiveDetailLoad();", viewModel, StringComparison.Ordinal);
            Assert.Contains("CancelPendingSelectionLoad();", viewModel, StringComparison.Ordinal);
            Assert.Contains(
                "Interlocked.Increment(ref _selectionPresentationGeneration);",
                codeBehind,
                StringComparison.Ordinal);
        }
        else if (string.Equals(pageName, "RepoPullRequestPage", StringComparison.Ordinal))
        {
            string viewModel = ReadProductFile("ViewModels", "Pages", "RepoPullRequestPageViewModel.cs");
            Assert.Contains("public void CancelNavigationWork()", viewModel, StringComparison.Ordinal);
            Assert.Contains("CancelActiveListLoad(restoreUiState: true);", viewModel, StringComparison.Ordinal);
            Assert.Contains("CancelPendingSelectionLoad();", viewModel, StringComparison.Ordinal);
            Assert.Contains("CancelPredictivePrefetches();", viewModel, StringComparison.Ordinal);
        }
        else if (string.Equals(pageName, "RepoCommitsPage", StringComparison.Ordinal))
        {
            string viewModel = ReadProductFile("ViewModels", "Pages", "RepoCommitsPageViewModel.cs");
            Assert.Contains("Interlocked.Increment(ref _navigationGeneration);", viewModel, StringComparison.Ordinal);
            Assert.Contains(
                "navigationGeneration == Volatile.Read(ref _navigationGeneration)",
                viewModel,
                StringComparison.Ordinal);
        }
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

    [Fact]
    public void RepoCode_UsesLatestWinsPredictionAndFocusableTreeContainers()
    {
        string viewModel = ReadProductFile("ViewModels", "CodeViewer", "RepoCodePageViewModel.cs");
        string control = ReadProductFile("Views", "Controls", "CodeViewer", "RepoFileTreeView.xaml.cs");
        string xaml = ReadProductFile("Views", "Controls", "CodeViewer", "RepoFileTreeView.xaml");

        Assert.Contains("LatestWinsPrefetchScheduler _treeNodePrefetch", viewModel, StringComparison.Ordinal);
        Assert.Contains("_treeNodePrefetch.Schedule(", viewModel, StringComparison.Ordinal);
        Assert.Contains("_treeNodePrefetch.Cancel();", viewModel, StringComparison.Ordinal);
        Assert.Contains("TreeNodePrefetchDebounce", viewModel, StringComparison.Ordinal);
        Assert.Contains(
            "TreeNodePrefetchDebounce = TimeSpan.FromMilliseconds(500)",
            viewModel,
            StringComparison.Ordinal);
        Assert.Contains("Tree.OnCancelPrefetch = CancelTreeNodePrefetch;", viewModel, StringComparison.Ordinal);
        Assert.Contains("public void CancelTreeNodePrefetch()", viewModel, StringComparison.Ordinal);
        Assert.Contains("repo_code.file_hover_prefetch", viewModel, StringComparison.Ordinal);
        Assert.Contains("GitHubRequestPriority.Prefetch", viewModel, StringComparison.Ordinal);
        Assert.Contains("new CancellationTokenSourceLease(request)", viewModel, StringComparison.Ordinal);
        Assert.Contains("source.Cancel();", viewModel, StringComparison.Ordinal);
        Assert.Contains("container.GotFocus -= OnTreeItemContainerGotFocus;", control, StringComparison.Ordinal);
        Assert.Contains("container.GotFocus += OnTreeItemContainerGotFocus;", control, StringComparison.Ordinal);
        Assert.Contains("sender is TreeViewItem", control, StringComparison.Ordinal);
        Assert.Contains("TreeViewNode { Content: RepoTreeNodeViewModel node }", control, StringComparison.Ordinal);
        Assert.Contains("PointerEntered=\"OnTreeItemPointerEntered\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("GotFocus=\"OnTreeItemGotFocus\"", xaml, StringComparison.Ordinal);
        int selectionStart = control.IndexOf("ProductPerformanceReadiness.BeginTraversal(", StringComparison.Ordinal);
        int cancelPrefetch = control.IndexOf("ViewModel?.CancelPrefetch();", selectionStart, StringComparison.Ordinal);
        int invokeSelection = control.IndexOf("RaiseFileInvoked(nodeVm);", cancelPrefetch, StringComparison.Ordinal);
        Assert.True(selectionStart >= 0 && cancelPrefetch > selectionStart && invokeSelection > cancelPrefetch);
        Assert.Contains("repo_code.prefetch.cancelled", control, StringComparison.Ordinal);
        Assert.True(
            control.IndexOf("RaiseFileInvoked(nodeVm);", StringComparison.Ordinal) <
            control.IndexOf("ViewModel?.SelectNodeCommand.Execute(nodeVm);", StringComparison.Ordinal));
    }

    [Fact]
    public void RepoPullRequests_LeavesImmediateSelectionAheadOfHoverPrediction()
    {
        string viewModel = ReadProductFile("ViewModels", "Pages", "RepoPullRequestPageViewModel.cs");

        Assert.Contains(
            "HoverPrefetchDebounce = TimeSpan.FromMilliseconds(500)",
            viewModel,
            StringComparison.Ordinal);
        Assert.Contains("public void CancelHoverPrefetch()", viewModel, StringComparison.Ordinal);

        string codeBehind = ReadProductFile("Views", "Pages", "RepoPullRequestPage.xaml.cs");
        Assert.Contains("ViewModel.CancelHoverPrefetch();", codeBehind, StringComparison.Ordinal);
        Assert.Contains("repo_pull_requests.hover.cancelled", codeBehind, StringComparison.Ordinal);
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
