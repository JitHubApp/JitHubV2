using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using JitHub.Models.GitHub;
using JitHub.Services;
using Xunit;

namespace JitHub.WinUI.Tests.Services;

public sealed class GitHubIssueQueryServiceTests
{
    [Fact]
    public async Task GetIssuesPageAsync_UsesNormalizedCachedQueryAndExcludesPullRequests()
    {
        RecordingQueryService queryService = new();
        GitHubIssueQueryService service = new(queryService);
        GitHubIssueQueryOptions options = new()
        {
            State = "all",
            Sort = "created",
            Direction = "asc",
            Labels = "bug,help wanted",
            Assignee = "octo cat",
            Since = new DateTimeOffset(2026, 7, 1, 0, 0, 0, TimeSpan.Zero)
        };

        CachedResult<GitHubIssue[]> result = await service.GetIssuesPageAsync(
            "token",
            "42",
            "octo",
            "app",
            options,
            50,
            2);

        Assert.Single(result.Value!);
        CapturedQuery query = Assert.Single(queryService.Queries);
        Assert.Contains("repos/octo/app/issues?", query.RelativePath, StringComparison.Ordinal);
        Assert.Contains("state=all", query.RelativePath, StringComparison.Ordinal);
        Assert.Contains("sort=created", query.RelativePath, StringComparison.Ordinal);
        Assert.Contains("direction=asc", query.RelativePath, StringComparison.Ordinal);
        Assert.Contains("labels=bug%2Chelp%20wanted", query.RelativePath, StringComparison.Ordinal);
        Assert.Contains("assignee=octo%20cat", query.RelativePath, StringComparison.Ordinal);
        Assert.Contains("page=2", query.RelativePath, StringComparison.Ordinal);
        Assert.Contains("issue-list", query.Tags);
        Assert.Equal(QueryFetchPolicy.StaleFirst, query.FetchPolicy);
    }

    [Fact]
    public async Task GetAllIssuesAsync_ContinuesAfterFullRawPageContainingPullRequests()
    {
        RecordingQueryService queryService = new() { ReturnPagedIssueList = true };
        GitHubIssueQueryService service = new(queryService);

        IssuePagedSection<GitHubIssue> result = await service.GetAllIssuesAsync(
            "token",
            "42",
            "octo",
            "app",
            new GitHubIssueQueryOptions());

        Assert.Equal(100, result.Items.Length);
        Assert.Equal(PagedDataCompleteness.Complete, result.State.Completeness);
        Assert.Equal(2, result.State.LoadedPageCount);
        Assert.Contains(queryService.Queries, static query => query.RelativePath.Contains("page=1", StringComparison.Ordinal));
        Assert.Contains(queryService.Queries, static query => query.RelativePath.Contains("page=2", StringComparison.Ordinal));
    }

    [Fact]
    public async Task GetAllIssuesProgressivelyAsync_PublishesFirstPageBeforeLaterPagesComplete()
    {
        TaskCompletionSource<bool> secondPageGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource<IssuePagedSection<GitHubIssue>> firstPagePublished =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        RecordingQueryService queryService = new()
        {
            ReturnPagedIssueList = true,
            IssuePage2Gate = secondPageGate
        };
        GitHubIssueQueryService service = new(queryService);

        Task<IssuePagedSection<GitHubIssue>> load = service.GetAllIssuesProgressivelyAsync(
            "token",
            "42",
            "octo",
            "app",
            new GitHubIssueQueryOptions(),
            (progress, _) =>
            {
                if (progress.State.LoadedPageCount == 1 && progress.Items.Length == 99)
                {
                    firstPagePublished.TrySetResult(progress);
                }

                return Task.CompletedTask;
            });

        IssuePagedSection<GitHubIssue> firstPage = await firstPagePublished.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(PagedDataCompleteness.Loading, firstPage.State.Completeness);
        Assert.False(load.IsCompleted);

        secondPageGate.SetResult(true);
        IssuePagedSection<GitHubIssue> result = await load;
        Assert.Equal(100, result.Items.Length);
        Assert.Equal(PagedDataCompleteness.Complete, result.State.Completeness);
    }

    [Fact]
    public async Task GetAllIssuesProgressivelyAsync_CancellationAfterFirstPageStopsPagination()
    {
        using CancellationTokenSource cancellation = new();
        RecordingQueryService queryService = new() { ReturnPagedIssueList = true };
        GitHubIssueQueryService service = new(queryService);

        Task load = service.GetAllIssuesProgressivelyAsync(
            "token",
            "42",
            "octo",
            "app",
            new GitHubIssueQueryOptions(),
            (progress, _) =>
            {
                if (progress.State.LoadedPageCount == 1)
                {
                    cancellation.Cancel();
                }

                return Task.CompletedTask;
            },
            cancellation.Token);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => load);
        Assert.DoesNotContain(
            queryService.Queries,
            static query => query.RelativePath.Contains("page=2", StringComparison.Ordinal));
    }

    [Fact]
    public async Task GetIssueDetailAsync_SectionFailureDoesNotDiscardAvailableIssue()
    {
        RecordingQueryService queryService = new() { FailTimeline = true };
        GitHubIssueQueryService service = new(queryService);

        IssueDetailAggregate? result = await service.GetIssueDetailAsync(
            "token",
            "42",
            "octo",
            "app",
            17);

        Assert.NotNull(result);
        Assert.Equal(17, result!.Issue.Number);
        Assert.Single(result.Comments);
        Assert.Empty(result.TimelineEvents);
        Assert.Equal(CacheState.Error, result.TimelineState.CacheState);
        Assert.Equal(PagedDataCompleteness.Partial, result.TimelineState.Completeness);
    }

    [Fact]
    public async Task GetIssuePrefetchAsync_IsBoundedAndUsesOnlyThePrefetchLane()
    {
        RecordingQueryService queryService = new();
        GitHubIssueQueryService service = new(queryService);

        IssuePrefetchAggregate result = await service.GetIssuePrefetchAsync(
            "token", "42", "octo", "app", 17);

        Assert.NotNull(result.Issue);
        Assert.Equal(17, result.Issue!.Number);
        Assert.Single(result.Comments);
        Assert.Equal(2, queryService.Queries.Count);
        Assert.All(
            queryService.Queries,
            static query => Assert.Equal(GitHubRequestPriority.Prefetch, query.Priority));
        Assert.DoesNotContain(
            queryService.Queries,
            static query => query.RelativePath.Contains("page=2", StringComparison.Ordinal));
    }

    [Fact]
    public async Task GetAllIssueCommentsAsync_RefreshesStaleShortPageBeforeDeclaringComplete()
    {
        RecordingQueryService queryService = new() { ReturnStaleShortCommentPage = true };
        GitHubIssueQueryService service = new(queryService);

        IssuePagedSection<GitHubIssueComment> result = await service.GetAllIssueCommentsAsync(
            "token", "42", "octo", "app", 17);

        Assert.Equal(101, result.Items.Length);
        Assert.Equal(PagedDataCompleteness.Complete, result.State.Completeness);
        Assert.Equal(2, result.State.LoadedPageCount);
        Assert.Contains(queryService.Queries, static query =>
            query.RelativePath.Contains("/comments?", StringComparison.Ordinal) &&
            query.RelativePath.Contains("&page=1", StringComparison.Ordinal) &&
            query.FetchPolicy == QueryFetchPolicy.NetworkOnly);
    }

    [Fact]
    public async Task GetAllIssueCommentsProgressivelyAsync_PublishesFirstPageBeforeLaterPagesComplete()
    {
        TaskCompletionSource<bool> secondPageGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource<IssuePagedSection<GitHubIssueComment>> firstPagePublished =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        RecordingQueryService queryService = new()
        {
            ReturnPagedComments = true,
            CommentPage2Gate = secondPageGate
        };
        GitHubIssueQueryService service = new(queryService);

        Task<IssuePagedSection<GitHubIssueComment>> load = service.GetAllIssueCommentsProgressivelyAsync(
            "token",
            "42",
            "octo",
            "app",
            17,
            (progress, _) =>
            {
                if (progress.State.LoadedPageCount == 1 && progress.Items.Length == 100)
                {
                    firstPagePublished.TrySetResult(progress);
                }

                return Task.CompletedTask;
            });

        IssuePagedSection<GitHubIssueComment> firstPage =
            await firstPagePublished.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(PagedDataCompleteness.Loading, firstPage.State.Completeness);
        Assert.False(load.IsCompleted);

        secondPageGate.SetResult(true);
        IssuePagedSection<GitHubIssueComment> result = await load;
        Assert.Equal(101, result.Items.Length);
        Assert.Equal(PagedDataCompleteness.Complete, result.State.Completeness);
    }

    [Fact]
    public async Task GetAllIssueCommentsProgressivelyAsync_PublishesStaleThenAuthoritativePagesInPlace()
    {
        RecordingQueryService queryService = new() { ReturnStaleShortCommentPage = true };
        GitHubIssueQueryService service = new(queryService);
        List<(int Count, CacheState State)> publications = [];

        IssuePagedSection<GitHubIssueComment> result = await service.GetAllIssueCommentsProgressivelyAsync(
            "token",
            "42",
            "octo",
            "app",
            17,
            (progress, _) =>
            {
                publications.Add((progress.Items.Length, progress.State.CacheState));
                return Task.CompletedTask;
            });

        Assert.Contains((1, CacheState.Stale), publications);
        Assert.Contains((100, CacheState.Fresh), publications);
        Assert.Contains(publications, static publication => publication.Count == 101);
        Assert.Equal(101, result.Items.Length);
    }

    [Fact]
    public async Task GetAllIssueCommentsProgressivelyAsync_CancellationAfterFirstPageStopsPagination()
    {
        using CancellationTokenSource cancellation = new();
        RecordingQueryService queryService = new() { ReturnPagedComments = true };
        GitHubIssueQueryService service = new(queryService);

        Task load = service.GetAllIssueCommentsProgressivelyAsync(
            "token",
            "42",
            "octo",
            "app",
            17,
            (progress, _) =>
            {
                if (progress.State.LoadedPageCount == 1)
                {
                    cancellation.Cancel();
                }

                return Task.CompletedTask;
            },
            cancellation.Token);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => load);
        Assert.DoesNotContain(
            queryService.Queries,
            static query => query.RelativePath.Contains("/comments?", StringComparison.Ordinal) &&
                query.RelativePath.Contains("page=2", StringComparison.Ordinal));
    }

    [Fact]
    public async Task GetAllIssueCommentsAsync_LaterPageFailureReturnsExplicitPartialPrefix()
    {
        RecordingQueryService queryService = new() { FailSecondCommentPage = true };
        GitHubIssueQueryService service = new(queryService);

        IssuePagedSection<GitHubIssueComment> result = await service.GetAllIssueCommentsAsync(
            "token", "42", "octo", "app", 17);

        Assert.Equal(100, result.Items.Length);
        Assert.Equal(PagedDataCompleteness.Partial, result.State.Completeness);
        Assert.Equal(CacheState.Error, result.State.CacheState);
        Assert.Equal(
            "JitHub could not refresh this content. Existing data is still available.",
            result.State.ErrorMessage);
        Assert.DoesNotContain("page 2", result.State.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RepositoryMetadataAndReactions_UseIndependentCacheTags()
    {
        RecordingQueryService queryService = new();
        GitHubIssueQueryService service = new(queryService);

        IssueRepositoryMetadata metadata = await service.GetRepositoryMetadataAsync(
            "token", "42", "octo", "app");
        CachedResult<GitHubReaction[]> reactions = await service.GetIssueReactionsAsync(
            "token", "42", "octo", "app", 17);

        Assert.Single(metadata.Assignees);
        Assert.Single(metadata.Labels);
        Assert.Single(metadata.Milestones);
        Assert.Single(reactions.Value!);
        Assert.Contains(queryService.Queries, static query => query.RelativePath.Contains("/assignees?", StringComparison.Ordinal));
        Assert.Contains(queryService.Queries, static query => query.RelativePath.Contains("/labels?", StringComparison.Ordinal));
        Assert.Contains(queryService.Queries, static query => query.RelativePath.Contains("/milestones?", StringComparison.Ordinal));
        Assert.Contains(queryService.Queries, static query => query.RelativePath.Contains("/reactions?", StringComparison.Ordinal));
    }

    [Fact]
    public async Task InvalidateIssueAsync_InvalidatesDetailAndRepositoryListTags()
    {
        RecordingQueryService queryService = new();
        GitHubIssueQueryService service = new(queryService);

        await service.InvalidateIssueAsync("42", "octo", "app", 17);

        IReadOnlyCollection<string> tags = Assert.Single(queryService.InvalidatedTagSets);
        Assert.Contains("issue:octo/app#17", tags);
        Assert.Contains("repo:octo/app", tags);
    }

    [Fact]
    public void PartialRefresh_PreservesExistingTailOnlyForTheSameQuery()
    {
        GitHubIssue[] existing = [CreateIssue(1), CreateIssue(2), CreateIssue(3)];
        GitHubIssue[] incoming = [CreateIssue(1), CreateIssue(2)];
        IssueSectionState partial = new(
            CacheState.Stale,
            ErrorMessage: "page 2 failed",
            Completeness: PagedDataCompleteness.Partial,
            LoadedItemCount: 2,
            LoadedPageCount: 1);

        IReadOnlyList<GitHubIssue> preserved = IssueRefreshProjectionPolicy.PreserveExistingRowsOnPartialRefresh(
            incoming,
            existing,
            partial,
            isSameQuery: true);
        IReadOnlyList<GitHubIssue> replaced = IssueRefreshProjectionPolicy.PreserveExistingRowsOnPartialRefresh(
            incoming,
            existing,
            partial,
            isSameQuery: false);

        Assert.Equal([1, 2, 3], preserved.Select(static issue => issue.Number));
        Assert.Equal([1, 2], replaced.Select(static issue => issue.Number));
    }

    [Fact]
    public void FailedEmptyCommentRefresh_PreservesVisibleConversation()
    {
        IssuePagedSection<GitHubIssueComment> failed = new(
            [],
            new IssueSectionState(
                CacheState.Error,
                ErrorMessage: "offline",
                Completeness: PagedDataCompleteness.Partial));
        IssuePagedSection<GitHubIssueComment> successfulEmpty = new(
            [],
            new IssueSectionState(CacheState.Fresh));

        Assert.True(IssueRefreshProjectionPolicy.ShouldPreserveVisibleSection(failed, visibleItemCount: 2));
        Assert.False(IssueRefreshProjectionPolicy.ShouldPreserveVisibleSection(failed, visibleItemCount: 0));
        Assert.False(IssueRefreshProjectionPolicy.ShouldPreserveVisibleSection(successfulEmpty, visibleItemCount: 2));
    }

    [Fact]
    public void PartialCommentRefresh_MergesFreshPrefixWithVisibleTail()
    {
        GitHubIssueComment[] existing = Enumerable.Range(1, 125)
            .Select(static id => new GitHubIssueComment { Id = id, Body = $"old {id}" })
            .ToArray();
        GitHubIssueComment[] prefix = Enumerable.Range(1, 100)
            .Select(static id => new GitHubIssueComment { Id = id, Body = $"fresh {id}" })
            .ToArray();
        IssuePagedSection<GitHubIssueComment> partial = new(
            prefix,
            new IssueSectionState(
                CacheState.Error,
                ErrorMessage: "page 2 failed",
                Completeness: PagedDataCompleteness.Partial,
                LoadedItemCount: 100,
                LoadedPageCount: 1));

        IReadOnlyList<GitHubIssueComment> merged =
            IssueRefreshProjectionPolicy.PreserveExistingSectionOnPartialRefresh(
                partial,
                existing,
                static comment => comment.Id);

        Assert.Equal(125, merged.Count);
        Assert.Equal("fresh 1", merged[0].Body);
        Assert.Equal("old 125", merged[^1].Body);
    }

    [Fact]
    public void QueryIdentity_IsStableForEquivalentFiltersAndChangesWithScope()
    {
        GitHubIssueQueryOptions first = new() { State = " Open ", Assignee = "Octo" };
        GitHubIssueQueryOptions equivalent = new() { State = "open", Assignee = "octo" };
        GitHubIssueQueryOptions different = new() { State = "closed", Assignee = "octo" };

        Assert.Equal(
            IssueRefreshProjectionPolicy.CreateQueryIdentity(first),
            IssueRefreshProjectionPolicy.CreateQueryIdentity(equivalent));
        Assert.NotEqual(
            IssueRefreshProjectionPolicy.CreateQueryIdentity(first),
            IssueRefreshProjectionPolicy.CreateQueryIdentity(different));
    }

    [Fact]
    public async Task MetadataAndReactions_AutoPageWithoutManualLoadMore()
    {
        RecordingQueryService queryService = new() { ReturnPagedMetadataAndReactions = true };
        GitHubIssueQueryService service = new(queryService);

        IssueRepositoryMetadata metadata = await service.GetRepositoryMetadataAsync(
            "token", "42", "octo", "app");
        IssuePagedSection<GitHubReaction> issueReactions = await service.GetAllIssueReactionsAsync(
            "token", "42", "octo", "app", 17);
        IssuePagedSection<GitHubReaction> commentReactions = await service.GetAllIssueCommentReactionsAsync(
            "token", "42", "octo", "app", 99);

        Assert.Equal(101, metadata.Assignees.Length);
        Assert.Equal(101, metadata.Labels.Length);
        Assert.Equal(101, metadata.Milestones.Length);
        Assert.Equal(101, issueReactions.Items.Length);
        Assert.Equal(101, commentReactions.Items.Length);
        Assert.All(
            new[]
            {
                metadata.AssigneesState,
                metadata.LabelsState,
                metadata.MilestonesState,
                issueReactions.State,
                commentReactions.State
            },
            state =>
            {
                Assert.Equal(PagedDataCompleteness.Complete, state.Completeness);
                Assert.Equal(2, state.LoadedPageCount);
            });
        Assert.Contains(queryService.Queries, static query => query.RelativePath.Contains("/assignees?per_page=100&page=2", StringComparison.Ordinal));
        Assert.Contains(queryService.Queries, static query => query.RelativePath.Contains("/labels?per_page=100&page=2", StringComparison.Ordinal));
        Assert.Contains(queryService.Queries, static query => query.RelativePath.Contains("/milestones?state=all&per_page=100&page=2", StringComparison.Ordinal));
        Assert.Contains(queryService.Queries, static query => query.RelativePath.Contains("/issues/17/reactions?per_page=100&page=2", StringComparison.Ordinal));
        Assert.Contains(queryService.Queries, static query => query.RelativePath.Contains("/issues/comments/99/reactions?per_page=100&page=2", StringComparison.Ordinal));
        Assert.All(
            queryService.Queries.Where(static query =>
                query.RelativePath.Contains("/assignees?", StringComparison.Ordinal) ||
                query.RelativePath.Contains("/labels?", StringComparison.Ordinal) ||
                query.RelativePath.Contains("/milestones?", StringComparison.Ordinal)),
            static query => Assert.Equal(GitHubCachePolicy.RepositoryMetadataResource, query.ResourceKind));
        Assert.All(
            queryService.Queries.Where(static query => query.RelativePath.Contains("page=2", StringComparison.Ordinal)),
            static query => Assert.Equal(GitHubRequestPriority.BackgroundRefresh, query.Priority));
    }

    [Fact]
    public async Task LaterPageRefreshFailure_RetainsCachedMetadataAndReactionRows()
    {
        RecordingQueryService queryService = new()
        {
            ReturnPagedMetadataAndReactions = true,
            FailSecondMetadataRefresh = true
        };
        GitHubIssueQueryService service = new(queryService);

        IssueRepositoryMetadata metadata = await service.GetRepositoryMetadataAsync(
            "token", "42", "octo", "app");
        IssuePagedSection<GitHubReaction> reactions = await service.GetAllIssueReactionsAsync(
            "token", "42", "octo", "app", 17);

        Assert.Equal(101, metadata.Assignees.Length);
        Assert.Equal(101, metadata.Labels.Length);
        Assert.Equal(101, metadata.Milestones.Length);
        Assert.Equal(101, reactions.Items.Length);
        Assert.All(
            new[] { metadata.AssigneesState, metadata.LabelsState, metadata.MilestonesState, reactions.State },
            state =>
            {
                Assert.Equal(PagedDataCompleteness.Partial, state.Completeness);
                Assert.Equal(2, state.LoadedPageCount);
                Assert.False(string.IsNullOrWhiteSpace(state.ErrorMessage));
            });
    }

    private sealed class RecordingQueryService : IGitHubQueryService
    {
        public List<CapturedQuery> Queries { get; } = [];

        public bool ReturnPagedIssueList { get; set; }

        public bool FailTimeline { get; set; }

        public bool ReturnStaleShortCommentPage { get; set; }

        public bool ReturnPagedComments { get; set; }

        public TaskCompletionSource<bool>? IssuePage2Gate { get; set; }

        public TaskCompletionSource<bool>? CommentPage2Gate { get; set; }

        public bool FailSecondCommentPage { get; set; }

        public bool ReturnPagedMetadataAndReactions { get; set; }

        public bool FailSecondMetadataRefresh { get; set; }

        public List<IReadOnlyCollection<string>> InvalidatedTagSets { get; } = [];

        public async Task<CachedResult<T>> GetAsync<T>(
            GitHubQuery<T> query,
            QueryFetchPolicy fetchPolicy,
            CancellationToken cancellationToken = default)
            where T : class
        {
            Queries.Add(new CapturedQuery(
                query.RelativePath,
                query.Tags?.ToArray() ?? [],
                fetchPolicy,
                query.ResourceKind,
                query.Priority));
            if (FailTimeline && query.RelativePath.Contains("/events?", StringComparison.Ordinal))
            {
                throw new HttpRequestException("timeline unavailable");
            }

            if (FailSecondCommentPage &&
                query.RelativePath.Contains("/comments?", StringComparison.Ordinal) &&
                query.RelativePath.Contains("&page=2", StringComparison.Ordinal))
            {
                throw new HttpRequestException("page 2 unavailable");
            }

            if (FailSecondMetadataRefresh &&
                fetchPolicy == QueryFetchPolicy.NetworkOnly &&
                query.RelativePath.Contains("page=2", StringComparison.Ordinal) &&
                IsMetadataOrReactionPath(query.RelativePath))
            {
                throw new HttpRequestException("metadata page 2 refresh unavailable");
            }

            if (query.RelativePath.Contains("page=2", StringComparison.Ordinal))
            {
                TaskCompletionSource<bool>? pageGate = query.RelativePath.Contains("/comments?", StringComparison.Ordinal)
                    ? CommentPage2Gate
                    : query.RelativePath.Contains("/issues?", StringComparison.Ordinal)
                        ? IssuePage2Gate
                        : null;
                if (pageGate is not null)
                {
                    await pageGate.Task.WaitAsync(cancellationToken);
                }
            }

            object payload = ResolvePayload(typeof(T), query.RelativePath, fetchPolicy);
            bool isStaleMetadataPage = ReturnPagedMetadataAndReactions &&
                fetchPolicy == QueryFetchPolicy.StaleFirst &&
                query.RelativePath.Contains("page=2", StringComparison.Ordinal) &&
                IsMetadataOrReactionPath(query.RelativePath);
            CacheState cacheState = ReturnStaleShortCommentPage &&
                query.RelativePath.Contains("/comments?", StringComparison.Ordinal) &&
                query.RelativePath.Contains("&page=1", StringComparison.Ordinal) &&
                fetchPolicy == QueryFetchPolicy.StaleFirst
                    ? CacheState.Stale
                    : isStaleMetadataPage ? CacheState.Stale : CacheState.Fresh;
            return new CachedResult<T>(
                (T)payload,
                cacheState,
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow.AddMinutes(5));
        }

        public Task<CachedResult<T>> RefreshAsync<T>(
            GitHubQuery<T> query,
            CancellationToken cancellationToken = default)
            where T : class =>
            GetAsync(query, QueryFetchPolicy.NetworkOnly, cancellationToken);

        public Task InvalidateAsync(string cacheKey, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task InvalidateTagsAsync(IReadOnlyCollection<string> tags, CancellationToken cancellationToken = default)
        {
            InvalidatedTagSets.Add(tags.ToArray());
            return Task.CompletedTask;
        }

        private object ResolvePayload(Type type, string path, QueryFetchPolicy fetchPolicy)
        {
            if (type == typeof(GitHubIssue[]))
            {
                if (ReturnPagedIssueList)
                {
                    if (path.Contains("&page=1", StringComparison.Ordinal))
                    {
                        return Enumerable.Range(1, 99)
                            .Select(static number => CreateIssue(number))
                            .Append(CreateIssue(1000, pullRequest: true))
                            .ToArray();
                    }

                    return new[] { CreateIssue(100) };
                }

                return new[] { CreateIssue(17), CreateIssue(18, pullRequest: true) };
            }

            if (type == typeof(GitHubIssue))
            {
                int number = path.EndsWith("/17", StringComparison.Ordinal) ? 17 : 1;
                return CreateIssue(number);
            }

            if (type == typeof(GitHubIssueComment[]))
            {
                if (ReturnPagedComments)
                {
                    return CreatePage(
                        path,
                        static id => new GitHubIssueComment { Id = id, Body = $"Comment {id}" });
                }

                if (ReturnStaleShortCommentPage)
                {
                    if (path.Contains("&page=1", StringComparison.Ordinal))
                    {
                        return fetchPolicy == QueryFetchPolicy.StaleFirst
                            ? new[] { new GitHubIssueComment { Id = 1, Body = "Stale comment" } }
                            : Enumerable.Range(1, 100)
                                .Select(static id => new GitHubIssueComment { Id = id, Body = $"Comment {id}" })
                                .ToArray();
                    }

                    return new[] { new GitHubIssueComment { Id = 101, Body = "Comment 101" } };
                }

                if (FailSecondCommentPage && path.Contains("&page=1", StringComparison.Ordinal))
                {
                    return Enumerable.Range(1, 100)
                        .Select(static id => new GitHubIssueComment { Id = id, Body = $"Comment {id}" })
                        .ToArray();
                }

                return new[] { new GitHubIssueComment { Id = 1, Body = "Cached comment" } };
            }

            if (type == typeof(GitHubIssueEvent[]))
            {
                return new[] { new GitHubIssueEvent { Id = 1, Event = "labeled" } };
            }

            if (type == typeof(GitHubActor[]))
            {
                if (ReturnPagedMetadataAndReactions)
                {
                    return CreatePage(
                        path,
                        static id => new GitHubActor { Id = id, Login = $"user-{id}" });
                }

                return new[] { new GitHubActor { Login = "octo" } };
            }

            if (type == typeof(GitHubLabel[]))
            {
                if (ReturnPagedMetadataAndReactions)
                {
                    return CreatePage(
                        path,
                        static id => new GitHubLabel { Id = id, Name = $"label-{id}" });
                }

                return new[] { new GitHubLabel { Name = "bug" } };
            }

            if (type == typeof(GitHubMilestone[]))
            {
                if (ReturnPagedMetadataAndReactions)
                {
                    return CreatePage(
                        path,
                        static id => new GitHubMilestone { Number = id, Title = $"milestone-{id}" });
                }

                return new[] { new GitHubMilestone { Number = 1, Title = "vNext" } };
            }

            if (type == typeof(GitHubReaction[]))
            {
                if (ReturnPagedMetadataAndReactions)
                {
                    return CreatePage(
                        path,
                        static id => new GitHubReaction { Id = id, Content = "+1" });
                }

                return new[] { new GitHubReaction { Id = 1, Content = "+1" } };
            }

            throw new InvalidOperationException($"No payload for {type.Name}.");
        }

        private static TItem[] CreatePage<TItem>(string path, Func<int, TItem> create)
        {
            int start = path.Contains("page=2", StringComparison.Ordinal) ? 101 : 1;
            int count = start == 1 ? 100 : 1;
            return Enumerable.Range(start, count).Select(create).ToArray();
        }

        private static bool IsMetadataOrReactionPath(string path) =>
            path.Contains("/assignees?", StringComparison.Ordinal) ||
            path.Contains("/labels?", StringComparison.Ordinal) ||
            path.Contains("/milestones?", StringComparison.Ordinal) ||
            path.Contains("/reactions?", StringComparison.Ordinal);
    }

    private sealed record CapturedQuery(
        string RelativePath,
        IReadOnlyList<string> Tags,
        QueryFetchPolicy FetchPolicy,
        string ResourceKind,
        GitHubRequestPriority Priority);

    private static GitHubIssue CreateIssue(int number, bool pullRequest = false) =>
        new()
        {
            Id = number,
            Number = number,
            Title = $"Issue {number}",
            State = "open",
            PullRequest = pullRequest ? new GitHubIssuePullRequestMarker() : null
        };
}
