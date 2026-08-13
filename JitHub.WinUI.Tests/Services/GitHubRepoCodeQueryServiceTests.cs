using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Text.Json.Serialization.Metadata;
using System.Threading;
using System.Threading.Tasks;
using JitHub.Models.CodeViewer;
using JitHub.Models.GitHub;
using JitHub.Services;
using JitHub.Services.CodeViewer;
using Xunit;

namespace JitHub.WinUI.Tests.Services;

public sealed class GitHubRepoCodeQueryServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "JitHubRepoCodeQueryTests", Guid.NewGuid().ToString());

    [Fact]
    public void PerformanceFixture_IsLargeDeterministicAndWithinInteractiveEditorBudget()
    {
        string source = GitHubRepoCodeQueryService.CreatePerformanceFixtureSourceForTests();
        int bytes = System.Text.Encoding.UTF8.GetByteCount(source);

        Assert.InRange(bytes, 96 * 1024, FilePreviewResolver.MaximumInteractiveTextBytes);
        Assert.Contains("public static class App", source, StringComparison.Ordinal);
        Assert.Contains("Experience = \"Native\"", source, StringComparison.Ordinal);
        Assert.True(GitHubRepoCodeQueryService.PerformanceFixtureTreeFileCount >= 1_000);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        }
        catch
        {
        }
    }

    [Fact]
    public async Task Tree_UsesPhase0CacheAndRepositoryInvalidationTag()
    {
        (GitHubRepoCodeQueryService service, SqliteGitHubCacheStore store, RecordingTransport transport) = CreateService();

        CachedResult<GitHubTree> first = await service.GetTreeAsync("token", "42", "octo", "repo", "feature/test");
        CachedResult<GitHubTree> cached = await service.GetTreeAsync(
            "token", "42", "octo", "repo", "feature/test", QueryFetchPolicy.CacheFirst);

        Assert.Equal("repos/octo/repo/git/trees/feature%2Ftest?recursive=1", transport.Requests[0].RelativePath);
        Assert.Equal(CacheState.Fresh, cached.CacheState);
        Assert.Equal("tree-sha", first.Value!.Sha);
        Assert.Single(transport.Requests);

        await store.InvalidateTagsAsync(["repo:octo/repo"]);
        await service.GetTreeAsync("token", "42", "octo", "repo", "feature/test", QueryFetchPolicy.CacheFirst);
        Assert.Equal(2, transport.Requests.Count);
    }

    [Fact]
    public async Task Directory_EscapesEachPathSegmentAndKeepsRefInCacheIdentity()
    {
        (GitHubRepoCodeQueryService service, _, RecordingTransport transport) = CreateService();

        await service.GetDirectoryAsync("token", "42", "octo", "repo", "src/My Folder", "release/v1");

        Assert.Equal(
            "repos/octo/repo/contents/src/My%20Folder?ref=release%2Fv1",
            transport.Requests[0].RelativePath);
    }

    [Fact]
    public async Task Directory_PreservesWhitespaceAndEscapesReservedAndUnicodePathCharacters()
    {
        (GitHubRepoCodeQueryService service, _, RecordingTransport transport) = CreateService();

        await service.GetDirectoryAsync(
            "token",
            "42",
            "octo",
            "repo",
            "src/ padded /#?%/café.cs",
            "feature/100% ready");

        Assert.Equal(
            "repos/octo/repo/contents/src/%20padded%20/%23%3F%25/caf%C3%A9.cs?ref=feature%2F100%25%20ready",
            transport.Requests[0].RelativePath);
    }

    [Fact]
    public async Task Directory_PreservesWhitespaceOnlyGitSegment()
    {
        (GitHubRepoCodeQueryService service, _, RecordingTransport transport) = CreateService();

        await service.GetDirectoryAsync("token", "42", "octo", "repo", " ", "main");

        Assert.Equal("repos/octo/repo/contents/%20?ref=main", transport.Requests[0].RelativePath);
    }

    [Fact]
    public void BuildRepoTree_PreservesWhitespaceOnlyGitPath()
    {
        RepoTree tree = RepoTreeService.BuildRepoTree(new GitHubTree
        {
            Tree = [new GitHubTreeEntry { Path = " ", Type = "blob", Sha = "space-sha" }]
        });

        RepoTreeNode item = Assert.Single(tree.Root.Children);
        Assert.Equal(" ", item.Name);
        Assert.Equal(" ", item.Path);
        Assert.Equal("space-sha", item.Sha);
    }

    [Fact]
    public void CodeUrls_EncodeRefAndEveryPathSegmentWithoutChangingGitWhitespace()
    {
        string blob = GitHubCodeUrlBuilder.BuildBlobUrl(
            "owner",
            "repo",
            "feature/a?b#c%",
            "src/ padded /café #?.cs");
        string raw = GitHubCodeUrlBuilder.BuildRawUrl(
            "owner",
            "repo",
            "feature/a?b#c%",
            "src/ padded /café #?.cs");

        Assert.Equal(
            "https://github.com/owner/repo/blob/feature%2Fa%3Fb%23c%25/src/%20padded%20/caf%C3%A9%20%23%3F.cs",
            blob);
        Assert.Equal(
            "https://raw.githubusercontent.com/owner/repo/feature%2Fa%3Fb%23c%25/src/%20padded%20/caf%C3%A9%20%23%3F.cs",
            raw);
        Assert.Equal($"{blob}#L42", GitHubCodeUrlBuilder.AppendLineFragment(blob, 42));
    }

    [Fact]
    public async Task Blob_UsesImmutableShaEndpointAndReturnsCachedPayload()
    {
        (GitHubRepoCodeQueryService service, _, RecordingTransport transport) = CreateService();

        CachedResult<GitHubBlob> first = await service.GetBlobAsync("token", "42", "octo", "repo", "abc123");
        CachedResult<GitHubBlob> cached = await service.GetBlobAsync(
            "token", "42", "octo", "repo", "abc123", QueryFetchPolicy.CacheFirst);

        Assert.Equal("repos/octo/repo/git/blobs/abc123", transport.Requests[0].RelativePath);
        Assert.Equal("abc123", first.Value!.Sha);
        Assert.Equal(CacheState.Fresh, cached.CacheState);
        Assert.Single(transport.Requests);
    }

    [Fact]
    public async Task Blob_PreservesRequestedPrefetchPriorityAtQueryBoundary()
    {
        CapturingQueryService queryService = new();
        GitHubRepoCodeQueryService service = new(queryService);

        await service.GetBlobAsync(
            "token",
            "42",
            "octo",
            "repo",
            "abc123",
            GitHubRequestPriority.Prefetch);

        Assert.Equal(GitHubRequestPriority.Prefetch, queryService.Priority);
        Assert.Equal("repos/octo/repo/git/blobs/abc123", queryService.RelativePath);
    }

    [Theory]
    [InlineData("main", 5)]
    [InlineData("0123456789abcdef0123456789abcdef01234567", 43200)]
    public async Task Tree_UsesRefMutabilityTtl(string gitRef, int expectedMinutes)
    {
        (GitHubRepoCodeQueryService service, _, _) = CreateService();

        CachedResult<GitHubTree> result = await service.GetTreeAsync("token", "42", "octo", "repo", gitRef);

        Assert.NotNull(result.StaleAfter);
        Assert.NotNull(result.FetchedAt);
        Assert.InRange(
            result.StaleAfter.Value - result.FetchedAt.Value,
            TimeSpan.FromMinutes(expectedMinutes).Subtract(TimeSpan.FromSeconds(1)),
            TimeSpan.FromMinutes(expectedMinutes).Add(TimeSpan.FromSeconds(1)));
    }

    [Theory]
    [InlineData("feature/work", 5)]
    [InlineData("abcdefabcdefabcdefabcdefabcdefabcdefabcd", 43200)]
    public async Task Directory_UsesRefMutabilityTtl(string gitRef, int expectedMinutes)
    {
        (GitHubRepoCodeQueryService service, _, _) = CreateService();

        CachedResult<GitHubRepositoryContent[]> result = await service.GetDirectoryAsync(
            "token", "42", "octo", "repo", "src", gitRef);

        Assert.NotNull(result.StaleAfter);
        Assert.NotNull(result.FetchedAt);
        Assert.InRange(
            result.StaleAfter.Value - result.FetchedAt.Value,
            TimeSpan.FromMinutes(expectedMinutes).Subtract(TimeSpan.FromSeconds(1)),
            TimeSpan.FromMinutes(expectedMinutes).Add(TimeSpan.FromSeconds(1)));
    }

    private (GitHubRepoCodeQueryService Service, SqliteGitHubCacheStore Store, RecordingTransport Transport) CreateService()
    {
        Directory.CreateDirectory(_root);
        SqliteGitHubCacheStore store = new(
            Path.Combine(_root, "cache.db"),
            Path.Combine(_root, "payloads"),
            GitHubCachePolicy.Default);
        RecordingTransport transport = new();
        GitHubQueryService queryService = new(store, transport, new GitHubRequestQueue(), new NoopTelemetryService());
        return (new GitHubRepoCodeQueryService(queryService), store, transport);
    }

    private sealed class RecordingTransport : IGitHubRestTransport
    {
        public List<GitHubRestRequest> Requests { get; } = [];

        public Task<GitHubRestResponse<T>> SendJsonAsync<T>(
            GitHubRestRequest request,
            JsonTypeInfo<T> jsonTypeInfo,
            CancellationToken cancellationToken = default)
            where T : class
        {
            Requests.Add(request);
            object payload = typeof(T) == typeof(GitHubTree)
                ? new GitHubTree
                {
                    Sha = "tree-sha",
                    Tree = [new GitHubTreeEntry { Path = "README.md", Type = "blob", Sha = "abc123" }]
                }
                : typeof(T) == typeof(GitHubRepositoryContent[])
                    ? new[] { new GitHubRepositoryContent { Name = "file.cs", Path = "src/file.cs", Type = "file", Sha = "abc123" } }
                    : typeof(T) == typeof(GitHubBlob)
                        ? new GitHubBlob
                        {
                            Sha = "abc123",
                            Encoding = "base64",
                            Content = Convert.ToBase64String(Encoding.UTF8.GetBytes("content"))
                        }
                        : throw new InvalidOperationException($"Unexpected response type {typeof(T).Name}.");

            return Task.FromResult(new GitHubRestResponse<T>(
                HttpStatusCode.OK,
                (T)payload,
                IsNotModified: false,
                ETag: "\"repo-code\"",
                LastModified: DateTimeOffset.UtcNow,
                Link: null,
                RateLimitRemaining: 100,
                RateLimitReset: null,
                RetryAfter: null,
                FetchedAt: DateTimeOffset.UtcNow));
        }
    }

    private sealed class CapturingQueryService : IGitHubQueryService
    {
        public GitHubRequestPriority? Priority { get; private set; }
        public string? RelativePath { get; private set; }

        public Task<CachedResult<T>> GetAsync<T>(
            GitHubQuery<T> query,
            QueryFetchPolicy fetchPolicy,
            CancellationToken cancellationToken = default)
            where T : class
        {
            Priority = query.Priority;
            RelativePath = query.RelativePath;
            T value = (T)(object)new GitHubBlob
            {
                Sha = "abc123",
                Encoding = "base64",
                Content = string.Empty
            };
            return Task.FromResult(new CachedResult<T>(
                value,
                CacheState.Fresh,
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow.AddDays(30)));
        }

        public Task<CachedResult<T>> RefreshAsync<T>(
            GitHubQuery<T> query,
            CancellationToken cancellationToken = default)
            where T : class =>
            GetAsync(query, QueryFetchPolicy.NetworkOnly, cancellationToken);

        public Task InvalidateAsync(string cacheKey, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task InvalidateTagsAsync(
            IReadOnlyCollection<string> tags,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    private sealed class NoopTelemetryService : ITelemetryService
    {
        public void TrackEvent(string name, IReadOnlyDictionary<string, string?>? properties = null) { }
        public void TrackMetric(string name, double value, IReadOnlyDictionary<string, string?>? properties = null) { }
        public IPerformanceTrace StartTrace(string name, IReadOnlyDictionary<string, string?>? properties = null) => new NoopTrace();

        private sealed class NoopTrace : IPerformanceTrace
        {
            public void Dispose() { }
            public void SetProperty(string key, string? value) { }
        }
    }
}
