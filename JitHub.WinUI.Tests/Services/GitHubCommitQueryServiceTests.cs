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

public sealed class GitHubCommitQueryServiceTests
{
    [Fact]
    public async Task GetCommitsAsync_BuildsFilteredCommitListQuery()
    {
        FakeQueryService queryService = new();
        GitHubCommitQueryService service = new(queryService);
        CommitListQueryOptions options = new()
        {
            GitRef = "main",
            Path = "src/app.cs",
            Author = "renanyoy",
            Since = new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero),
            Until = new DateTimeOffset(2026, 6, 2, 0, 0, 0, TimeSpan.Zero)
        };

        CachedResult<GitHubCommit[]> result = await service.GetCommitsAsync(
            "token",
            "42",
            "octo",
            "app",
            options,
            pageSize: 50,
            pageNumber: 2);

        Assert.Single(result.Value!);
        Assert.Single(queryService.Queries);
        CapturedQuery query = queryService.Queries[0];
        Assert.Equal(HttpMethod.Get, query.Method);
        Assert.StartsWith("repos/octo/app/commits?", query.RelativePath, StringComparison.Ordinal);
        Assert.Contains("per_page=50", query.RelativePath, StringComparison.Ordinal);
        Assert.Contains("page=2", query.RelativePath, StringComparison.Ordinal);
        Assert.Contains("sha=main", query.RelativePath, StringComparison.Ordinal);
        Assert.Contains("path=src%2Fapp.cs", query.RelativePath, StringComparison.Ordinal);
        Assert.Contains("author=renanyoy", query.RelativePath, StringComparison.Ordinal);
        Assert.Contains("since=2026-06-01T00%3A00%3A00.0000000Z", query.RelativePath, StringComparison.Ordinal);
        Assert.Contains("until=2026-06-02T00%3A00%3A00.0000000Z", query.RelativePath, StringComparison.Ordinal);
        Assert.Contains("commit-list", query.Tags!);
    }

    [Fact]
    public async Task GetCommitDetailAsync_LoadsSectionsIndependently()
    {
        FakeQueryService queryService = new()
        {
            CacheState = CacheState.Stale,
            IsRefreshInProgress = true
        };
        GitHubCommitQueryService service = new(queryService);

        CommitDetailAggregate? aggregate = await service.GetCommitDetailAsync(
            "token",
            "42",
            "octo",
            "app",
            "3f9a1c2");

        Assert.NotNull(aggregate);
        Assert.Equal("3f9a1c2", aggregate!.Commit.Sha);
        Assert.Single(aggregate.Comments);
        Assert.Equal("success", aggregate.CombinedStatus!.State);
        Assert.Single(aggregate.CheckRuns);
        Assert.Single(aggregate.AssociatedPullRequests);
        Assert.Equal(CacheState.Stale, aggregate.CommitState.CacheState);
        Assert.True(aggregate.CheckRunsState.IsRefreshInProgress);
        Assert.Contains(queryService.Queries, static query => query.RelativePath == "repos/octo/app/commits/3f9a1c2");
        Assert.Contains(queryService.Queries, static query => query.RelativePath == "repos/octo/app/commits/3f9a1c2/comments?per_page=100&page=1");
        Assert.Contains(queryService.Queries, static query => query.RelativePath == "repos/octo/app/commits/3f9a1c2/status");
        Assert.Contains(queryService.Queries, static query => query.RelativePath == "repos/octo/app/commits/3f9a1c2/check-runs?per_page=100&page=1");
        Assert.Contains(queryService.Queries, static query => query.RelativePath == "repos/octo/app/commits/3f9a1c2/pulls?per_page=100&page=1");
    }

    [Fact]
    public async Task GetCommitPrefetchAsync_IsBoundedAndUsesOnlyThePrefetchLane()
    {
        FakeQueryService queryService = new();
        GitHubCommitQueryService service = new(queryService);

        CommitDetailAggregate? aggregate = await service.GetCommitPrefetchAsync(
            "token", "42", "octo", "app", "3f9a1c2");

        Assert.NotNull(aggregate);
        Assert.Equal(5, queryService.Queries.Count);
        Assert.All(
            queryService.Queries,
            static query => Assert.Equal(GitHubRequestPriority.Prefetch, query.Priority));
        Assert.DoesNotContain(
            queryService.Queries,
            static query => query.RelativePath.Contains("page=2", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CompareCommitsAsync_UsesCompareEndpoint()
    {
        FakeQueryService queryService = new();
        GitHubCommitQueryService service = new(queryService);

        CachedResult<GitHubCompareResult> result = await service.CompareCommitsAsync(
            "token",
            "42",
            "octo",
            "app",
            "main",
            "feature/native diff");

        Assert.Equal(2, result.Value!.TotalCommits);
        Assert.Equal("repos/octo/app/compare/main...feature%2Fnative%20diff", queryService.Queries.Single().RelativePath);
        Assert.Contains("commit-compare", queryService.Queries.Single().Tags!);
    }

    [Fact]
    public async Task HistoryAndDetailCollections_AutoPageUntilTheAuthoritativeShortPage()
    {
        PagedQueryService queryService = new();
        GitHubCommitQueryService service = new(queryService);

        CommitPagedSection<GitHubCommit> commits = await service.GetAllCommitsAsync(
            "token", "42", "octo", "app", new CommitListQueryOptions());
        CommitPagedSection<GitHubCommitComment> comments = await service.GetAllCommitCommentsAsync(
            "token", "42", "octo", "app", "abc1234");
        CommitPagedSection<GitHubCheckRun> checks = await service.GetAllCheckRunsAsync(
            "token", "42", "octo", "app", "abc1234");
        CommitPagedSection<GitHubPullRequest> pullRequests = await service.GetAllAssociatedPullRequestsAsync(
            "token", "42", "octo", "app", "abc1234");

        Assert.Equal(101, commits.Items.Length);
        Assert.Equal(101, comments.Items.Length);
        Assert.Equal(101, checks.Items.Length);
        Assert.Equal(101, pullRequests.Items.Length);
        Assert.All(new[] { commits.State, comments.State, checks.State, pullRequests.State }, state =>
        {
            Assert.Equal(PagedDataCompleteness.Complete, state.Completeness);
            Assert.Equal(2, state.LoadedPageCount);
        });
        Assert.Contains(queryService.Paths, static path => path.Contains("commits?per_page=100&page=2", StringComparison.Ordinal));
        Assert.Contains(queryService.Paths, static path => path.Contains("/comments?per_page=100&page=2", StringComparison.Ordinal));
        Assert.Contains(queryService.Paths, static path => path.Contains("/check-runs?per_page=100&page=2", StringComparison.Ordinal));
        Assert.Contains(queryService.Paths, static path => path.Contains("/pulls?per_page=100&page=2", StringComparison.Ordinal));
    }

    [Fact]
    public async Task BranchFilters_AutoPageWithoutManualLoadMore()
    {
        BranchPagingQueryService queryService = new();
        GitHubCommitQueryService service = new(queryService);

        CommitPagedSection<GitHubBranch> branches = await service.GetAllBranchesAsync(
            "token", "42", "octo", "app");

        Assert.Equal(101, branches.Items.Length);
        Assert.Equal(PagedDataCompleteness.Complete, branches.State.Completeness);
        Assert.Equal(2, branches.State.LoadedPageCount);
        Assert.Contains(queryService.Paths, static path => path.EndsWith("per_page=100&page=2", StringComparison.Ordinal));
        Assert.Contains(GitHubRequestPriority.BackgroundRefresh, queryService.Priorities);
    }

    [Fact]
    public async Task BranchRefreshFailure_RetainsCachedLaterPageRowsAndReportsPartialScope()
    {
        BranchPagingQueryService queryService = new(failSecondRefresh: true);
        GitHubCommitQueryService service = new(queryService);

        CommitPagedSection<GitHubBranch> branches = await service.GetAllBranchesAsync(
            "token", "42", "octo", "app");

        Assert.Equal(101, branches.Items.Length);
        Assert.Equal(PagedDataCompleteness.Partial, branches.State.Completeness);
        Assert.Equal(2, branches.State.LoadedPageCount);
        Assert.Equal(
            "JitHub could not refresh this content. Existing data is still available.",
            branches.State.ErrorMessage);
        Assert.DoesNotContain("page 2", branches.State.ErrorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BranchRefreshFailure_WithEvictedLaterPage_PublishedTailRemainsAvailable()
    {
        BranchPagingQueryService queryService = new(failSecondRead: true);
        GitHubCommitQueryService service = new(queryService);
        GitHubBranch publishedTail = new() { Name = "branch-101" };

        CommitPagedSection<GitHubBranch> refresh = await service.GetAllBranchesAsync(
            "token", "42", "octo", "app");
        GitHubBranch[] projection = PagedRefreshProjectionPolicy.Merge(
            refresh.Items,
            [new GitHubBranch { Name = "branch-1" }, publishedTail],
            static branch => branch.Name,
            refresh.State.Completeness);

        Assert.Equal(PagedDataCompleteness.Partial, refresh.State.Completeness);
        Assert.Equal(101, projection.Length);
        Assert.Contains(projection, branch => branch.Name == publishedTail.Name);
    }

    [Fact]
    public async Task CommitHistoryLaterPageFailure_RetainsLoadedPrefixAndReportsPartialScope()
    {
        PagedQueryService queryService = new() { FailSecondCommitPage = true };
        GitHubCommitQueryService service = new(queryService);

        CommitPagedSection<GitHubCommit> commits = await service.GetAllCommitsAsync(
            "token", "42", "octo", "app", new CommitListQueryOptions());

        Assert.Equal(100, commits.Items.Length);
        Assert.Equal(PagedDataCompleteness.Partial, commits.State.Completeness);
        Assert.Equal(1, commits.State.LoadedPageCount);
        Assert.Equal(
            "JitHub could not refresh this content. Existing data is still available.",
            commits.State.ErrorMessage);
        Assert.DoesNotContain("page 2", commits.State.ErrorMessage, StringComparison.Ordinal);
    }

    private sealed class PagedQueryService : IGitHubQueryService
    {
        public List<string> Paths { get; } = [];

        public List<GitHubRequestPriority> Priorities { get; } = [];

        public bool FailSecondCommitPage { get; init; }

        public Task<CachedResult<T>> GetAsync<T>(
            GitHubQuery<T> query,
            QueryFetchPolicy fetchPolicy,
            CancellationToken cancellationToken = default)
            where T : class
        {
            Paths.Add(query.RelativePath);
            Priorities.Add(query.Priority);
            if (FailSecondCommitPage &&
                typeof(T) == typeof(GitHubCommit[]) &&
                query.RelativePath.Contains("&page=2", StringComparison.Ordinal))
            {
                throw new HttpRequestException("commit page 2 unavailable");
            }

            bool firstPage = query.RelativePath.Contains("&page=1", StringComparison.Ordinal) &&
                !query.RelativePath.Contains("&page=10", StringComparison.Ordinal);
            int count = firstPage ? 100 : 1;
            object payload;
            if (typeof(T) == typeof(GitHubCommit[]))
            {
                payload = Enumerable.Range(firstPage ? 1 : 101, count)
                    .Select(index => new GitHubCommit { Sha = index.ToString("x7") })
                    .ToArray();
            }
            else if (typeof(T) == typeof(GitHubCommitComment[]))
            {
                payload = Enumerable.Range(firstPage ? 1 : 101, count)
                    .Select(index => new GitHubCommitComment { Id = index })
                    .ToArray();
            }
            else if (typeof(T) == typeof(GitHubCheckRunResponse))
            {
                payload = new GitHubCheckRunResponse
                {
                    TotalCount = 101,
                    CheckRuns = Enumerable.Range(firstPage ? 1 : 101, count)
                        .Select(index => new GitHubCheckRun { Id = index })
                        .ToArray()
                };
            }
            else if (typeof(T) == typeof(GitHubPullRequest[]))
            {
                payload = Enumerable.Range(firstPage ? 1 : 101, count)
                    .Select(index => new GitHubPullRequest { Id = index, Number = index })
                    .ToArray();
            }
            else
            {
                throw new InvalidOperationException($"No paged payload for {typeof(T).Name}.");
            }

            return Task.FromResult(new CachedResult<T>(
                (T)payload,
                CacheState.Fresh,
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow.AddMinutes(5)));
        }

        public Task<CachedResult<T>> RefreshAsync<T>(GitHubQuery<T> query, CancellationToken cancellationToken = default)
            where T : class =>
            GetAsync(query, QueryFetchPolicy.NetworkOnly, cancellationToken);

        public Task InvalidateAsync(string cacheKey, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task InvalidateTagsAsync(IReadOnlyCollection<string> tags, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class BranchPagingQueryService(
        bool failSecondRefresh = false,
        bool failSecondRead = false) : IGitHubQueryService
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
            if (typeof(T) != typeof(GitHubBranch[]))
            {
                throw new InvalidOperationException($"No branch payload for {typeof(T).Name}.");
            }

            bool secondPage = query.RelativePath.Contains("page=2", StringComparison.Ordinal);
            if (secondPage && failSecondRead && fetchPolicy == QueryFetchPolicy.StaleFirst)
            {
                throw new HttpRequestException("branch page 2 cache was evicted");
            }

            if (secondPage && failSecondRefresh && fetchPolicy == QueryFetchPolicy.NetworkOnly)
            {
                throw new HttpRequestException("branch page 2 refresh unavailable");
            }

            GitHubBranch[] branches = secondPage
                ? [new GitHubBranch { Name = "branch-101" }]
                : Enumerable.Range(1, 100).Select(static id => new GitHubBranch { Name = $"branch-{id}" }).ToArray();
            CacheState state = secondPage && fetchPolicy == QueryFetchPolicy.StaleFirst
                ? CacheState.Stale
                : CacheState.Fresh;
            return Task.FromResult(new CachedResult<T>(
                (T)(object)branches,
                state,
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow.AddMinutes(30),
                IsRefreshInProgress: state == CacheState.Stale));
        }

        public Task<CachedResult<T>> RefreshAsync<T>(
            GitHubQuery<T> query,
            CancellationToken cancellationToken = default)
            where T : class =>
            GetAsync(query, QueryFetchPolicy.NetworkOnly, cancellationToken);

        public Task InvalidateAsync(string cacheKey, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task InvalidateTagsAsync(IReadOnlyCollection<string> tags, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class FakeQueryService : IGitHubQueryService
    {
        public CacheState CacheState { get; set; } = CacheState.Fresh;

        public bool IsRefreshInProgress { get; set; }

        public List<CapturedQuery> Queries { get; } = [];

        public Task<CachedResult<T>> GetAsync<T>(
            GitHubQuery<T> query,
            QueryFetchPolicy fetchPolicy,
            CancellationToken cancellationToken = default)
            where T : class
        {
            Queries.Add(new CapturedQuery(query.Method, query.RelativePath, query.Tags, query.Priority));

            object payload = ResolvePayload(typeof(T));
            return Task.FromResult(new CachedResult<T>(
                (T)payload,
                CacheState,
                DateTimeOffset.UtcNow.AddMinutes(-1),
                DateTimeOffset.UtcNow.AddMinutes(4),
                IsRefreshInProgress));
        }

        public Task<CachedResult<T>> RefreshAsync<T>(
            GitHubQuery<T> query,
            CancellationToken cancellationToken = default)
            where T : class =>
            GetAsync(query, QueryFetchPolicy.NetworkOnly, cancellationToken);

        public Task InvalidateAsync(string cacheKey, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task InvalidateTagsAsync(IReadOnlyCollection<string> tags, CancellationToken cancellationToken = default) => Task.CompletedTask;

        private static object ResolvePayload(Type type)
        {
            if (type == typeof(GitHubBranch[]))
            {
                return new[] { new GitHubBranch { Name = "main" } };
            }

            if (type == typeof(GitHubCommit[]))
            {
                return new[] { CreateCommit("3f9a1c2") };
            }

            if (type == typeof(GitHubCommit))
            {
                GitHubCommit commit = CreateCommit("3f9a1c2");
                commit.Files =
                [
                    new GitHubCommitFile
                    {
                        Filename = "src/app.cs",
                        Patch = "@@ -1 +1 @@\n-old\n+new",
                        Additions = 1,
                        Deletions = 1,
                        Changes = 2
                    }
                ];
                return commit;
            }

            if (type == typeof(GitHubCommitComment[]))
            {
                return new[]
                {
                    new GitHubCommitComment
                    {
                        Id = 1,
                        Body = "Looks good.",
                        User = new GitHubActor { Login = "octo" }
                    }
                };
            }

            if (type == typeof(GitHubCombinedStatus))
            {
                return new GitHubCombinedStatus
                {
                    State = "success",
                    Statuses = [new GitHubCommitStatus { State = "success", Context = "ci" }]
                };
            }

            if (type == typeof(GitHubCheckRunResponse))
            {
                return new GitHubCheckRunResponse
                {
                    CheckRuns = [new GitHubCheckRun { Id = 1, Name = "build", Status = "completed", Conclusion = "success" }]
                };
            }

            if (type == typeof(GitHubPullRequest[]))
            {
                return new[] { new GitHubPullRequest { Id = 1, Number = 42, Title = "Fix commit page" } };
            }

            if (type == typeof(GitHubCompareResult))
            {
                return new GitHubCompareResult
                {
                    Status = "ahead",
                    AheadBy = 2,
                    TotalCommits = 2,
                    Files = [new GitHubCommitFile { Filename = "src/app.cs", Patch = "@@ -1 +1 @@\n-old\n+new" }]
                };
            }

            throw new InvalidOperationException($"No fake payload for {type.Name}.");
        }

        private static GitHubCommit CreateCommit(string sha) =>
            new()
            {
                Sha = sha,
                Commit = new GitHubCommitInfo
                {
                    Message = "Update native commit page",
                    Author = new GitHubCommitSignature
                    {
                        Name = "Octo",
                        Date = DateTimeOffset.UtcNow.AddMinutes(-30)
                    },
                    Verification = new GitHubCommitVerification { Verified = true }
                },
                Stats = new GitHubCommitStats { Additions = 3, Deletions = 1, Total = 4 },
                Parents = [new GitHubCommitParent { Sha = "8a7b6c1" }]
            };
    }

    private sealed record CapturedQuery(
        HttpMethod Method,
        string RelativePath,
        IReadOnlyList<string>? Tags,
        GitHubRequestPriority Priority);
}
