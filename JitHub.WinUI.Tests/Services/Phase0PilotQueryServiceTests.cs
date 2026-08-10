using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json.Serialization.Metadata;
using System.Threading;
using System.Threading.Tasks;
using JitHub.Models.GitHub;
using JitHub.Services;
using Xunit;

namespace JitHub.WinUI.Tests.Services;

public sealed class Phase0PilotQueryServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "JitHubPhase0PilotTests", Guid.NewGuid().ToString());

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch
        {
        }
    }

    [Fact]
    public async Task ShellSearchPilot_ReturnsStaleCacheWhenRefreshFails()
    {
        SqliteGitHubCacheStore store = CreateStore();
        GitHubRepository repository = CreateRepository(1, "flutter", "flutter");
        string path = "search/repositories?q=flutter&per_page=8&page=1";
        GitHubQuery<GitHubRepositorySearchResponse> seedQuery = CreateQuery(
            path,
            GitHubCachePolicy.SearchResource,
            TimeSpan.FromMilliseconds(-1),
            Phase0GitHubJsonSerializerContext.Default.GitHubRepositorySearchResponse,
            ["repo-search"]);
        await store.PutAsync(
            seedQuery,
            CreateResponse(new GitHubRepositorySearchResponse { Items = [repository] }));
        GitHubPilotQueryService pilot = CreatePilot(store, new FailingTransport());

        CachedResult<GitHubRepository[]> result = await pilot.SearchRepositoriesAsync(
            GitHubAuthenticationConstants.PublicAccessToken,
            "public",
            "flutter",
            8);

        Assert.Equal(CacheState.Stale, result.CacheState);
        Assert.True(result.IsRefreshInProgress);
        Assert.Single(result.Value!);
        Assert.Equal("flutter/flutter", result.Value![0].FullName);
    }

    [Fact]
    public async Task DashboardRecentReposPilot_ReturnsStaleCacheWhenRefreshFails()
    {
        SqliteGitHubCacheStore store = CreateStore();
        GitHubRepository repository = CreateRepository(2, "JitHubApp", "JitHubV2");
        string path = "users/JitHubApp/repos?sort=updated&direction=desc&per_page=20&page=1";
        GitHubQuery<GitHubRepository[]> seedQuery = CreateQuery(
            path,
            GitHubCachePolicy.RepositoryResource,
            TimeSpan.FromMilliseconds(-1),
            Phase0GitHubJsonSerializerContext.Default.GitHubRepositoryArray,
            ["user-repos", "repo"]);
        await store.PutAsync(seedQuery, CreateResponse(new[] { repository }));
        GitHubPilotQueryService pilot = CreatePilot(store, new FailingTransport());

        CachedResult<GitHubRepository[]> result = await pilot.GetRecentRepositoriesAsync(
            GitHubAuthenticationConstants.PublicAccessToken,
            "public",
            20);

        Assert.Equal(CacheState.Stale, result.CacheState);
        Assert.True(result.IsRefreshInProgress);
        Assert.Single(result.Value!);
        Assert.Equal("JitHubApp/JitHubV2", result.Value![0].FullName);
    }

    private SqliteGitHubCacheStore CreateStore()
    {
        Directory.CreateDirectory(_root);
        return new SqliteGitHubCacheStore(
            Path.Combine(_root, "cache.db"),
            Path.Combine(_root, "payloads"),
            GitHubCachePolicy.Default);
    }

    private static GitHubPilotQueryService CreatePilot(
        SqliteGitHubCacheStore store,
        IGitHubRestTransport transport) =>
        new(new GitHubQueryService(
            store,
            transport,
            new GitHubRequestQueue(),
            new NoopTelemetryService()));

    private static GitHubQuery<T> CreateQuery<T>(
        string path,
        string resourceKind,
        TimeSpan ttl,
        JsonTypeInfo<T> jsonTypeInfo,
        IReadOnlyList<string> tags)
        where T : class =>
        new(
            GitHubAuthenticationConstants.PublicAccessToken,
            "public",
            HttpMethod.Get,
            path,
            GitHubQueryKeys.Create(
                "public",
                HttpMethod.Get,
                path,
                acceptMediaType: null,
                resultType: jsonTypeInfo.Type),
            resourceKind,
            ttl,
            jsonTypeInfo,
            tags,
            GitHubRequestPriority.Visible);

    private static GitHubRestResponse<T> CreateResponse<T>(T payload)
        where T : class =>
        new(
            HttpStatusCode.OK,
            payload,
            IsNotModified: false,
            ETag: "\"etag\"",
            LastModified: DateTimeOffset.UtcNow,
            Link: null,
            RateLimitRemaining: 100,
            RateLimitReset: null,
            RetryAfter: null,
            FetchedAt: DateTimeOffset.UtcNow);

    private static GitHubRepository CreateRepository(long id, string owner, string name) =>
        new()
        {
            Id = id,
            Name = name,
            FullName = $"{owner}/{name}",
            HtmlUrl = $"https://github.com/{owner}/{name}",
            Owner = new GitHubRepositoryOwner
            {
                Login = owner,
                HtmlUrl = $"https://github.com/{owner}"
            }
        };

    private sealed class FailingTransport : IGitHubRestTransport
    {
        public Task<GitHubRestResponse<T>> SendJsonAsync<T>(
            GitHubRestRequest request,
            JsonTypeInfo<T> jsonTypeInfo,
            CancellationToken cancellationToken = default)
            where T : class =>
            throw new HttpRequestException("offline");
    }

    private sealed class NoopTelemetryService : ITelemetryService
    {
        public void TrackEvent(string name, IReadOnlyDictionary<string, string?>? properties = null)
        {
        }

        public void TrackMetric(string name, double value, IReadOnlyDictionary<string, string?>? properties = null)
        {
        }

        public IPerformanceTrace StartTrace(string name, IReadOnlyDictionary<string, string?>? properties = null) =>
            new NoopTrace();

        private sealed class NoopTrace : IPerformanceTrace
        {
            public void Dispose()
            {
            }

            public void SetProperty(string key, string? value)
            {
            }
        }
    }
}
