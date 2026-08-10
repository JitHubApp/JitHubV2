using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json.Serialization.Metadata;
using System.Threading;
using System.Threading.Tasks;
using JitHub.Services;
using JitHub.WinUI.Tests.TestDoubles;
using Xunit;

namespace JitHub.WinUI.Tests.Services;

public sealed class Phase0QueryServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "JitHubPhase0QueryTests", Guid.NewGuid().ToString());

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
    public async Task GetAsync_MissFetchesAndSecondReadUsesFreshCache()
    {
        SqliteGitHubCacheStore store = CreateStore();
        FakeTransport transport = new(new Phase0TestPayload { Name = "network" });
        GitHubQueryService service = CreateService(store, transport);
        GitHubQuery<Phase0TestPayload> query = CreateQuery("test/fresh", TimeSpan.FromMinutes(5));

        CachedResult<Phase0TestPayload> first = await service.GetAsync(query, QueryFetchPolicy.StaleFirst);
        CachedResult<Phase0TestPayload> second = await service.GetAsync(query, QueryFetchPolicy.StaleFirst);

        Assert.Equal("network", first.Value!.Name);
        Assert.Equal(CacheState.Fresh, second.CacheState);
        Assert.Equal(1, transport.CallCount);
    }

    [Fact]
    public async Task GetAsync_StaleCacheReturnsCachedValueBeforeBackgroundRefresh()
    {
        SqliteGitHubCacheStore store = CreateStore();
        GitHubQuery<Phase0TestPayload> query = CreateQuery("test/stale", TimeSpan.FromMilliseconds(-1));
        await store.PutAsync(query, CreateResponse(new Phase0TestPayload { Name = "cached" }));
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        FakeTransport transport = new(new Phase0TestPayload { Name = "refreshed" }, release.Task);
        GitHubQueryService service = CreateService(store, transport);

        CachedResult<Phase0TestPayload> result = await service.GetAsync(query, QueryFetchPolicy.StaleFirst);

        Assert.Equal("cached", result.Value!.Name);
        Assert.Equal(CacheState.Stale, result.CacheState);
        Assert.True(result.IsRefreshInProgress);

        release.SetResult();
        await WaitForAsync(() => transport.CallCount > 0);
    }

    [Fact]
    public async Task RefreshAsync_RecordsExactlyOneSuccessfulTerminalTrace()
    {
        SqliteGitHubCacheStore store = CreateStore();
        RecordingTelemetryService telemetry = new();
        GitHubQueryService service = new(
            store,
            new FakeTransport(new Phase0TestPayload { Name = "network" }),
            new GitHubRequestQueue(),
            telemetry);

        _ = await service.RefreshAsync(CreateQuery("test/trace-success", TimeSpan.FromMinutes(5)));

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
        GitHubQueryService service = new(
            store,
            new FakeTransport(rateLimitFailures: 2),
            new GitHubRequestQueue(),
            telemetry,
            (_, _) => Task.CompletedTask);

        await Assert.ThrowsAsync<GitHubRateLimitException>(() =>
            service.RefreshAsync(CreateQuery("test/trace-error", TimeSpan.FromMinutes(5))));

        RecordedTelemetryTrace trace = Assert.Single(telemetry.Traces);
        Assert.Equal(TelemetryTaxonomy.Results.Error, trace.Properties["result"]);
    }

    [Fact]
    public async Task RefreshAsync_RecordsExactlyOneCancelledTerminalTrace()
    {
        SqliteGitHubCacheStore store = CreateStore();
        RecordingTelemetryService telemetry = new();
        GitHubQueryService service = new(
            store,
            new FakeTransport(new Phase0TestPayload { Name = "unused" }),
            new GitHubRequestQueue(),
            telemetry);
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.RefreshAsync(
                CreateQuery("test/trace-cancelled", TimeSpan.FromMinutes(5)),
                cancellation.Token));

        RecordedTelemetryTrace trace = Assert.Single(telemetry.Traces);
        Assert.Equal(TelemetryTaxonomy.Results.Cancelled, trace.Properties["result"]);
        Assert.Equal(TelemetryTaxonomy.ErrorKinds.Cancelled, trace.Properties["error_kind"]);
    }

    [Fact]
    public async Task GetAsync_StalePrefetchPreservesPrefetchLaneDuringBackgroundRefresh()
    {
        SqliteGitHubCacheStore store = CreateStore();
        GitHubQuery<Phase0TestPayload> query = CreateQuery(
            "test/stale-prefetch-priority",
            TimeSpan.FromMilliseconds(-1)) with
        {
            Priority = GitHubRequestPriority.Prefetch
        };
        await store.PutAsync(query, CreateResponse(new Phase0TestPayload { Name = "cached" }));
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        FakeTransport transport = new(new Phase0TestPayload { Name = "refreshed" }, release.Task);
        GitHubQueryService service = CreateService(store, transport);

        CachedResult<Phase0TestPayload> result = await service.GetAsync(query, QueryFetchPolicy.StaleFirst);
        await WaitForAsync(() => transport.LastRequest is not null);

        Assert.Equal("cached", result.Value!.Name);
        Assert.Equal(GitHubRequestPriority.Prefetch, transport.LastRequest!.Priority);
        release.TrySetResult();
    }

    [Fact]
    public async Task GetAsync_NetworkOnlyPromotesMatchingQueuedStaleRefreshToForeground()
    {
        SqliteGitHubCacheStore store = CreateStore();
        GitHubQuery<Phase0TestPayload> query = CreateQuery("test/stale-promotion", TimeSpan.FromMilliseconds(-1));
        await store.PutAsync(query, CreateResponse(new Phase0TestPayload { Name = "cached" }));
        GitHubRequestQueue queue = new(foregroundReadConcurrency: 1, backgroundReadConcurrency: 1, mutationConcurrency: 1);
        TaskCompletionSource releaseBlocker = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource blockerStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<int> blocker = queue.EnqueueAsync("query-background-blocker", GitHubRequestPriority.BackgroundRefresh, async _ =>
        {
            blockerStarted.SetResult();
            await releaseBlocker.Task;
            return 1;
        });
        await blockerStarted.Task;

        FakeTransport transport = new(new Phase0TestPayload { Name = "network" });
        GitHubQueryService service = new(store, transport, queue, new NoopTelemetryService());
        CachedResult<Phase0TestPayload> stale = await service.GetAsync(query, QueryFetchPolicy.StaleFirst);
        Assert.Equal("cached", stale.Value!.Name);
        await WaitForAsync(() => queue.InFlightCount == 2);

        GitHubQuery<Phase0TestPayload> foregroundQuery = query with { Priority = GitHubRequestPriority.UserInitiated };
        CachedResult<Phase0TestPayload> refreshed = await service
            .GetAsync(foregroundQuery, QueryFetchPolicy.NetworkOnly)
            .WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal("network", refreshed.Value!.Name);
        Assert.Equal(1, transport.CallCount);
        Assert.False(blocker.IsCompleted);

        releaseBlocker.SetResult();
        Assert.Equal(1, await blocker);
    }

    [Fact]
    public async Task GetAsync_CancelledForegroundJoinDoesNotCancelStaleBackgroundRefresh()
    {
        SqliteGitHubCacheStore store = CreateStore();
        GitHubQuery<Phase0TestPayload> query = CreateQuery("test/stale-caller-cancellation", TimeSpan.FromMilliseconds(-1));
        await store.PutAsync(query, CreateResponse(new Phase0TestPayload { Name = "cached" }));
        TaskCompletionSource releaseTransport = new(TaskCreationOptions.RunContinuationsAsynchronously);
        FakeTransport transport = new(new Phase0TestPayload { Name = "network" }, releaseTransport.Task);
        GitHubRequestQueue queue = new(foregroundReadConcurrency: 1, backgroundReadConcurrency: 1, mutationConcurrency: 1);
        GitHubQueryService service = new(store, transport, queue, new NoopTelemetryService());

        CachedResult<Phase0TestPayload> stale = await service.GetAsync(query, QueryFetchPolicy.StaleFirst);
        Assert.Equal("cached", stale.Value!.Name);
        await WaitForAsync(() => transport.CallCount == 1);

        using CancellationTokenSource callerCancellation = new();
        GitHubQuery<Phase0TestPayload> foregroundQuery = query with { Priority = GitHubRequestPriority.UserInitiated };
        Task<CachedResult<Phase0TestPayload>> foreground = service.GetAsync(
            foregroundQuery,
            QueryFetchPolicy.NetworkOnly,
            callerCancellation.Token);
        callerCancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => foreground);
        Assert.False(transport.LastCancellationToken.IsCancellationRequested);

        releaseTransport.SetResult();
        await WaitForAsync(() => queue.InFlightCount == 0);
        Assert.False(transport.LastCancellationToken.IsCancellationRequested);
        Assert.Equal(1, transport.CallCount);
    }

    [Fact]
    public async Task RefreshAsync_NotModifiedReusesCachedPayloadAndValidators()
    {
        SqliteGitHubCacheStore store = CreateStore();
        GitHubQuery<Phase0TestPayload> query = CreateQuery("test/not-modified", TimeSpan.FromMinutes(5));
        await store.PutAsync(query, CreateResponse(new Phase0TestPayload { Name = "cached" }, etag: "\"etag-cached\""));
        FakeTransport transport = new(notModified: true);
        GitHubQueryService service = CreateService(store, transport);

        CachedResult<Phase0TestPayload> result = await service.RefreshAsync(query);

        Assert.Equal("cached", result.Value!.Name);
        Assert.Equal(CacheState.Fresh, result.CacheState);
        Assert.Equal("\"etag-cached\"", transport.LastRequest?.ETag);
    }

    [Fact]
    public async Task RefreshAsync_ShortRateLimitDelayRetriesOnce()
    {
        SqliteGitHubCacheStore store = CreateStore();
        FakeTransport transport = new(
            new Phase0TestPayload { Name = "after-retry" },
            rateLimitFailures: 1);
        GitHubQueryService service = new(
            store,
            transport,
            new GitHubRequestQueue(),
            new NoopTelemetryService(),
            (_, _) => Task.CompletedTask);
        GitHubQuery<Phase0TestPayload> query = CreateQuery("test/retry", TimeSpan.FromMinutes(5));

        CachedResult<Phase0TestPayload> result = await service.RefreshAsync(query);

        Assert.Equal("after-retry", result.Value!.Name);
        Assert.Equal(2, transport.CallCount);
    }

    [Fact]
    public async Task RefreshAsync_UpdatesAdaptivePrefetchRateLimitHeadroomForTheAccount()
    {
        SqliteGitHubCacheStore store = CreateStore();
        FakeTransport transport = new(new Phase0TestPayload { Name = "network" });
        AdaptivePrefetchPolicy policy = new(new AvailablePrefetchEnvironment(), new NoopTelemetryService());
        GitHubQueryService service = new(
            store,
            transport,
            new GitHubRequestQueue(),
            new NoopTelemetryService(),
            new ApplicationTaskCoordinator(),
            policy);
        GitHubQuery<Phase0TestPayload> query = CreateQuery("test/rate-headroom", TimeSpan.FromMinutes(5));

        _ = await service.RefreshAsync(query);

        AdaptivePrefetchDecision decision = policy.Evaluate(
            query.UserId,
            AdaptivePrefetchFeature.Issues,
            AdaptivePrefetchStage.Schedule);
        Assert.Equal(AdaptivePrefetchSuppressionReason.RateLimitHeadroom, decision.SuppressionReason);
    }

    [Fact]
    public async Task RefreshAsync_PropagatesRateLimitResourceWithoutCrossBucketOverwrite()
    {
        SqliteGitHubCacheStore store = CreateStore();
        AdaptivePrefetchPolicy policy = new(new AvailablePrefetchEnvironment(), new NoopTelemetryService());
        DateTimeOffset now = DateTimeOffset.UtcNow;
        GitHubQueryService coreService = new(
            store,
            new FakeTransport(
                responseRemaining: 0,
                responseReset: now.AddMinutes(2),
                rateLimitResource: "core"),
            new GitHubRequestQueue(),
            new NoopTelemetryService(),
            new ApplicationTaskCoordinator(),
            policy);
        GitHubQueryService searchService = new(
            store,
            new FakeTransport(
                responseRemaining: 4_900,
                responseReset: now.AddHours(1),
                rateLimitResource: "search"),
            new GitHubRequestQueue(),
            new NoopTelemetryService(),
            new ApplicationTaskCoordinator(),
            policy);

        _ = await coreService.RefreshAsync(CreateQuery("test/rate-core", TimeSpan.FromMinutes(5)));
        _ = await searchService.RefreshAsync(CreateQuery("test/rate-search", TimeSpan.FromMinutes(5)));

        Assert.False(policy.Evaluate(
            "u1",
            AdaptivePrefetchFeature.Issues,
            AdaptivePrefetchStage.Schedule).IsAllowed);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("current")]
    [InlineData(" CURRENT ")]
    [InlineData("anonymous")]
    public async Task QueryCache_RejectsUnstableAccountPartitionBeforeCacheOrTransport(string accountPartition)
    {
        SqliteGitHubCacheStore store = CreateStore();
        FakeTransport transport = new(new Phase0TestPayload { Name = "must-not-load" });
        GitHubQueryService service = CreateService(store, transport);
        GitHubQuery<Phase0TestPayload> query = CreateQuery("test/account-partition", TimeSpan.FromMinutes(5)) with
        {
            UserId = accountPartition,
            CacheKey = GitHubQueryKeys.Create(accountPartition, HttpMethod.Get, "test/account-partition")
        };

        await Assert.ThrowsAsync<ArgumentException>(() => service.GetAsync(query, QueryFetchPolicy.StaleFirst));

        Assert.Equal(0, transport.CallCount);
    }

    [Fact]
    public void PublicPreviewPartition_IsExplicitAndStable()
    {
        Assert.Equal(
            "public",
            GitHubAccountPartition.Resolve(GitHubAuthenticationConstants.PublicAccessToken, string.Empty));
    }

    [Fact]
    [Trait("Category", "ReleaseSecurity")]
    public async Task RefreshAsync_AccountCleanupCancelsDelayedCachePutBeforePartitionClear()
    {
        SqliteGitHubCacheStore innerStore = CreateStore();
        BlockingPutCacheStore store = new(innerStore);
        AccountWorkQuiescence accountWork = new();
        GitHubRequestQueue queue = new(accountWork);
        FakeTransport transport = new(new Phase0TestPayload { Name = "late" });
        GitHubQueryService service = new(store, transport, queue, new NoopTelemetryService());
        GitHubQuery<Phase0TestPayload> query = CreateQuery("test/cleanup-race", TimeSpan.FromMinutes(5));

        Task<CachedResult<Phase0TestPayload>> refresh = service.RefreshAsync(query);
        await store.PutStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Task quiesce = accountWork.QuiesceAsync("u1");
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => refresh);
        await quiesce.WaitAsync(TimeSpan.FromSeconds(2));
        await store.ClearPartitionAsync("u1");
        store.ReleasePut.TrySetResult();

        Assert.True(store.PutCancellationObserved);
        Assert.Null(await innerStore.TryGetAsync(query));
        Assert.True(accountWork.IsQuiesced("u1"));
    }

    private SqliteGitHubCacheStore CreateStore()
    {
        Directory.CreateDirectory(_root);
        return new SqliteGitHubCacheStore(
            Path.Combine(_root, "cache.db"),
            Path.Combine(_root, "payloads"),
            GitHubCachePolicy.Default);
    }

    private static GitHubQueryService CreateService(SqliteGitHubCacheStore store, FakeTransport transport) =>
        new(
            store,
            transport,
            new GitHubRequestQueue(),
            new NoopTelemetryService());

    private static GitHubQuery<Phase0TestPayload> CreateQuery(string path, TimeSpan ttl) =>
        new(
            GitHubAuthenticationConstants.PublicAccessToken,
            "u1",
            HttpMethod.Get,
            path,
            GitHubQueryKeys.Create(
                "u1",
                HttpMethod.Get,
                path,
                acceptMediaType: null,
                Phase0TestJsonContext.Default.Phase0TestPayload.Type),
            GitHubCachePolicy.MutableResource,
            ttl,
            Phase0TestJsonContext.Default.Phase0TestPayload,
            ["test"],
            GitHubRequestPriority.Visible);

    private static GitHubRestResponse<Phase0TestPayload> CreateResponse(
        Phase0TestPayload payload,
        string etag = "\"etag\"") =>
        new(
            HttpStatusCode.OK,
            payload,
            IsNotModified: false,
            ETag: etag,
            LastModified: DateTimeOffset.UtcNow,
            Link: null,
            RateLimitRemaining: 100,
            RateLimitReset: null,
            RetryAfter: null,
            FetchedAt: DateTimeOffset.UtcNow);

    private static async Task WaitForAsync(Func<bool> predicate)
    {
        for (int i = 0; i < 50; i++)
        {
            if (predicate())
            {
                return;
            }

            await Task.Delay(20);
        }

        Assert.True(predicate());
    }

    private sealed class FakeTransport : IGitHubRestTransport
    {
        private readonly Phase0TestPayload? _payload;
        private readonly bool _notModified;
        private readonly Task? _waitFor;
        private readonly int? _responseRemaining;
        private readonly DateTimeOffset? _responseReset;
        private readonly string? _rateLimitResource;
        private int _rateLimitFailures;

        public FakeTransport(
            Phase0TestPayload? payload = null,
            Task? waitFor = null,
            bool notModified = false,
            int rateLimitFailures = 0,
            int? responseRemaining = 100,
            DateTimeOffset? responseReset = null,
            string? rateLimitResource = null)
        {
            _payload = payload;
            _waitFor = waitFor;
            _notModified = notModified;
            _rateLimitFailures = rateLimitFailures;
            _responseRemaining = responseRemaining;
            _responseReset = responseReset;
            _rateLimitResource = rateLimitResource;
        }

        public int CallCount { get; private set; }

        public GitHubRestRequest? LastRequest { get; private set; }

        public CancellationToken LastCancellationToken { get; private set; }

        public async Task<GitHubRestResponse<T>> SendJsonAsync<T>(
            GitHubRestRequest request,
            JsonTypeInfo<T> jsonTypeInfo,
            CancellationToken cancellationToken = default)
            where T : class
        {
            CallCount++;
            LastRequest = request;
            LastCancellationToken = cancellationToken;
            if (_rateLimitFailures > 0)
            {
                _rateLimitFailures--;
                throw new GitHubRateLimitException(HttpStatusCode.Forbidden, "retry later", TimeSpan.Zero);
            }

            if (_waitFor is not null)
            {
                await _waitFor;
            }

            if (_notModified)
            {
                return new GitHubRestResponse<T>(
                    HttpStatusCode.NotModified,
                    Payload: null,
                    IsNotModified: true,
                    request.ETag,
                    request.LastModified,
                    Link: null,
                    RateLimitRemaining: _responseRemaining,
                    RateLimitReset: _responseReset,
                    RetryAfter: null,
                    FetchedAt: DateTimeOffset.UtcNow,
                    RateLimitResource: _rateLimitResource);
            }

            return new GitHubRestResponse<T>(
                HttpStatusCode.OK,
                (T)(object)(_payload ?? new Phase0TestPayload { Name = "default" }),
                IsNotModified: false,
                ETag: "\"etag-network\"",
                LastModified: DateTimeOffset.UtcNow,
                Link: null,
                RateLimitRemaining: _responseRemaining,
                RateLimitReset: _responseReset,
                RetryAfter: null,
                FetchedAt: DateTimeOffset.UtcNow,
                RateLimitResource: _rateLimitResource);
        }
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

    private sealed class AvailablePrefetchEnvironment : IPrefetchEnvironmentState
    {
        public bool IsNetworkAvailable => true;

        public bool IsMetered => false;

        public bool IsEnergySaverEnabled => false;

        public bool IsMemoryPressureHigh => false;
    }

    private sealed class BlockingPutCacheStore : IGitHubCacheStore
    {
        private readonly IGitHubCacheStore _inner;

        public BlockingPutCacheStore(IGitHubCacheStore inner) => _inner = inner;

        public TaskCompletionSource PutStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource ReleasePut { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool PutCancellationObserved { get; private set; }

        public Task<CachedResult<T>?> TryGetAsync<T>(GitHubQuery<T> query, CancellationToken cancellationToken = default)
            where T : class =>
            _inner.TryGetAsync(query, cancellationToken);

        public async Task PutAsync<T>(
            GitHubQuery<T> query,
            GitHubRestResponse<T> response,
            CancellationToken cancellationToken = default)
            where T : class
        {
            PutStarted.TrySetResult();
            try
            {
                await ReleasePut.Task.WaitAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                PutCancellationObserved = true;
                throw;
            }

            await _inner.PutAsync(query, response, cancellationToken);
        }

        public Task MarkRevalidatedAsync<T>(GitHubQuery<T> query, GitHubRestResponse<T> response, CancellationToken cancellationToken = default)
            where T : class =>
            _inner.MarkRevalidatedAsync(query, response, cancellationToken);

        public Task InvalidateAsync(string cacheKey, CancellationToken cancellationToken = default) =>
            _inner.InvalidateAsync(cacheKey, cancellationToken);

        public Task InvalidateTagsAsync(IReadOnlyCollection<string> tags, CancellationToken cancellationToken = default) =>
            _inner.InvalidateTagsAsync(tags, cancellationToken);

        public Task ClearAllAsync(CancellationToken cancellationToken = default) =>
            _inner.ClearAllAsync(cancellationToken);

        public Task ClearPartitionAsync(string userId, CancellationToken cancellationToken = default) =>
            _inner.ClearPartitionAsync(userId, cancellationToken);

        public Task<long> GetTotalPayloadBytesAsync(CancellationToken cancellationToken = default) =>
            _inner.GetTotalPayloadBytesAsync(cancellationToken);

        public Task<long> GetTotalMetadataBytesAsync(CancellationToken cancellationToken = default) =>
            _inner.GetTotalMetadataBytesAsync(cancellationToken);

        public Task<int> GetSchemaVersionAsync(CancellationToken cancellationToken = default) =>
            _inner.GetSchemaVersionAsync(cancellationToken);

        public Task EnforceCapsAsync(CancellationToken cancellationToken = default) =>
            _inner.EnforceCapsAsync(cancellationToken);

        public Task<CacheStoreInspection> InspectAsync(CancellationToken cancellationToken = default) =>
            _inner.InspectAsync(cancellationToken);
    }
}
