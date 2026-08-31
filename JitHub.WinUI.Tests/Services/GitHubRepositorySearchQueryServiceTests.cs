using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using JitHub.Models.GitHub;
using JitHub.Services;
using Xunit;

namespace JitHub.WinUI.Tests.Services;

public sealed class GitHubRepositorySearchQueryServiceTests
{
    [Fact]
    public void BuildRelativePath_EncodesEveryFilterAndSort()
    {
        RepositorySearchQuery query = new(
            "native github",
            "JitHubApp",
            "C#",
            "winui",
            RepositorySearchVisibility.Public,
            RepositorySearchForkScope.Forks,
            RepositorySearchArchiveScope.Active,
            RepositorySearchSort.RecentlyUpdated);

        string path = GitHubRepositorySearchQueryService.BuildRelativePath(query, 2, 50);

        Assert.StartsWith("search/repositories?q=", path, StringComparison.Ordinal);
        Assert.Contains(Uri.EscapeDataString("native github user:JitHubApp language:C# topic:winui is:public fork:only archived:false"), path, StringComparison.Ordinal);
        Assert.Contains("per_page=50&page=2", path, StringComparison.Ordinal);
        Assert.Contains("sort=updated&order=desc", path, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(RepositorySearchSort.BestMatch, "")]
    [InlineData(RepositorySearchSort.RecentlyUpdated, "sort=updated&order=desc")]
    [InlineData(RepositorySearchSort.MostStars, "sort=stars&order=desc")]
    [InlineData(RepositorySearchSort.MostForks, "sort=forks&order=desc")]
    public void BuildRelativePath_UsesOnlyGitHubSupportedSortValues(RepositorySearchSort sort, string expectedSort)
    {
        string path = GitHubRepositorySearchQueryService.BuildRelativePath(
            new RepositorySearchQuery("windows", Sort: sort),
            1,
            50);

        Assert.DoesNotContain("sort=name", path, StringComparison.Ordinal);
        if (expectedSort.Length == 0)
        {
            Assert.DoesNotContain("&sort=", path, StringComparison.Ordinal);
        }
        else
        {
            Assert.Contains(expectedSort, path, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task SearchAsync_UsesStaleFirstAndPrefetchesLaterPages()
    {
        RecordingQueryService queries = new();
        GitHubRepositorySearchQueryService service = new(queries);

        CachedResult<GitHubRepositorySearchResponse> result = await service.SearchAsync(
            "token",
            "42",
            new RepositorySearchQuery("jithub"),
            page: 3,
            pageSize: 100);

        Assert.NotNull(result.Value);
        Assert.Equal(QueryFetchPolicy.StaleFirst, queries.FetchPolicy);
        Assert.Equal(GitHubRequestPriority.Prefetch, queries.Priority);
        Assert.Contains("page=3", queries.RelativePath, StringComparison.Ordinal);
        Assert.Contains("repository-search-workspace", queries.Tags);
    }

    [Fact]
    public async Task SearchAsync_ForceRefreshUsesNetworkRefreshPath()
    {
        RecordingQueryService queries = new();
        GitHubRepositorySearchQueryService service = new(queries);

        await service.SearchAsync(
            "token",
            "42",
            new RepositorySearchQuery("jithub"),
            page: 1,
            pageSize: 50,
            forceRefresh: true);

        Assert.True(queries.RefreshCalled);
        Assert.Equal(GitHubRequestPriority.UserInitiated, queries.Priority);
    }

    private sealed class RecordingQueryService : IGitHubQueryService
    {
        public QueryFetchPolicy? FetchPolicy { get; private set; }

        public bool RefreshCalled { get; private set; }

        public string RelativePath { get; private set; } = string.Empty;

        public GitHubRequestPriority Priority { get; private set; }

        public IReadOnlyList<string> Tags { get; private set; } = [];

        public Task<CachedResult<T>> GetAsync<T>(
            GitHubQuery<T> query,
            QueryFetchPolicy fetchPolicy,
            CancellationToken cancellationToken = default)
            where T : class
        {
            FetchPolicy = fetchPolicy;
            Capture(query);
            return Task.FromResult(Result<T>());
        }

        public Task<CachedResult<T>> RefreshAsync<T>(
            GitHubQuery<T> query,
            CancellationToken cancellationToken = default)
            where T : class
        {
            RefreshCalled = true;
            Capture(query);
            return Task.FromResult(Result<T>());
        }

        public Task InvalidateAsync(string cacheKey, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task InvalidateTagsAsync(IReadOnlyCollection<string> tags, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        private void Capture<T>(GitHubQuery<T> query)
            where T : class
        {
            RelativePath = query.RelativePath;
            Priority = query.Priority;
            Tags = query.Tags ?? [];
        }

        private static CachedResult<T> Result<T>()
            where T : class
        {
            T value = (T)(object)new GitHubRepositorySearchResponse
            {
                TotalCount = 1,
                Items = [new GitHubRepository { Id = 1, FullName = "JitHubApp/JitHubV2" }]
            };
            return new CachedResult<T>(
                value,
                CacheState.Fresh,
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow.AddMinutes(15));
        }
    }
}
