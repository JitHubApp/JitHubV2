using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using JitHub.Services;
using JitHub.WinUI.Tests.TestDoubles;
using Xunit;

namespace JitHub.WinUI.Tests.Services;

public sealed class GitHubGraphQlQueryServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "JitHubGraphQlQueryTests",
        Guid.NewGuid().ToString("N"));

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
    public async Task RefreshAsync_RecordsExactlyOneSuccessfulTerminalTrace()
    {
        SqliteGitHubCacheStore store = CreateStore();
        RecordingTelemetryService telemetry = new();
        GitHubGraphQlQueryService service = new(
            store,
            new FakeTransport(CreatePayload("network")),
            new GitHubRequestQueue(),
            telemetry,
            new ApplicationTaskCoordinator());

        _ = await service.RefreshAsync(CreateQuery(TimeSpan.FromMinutes(5)));

        RecordedTelemetryTrace trace = Assert.Single(telemetry.Traces);
        Assert.Equal("github.request.completed", trace.Name);
        Assert.Equal(TelemetryTaxonomy.Results.Success, trace.Properties["result"]);
        Assert.Equal("200", trace.Properties["http_status"]);
    }

    [Fact]
    public async Task RefreshAsync_RecordsExactlyOneErrorTerminalTrace()
    {
        SqliteGitHubCacheStore store = CreateStore();
        RecordingTelemetryService telemetry = new();
        GitHubGraphQlQueryService service = new(
            store,
            new FakeTransport(CreatePayload("unused"), rateLimitFailures: 2),
            new GitHubRequestQueue(),
            telemetry,
            new ApplicationTaskCoordinator(),
            (_, _) => Task.CompletedTask);

        await Assert.ThrowsAsync<GitHubRateLimitException>(() =>
            service.RefreshAsync(CreateQuery(TimeSpan.FromMinutes(5))));

        RecordedTelemetryTrace trace = Assert.Single(telemetry.Traces);
        Assert.Equal(TelemetryTaxonomy.Results.Error, trace.Properties["result"]);
    }

    [Fact]
    public async Task RefreshAsync_RecordsExactlyOneCancelledTerminalTrace()
    {
        SqliteGitHubCacheStore store = CreateStore();
        RecordingTelemetryService telemetry = new();
        GitHubGraphQlQueryService service = new(
            store,
            new FakeTransport(CreatePayload("unused")),
            new GitHubRequestQueue(),
            telemetry,
            new ApplicationTaskCoordinator());
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.RefreshAsync(CreateQuery(TimeSpan.FromMinutes(5)), cancellation.Token));

        RecordedTelemetryTrace trace = Assert.Single(telemetry.Traces);
        Assert.Equal(TelemetryTaxonomy.Results.Cancelled, trace.Properties["result"]);
        Assert.Equal(TelemetryTaxonomy.ErrorKinds.Cancelled, trace.Properties["error_kind"]);
    }

    [Fact]
    public async Task GetAsync_MissFetchesAndFreshReadUsesAccountCache()
    {
        SqliteGitHubCacheStore store = CreateStore();
        FakeTransport transport = new(CreatePayload("network"));
        GitHubGraphQlQueryService service = CreateService(store, transport);
        GitHubGraphQlQuery<GitHubProfileGraphQlData> query = CreateQuery(TimeSpan.FromMinutes(5));

        CachedResult<GitHubProfileGraphQlData> first = await service.GetAsync(query, QueryFetchPolicy.StaleFirst);
        CachedResult<GitHubProfileGraphQlData> second = await service.GetAsync(query, QueryFetchPolicy.StaleFirst);

        Assert.Equal("network", first.Value!.User!.Login);
        Assert.Equal(CacheState.Fresh, second.CacheState);
        Assert.Equal(1, transport.CallCount);
    }

    [Fact]
    public async Task GetAsync_StaleValueReturnsBeforeCoordinatedBackgroundRefresh()
    {
        SqliteGitHubCacheStore store = CreateStore();
        GitHubGraphQlQuery<GitHubProfileGraphQlData> query = CreateQuery(TimeSpan.FromMilliseconds(-1));
        await PutAsync(store, query.CacheQuery, CreatePayload("cached"));
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        FakeTransport transport = new(CreatePayload("refreshed"), release.Task);
        GitHubGraphQlQueryService service = CreateService(store, transport);

        CachedResult<GitHubProfileGraphQlData> result = await service.GetAsync(query, QueryFetchPolicy.StaleFirst);

        Assert.Equal("cached", result.Value!.User!.Login);
        Assert.Equal(CacheState.Stale, result.CacheState);
        Assert.True(result.IsRefreshInProgress);
        await WaitForAsync(() => transport.CallCount == 1);
        release.SetResult();
    }

    [Fact]
    public async Task RefreshAsync_ConcurrentRequestsShareOneAccountPartitionedRequest()
    {
        SqliteGitHubCacheStore store = CreateStore();
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        FakeTransport transport = new(CreatePayload("shared"), release.Task);
        GitHubGraphQlQueryService service = CreateService(store, transport);
        GitHubGraphQlQuery<GitHubProfileGraphQlData> query = CreateQuery(TimeSpan.FromMinutes(5));

        Task<CachedResult<GitHubProfileGraphQlData>> first = service.RefreshAsync(query);
        Task<CachedResult<GitHubProfileGraphQlData>> second = service.RefreshAsync(query);
        await WaitForAsync(() => transport.CallCount == 1);
        release.SetResult();

        CachedResult<GitHubProfileGraphQlData>[] results = await Task.WhenAll(first, second);
        Assert.All(results, result => Assert.Equal("shared", result.Value!.User!.Login));
        Assert.Equal(1, transport.CallCount);
    }

    [Fact]
    public async Task RefreshAsync_ShortRateLimitDelayRetriesOnce()
    {
        SqliteGitHubCacheStore store = CreateStore();
        FakeTransport transport = new(CreatePayload("retry"), rateLimitFailures: 1);
        GitHubGraphQlQueryService service = new(
            store,
            transport,
            new GitHubRequestQueue(),
            new NoopTelemetryService(),
            new ApplicationTaskCoordinator(),
            (_, _) => Task.CompletedTask);

        CachedResult<GitHubProfileGraphQlData> result = await service.RefreshAsync(
            CreateQuery(TimeSpan.FromMinutes(5)));

        Assert.Equal("retry", result.Value!.User!.Login);
        Assert.Equal(2, transport.CallCount);
    }

    [Theory]
    [InlineData("")]
    [InlineData("current")]
    [InlineData("anonymous")]
    public async Task GetAsync_RejectsUnstableAccountPartitions(string accountPartition)
    {
        SqliteGitHubCacheStore store = CreateStore();
        FakeTransport transport = new(CreatePayload("unused"));
        GitHubGraphQlQueryService service = CreateService(store, transport);
        GitHubGraphQlQuery<GitHubProfileGraphQlData> query = CreateQuery(TimeSpan.FromMinutes(5));
        query = query with
        {
            CacheQuery = query.CacheQuery with
            {
                UserId = accountPartition,
                CacheKey = GitHubQueryKeys.Create(accountPartition, HttpMethod.Post, query.CacheQuery.RelativePath)
            }
        };

        await Assert.ThrowsAsync<ArgumentException>(() => service.GetAsync(query, QueryFetchPolicy.StaleFirst));
        Assert.Equal(0, transport.CallCount);
    }

    [Fact]
    public async Task Transport_RateLimitHeadersProduceRetryDelay()
    {
        using HttpResponseMessage response = new(HttpStatusCode.TooManyRequests)
        {
            Content = new StringContent("{\"message\":\"slow down\"}")
        };
        response.Headers.TryAddWithoutValidation("Retry-After", "1");
        response.Headers.TryAddWithoutValidation("X-RateLimit-Remaining", "0");
        response.Headers.TryAddWithoutValidation("X-RateLimit-Resource", "graphql");
        using HttpClient client = new(new SingleResponseHandler(response))
        {
            BaseAddress = new Uri("https://api.github.com/")
        };
        GitHubGraphQlTransport transport = new(client);

        GitHubRateLimitException exception = await Assert.ThrowsAsync<GitHubRateLimitException>(() =>
            transport.SendAsync<GitHubProfileGraphQlData>(
                "token",
                new GitHubGraphQlRequest { Query = "query { viewer { login } }" }));

        Assert.Equal(HttpStatusCode.TooManyRequests, exception.StatusCode);
        Assert.Equal(TimeSpan.FromSeconds(1), exception.RetryDelay);
        Assert.Equal(0, exception.RateLimitRemaining);
        Assert.Equal("graphql", exception.RateLimitResource);
    }

    [Fact]
    public async Task Transport_Http200GraphQlRateLimitErrorProducesRetryDelay()
    {
        using HttpResponseMessage response = new(HttpStatusCode.OK)
        {
            Content = new StringContent(
                "{\"data\":null,\"errors\":[{\"message\":\"API rate limit exceeded\"}]}")
        };
        response.Headers.TryAddWithoutValidation("X-RateLimit-Remaining", "0");
        response.Headers.TryAddWithoutValidation("Retry-After", "1");
        response.Headers.TryAddWithoutValidation("X-RateLimit-Resource", "graphql");
        using HttpClient client = new(new SingleResponseHandler(response))
        {
            BaseAddress = new Uri("https://api.github.com/")
        };
        GitHubGraphQlTransport transport = new(client);

        GitHubRateLimitException exception = await Assert.ThrowsAsync<GitHubRateLimitException>(() =>
            transport.SendAsync<GitHubProfileGraphQlData>(
                "token",
                new GitHubGraphQlRequest { Query = "query { viewer { login } }" }));

        Assert.Equal(HttpStatusCode.OK, exception.StatusCode);
        Assert.Equal(TimeSpan.FromSeconds(1), exception.RetryDelay);
        Assert.Equal("graphql", exception.RateLimitResource);
    }

    [Fact]
    public async Task Transport_SuccessPropagatesGraphQlRateLimitBucket()
    {
        DateTimeOffset resetAt = DateTimeOffset.UtcNow.AddMinutes(10);
        using HttpResponseMessage response = new(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"data\":{\"user\":{\"login\":\"octocat\"}}}")
        };
        response.Headers.TryAddWithoutValidation("X-RateLimit-Remaining", "42");
        response.Headers.TryAddWithoutValidation(
            "X-RateLimit-Reset",
            resetAt.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture));
        response.Headers.TryAddWithoutValidation("X-RateLimit-Resource", "graphql");
        using HttpClient client = new(new SingleResponseHandler(response))
        {
            BaseAddress = new Uri("https://api.github.com/")
        };
        GitHubGraphQlTransport transport = new(client);

        GitHubGraphQlResponse<GitHubProfileGraphQlData> result = await transport.SendAsync<GitHubProfileGraphQlData>(
            "token",
            new GitHubGraphQlRequest { Query = "query { user(login: \"octocat\") { login } }" });

        Assert.Equal(42, result.RateLimitRemaining);
        Assert.Equal(resetAt.ToUnixTimeSeconds(), result.RateLimitReset?.ToUnixTimeSeconds());
        Assert.Equal("graphql", result.RateLimitResource);
    }

    [Fact]
    public async Task RefreshAsync_PropagatesGraphQlBucketObservationToPrefetchPolicy()
    {
        DateTimeOffset resetAt = DateTimeOffset.UtcNow.AddMinutes(20);
        SqliteGitHubCacheStore store = CreateStore();
        FakeTransport transport = new(
            CreatePayload("network"),
            remaining: 75,
            resetAt: resetAt,
            resource: "graphql");
        RecordingPrefetchPolicy policy = new();
        GitHubGraphQlQueryService service = new(
            store,
            transport,
            new GitHubRequestQueue(),
            new NoopTelemetryService(),
            new ApplicationTaskCoordinator(),
            policy,
            (_, _) => Task.CompletedTask);

        await service.RefreshAsync(CreateQuery(TimeSpan.FromMinutes(5)));

        RateLimitObservation observation = Assert.Single(policy.Observations);
        Assert.Equal("u1", observation.AccountPartition);
        Assert.Equal(75, observation.Remaining);
        Assert.Equal(resetAt, observation.ResetAt);
        Assert.Equal("graphql", observation.Resource);
    }

    private SqliteGitHubCacheStore CreateStore()
    {
        Directory.CreateDirectory(_root);
        return new SqliteGitHubCacheStore(
            Path.Combine(_root, "cache.db"),
            Path.Combine(_root, "payloads"),
            GitHubCachePolicy.Default);
    }

    private static GitHubGraphQlQueryService CreateService(
        IGitHubCacheStore store,
        IGitHubGraphQlTransport transport) =>
        new(
            store,
            transport,
            new GitHubRequestQueue(),
            new NoopTelemetryService(),
            new ApplicationTaskCoordinator());

    private static GitHubGraphQlQuery<GitHubProfileGraphQlData> CreateQuery(TimeSpan ttl)
    {
        const string relativePath = "graphql/profile?login=octocat";
        GitHubQuery<GitHubProfileGraphQlData> cacheQuery = new(
            "token",
            "u1",
            HttpMethod.Post,
            relativePath,
            GitHubQueryKeys.Create(
                "u1",
                HttpMethod.Post,
                relativePath,
                acceptMediaType: null,
                JitHub.Models.GitHub.GitHubJsonSerializerContext.Default.GitHubProfileGraphQlData.Type),
            "profile_graphql",
            ttl,
            JitHub.Models.GitHub.GitHubJsonSerializerContext.Default.GitHubProfileGraphQlData,
            ["profile", "profile-graphql"]);
        return new GitHubGraphQlQuery<GitHubProfileGraphQlData>(
            cacheQuery,
            new GitHubGraphQlRequest
            {
                Query = "query Profile($login: String!) { user(login: $login) { login } }",
                Variables = new Dictionary<string, string?> { ["login"] = "octocat" }
            });
    }

    private static GitHubProfileGraphQlData CreatePayload(string login) => new()
    {
        User = new GitHubProfileGraphQlUser { Login = login }
    };

    private static Task PutAsync(
        IGitHubCacheStore store,
        GitHubQuery<GitHubProfileGraphQlData> query,
        GitHubProfileGraphQlData payload) =>
        store.PutAsync(
            query,
            new GitHubRestResponse<GitHubProfileGraphQlData>(
                HttpStatusCode.OK,
                payload,
                IsNotModified: false,
                ETag: null,
                LastModified: null,
                Link: null,
                RateLimitRemaining: 100,
                RateLimitReset: null,
                RetryAfter: null,
                FetchedAt: DateTimeOffset.UtcNow));

    private static async Task WaitForAsync(Func<bool> predicate)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow.AddSeconds(3);
        while (!predicate())
        {
            if (DateTimeOffset.UtcNow >= deadline)
            {
                throw new TimeoutException("The expected GraphQL operation did not start.");
            }

            await Task.Delay(10);
        }
    }

    private sealed class FakeTransport : IGitHubGraphQlTransport
    {
        private readonly GitHubProfileGraphQlData _payload;
        private readonly Task _gate;
        private readonly int? _remaining;
        private readonly DateTimeOffset? _resetAt;
        private readonly string? _resource;
        private int _rateLimitFailures;
        private int _callCount;

        public FakeTransport(
            GitHubProfileGraphQlData payload,
            Task? gate = null,
            int rateLimitFailures = 0,
            int? remaining = null,
            DateTimeOffset? resetAt = null,
            string? resource = null)
        {
            _payload = payload;
            _gate = gate ?? Task.CompletedTask;
            _rateLimitFailures = rateLimitFailures;
            _remaining = remaining;
            _resetAt = resetAt;
            _resource = resource;
        }

        public int CallCount => Volatile.Read(ref _callCount);

        public async Task<GitHubGraphQlResponse<T>> SendAsync<T>(
            string accessToken,
            GitHubGraphQlRequest request,
            CancellationToken cancellationToken = default)
            where T : class
        {
            Interlocked.Increment(ref _callCount);
            if (Interlocked.Decrement(ref _rateLimitFailures) >= 0)
            {
                throw new GitHubRateLimitException(
                    HttpStatusCode.TooManyRequests,
                    "rate limited",
                    TimeSpan.FromMilliseconds(10),
                    _remaining,
                    _resetAt,
                    retryAfter: null,
                    rateLimitResource: _resource);
            }

            await _gate.WaitAsync(cancellationToken);
            return new GitHubGraphQlResponse<T>
            {
                Data = (T)(object)_payload,
                RateLimitRemaining = _remaining,
                RateLimitReset = _resetAt,
                RateLimitResource = _resource
            };
        }
    }

    private sealed class RecordingPrefetchPolicy : IAdaptivePrefetchPolicy
    {
        public List<RateLimitObservation> Observations { get; } = [];

        public AdaptivePrefetchDecision Evaluate(
            string accountPartition,
            AdaptivePrefetchFeature feature,
            AdaptivePrefetchStage stage) =>
            new(true, AdaptivePrefetchSuppressionReason.None);

        public void ObserveRateLimit(
            string accountPartition,
            int? remaining,
            DateTimeOffset? resetAt,
            TimeSpan? retryAfter = null,
            string? resource = null) =>
            Observations.Add(new RateLimitObservation(accountPartition, remaining, resetAt, retryAfter, resource));

        public IReadOnlyList<AdaptivePrefetchCounter> GetCounters() => [];
    }

    private sealed record RateLimitObservation(
        string AccountPartition,
        int? Remaining,
        DateTimeOffset? ResetAt,
        TimeSpan? RetryAfter,
        string? Resource);

    private sealed class SingleResponseHandler : HttpMessageHandler
    {
        private readonly HttpResponseMessage _response;

        public SingleResponseHandler(HttpResponseMessage response) => _response = response;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(_response);
    }

    private sealed class NoopTelemetryService : ITelemetryService
    {
        public void TrackEvent(string name, IReadOnlyDictionary<string, string?>? properties = null)
        {
        }

        public void TrackMetric(string name, double value, IReadOnlyDictionary<string, string?>? properties = null)
        {
        }

        public IPerformanceTrace StartTrace(
            string name,
            IReadOnlyDictionary<string, string?>? properties = null) =>
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
