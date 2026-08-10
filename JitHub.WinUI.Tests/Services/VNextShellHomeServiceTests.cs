using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using JitHub.Models.GitHub;
using JitHub.Services;
using Xunit;

namespace JitHub.WinUI.Tests.Services;

public sealed class VNextShellHomeServiceTests
{
    [Fact]
    public void DashboardWidgetLayout_LoadsDefaultWhenMissing()
    {
        DashboardWidgetLayoutService service = new(new MemorySettingService());

        DashboardWidgetLayout layout = service.Load();

        Assert.Equal(
            [DashboardWidgetIds.RecentActivity, DashboardWidgetIds.Repositories, DashboardWidgetIds.QuickActions],
            layout.MainWidgetIds);
        Assert.Equal(
            [DashboardWidgetIds.Overview, DashboardWidgetIds.RecommendedRepositories, DashboardWidgetIds.Notifications],
            layout.SideWidgetIds);
        Assert.Empty(layout.HiddenWidgetIds);
    }

    [Fact]
    public void DashboardWidgetLayout_RepairsCorruptJson()
    {
        MemorySettingService settings = new();
        settings.Save(DashboardWidgetLayoutService.SettingKey, "{not json");
        DashboardWidgetLayoutService service = new(settings);

        DashboardWidgetLayout layout = service.Load();

        Assert.Contains(DashboardWidgetIds.RecentActivity, layout.MainWidgetIds);
        Assert.Contains(DashboardWidgetIds.Overview, layout.SideWidgetIds);
    }

    [Fact]
    public void DashboardWidgetLayout_DropsUnknownAndRepairsMissingDefaults()
    {
        DashboardWidgetLayoutService service = new(new MemorySettingService());

        DashboardWidgetLayout normalized = service.Normalize(new DashboardWidgetLayout(
            1,
            ["unknown", DashboardWidgetIds.QuickActions],
            [DashboardWidgetIds.Overview],
            [DashboardWidgetIds.Repositories]));

        Assert.DoesNotContain("unknown", normalized.MainWidgetIds);
        Assert.Contains(DashboardWidgetIds.QuickActions, normalized.MainWidgetIds);
        Assert.Contains(DashboardWidgetIds.Overview, normalized.SideWidgetIds);
        Assert.Contains(DashboardWidgetIds.Repositories, normalized.HiddenWidgetIds);
        Assert.Contains(DashboardWidgetIds.RecentActivity, normalized.MainWidgetIds);
        Assert.Contains(DashboardWidgetIds.Notifications, normalized.SideWidgetIds);
    }

    [Fact]
    public void DashboardWidgetLayout_SaveRoundTrips()
    {
        MemorySettingService settings = new();
        DashboardWidgetLayoutService service = new(settings);
        DashboardWidgetLayout custom = new(
            1,
            [DashboardWidgetIds.Overview, DashboardWidgetIds.RecentActivity],
            [DashboardWidgetIds.QuickActions],
            [DashboardWidgetIds.Repositories]);

        service.Save(custom);
        DashboardWidgetLayout loaded = service.Load();

        Assert.Equal([DashboardWidgetIds.Overview, DashboardWidgetIds.RecentActivity], loaded.MainWidgetIds.Take(2).ToArray());
        Assert.Contains(DashboardWidgetIds.QuickActions, loaded.SideWidgetIds);
        Assert.Contains(DashboardWidgetIds.Repositories, loaded.HiddenWidgetIds);
    }

    [Fact]
    public void DashboardWidgetLayout_MigratesOlderDuplicateLayoutDeterministically()
    {
        DashboardWidgetLayoutService service = new(new MemorySettingService());

        DashboardWidgetLayout migrated = service.Normalize(new DashboardWidgetLayout(
            0,
            [DashboardWidgetIds.RecentActivity, DashboardWidgetIds.RecentActivity],
            [DashboardWidgetIds.Notifications, DashboardWidgetIds.Notifications],
            ["removed-widget"]));

        string[] all = migrated.MainWidgetIds
            .Concat(migrated.SideWidgetIds)
            .Concat(migrated.HiddenWidgetIds)
            .ToArray();
        Assert.Equal(DashboardWidgetIds.All.Count, all.Length);
        Assert.Equal(DashboardWidgetIds.All.Count, all.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(DashboardWidgetIds.All.OrderBy(static id => id), all.OrderBy(static id => id));
        Assert.Equal(1, migrated.Version);
    }

    [Fact]
    public async Task MeQueryService_UsesExpectedSearchAndCollectionPaths()
    {
        RecordingQueryService query = new();
        GitHubMeQueryService service = new(query);

        await service.GetIssuesAsync("token", "42", "octo", GitHubMeIssueFilter.Assigned, 30);
        await service.GetIssuesAsync("token", "42", "octo", GitHubMeIssueFilter.Mentioned, 30, GitHubMeWorkItemState.Closed);
        await service.GetIssuesPageAsync("token", "42", "octo", GitHubMeIssueFilter.Created, 500, 2, GitHubMeWorkItemState.All);
        await service.GetPullRequestsAsync("token", "42", "octo", GitHubMePullRequestFilter.Involves, 30);
        await service.GetPullRequestsPageAsync("token", "42", "octo", GitHubMePullRequestFilter.Authored, 100, 3, GitHubMeWorkItemState.Open);
        await service.GetIssueDetailAsync("token", "42", "octo", "hello-world", 17);
        await service.GetIssueCommentsAsync("token", "42", "octo", "hello-world", 17, 30);
        await service.GetIssueCommentsPageAsync("token", "42", "octo", "hello-world", 17, 100, 4);
        await service.GetStarredRepositoriesAsync("token", "42", 30);
        await service.GetGistsAsync("token", "42", 30);

        Assert.Contains(query.Paths, path => path.StartsWith("search/issues?", StringComparison.Ordinal) && path.Contains("is%3Aissue", StringComparison.Ordinal));
        Assert.Contains(query.Paths, path => path.StartsWith("search/issues?", StringComparison.Ordinal) && path.Contains("is%3Aclosed", StringComparison.Ordinal) && path.Contains("mentions%3Aocto", StringComparison.Ordinal));
        Assert.Contains(query.Paths, path => path.StartsWith("search/issues?", StringComparison.Ordinal) && path.Contains("is%3Apr", StringComparison.Ordinal));
        Assert.Contains(query.Paths, path => path.Contains("author%3Aocto", StringComparison.Ordinal) && path.Contains("per_page=100&page=2", StringComparison.Ordinal));
        Assert.Contains(query.Paths, path => path.Contains("is%3Apr", StringComparison.Ordinal) && path.Contains("page=3", StringComparison.Ordinal));
        Assert.Contains("repos/octo/hello-world/issues/17", query.Paths);
        Assert.Contains("repos/octo/hello-world/issues/17/comments?sort=created&direction=asc&per_page=30&page=1", query.Paths);
        Assert.Contains("repos/octo/hello-world/issues/17/comments?sort=created&direction=asc&per_page=100&page=4", query.Paths);
        Assert.Contains("user/starred?sort=updated&direction=desc&per_page=30&page=1", query.Paths);
        Assert.Contains("gists?per_page=30&page=1", query.Paths);
        Assert.Equal(3, query.Priorities.Count(static priority => priority == GitHubRequestPriority.BackgroundRefresh));
    }

    [Fact]
    public async Task PullRequestConversationQuery_LoadsOnlyVisibleConversationSections()
    {
        RecordingQueryService query = new();
        GitHubPullRequestQueryService service = new(query);

        PullRequestConversationAggregate? aggregate = await service.GetPullRequestConversationAsync(
            "token",
            "42",
            "octo",
            "hello-world",
            17);

        Assert.NotNull(aggregate);
        Assert.Contains("repos/octo/hello-world/pulls/17", query.Paths);
        Assert.Contains("repos/octo/hello-world/issues/17", query.Paths);
        Assert.Contains("repos/octo/hello-world/issues/17/comments?sort=created&direction=asc&per_page=100&page=1", query.Paths);
        Assert.DoesNotContain(query.Paths, path => path.Contains("/commits", StringComparison.Ordinal));
        Assert.DoesNotContain(query.Paths, path => path.Contains("/reviews", StringComparison.Ordinal));
        Assert.DoesNotContain(query.Paths, path => path.Contains("/events", StringComparison.Ordinal));
    }

    [Fact]
    public async Task IssuePrefetch_IsBoundedAndUsesPrefetchPriority()
    {
        RecordingQueryService query = new();
        GitHubMeQueryService service = new(query);

        IssuePrefetchAggregate aggregate = await service.GetIssuePrefetchAsync(
            "token", "42", "octo", "hello-world", 17);

        Assert.NotNull(aggregate.Issue);
        Assert.Equal(2, query.Paths.Count);
        Assert.All(query.Priorities, static priority => Assert.Equal(GitHubRequestPriority.Prefetch, priority));
        Assert.Contains(query.Paths, static path => path.EndsWith("/issues/17", StringComparison.Ordinal));
        Assert.Contains(query.Paths, static path => path.Contains("/issues/17/comments", StringComparison.Ordinal));
    }

    [Fact]
    public async Task PullRequestDetailQuery_PaginatesEveryCollectionSectionBeyondPageOne()
    {
        PagedPullRequestRecordingQueryService query = new();
        GitHubPullRequestQueryService service = new(query);

        PullRequestDetailAggregate? aggregate = await service.GetPullRequestDetailAsync(
            "token",
            "42",
            "octo",
            "hello-world",
            17);

        Assert.NotNull(aggregate);
        Assert.Equal(101, aggregate.Comments.Length);
        Assert.Equal(101, aggregate.Commits.Length);
        Assert.Equal(101, aggregate.Reviews.Length);
        Assert.Equal(101, aggregate.ReviewComments.Length);
        Assert.Equal(101, aggregate.TimelineEvents.Length);
        Assert.Contains(query.Paths, path => path.Contains("issues/17/comments", StringComparison.Ordinal) && path.EndsWith("page=2", StringComparison.Ordinal));
        Assert.Contains(query.Paths, path => path.Contains("pulls/17/commits", StringComparison.Ordinal) && path.EndsWith("page=2", StringComparison.Ordinal));
        Assert.Contains(query.Paths, path => path.Contains("pulls/17/reviews", StringComparison.Ordinal) && path.EndsWith("page=2", StringComparison.Ordinal));
        Assert.Contains(query.Paths, path => path.Contains("pulls/17/comments", StringComparison.Ordinal) && path.EndsWith("page=2", StringComparison.Ordinal));
        Assert.Contains(query.Paths, path => path.Contains("issues/17/events", StringComparison.Ordinal) && path.EndsWith("page=2", StringComparison.Ordinal));
    }

    [Fact]
    public async Task PullRequestList_PaginatesProgressivelyBeyondFirstPage()
    {
        PagedPullRequestRecordingQueryService query = new();
        GitHubPullRequestQueryService service = new(query);
        List<PullRequestPagedSection<GitHubPullRequest>> progress = [];

        PullRequestPagedSection<GitHubPullRequest> result = await service.GetAllPullRequestsAsync(
            "token",
            "42",
            "octo",
            "hello-world",
            new GitHubPullRequestQueryOptions(),
            progress.Add);

        Assert.Equal(101, result.Items.Length);
        Assert.Equal(PagedDataCompleteness.Complete, result.Completeness);
        Assert.Contains(query.Paths, path => path.Contains("/pulls?", StringComparison.Ordinal) && path.EndsWith("page=2", StringComparison.Ordinal));
        Assert.Contains(progress, snapshot => snapshot.Items.Length == 100 && snapshot.Completeness == PagedDataCompleteness.Loading);
        Assert.Equal(101, progress[^1].Items.Length);
        Assert.Equal(PagedDataCompleteness.Complete, progress[^1].Completeness);
    }

    [Fact]
    public async Task PullRequestList_RefreshesStaleShortPageBeforeDeclaringComplete()
    {
        StaleShortPullRequestQueryService query = new();
        GitHubPullRequestQueryService service = new(query);

        PullRequestPagedSection<GitHubPullRequest> result = await service.GetAllPullRequestsAsync(
            "token",
            "42",
            "octo",
            "hello-world",
            new GitHubPullRequestQueryOptions());

        Assert.Equal(101, result.Items.Length);
        Assert.Equal(2, result.LoadedPageCount);
        Assert.Equal(PagedDataCompleteness.Complete, result.Completeness);
        Assert.Equal(2, query.RefreshCalls);
    }

    [Fact]
    public async Task PullRequestList_LaterPageFailurePreservesLoadedPrefix()
    {
        PagedPullRequestRecordingQueryService query = new(failSecondPullRequestPage: true);
        GitHubPullRequestQueryService service = new(query);

        PullRequestPagedSection<GitHubPullRequest> result = await service.GetAllPullRequestsAsync(
            "token",
            "42",
            "octo",
            "hello-world",
            new GitHubPullRequestQueryOptions());

        Assert.Equal(100, result.Items.Length);
        Assert.Equal(1, result.LoadedPageCount);
        Assert.Equal(PagedDataCompleteness.Partial, result.Completeness);
        Assert.NotNull(result.State.ErrorMessage);
    }

    [Fact]
    public async Task PullRequestPrefetch_IsBoundedToVisibleConversationAndUsesPrefetchPriority()
    {
        PagedPullRequestRecordingQueryService query = new();
        GitHubPullRequestQueryService service = new(query);

        PullRequestConversationAggregate? aggregate = await service.GetPullRequestPrefetchAsync(
            "token", "42", "octo", "hello-world", 17);

        Assert.NotNull(aggregate);
        Assert.Equal(3, query.Paths.Count);
        Assert.All(query.Priorities, static priority => Assert.Equal(GitHubRequestPriority.Prefetch, priority));
        Assert.DoesNotContain(query.Paths, static path => path.Contains("/commits", StringComparison.Ordinal));
        Assert.DoesNotContain(query.Paths, static path => path.Contains("/reviews", StringComparison.Ordinal));
        Assert.DoesNotContain(query.Paths, static path => path.Contains("/events", StringComparison.Ordinal));
    }

    [Fact]
    public async Task PullRequestPagedSection_LaterPageFailureKeepsLoadedRowsAndReportsError()
    {
        PagedPullRequestRecordingQueryService query = new(failSecondCommentsPage: true);
        GitHubPullRequestQueryService service = new(query);

        PullRequestPagedSection<GitHubIssueComment> result = await service.GetAllPullRequestCommentsAsync(
            "token",
            "42",
            "octo",
            "hello-world",
            17);

        Assert.Equal(100, result.Items.Length);
        Assert.NotNull(result.State.ErrorMessage);
        Assert.Equal(1, result.LoadedPageCount);
        Assert.Equal(PagedDataCompleteness.Partial, result.Completeness);
    }

    [Fact]
    public async Task PullRequestCommits_ReportsGitHubsExplicitTwoHundredFiftyCommitLimit()
    {
        PagedPullRequestRecordingQueryService query = new(fullCommitPages: true);
        GitHubPullRequestQueryService service = new(query);

        PullRequestPagedSection<GitHubCommit> result = await service.GetAllPullRequestCommitsAsync(
            "token", "42", "octo", "hello-world", 17);

        Assert.Equal(250, result.Items.Length);
        Assert.Equal(PagedDataCompleteness.ApiLimited, result.Completeness);
        Assert.Equal(250, result.ApiLimit);
        Assert.Equal(250, result.State.LoadedItemCount);
        Assert.Equal(PagedDataCompleteness.ApiLimited, result.State.Completeness);
    }

    [Fact]
    public async Task IssueNavigationCache_StoresAndPrefetchesSnapshots()
    {
        FakeMeQueryService query = new();
        IssueNavigationCache cache = new(query);
        GitHubIssue seededIssue = CreateIssue(42, "Seeded");
        GitHubIssueComment seededComment = CreateComment(1, "Cached comment");

        cache.Store("42", new IssueNavigationSnapshot(
            "octo",
            "hello-world",
            42,
            seededIssue,
            [seededComment],
            DateTimeOffset.UtcNow,
            "test"));

        Assert.True(cache.TryGet("42", "octo", "hello-world", 42, out IssueNavigationSnapshot seeded));
        Assert.Equal("Seeded", seeded.Issue.Title);
        Assert.Single(seeded.Comments);

        await cache.PrefetchAsync("token", "42", "octo", "hello-world", 43, IssuePrefetchReason.Hover);

        Assert.True(cache.TryGet("42", "octo", "hello-world", 43, out IssueNavigationSnapshot prefetched));
        Assert.Equal("Prefetched 43", prefetched.Issue.Title);
        Assert.Equal("Prefetched comment 43", prefetched.Comments.Single().Body);
        Assert.Equal(1, query.DetailCalls);
        Assert.Equal(1, query.CommentCalls);
    }

    [Fact]
    public async Task IssueNavigationCache_CancelledScheduledPrefetchDoesNotFetch()
    {
        FakeMeQueryService query = new();
        IssueNavigationCache cache = new(query);

        using (IDisposable scheduled = cache.SchedulePrefetch(
            "token",
            "42",
            "octo",
            "hello-world",
            44,
            IssuePrefetchReason.Dwell,
            TimeSpan.FromMilliseconds(200)))
        {
            scheduled.Dispose();
        }

        await Task.Delay(260);

        Assert.False(cache.TryGet("42", "octo", "hello-world", 44, out _));
        Assert.Equal(0, query.DetailCalls);
        Assert.Equal(0, query.CommentCalls);
    }

    [Fact]
    public async Task IssueNavigationCache_DisposingCompletedScheduledPrefetchIsNoOp()
    {
        FakeMeQueryService query = new();
        IssueNavigationCache cache = new(query);

        IDisposable scheduled = cache.SchedulePrefetch(
            "token",
            "42",
            "octo",
            "hello-world",
            45,
            IssuePrefetchReason.Neighbor,
            TimeSpan.Zero);

        await Task.Delay(80);

        scheduled.Dispose();
        scheduled.Dispose();

        Assert.True(cache.TryGet("42", "octo", "hello-world", 45, out IssueNavigationSnapshot prefetched));
        Assert.Equal("Prefetched 45", prefetched.Issue.Title);
    }

    [Fact]
    public void IssueNavigationCache_IsolatesSnapshotsByAccountPartition()
    {
        IssueNavigationCache cache = new(new FakeMeQueryService());
        cache.Store("account-a", new IssueNavigationSnapshot(
            "octo",
            "hello-world",
            46,
            CreateIssue(46, "Account A"),
            [CreateComment(46, "Private to A")],
            DateTimeOffset.UtcNow,
            "test"));

        Assert.True(cache.TryGet("account-a", "octo", "hello-world", 46, out IssueNavigationSnapshot accountA));
        Assert.False(cache.TryGet("account-b", "octo", "hello-world", 46, out _));
        Assert.False(cache.TryGet(string.Empty, "octo", "hello-world", 46, out _));
        Assert.False(cache.TryGet("current", "octo", "hello-world", 46, out _));
        Assert.False(cache.TryGet("anonymous", "octo", "hello-world", 46, out _));
        Assert.Equal("Private to A", accountA.Comments.Single().Body);
    }

    [Fact]
    public async Task IssueNavigationCache_ClearPartitionRemovesOnlyRequestedAccount()
    {
        IssueNavigationCache cache = new(new FakeMeQueryService());
        IssueNavigationSnapshot snapshot = new(
            "octo",
            "hello-world",
            46,
            CreateIssue(46, "Private issue"),
            [CreateComment(46, "Private comment")],
            DateTimeOffset.UtcNow,
            "test");
        cache.Store("account-a", snapshot);
        cache.Store("account-b", snapshot);

        await cache.ClearPartitionAsync("account-a");

        Assert.False(cache.TryGet("account-a", "octo", "hello-world", 46, out _));
        Assert.True(cache.TryGet("account-b", "octo", "hello-world", 46, out _));
    }

    [Fact]
    public async Task IssueNavigationCache_QuiescencePreventsLateScheduledPrefetchFromRepopulatingClearedPartition()
    {
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        FakeMeQueryService query = new()
        {
            PrefetchEntered = entered,
            PrefetchRelease = release
        };
        using ApplicationTaskCoordinator coordinator = new();
        AccountWorkQuiescence accountWork = new(coordinator);
        IssueNavigationCache cache = new(query, accountWork, coordinator);

        using IDisposable scheduled = cache.SchedulePrefetch(
            "token",
            "42",
            "octo",
            "hello-world",
            47,
            IssuePrefetchReason.Hover,
            TimeSpan.Zero);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Task cancel = coordinator.CancelAccountAsync("42");
        Task quiesce = accountWork.QuiesceAsync("42");
        await Task.Delay(30);
        Assert.False(cancel.IsCompleted);
        Assert.False(quiesce.IsCompleted);

        release.SetResult();
        await Task.WhenAll(cancel, quiesce).WaitAsync(TimeSpan.FromSeconds(2));
        await cache.ClearPartitionAsync("42");

        Assert.False(cache.TryGet("42", "octo", "hello-world", 47, out _));
    }

    [Fact]
    public async Task PullRequestNavigationCache_StoresAndPrefetchesAggregateSnapshots()
    {
        FakePullRequestQueryService query = new();
        PullRequestNavigationCache cache = new(query);
        GitHubPullRequest seededPullRequest = CreatePullRequest(42, "Seeded PR");
        GitHubIssue seededIssue = CreateIssue(42, "Seeded PR");
        GitHubIssueComment seededComment = CreateComment(1, "Cached PR comment");

        cache.Store("42", new PullRequestNavigationSnapshot(
            "octo",
            "hello-world",
            42,
            seededPullRequest,
            seededIssue,
            [seededComment],
            [],
            [],
            [],
            [],
            DateTimeOffset.UtcNow,
            "test"));

        Assert.True(cache.TryGet("42", "octo", "hello-world", 42, out PullRequestNavigationSnapshot seeded));
        Assert.Equal("Seeded PR", seeded.PullRequest.Title);
        Assert.Single(seeded.Comments);

        await cache.PrefetchAsync("token", "42", "octo", "hello-world", 43, PullRequestPrefetchReason.Hover);

        Assert.True(cache.TryGet("42", "octo", "hello-world", 43, out PullRequestNavigationSnapshot prefetched));
        Assert.Equal("Prefetched PR 43", prefetched.PullRequest.Title);
        Assert.Equal("Prefetched PR comment 43", prefetched.Comments.Single().Body);
        Assert.Empty(prefetched.Commits);
        Assert.Empty(prefetched.Reviews);
        Assert.Empty(prefetched.ReviewComments);
        Assert.Empty(prefetched.TimelineEvents);
        Assert.Equal(1, query.PrefetchCalls);
    }

    [Fact]
    public async Task PullRequestNavigationCache_CancelledScheduledPrefetchDoesNotFetch()
    {
        FakePullRequestQueryService query = new();
        PullRequestNavigationCache cache = new(query);
        TaskCompletionSource<PullRequestPrefetchResult> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

        using (IDisposable scheduled = cache.SchedulePrefetch(
            "token",
            "42",
            "octo",
            "hello-world",
            44,
            PullRequestPrefetchReason.Dwell,
            TimeSpan.FromMilliseconds(200),
            (result, _) => completion.TrySetResult(result)))
        {
            scheduled.Dispose();
        }

        PullRequestPrefetchResult result = await completion.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(PullRequestPrefetchResult.Cancelled, result);
        Assert.False(cache.TryGet("42", "octo", "hello-world", 44, out _));
        Assert.Equal(0, query.AggregateCalls);
    }

    [Fact]
    public async Task PullRequestNavigationCache_ScheduledPrefetchReportsSuccessAfterSnapshotIsUsable()
    {
        PullRequestNavigationCache cache = new(new FakePullRequestQueryService());
        TaskCompletionSource<PullRequestPrefetchResult> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

        using IDisposable scheduled = cache.SchedulePrefetch(
            "token",
            "42",
            "octo",
            "hello-world",
            45,
            PullRequestPrefetchReason.Neighbor,
            TimeSpan.Zero,
            (result, _) => completion.TrySetResult(result));

        PullRequestPrefetchResult result = await completion.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(PullRequestPrefetchResult.Success, result);
        Assert.True(cache.TryGet("42", "octo", "hello-world", 45, out _));
    }

    [Fact]
    public void PullRequestNavigationCache_IsolatesAccountsAndPreservesRichHandoffSnapshot()
    {
        PullRequestNavigationCache cache = new(new FakePullRequestQueryService());
        PullRequestNavigationSnapshot rich = new(
            "octo",
            "hello-world",
            47,
            CreatePullRequest(47, "Rich pull request"),
            CreateIssue(47, "Rich pull request"),
            [CreateComment(47, "Cached conversation")],
            [new GitHubCommit { Sha = "rich-sha" }],
            [new GitHubPullRequestReview { Id = 47, State = "APPROVED" }],
            [new GitHubPullRequestReviewComment { Id = 47, Body = "Review context" }],
            [new GitHubIssueEvent { Id = 47, Event = "reviewed" }],
            DateTimeOffset.UtcNow,
            "prefetch");
        cache.Store("account-a", rich);

        PullRequestNavigationSnapshot handoff = new(
            "octo",
            "hello-world",
            47,
            CreatePullRequest(47, "Minimal handoff"),
            CreateIssue(47, "Minimal handoff"),
            [],
            [],
            [],
            [],
            [],
            DateTimeOffset.UtcNow,
            "navigation-handoff");
        cache.Store("account-a", handoff, PullRequestNavigationStoreMode.PreservePopulatedSections);

        Assert.True(cache.TryGet("account-a", "octo", "hello-world", 47, out PullRequestNavigationSnapshot merged));
        Assert.Equal("Rich pull request", merged.PullRequest.Title);
        Assert.Equal("Cached conversation", merged.Comments.Single().Body);
        Assert.Equal("rich-sha", merged.Commits.Single().Sha);
        Assert.Single(merged.Reviews);
        Assert.Single(merged.ReviewComments);
        Assert.Single(merged.TimelineEvents);
        Assert.False(cache.TryGet("account-b", "octo", "hello-world", 47, out _));
        Assert.False(cache.TryGet(string.Empty, "octo", "hello-world", 47, out _));
        Assert.False(cache.TryGet("current", "octo", "hello-world", 47, out _));
        Assert.False(cache.TryGet("anonymous", "octo", "hello-world", 47, out _));
    }

    [Fact]
    public async Task PullRequestNavigationCache_ClearPartitionRemovesOnlyRequestedAccount()
    {
        PullRequestNavigationCache cache = new(new FakePullRequestQueryService());
        PullRequestNavigationSnapshot snapshot = new(
            "octo",
            "hello-world",
            47,
            CreatePullRequest(47, "Private pull request"),
            CreateIssue(47, "Private pull request"),
            [CreateComment(47, "Private comment")],
            [],
            [],
            [],
            [],
            DateTimeOffset.UtcNow,
            "test");
        cache.Store("account-a", snapshot);
        cache.Store("account-b", snapshot);

        await cache.ClearPartitionAsync("account-a");

        Assert.False(cache.TryGet("account-a", "octo", "hello-world", 47, out _));
        Assert.True(cache.TryGet("account-b", "octo", "hello-world", 47, out _));
    }

    [Fact]
    public async Task PullRequestNavigationCache_QuiescencePreventsLateScheduledPrefetchFromRepopulatingClearedPartition()
    {
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        FakePullRequestQueryService query = new()
        {
            PrefetchEntered = entered,
            PrefetchRelease = release
        };
        using ApplicationTaskCoordinator coordinator = new();
        AccountWorkQuiescence accountWork = new(coordinator);
        PullRequestNavigationCache cache = new(query, accountWork, coordinator);

        using IDisposable scheduled = cache.SchedulePrefetch(
            "token",
            "42",
            "octo",
            "hello-world",
            48,
            PullRequestPrefetchReason.Hover,
            TimeSpan.Zero);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Task cancel = coordinator.CancelAccountAsync("42");
        Task quiesce = accountWork.QuiesceAsync("42");
        await Task.Delay(30);
        Assert.False(cancel.IsCompleted);
        Assert.False(quiesce.IsCompleted);

        release.SetResult();
        await Task.WhenAll(cancel, quiesce).WaitAsync(TimeSpan.FromSeconds(2));
        await cache.ClearPartitionAsync("42");

        Assert.False(cache.TryGet("42", "octo", "hello-world", 48, out _));
    }

    [Fact]
    public void PullRequestNavigationCache_DoesNotReviveExpiredRichSnapshotDuringHandoff()
    {
        PullRequestNavigationCache cache = new(new FakePullRequestQueryService());
        cache.Store("account-a", new PullRequestNavigationSnapshot(
            "octo",
            "hello-world",
            48,
            CreatePullRequest(48, "Expired rich pull request"),
            CreateIssue(48, "Expired rich pull request"),
            [CreateComment(48, "Expired comment")],
            [new GitHubCommit { Sha = "expired-sha" }],
            [],
            [],
            [],
            DateTimeOffset.UtcNow.AddMinutes(-20),
            "old-prefetch"));

        cache.Store(
            "account-a",
            new PullRequestNavigationSnapshot(
                "octo",
                "hello-world",
                48,
                CreatePullRequest(48, "Current handoff"),
                CreateIssue(48, "Current handoff"),
                [],
                [],
                [],
                [],
                [],
                DateTimeOffset.UtcNow,
                "navigation-handoff"),
            PullRequestNavigationStoreMode.PreservePopulatedSections);

        Assert.True(cache.TryGet("account-a", "octo", "hello-world", 48, out PullRequestNavigationSnapshot current));
        Assert.Equal("Current handoff", current.PullRequest.Title);
        Assert.Empty(current.Comments);
        Assert.Empty(current.Commits);
    }

    [Fact]
    public async Task PullRequestNavigationCache_FailedSectionRefreshPreservesCachedRows()
    {
        PullRequestSectionState fresh = new(CacheState.Fresh);
        FakePullRequestQueryService query = new()
        {
            AggregateResult = new PullRequestDetailAggregate(
                CreatePullRequest(49, "Refreshed pull request"),
                CreateIssue(49, "Refreshed pull request"),
                [],
                [new GitHubCommit { Sha = "new-sha" }],
                [],
                [],
                [],
                fresh,
                fresh,
                new PullRequestSectionState(CacheState.Miss, ErrorMessage: "Comments failed"),
                fresh,
                fresh,
                fresh,
                fresh)
        };
        PullRequestNavigationCache cache = new(query);
        cache.Store("account-a", new PullRequestNavigationSnapshot(
            "octo",
            "hello-world",
            49,
            CreatePullRequest(49, "Cached pull request"),
            CreateIssue(49, "Cached pull request"),
            [CreateComment(49, "Preserve me")],
            [new GitHubCommit { Sha = "old-sha" }],
            [],
            [],
            [],
            DateTimeOffset.UtcNow.AddMinutes(-3),
            "prefetch"));

        await cache.PrefetchAsync(
            "token",
            "account-a",
            "octo",
            "hello-world",
            49,
            PullRequestPrefetchReason.Hover);

        Assert.True(cache.TryGet("account-a", "octo", "hello-world", 49, out PullRequestNavigationSnapshot refreshed));
        Assert.Equal("Preserve me", refreshed.Comments.Single().Body);
        Assert.Equal("old-sha", refreshed.Commits.Single().Sha);
    }

    private static GitHubIssue CreateIssue(int number, string title) => new()
    {
        Number = number,
        Title = title,
        State = "open",
        HtmlUrl = $"https://github.com/octo/hello-world/issues/{number}",
        RepositoryUrl = "https://api.github.com/repos/octo/hello-world",
        CreatedAt = DateTimeOffset.UtcNow.AddDays(-1),
        UpdatedAt = DateTimeOffset.UtcNow,
        User = new GitHubActor { Login = "octo" }
    };

    private static GitHubPullRequest CreatePullRequest(int number, string title) => new()
    {
        Id = number,
        Number = number,
        Title = title,
        Body = $"Body {number}",
        State = "open",
        HtmlUrl = $"https://github.com/octo/hello-world/pull/{number}",
        Comments = 1,
        CreatedAt = DateTimeOffset.UtcNow.AddDays(-1),
        UpdatedAt = DateTimeOffset.UtcNow,
        User = new GitHubActor { Login = "octo" },
        Head = new GitHubPullRequestBranch { GitRef = "feature", Label = "octo:feature" },
        Base = new GitHubPullRequestBranch { GitRef = "main", Label = "octo:main" }
    };

    private static GitHubIssueComment CreateComment(long id, string body) => new()
    {
        Id = id,
        Body = body,
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow,
        User = new GitHubActor { Login = "octo" }
    };

    private sealed class RecordingQueryService : IGitHubQueryService
    {
        public List<string> Paths { get; } = [];

        public List<GitHubRequestPriority> Priorities { get; } = [];

        public Task<CachedResult<T>> GetAsync<T>(
            GitHubQuery<T> query,
            QueryFetchPolicy fetchPolicy,
            CancellationToken cancellationToken = default)
            where T : class
        {
            Paths.Add(query.RelativePath);
            Priorities.Add(query.Priority);
            object payload = typeof(T) == typeof(GitHubSearchIssuesResponse)
                ? new GitHubSearchIssuesResponse()
                : typeof(T) == typeof(GitHubPullRequest)
                    ? new GitHubPullRequest { Number = 17 }
                : typeof(T) == typeof(GitHubIssue)
                    ? new GitHubIssue()
                : typeof(T) == typeof(GitHubRepository[])
                    ? Array.Empty<GitHubRepository>()
                    : typeof(T) == typeof(GitHubGist[])
                        ? Array.Empty<GitHubGist>()
                        : typeof(T) == typeof(GitHubIssueComment[])
                            ? Array.Empty<GitHubIssueComment>()
                            : throw new InvalidOperationException(typeof(T).FullName);
            return Task.FromResult(new CachedResult<T>(
                (T)payload,
                CacheState.Fresh,
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow.AddMinutes(5)));
        }

        public Task<CachedResult<T>> RefreshAsync<T>(
            GitHubQuery<T> query,
            CancellationToken cancellationToken = default)
            where T : class =>
            GetAsync(query, QueryFetchPolicy.NetworkOnly, cancellationToken);

        public Task InvalidateAsync(string cacheKey, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task InvalidateTagsAsync(IReadOnlyCollection<string> tags, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class FakeMeQueryService : IGitHubMeQueryService
    {
        public int DetailCalls { get; private set; }

        public int CommentCalls { get; private set; }

        public TaskCompletionSource? PrefetchEntered { get; init; }

        public TaskCompletionSource? PrefetchRelease { get; init; }

        public async Task<CachedResult<GitHubIssue>> GetIssueDetailAsync(
            string accessToken,
            string userId,
            string owner,
            string repositoryName,
            int issueNumber,
            CancellationToken cancellationToken = default)
        {
            DetailCalls++;
            PrefetchEntered?.TrySetResult();
            if (PrefetchRelease is not null)
            {
                await PrefetchRelease.Task.ConfigureAwait(false);
            }

            return new CachedResult<GitHubIssue>(
                CreateIssue(issueNumber, $"Prefetched {issueNumber}"),
                CacheState.Fresh,
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow.AddMinutes(5));
        }

        public Task<CachedResult<GitHubIssueComment[]>> GetIssueCommentsAsync(
            string accessToken,
            string userId,
            string owner,
            string repositoryName,
            int issueNumber,
            int pageSize,
            CancellationToken cancellationToken = default)
        {
            CommentCalls++;
            return Task.FromResult(new CachedResult<GitHubIssueComment[]>(
                [CreateComment(issueNumber, $"Prefetched comment {issueNumber}")],
                CacheState.Fresh,
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow.AddMinutes(5)));
        }

        public Task<CachedResult<GitHubIssueComment[]>> GetIssueCommentsPageAsync(
            string accessToken,
            string userId,
            string owner,
            string repositoryName,
            int issueNumber,
            int pageSize,
            int page,
            CancellationToken cancellationToken = default) =>
            GetIssueCommentsAsync(accessToken, userId, owner, repositoryName, issueNumber, pageSize, cancellationToken);

        public Task<CachedResult<GitHubIssueComment[]>> RefreshIssueCommentsPageAsync(
            string accessToken,
            string userId,
            string owner,
            string repositoryName,
            int issueNumber,
            int pageSize,
            int page,
            CancellationToken cancellationToken = default) =>
            GetIssueCommentsPageAsync(accessToken, userId, owner, repositoryName, issueNumber, pageSize, page, cancellationToken);

        public Task<CachedResult<GitHubSearchIssuesResponse>> GetIssuesAsync(
            string accessToken,
            string userId,
            string login,
            GitHubMeIssueFilter filter,
            int pageSize,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<CachedResult<GitHubSearchIssuesResponse>> GetIssuesAsync(
            string accessToken,
            string userId,
            string login,
            GitHubMeIssueFilter filter,
            int pageSize,
            GitHubMeWorkItemState state,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<CachedResult<GitHubSearchIssuesResponse>> GetIssuesPageAsync(
            string accessToken,
            string userId,
            string login,
            GitHubMeIssueFilter filter,
            int pageSize,
            int page,
            GitHubMeWorkItemState state,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<CachedResult<GitHubSearchIssuesResponse>> RefreshIssuesPageAsync(
            string accessToken,
            string userId,
            string login,
            GitHubMeIssueFilter filter,
            int pageSize,
            int page,
            GitHubMeWorkItemState state,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<CachedResult<GitHubSearchIssuesResponse>> GetPullRequestsAsync(
            string accessToken,
            string userId,
            string login,
            GitHubMePullRequestFilter filter,
            int pageSize,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<CachedResult<GitHubSearchIssuesResponse>> GetPullRequestsAsync(
            string accessToken,
            string userId,
            string login,
            GitHubMePullRequestFilter filter,
            int pageSize,
            GitHubMeWorkItemState state,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<CachedResult<GitHubSearchIssuesResponse>> GetPullRequestsPageAsync(
            string accessToken,
            string userId,
            string login,
            GitHubMePullRequestFilter filter,
            int pageSize,
            int page,
            GitHubMeWorkItemState state,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<CachedResult<GitHubSearchIssuesResponse>> RefreshPullRequestsPageAsync(
            string accessToken,
            string userId,
            string login,
            GitHubMePullRequestFilter filter,
            int pageSize,
            int page,
            GitHubMeWorkItemState state,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<CachedResult<GitHubRepository[]>> GetStarredRepositoriesAsync(
            string accessToken,
            string userId,
            int pageSize,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<CachedResult<GitHubGist[]>> GetGistsAsync(
            string accessToken,
            string userId,
            int pageSize,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class PagedPullRequestRecordingQueryService(
        bool failSecondCommentsPage = false,
        bool fullCommitPages = false,
        bool failSecondPullRequestPage = false) : IGitHubQueryService
    {
        public List<string> Paths { get; } = [];

        public List<GitHubRequestPriority> Priorities { get; } = [];

        public Task<CachedResult<T>> GetAsync<T>(
            GitHubQuery<T> query,
            QueryFetchPolicy fetchPolicy,
            CancellationToken cancellationToken = default)
            where T : class
        {
            Paths.Add(query.RelativePath);
            Priorities.Add(query.Priority);
            string pageText = query.RelativePath[(query.RelativePath.LastIndexOf("page=", StringComparison.Ordinal) + 5)..];
            int page = int.TryParse(pageText, out int parsedPage) ? parsedPage : 1;
            if (failSecondCommentsPage &&
                page == 2 &&
                query.RelativePath.Contains("issues/17/comments", StringComparison.Ordinal))
            {
                throw new GitHubApiException(System.Net.HttpStatusCode.ServiceUnavailable, "page failed");
            }

            if (failSecondPullRequestPage &&
                page == 2 &&
                typeof(T) == typeof(GitHubPullRequest[]))
            {
                throw new GitHubApiException(System.Net.HttpStatusCode.ServiceUnavailable, "page failed");
            }

            int count = fullCommitPages && typeof(T) == typeof(GitHubCommit[])
                ? 100
                : page == 1 ? 100 : 1;
            object payload = typeof(T) == typeof(GitHubPullRequest)
                ? new GitHubPullRequest { Number = 17 }
                : typeof(T) == typeof(GitHubPullRequest[])
                    ? Enumerable.Range((page - 1) * 100 + 1, count)
                        .Select(id => new GitHubPullRequest { Number = id })
                        .ToArray()
                : typeof(T) == typeof(GitHubIssue)
                    ? new GitHubIssue { Number = 17 }
                    : typeof(T) == typeof(GitHubIssueComment[])
                        ? Enumerable.Range((page - 1) * 100 + 1, count)
                            .Select(id => new GitHubIssueComment { Id = id, Body = $"comment {id}" })
                            .ToArray()
                        : typeof(T) == typeof(GitHubCommit[])
                            ? Enumerable.Range((page - 1) * 100 + 1, count)
                                .Select(id => new GitHubCommit { Sha = $"sha-{id}" })
                                .ToArray()
                            : typeof(T) == typeof(GitHubPullRequestReview[])
                                ? Enumerable.Range((page - 1) * 100 + 1, count)
                                    .Select(id => new GitHubPullRequestReview { Id = id })
                                    .ToArray()
                                : typeof(T) == typeof(GitHubPullRequestReviewComment[])
                                    ? Enumerable.Range((page - 1) * 100 + 1, count)
                                        .Select(id => new GitHubPullRequestReviewComment { Id = id })
                                        .ToArray()
                                    : typeof(T) == typeof(GitHubIssueEvent[])
                                        ? Enumerable.Range((page - 1) * 100 + 1, count)
                                            .Select(id => new GitHubIssueEvent { Id = id })
                                            .ToArray()
                                        : throw new InvalidOperationException(typeof(T).FullName);
            DateTimeOffset now = DateTimeOffset.UtcNow;
            return Task.FromResult(new CachedResult<T>((T)payload, CacheState.Fresh, now, now.AddMinutes(5)));
        }

        public Task<CachedResult<T>> RefreshAsync<T>(
            GitHubQuery<T> query,
            CancellationToken cancellationToken = default)
            where T : class =>
            GetAsync(query, QueryFetchPolicy.NetworkOnly, cancellationToken);

        public Task InvalidateAsync(string cacheKey, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task InvalidateTagsAsync(IReadOnlyCollection<string> tags, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class StaleShortPullRequestQueryService : IGitHubQueryService
    {
        public int RefreshCalls { get; private set; }

        public Task<CachedResult<T>> GetAsync<T>(
            GitHubQuery<T> query,
            QueryFetchPolicy fetchPolicy,
            CancellationToken cancellationToken = default)
            where T : class
        {
            Assert.Equal(typeof(GitHubPullRequest[]), typeof(T));
            object payload = new[] { new GitHubPullRequest { Number = 1 } };
            DateTimeOffset now = DateTimeOffset.UtcNow;
            return Task.FromResult(new CachedResult<T>(
                (T)payload,
                CacheState.Stale,
                now.AddHours(-1),
                now.AddMinutes(-30),
                IsRefreshInProgress: true));
        }

        public Task<CachedResult<T>> RefreshAsync<T>(
            GitHubQuery<T> query,
            CancellationToken cancellationToken = default)
            where T : class
        {
            RefreshCalls++;
            int page = query.RelativePath.EndsWith("page=2", StringComparison.Ordinal) ? 2 : 1;
            int count = page == 1 ? 100 : 1;
            object payload = Enumerable.Range((page - 1) * 100 + 1, count)
                .Select(id => new GitHubPullRequest { Number = id })
                .ToArray();
            DateTimeOffset now = DateTimeOffset.UtcNow;
            return Task.FromResult(new CachedResult<T>(
                (T)payload,
                CacheState.Fresh,
                now,
                now.AddMinutes(5)));
        }

        public Task InvalidateAsync(string cacheKey, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task InvalidateTagsAsync(IReadOnlyCollection<string> tags, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class FakePullRequestQueryService : IGitHubPullRequestQueryService
    {
        public int AggregateCalls { get; private set; }

        public int PrefetchCalls { get; private set; }

        public TaskCompletionSource? PrefetchEntered { get; init; }

        public TaskCompletionSource? PrefetchRelease { get; init; }

        public PullRequestDetailAggregate? AggregateResult { get; init; }

        public Task<CachedResult<GitHubPullRequest[]>> GetPullRequestsAsync(
            string accessToken,
            string userId,
            string owner,
            string repositoryName,
            GitHubPullRequestQueryOptions queryOptions,
            int pageSize,
            int pageNumber = 1,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<CachedResult<GitHubPullRequest>> GetPullRequestAsync(
            string accessToken,
            string userId,
            string owner,
            string repositoryName,
            int pullRequestNumber,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<CachedResult<GitHubIssue>> GetPullRequestIssueAsync(
            string accessToken,
            string userId,
            string owner,
            string repositoryName,
            int pullRequestNumber,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<CachedResult<GitHubIssueComment[]>> GetPullRequestCommentsAsync(
            string accessToken,
            string userId,
            string owner,
            string repositoryName,
            int pullRequestNumber,
            int pageSize,
            int pageNumber = 1,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(CreateCached(pageNumber == 1 ? AggregateResult?.Comments ?? [] : []));

        public Task<CachedResult<GitHubCommit[]>> GetPullRequestCommitsAsync(
            string accessToken,
            string userId,
            string owner,
            string repositoryName,
            int pullRequestNumber,
            int pageSize,
            int pageNumber = 1,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(CreateCached(pageNumber == 1 ? AggregateResult?.Commits ?? [] : []));

        public Task<CachedResult<GitHubPullRequestReview[]>> GetPullRequestReviewsAsync(
            string accessToken,
            string userId,
            string owner,
            string repositoryName,
            int pullRequestNumber,
            int pageSize,
            int pageNumber = 1,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(CreateCached(pageNumber == 1 ? AggregateResult?.Reviews ?? [] : []));

        public Task<CachedResult<GitHubPullRequestReviewComment[]>> GetPullRequestReviewCommentsAsync(
            string accessToken,
            string userId,
            string owner,
            string repositoryName,
            int pullRequestNumber,
            int pageSize,
            int pageNumber = 1,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(CreateCached(pageNumber == 1 ? AggregateResult?.ReviewComments ?? [] : []));

        public Task<CachedResult<GitHubIssueEvent[]>> GetPullRequestTimelineEventsAsync(
            string accessToken,
            string userId,
            string owner,
            string repositoryName,
            int pullRequestNumber,
            int pageSize,
            int pageNumber = 1,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(CreateCached(pageNumber == 1 ? AggregateResult?.TimelineEvents ?? [] : []));

        public Task<PullRequestPagedSection<GitHubIssueComment>> GetAllPullRequestCommentsAsync(
            string accessToken,
            string userId,
            string owner,
            string repositoryName,
            int pullRequestNumber,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(CreatePaged(AggregateResult?.Comments ?? []));

        public Task<PullRequestPagedSection<GitHubCommit>> GetAllPullRequestCommitsAsync(
            string accessToken,
            string userId,
            string owner,
            string repositoryName,
            int pullRequestNumber,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(CreatePaged(AggregateResult?.Commits ?? []));

        public Task<PullRequestPagedSection<GitHubPullRequestReview>> GetAllPullRequestReviewsAsync(
            string accessToken,
            string userId,
            string owner,
            string repositoryName,
            int pullRequestNumber,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(CreatePaged(AggregateResult?.Reviews ?? []));

        public Task<PullRequestPagedSection<GitHubPullRequestReviewComment>> GetAllPullRequestReviewCommentsAsync(
            string accessToken,
            string userId,
            string owner,
            string repositoryName,
            int pullRequestNumber,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(CreatePaged(AggregateResult?.ReviewComments ?? []));

        public Task<PullRequestPagedSection<GitHubIssueEvent>> GetAllPullRequestTimelineEventsAsync(
            string accessToken,
            string userId,
            string owner,
            string repositoryName,
            int pullRequestNumber,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(CreatePaged(AggregateResult?.TimelineEvents ?? []));

        public Task<PullRequestOverviewAggregate?> GetPullRequestOverviewAsync(
            string accessToken,
            string userId,
            string owner,
            string repositoryName,
            int pullRequestNumber,
            CancellationToken cancellationToken = default)
        {
            PullRequestSectionState state = new(CacheState.Fresh);
            return Task.FromResult<PullRequestOverviewAggregate?>(AggregateResult is not null
                ? new PullRequestOverviewAggregate(
                    AggregateResult.PullRequest,
                    AggregateResult.Issue,
                    AggregateResult.PullRequestState,
                    AggregateResult.IssueState)
                : new PullRequestOverviewAggregate(
                    CreatePullRequest(pullRequestNumber, $"Prefetched PR {pullRequestNumber}"),
                    CreateIssue(pullRequestNumber, $"Prefetched PR {pullRequestNumber}"),
                    state,
                    state));
        }

        public Task<PullRequestDetailAggregate?> GetPullRequestDetailAsync(
            string accessToken,
            string userId,
            string owner,
            string repositoryName,
            int pullRequestNumber,
            CancellationToken cancellationToken = default)
        {
            AggregateCalls++;
            if (AggregateResult is not null)
            {
                return Task.FromResult<PullRequestDetailAggregate?>(AggregateResult);
            }

            GitHubPullRequest pullRequest = CreatePullRequest(pullRequestNumber, $"Prefetched PR {pullRequestNumber}");
            GitHubIssue issue = CreateIssue(pullRequestNumber, $"Prefetched PR {pullRequestNumber}");
            GitHubIssueComment[] comments = [CreateComment(pullRequestNumber, $"Prefetched PR comment {pullRequestNumber}")];
            GitHubCommit[] commits =
            [
                new()
                {
                    Sha = $"sha-{pullRequestNumber}",
                    HtmlUrl = $"https://github.com/octo/hello-world/commit/sha-{pullRequestNumber}",
                    Commit = new GitHubCommitInfo
                    {
                        Message = "Commit message",
                        Author = new GitHubCommitSignature { Name = "octo", Date = DateTimeOffset.UtcNow }
                    },
                    Author = new GitHubActor { Login = "octo" }
                }
            ];
            GitHubPullRequestReview[] reviews =
            [
                new()
                {
                    Id = pullRequestNumber,
                    State = "APPROVED",
                    Body = "Looks good",
                    HtmlUrl = $"https://github.com/octo/hello-world/pull/{pullRequestNumber}#pullrequestreview-{pullRequestNumber}",
                    SubmittedAt = DateTimeOffset.UtcNow,
                    User = new GitHubActor { Login = "octo" }
                }
            ];
            GitHubPullRequestReviewComment[] reviewComments =
            [
                new()
                {
                    Id = pullRequestNumber,
                    Body = "Review comment",
                    HtmlUrl = $"https://github.com/octo/hello-world/pull/{pullRequestNumber}#discussion_r{pullRequestNumber}",
                    CreatedAt = DateTimeOffset.UtcNow,
                    UpdatedAt = DateTimeOffset.UtcNow,
                    User = new GitHubActor { Login = "octo" },
                    Path = "src/file.cs"
                }
            ];
            GitHubIssueEvent[] timelineEvents =
            [
                new()
                {
                    Id = pullRequestNumber,
                    Event = "closed",
                    CreatedAt = DateTimeOffset.UtcNow,
                    Actor = new GitHubActor { Login = "octo" }
                }
            ];

            PullRequestSectionState state = new(CacheState.Fresh);
            return Task.FromResult<PullRequestDetailAggregate?>(new PullRequestDetailAggregate(
                pullRequest,
                issue,
                comments,
                commits,
                reviews,
                reviewComments,
                timelineEvents,
                state,
                state,
                state,
                state,
                state,
                state,
                state));
        }

        public Task<PullRequestConversationAggregate?> GetPullRequestConversationAsync(
            string accessToken,
            string userId,
            string owner,
            string repositoryName,
            int pullRequestNumber,
            CancellationToken cancellationToken = default)
        {
            PullRequestSectionState state = new(CacheState.Fresh);
            return Task.FromResult<PullRequestConversationAggregate?>(new PullRequestConversationAggregate(
                CreatePullRequest(pullRequestNumber, $"Prefetched PR {pullRequestNumber}"),
                CreateIssue(pullRequestNumber, $"Prefetched PR {pullRequestNumber}"),
                [CreateComment(pullRequestNumber, $"Prefetched PR comment {pullRequestNumber}")],
                state,
                state,
                state));
        }

        public async Task<PullRequestConversationAggregate?> GetPullRequestPrefetchAsync(
            string accessToken,
            string userId,
            string owner,
            string repositoryName,
            int pullRequestNumber,
            CancellationToken cancellationToken = default)
        {
            PrefetchCalls++;
            PrefetchEntered?.TrySetResult();
            if (PrefetchRelease is not null)
            {
                await PrefetchRelease.Task.ConfigureAwait(false);
            }

            if (AggregateResult is not null)
            {
                return new PullRequestConversationAggregate(
                    AggregateResult.PullRequest,
                    AggregateResult.Issue,
                    AggregateResult.Comments,
                    AggregateResult.PullRequestState,
                    AggregateResult.IssueState,
                    AggregateResult.CommentsState);
            }

            return await GetPullRequestConversationAsync(
                accessToken, userId, owner, repositoryName, pullRequestNumber, cancellationToken).ConfigureAwait(false);
        }

        private static CachedResult<T> CreateCached<T>(T value)
            where T : class
        {
            DateTimeOffset now = DateTimeOffset.UtcNow;
            return new CachedResult<T>(value, CacheState.Fresh, now, now.AddMinutes(5));
        }

        private static PullRequestPagedSection<T> CreatePaged<T>(T[] items)
            where T : class =>
            new(items, new PullRequestSectionState(CacheState.Fresh), 1);
    }
}
