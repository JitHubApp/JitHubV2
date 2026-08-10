using System;
using System.Collections.Generic;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using JitHub.Models.GitHub;
using JitHub.Services;
using Xunit;

namespace JitHub.WinUI.Tests.Services;

public sealed class GitHubRepositoryQueryServiceTests
{
    [Theory]
    [InlineData(CacheState.Stale, false, true)]
    [InlineData(CacheState.Refreshing, false, true)]
    [InlineData(CacheState.Fresh, true, true)]
    [InlineData(CacheState.Fresh, false, false)]
    [InlineData(CacheState.Miss, false, false)]
    public void StaleFirstRefreshPolicy_PromotesOnlyCachedResultsWithPendingNetworkWork(
        CacheState state,
        bool refreshInProgress,
        bool expected)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        CachedResult<GitHubRepository> result = new(
            new GitHubRepository(),
            state,
            now,
            now.AddMinutes(5),
            refreshInProgress);

        Assert.Equal(expected, RepositoryQueryRefreshPolicy.ShouldPromote(result));
    }

    [Fact]
    public async Task RepositoryAndActionReadsUseStaleFirstTaggedQueries()
    {
        CapturingQueryService queryService = new();
        GitHubRepositoryQueryService service = new(queryService, enableAutomationFixtures: false);

        await service.GetRepositoryAsync("token", "42", "octo", "app");
        await service.GetBranchesPageAsync("token", "42", "octo", "app", 2);
        await service.GetStarStateAsync("token", "42", "octo", "app");
        await service.GetWatchStateAsync("token", "42", "octo", "app");

        Assert.Collection(
            queryService.Captures,
            item =>
            {
                Assert.Equal("repos/octo/app", item.RelativePath);
                Assert.Equal(QueryFetchPolicy.StaleFirst, item.Policy);
                Assert.Contains("repo:octo/app", item.Tags);
                Assert.Contains("repository-name:octo/app", item.Tags);
            },
            item =>
            {
                Assert.Equal("repos/octo/app/branches?per_page=100&page=2", item.RelativePath);
                Assert.Equal(QueryFetchPolicy.StaleFirst, item.Policy);
                Assert.Equal(GitHubRequestPriority.Visible, item.Priority);
                Assert.Contains("repository-branches", item.Tags);
            },
            item =>
            {
                Assert.Equal("user/starred/octo/app", item.RelativePath);
                Assert.True(item.AcceptNotFound);
                Assert.True(item.HasEmptyResponseFactory);
                Assert.Contains("repo:octo/app:star-state", item.Tags);
            },
            item =>
            {
                Assert.Equal("repos/octo/app/subscription", item.RelativePath);
                Assert.True(item.AcceptNotFound);
                Assert.True(item.HasEmptyResponseFactory);
                Assert.Contains("repo:octo/app:watch-state", item.Tags);
            });
    }

    [Fact]
    public async Task BranchPagesCanRunAsBackgroundNetworkRefreshes()
    {
        CapturingQueryService queryService = new();
        GitHubRepositoryQueryService service = new(queryService, enableAutomationFixtures: false);

        await service.GetBranchesPageAsync(
            "token",
            "42",
            "octo",
            "app",
            3,
            QueryFetchPolicy.NetworkOnly,
            GitHubRequestPriority.BackgroundRefresh);

        CapturedQuery capture = Assert.Single(queryService.Captures);
        Assert.Equal(QueryFetchPolicy.NetworkOnly, capture.Policy);
        Assert.True(capture.WasRefresh);
        Assert.Equal(GitHubRequestPriority.BackgroundRefresh, capture.Priority);
        Assert.EndsWith("page=3", capture.RelativePath, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RepositoryReadsHonorCallerPriorityWithoutChangingCacheIdentityOrTags()
    {
        CapturingQueryService queryService = new();
        GitHubRepositoryQueryService service = new(queryService, enableAutomationFixtures: false);

        await service.GetRepositoryAsync(
            "token",
            "42",
            "octo",
            "app",
            QueryFetchPolicy.StaleFirst,
            GitHubRequestPriority.BackgroundRefresh);

        CapturedQuery capture = Assert.Single(queryService.Captures);
        Assert.Equal("repos/octo/app", capture.RelativePath);
        Assert.Equal(QueryFetchPolicy.StaleFirst, capture.Policy);
        Assert.Equal(GitHubRequestPriority.BackgroundRefresh, capture.Priority);
        Assert.Contains("repository-metadata", capture.Tags);
        Assert.Contains("repo:octo/app", capture.Tags);
    }

    [Fact]
    public async Task ActionMutationsInvalidateOnlyTheirActionStateTags()
    {
        CapturingQueryService queryService = new();
        GitHubRepositoryQueryService service = new(queryService, enableAutomationFixtures: false);

        await service.InvalidateStarStateAsync("42", "Octo", "App", 17);
        await service.InvalidateWatchStateAsync("42", "Octo", "App", 17);

        Assert.Collection(
            queryService.Invalidations,
            tags =>
            {
                Assert.Equal(["repo:octo/app:star-state"], tags);
            },
            tags =>
            {
                Assert.Equal(["repo:octo/app:watch-state"], tags);
            });
    }

    [Fact]
    public async Task ActionInvalidationPreservesIdentityAndBranchCachesWhileRefreshingAffectedState()
    {
        CapturingQueryService queryService = new();
        GitHubRepositoryQueryService service = new(queryService, enableAutomationFixtures: false);

        await service.GetRepositoryAsync("token", "42", "octo", "app");
        await service.GetBranchesPageAsync("token", "42", "octo", "app", 1);
        await service.GetStarStateAsync("token", "42", "octo", "app");
        await service.GetWatchStateAsync("token", "42", "octo", "app");
        await service.InvalidateStarStateAsync("42", "octo", "app", 17);
        await service.InvalidateWatchStateAsync("42", "octo", "app", 17);

        CapturedQuery identity = queryService.Captures[0];
        CapturedQuery branches = queryService.Captures[1];
        CapturedQuery star = queryService.Captures[2];
        CapturedQuery watch = queryService.Captures[3];
        IReadOnlyCollection<string> invalidatedTags = queryService.Invalidations
            .SelectMany(static tags => tags)
            .ToHashSet(StringComparer.Ordinal);

        Assert.DoesNotContain(identity.Tags, invalidatedTags.Contains);
        Assert.DoesNotContain(branches.Tags, invalidatedTags.Contains);
        Assert.Contains(star.Tags, invalidatedTags.Contains);
        Assert.Contains(watch.Tags, invalidatedTags.Contains);
    }

    [Fact]
    public async Task PublicAccessDoesNotMisrepresentUnknownStarOrWatchAsFalse()
    {
        GitHubRepositoryQueryService service = new(new CapturingQueryService(), enableAutomationFixtures: false);

        await Assert.ThrowsAsync<GitHubAuthenticationException>(() => service.GetStarStateAsync(
            GitHubAuthenticationConstants.PublicAccessToken,
            "public",
            "octo",
            "app"));
        await Assert.ThrowsAsync<GitHubAuthenticationException>(() => service.GetWatchStateAsync(
            GitHubAuthenticationConstants.PublicAccessToken,
            "public",
            "octo",
            "app"));
    }

    [Fact]
    public void RepositoryActionSuccessFixtureRequiresAndSuppliesSecondBranchPage()
    {
        string? previous = Environment.GetEnvironmentVariable("JITHUB_PREVIEW_SCENARIO");
        try
        {
            Environment.SetEnvironmentVariable("JITHUB_PREVIEW_SCENARIO", "repository-actions-success");

            GitHubBranch[] first = RepositoryActionAutomationScenario.CreateBranches("octo", "app", 1);
            GitHubBranch[] second = RepositoryActionAutomationScenario.CreateBranches("octo", "app", 2);

            Assert.Equal(GitHubRepositoryQueryService.BranchPageSize, first.Length);
            Assert.Contains(second, branch => branch.Name == "release-page-2");
            Assert.True(second.Length < GitHubRepositoryQueryService.BranchPageSize);
        }
        finally
        {
            Environment.SetEnvironmentVariable("JITHUB_PREVIEW_SCENARIO", previous);
        }
    }

    private sealed class CapturingQueryService : IGitHubQueryService
    {
        public List<CapturedQuery> Captures { get; } = [];

        public List<IReadOnlyCollection<string>> Invalidations { get; } = [];

        public Task<CachedResult<T>> GetAsync<T>(
            GitHubQuery<T> query,
            QueryFetchPolicy fetchPolicy,
            CancellationToken cancellationToken = default)
            where T : class => Capture(query, fetchPolicy, wasRefresh: false);

        public Task<CachedResult<T>> RefreshAsync<T>(
            GitHubQuery<T> query,
            CancellationToken cancellationToken = default)
            where T : class => Capture(query, QueryFetchPolicy.NetworkOnly, wasRefresh: true);

        public Task InvalidateAsync(string cacheKey, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task InvalidateTagsAsync(
            IReadOnlyCollection<string> tags,
            CancellationToken cancellationToken = default)
        {
            Invalidations.Add(tags);
            return Task.CompletedTask;
        }

        private Task<CachedResult<T>> Capture<T>(
            GitHubQuery<T> query,
            QueryFetchPolicy policy,
            bool wasRefresh)
            where T : class
        {
            Captures.Add(new CapturedQuery(
                query.RelativePath,
                policy,
                query.Priority,
                query.Tags ?? [],
                query.AcceptNotFound,
                query.EmptyResponseFactory is not null,
                wasRefresh));
            object value = typeof(T) == typeof(GitHubRepository)
                ? new GitHubRepository { Id = 1, Name = "app", FullName = "octo/app", Owner = new() { Login = "octo" } }
                : typeof(T) == typeof(GitHubBranch[])
                    ? Array.Empty<GitHubBranch>()
                    : typeof(T) == typeof(GitHubResourceState)
                        ? new GitHubResourceState()
                        : typeof(T) == typeof(GitHubRepositorySubscription)
                            ? new GitHubRepositorySubscription()
                            : throw new InvalidOperationException(typeof(T).FullName);
            DateTimeOffset now = DateTimeOffset.UtcNow;
            return Task.FromResult(new CachedResult<T>((T)value, CacheState.Fresh, now, now.AddMinutes(5)));
        }
    }

    private sealed record CapturedQuery(
        string RelativePath,
        QueryFetchPolicy Policy,
        GitHubRequestPriority Priority,
        IReadOnlyList<string> Tags,
        bool AcceptNotFound,
        bool HasEmptyResponseFactory,
        bool WasRefresh);
}
