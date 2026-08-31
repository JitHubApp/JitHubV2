using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using JitHub.Models.GitHub;
using JitHub.Services;
using Xunit;

namespace JitHub.WinUI.Tests.Services;

public sealed class GitHubStarQueryServiceTests
{
    [Fact]
    public async Task GetPage_RequestsTimestampMediaTypeAndHundredRows()
    {
        FakeQueryService queryService = new();
        GitHubStarQueryService service = new(queryService);

        CachedResult<GitHubStarredRepository[]> result = await service.GetPageAsync(
            "token",
            "42",
            page: 3,
            QueryFetchPolicy.StaleFirst,
            GitHubRequestPriority.BackgroundRefresh);

        Assert.Single(result.Value!);
        Assert.NotNull(queryService.Query);
        Assert.Equal("user/starred?sort=created&direction=desc&per_page=100&page=3", queryService.Query!.RelativePath);
        Assert.Equal("application/vnd.github.star+json", queryService.Query.AcceptMediaType);
        Assert.Contains("star-library", queryService.Query.Tags!);
        Assert.Equal(GitHubRequestPriority.BackgroundRefresh, queryService.Query.Priority);
    }

    [Fact]
    public void TimestampWrapper_DeserializesStarredAtAndRepository()
    {
        const string json = """
            [{
              "starred_at": "2026-07-01T04:05:06Z",
              "repo": {
                "id": 42,
                "name": "app",
                "full_name": "octo/app",
                "html_url": "https://github.com/octo/app",
                "owner": { "login": "octo" }
              }
            }]
            """;

        GitHubStarredRepository[]? result = JsonSerializer.Deserialize(
            json,
            Phase0GitHubJsonSerializerContext.Default.GitHubStarredRepositoryArray);

        Assert.NotNull(result);
        Assert.Single(result!);
        Assert.Equal(new DateTimeOffset(2026, 7, 1, 4, 5, 6, TimeSpan.Zero), result[0].StarredAt);
        Assert.Equal("octo/app", result[0].Repository.FullName);
    }

    private sealed class FakeQueryService : IGitHubQueryService
    {
        public GitHubQuery<GitHubStarredRepository[]>? Query { get; private set; }

        public Task<CachedResult<T>> GetAsync<T>(GitHubQuery<T> query, QueryFetchPolicy fetchPolicy, CancellationToken cancellationToken = default)
            where T : class
        {
            Query = query as GitHubQuery<GitHubStarredRepository[]>;
            object value = new[]
            {
                new GitHubStarredRepository
                {
                    StarredAt = DateTimeOffset.UtcNow,
                    Repository = new GitHubRepository
                    {
                        Id = 1,
                        Name = "app",
                        FullName = "octo/app",
                        Owner = new GitHubRepositoryOwner { Login = "octo" }
                    }
                }
            };
            return Task.FromResult(new CachedResult<T>(
                (T)value,
                CacheState.Fresh,
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow.AddMinutes(30),
                false));
        }

        public Task<CachedResult<T>> RefreshAsync<T>(GitHubQuery<T> query, CancellationToken cancellationToken = default) where T : class =>
            GetAsync(query, QueryFetchPolicy.NetworkOnly, cancellationToken);

        public Task InvalidateAsync(string cacheKey, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task InvalidateTagsAsync(IReadOnlyCollection<string> tags, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
