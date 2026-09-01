using System;
using System.IO;
using Xunit;

namespace JitHub.WinUI.Tests.Services;

public sealed class RepositoryProgressiveNavigationContractTests
{
    [Fact]
    public void Issues_RealizesItsBoundListBeforeAwaitingProgressiveInitialization()
    {
        string source = ReadProductFile("Views", "Pages", "RepoIssuePage.xaml.cs");
        string navigation = Slice(
            source,
            "protected override void OnNavigatedTo",
            "private void CommitPerformanceReadiness");

        int realize = navigation.IndexOf("EnsureIssueListPane();", StringComparison.Ordinal);
        int initialize = navigation.IndexOf("ViewModel.InitializeForNavigationAsync(arg)", StringComparison.Ordinal);
        int awaitInitialization = navigation.IndexOf("await initialization;", StringComparison.Ordinal);
        int staleGuard = navigation.IndexOf(
            "navigationGeneration != Volatile.Read(ref _navigationGeneration)",
            StringComparison.Ordinal);

        Assert.True(realize >= 0 && realize < initialize);
        Assert.True(initialize < awaitInitialization);
        Assert.True(awaitInitialization < staleGuard);
        Assert.Contains("Interlocked.Increment(ref _navigationGeneration)", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Unloaded += RepoIssuePage_Unloaded", source, StringComparison.Ordinal);
    }

    [Fact]
    public void PullRequests_PublishesEveryProgressPageBeforeSuppressingDetailHydration()
    {
        string source = ReadProductFile(
            "ViewModels",
            "Pages",
            "RepoPullRequestPageViewModel.cs");
        string filter = Slice(
            source,
            "private async Task ApplyPullRequestListFilterAsync",
            "private void ApplyPullRequestQueryFromFilters");

        int publication = filter.IndexOf("ReplaceCollectionByKey(", StringComparison.Ordinal);
        int detailSuppression = filter.IndexOf("if (suppressDetailRefresh)", StringComparison.Ordinal);

        Assert.True(publication >= 0 && publication < detailSuppression);
        Assert.Contains("ApplyPullRequestListProjection(progress.Items, progress.Completeness);", source, StringComparison.Ordinal);
        Assert.Contains("cancellationToken: cancellationToken", source, StringComparison.Ordinal);
        Assert.Contains("CancellationTokenSource listLoad = BeginListLoad(requestId, previousUiState);", source, StringComparison.Ordinal);
        Assert.Contains("CompleteListLoad(listLoad);", source, StringComparison.Ordinal);
        Assert.Contains("PullRequestProgressiveSelectionPolicy.ResolvePreferredNumber(", source, StringComparison.Ordinal);
        Assert.Contains("public void CancelNavigationWork()", source, StringComparison.Ordinal);
        Assert.Contains("CancelActiveListLoad(restoreUiState: true);", source, StringComparison.Ordinal);
        Assert.Contains("RestorePullRequestListUiState(uiState, restoreStatusText: true);", source, StringComparison.Ordinal);
        Assert.Contains("_projectedPullRequestDetailNumber != selectedPullRequest.Number", source, StringComparison.Ordinal);
        Assert.Contains("SchedulePullRequestDetailLoad(selectedPullRequest, TimeSpan.Zero);", source, StringComparison.Ordinal);
        Assert.Contains("catch (OperationCanceledException)", source, StringComparison.Ordinal);
        Assert.Contains("MarkPullRequestListLoadIncomplete(\"Refresh timed out.\");", source, StringComparison.Ordinal);
        Assert.Contains("PullRequestSectionProjectionPolicy.IsTerminalListResult(", source, StringComparison.Ordinal);
        Assert.Contains("updateStatusText: previousUiState.OwnsDetailUi", source, StringComparison.Ordinal);
        Assert.Contains("NotifySelectedPullRequestHeaderPropertiesChanged();", filter, StringComparison.Ordinal);
    }

    [Fact]
    public void PullRequests_RecordsUserIntentBeforeDeferredPointerHydration()
    {
        string page = ReadProductFile("Views", "Pages", "RepoPullRequestPage.xaml.cs");
        string pointer = Slice(
            page,
            "private void PullRequestListItem_PointerPressed",
            "private void PrimePullRequestSelection");
        int intent = pointer.IndexOf(
            "ViewModel.RegisterPullRequestSelectionIntent(pullRequest);",
            StringComparison.Ordinal);
        int nativeSelection = pointer.IndexOf(
            "PullRequestsList.SelectedItem = pullRequest;",
            StringComparison.Ordinal);

        Assert.True(intent >= 0 && intent < nativeSelection);
        Assert.Contains("ViewModel.CommitPullRequestSelection(pullRequest);", page, StringComparison.Ordinal);

        string viewModel = ReadProductFile(
            "ViewModels",
            "Pages",
            "RepoPullRequestPageViewModel.cs");
        Assert.Contains("_selectionIntentNumber = pullRequest.Number;", viewModel, StringComparison.Ordinal);
        Assert.Contains("Interlocked.Increment(ref _selectionGeneration);", viewModel, StringComparison.Ordinal);
        Assert.Contains("GetProgressiveSelectionOwnerNumber()", viewModel, StringComparison.Ordinal);
    }

    [Fact]
    public void RepositoryPages_InvalidateLateInitializationWhenNavigationMovesAway()
    {
        string issuePage = ReadProductFile("Views", "Pages", "RepoIssuePage.xaml.cs");
        string pullRequestPage = ReadProductFile("Views", "Pages", "RepoPullRequestPage.xaml.cs");

        foreach (string page in new[] { issuePage, pullRequestPage })
        {
            Assert.Contains("long navigationGeneration = Interlocked.Increment(ref _navigationGeneration);", page, StringComparison.Ordinal);
            Assert.Contains("navigationGeneration != Volatile.Read(ref _navigationGeneration)", page, StringComparison.Ordinal);
            Assert.Contains("protected override void OnNavigatedFrom", page, StringComparison.Ordinal);
            Assert.Contains("Interlocked.Increment(ref _navigationGeneration);", page, StringComparison.Ordinal);
            Assert.Contains("ViewModel.CancelNavigationWork();", page, StringComparison.Ordinal);
        }
    }

    private static string Slice(string source, string startMarker, string endMarker)
    {
        int start = source.IndexOf(startMarker, StringComparison.Ordinal);
        int end = source.IndexOf(endMarker, start + startMarker.Length, StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start);
        return source[start..end];
    }

    private static string ReadProductFile(params string[] segments) =>
        File.ReadAllText(Path.Combine([FindRepositoryRoot(), "JitHub.WinUI", .. segments]));

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Directory.Build.props")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new DirectoryNotFoundException("Could not locate the JitHub repository root.");
    }
}
