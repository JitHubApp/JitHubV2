using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace JitHub.Services;

public interface IGitHubQueryService
{
    Task<CachedResult<T>> GetAsync<T>(
        GitHubQuery<T> query,
        QueryFetchPolicy fetchPolicy,
        CancellationToken cancellationToken = default)
        where T : class;

    Task<CachedResult<T>> RefreshAsync<T>(
        GitHubQuery<T> query,
        CancellationToken cancellationToken = default)
        where T : class;

    Task InvalidateAsync(string cacheKey, CancellationToken cancellationToken = default);

    Task InvalidateTagsAsync(IReadOnlyCollection<string> tags, CancellationToken cancellationToken = default);

    Task InvalidateTagsAsync(
        string userId,
        IReadOnlyCollection<string> tags,
        CancellationToken cancellationToken = default) =>
        InvalidateTagsAsync(tags, cancellationToken);
}

public sealed class GitHubQueryService : IGitHubQueryService
{
    private readonly IGitHubCacheStore _cacheStore;
    private readonly IGitHubRestTransport _transport;
    private readonly IGitHubRequestQueue _requestQueue;
    private readonly ITelemetryService _telemetryService;
    private readonly IApplicationTaskCoordinator _taskCoordinator;
    private readonly IAdaptivePrefetchPolicy _prefetchPolicy;
    private readonly Func<TimeSpan, CancellationToken, Task> _delayAsync;

    internal static readonly TimeSpan MaxInlineRetryDelay = TimeSpan.FromSeconds(2);

    public GitHubQueryService(
        IGitHubCacheStore cacheStore,
        IGitHubRestTransport transport,
        IGitHubRequestQueue requestQueue,
        ITelemetryService telemetryService)
        : this(
            cacheStore,
            transport,
            requestQueue,
            telemetryService,
            new ApplicationTaskCoordinator(),
            UnrestrictedAdaptivePrefetchPolicy.Instance,
            Task.Delay)
    {
    }

    public GitHubQueryService(
        IGitHubCacheStore cacheStore,
        IGitHubRestTransport transport,
        IGitHubRequestQueue requestQueue,
        ITelemetryService telemetryService,
        IApplicationTaskCoordinator taskCoordinator)
        : this(
            cacheStore,
            transport,
            requestQueue,
            telemetryService,
            taskCoordinator,
            UnrestrictedAdaptivePrefetchPolicy.Instance,
            Task.Delay)
    {
    }

    public GitHubQueryService(
        IGitHubCacheStore cacheStore,
        IGitHubRestTransport transport,
        IGitHubRequestQueue requestQueue,
        ITelemetryService telemetryService,
        IApplicationTaskCoordinator taskCoordinator,
        IAdaptivePrefetchPolicy prefetchPolicy)
        : this(cacheStore, transport, requestQueue, telemetryService, taskCoordinator, prefetchPolicy, Task.Delay)
    {
    }

    internal GitHubQueryService(
        IGitHubCacheStore cacheStore,
        IGitHubRestTransport transport,
        IGitHubRequestQueue requestQueue,
        ITelemetryService telemetryService,
        Func<TimeSpan, CancellationToken, Task> delayAsync)
        : this(
            cacheStore,
            transport,
            requestQueue,
            telemetryService,
            new ApplicationTaskCoordinator(),
            UnrestrictedAdaptivePrefetchPolicy.Instance,
            delayAsync)
    {
    }

    internal GitHubQueryService(
        IGitHubCacheStore cacheStore,
        IGitHubRestTransport transport,
        IGitHubRequestQueue requestQueue,
        ITelemetryService telemetryService,
        IApplicationTaskCoordinator taskCoordinator,
        Func<TimeSpan, CancellationToken, Task> delayAsync)
        : this(
            cacheStore,
            transport,
            requestQueue,
            telemetryService,
            taskCoordinator,
            UnrestrictedAdaptivePrefetchPolicy.Instance,
            delayAsync)
    {
    }

    internal GitHubQueryService(
        IGitHubCacheStore cacheStore,
        IGitHubRestTransport transport,
        IGitHubRequestQueue requestQueue,
        ITelemetryService telemetryService,
        IApplicationTaskCoordinator taskCoordinator,
        IAdaptivePrefetchPolicy prefetchPolicy,
        Func<TimeSpan, CancellationToken, Task> delayAsync)
    {
        _cacheStore = cacheStore;
        _transport = transport;
        _requestQueue = requestQueue;
        _telemetryService = SafeTelemetryService.Wrap(telemetryService);
        _taskCoordinator = taskCoordinator;
        _prefetchPolicy = prefetchPolicy;
        _delayAsync = delayAsync;
    }

    public async Task<CachedResult<T>> GetAsync<T>(
        GitHubQuery<T> query,
        QueryFetchPolicy fetchPolicy,
        CancellationToken cancellationToken = default)
        where T : class
    {
        _ = GitHubAccountPartition.Require(query.UserId, nameof(query));
        query = WithCanonicalRepresentationKey(query);
        if (fetchPolicy != QueryFetchPolicy.NetworkOnly)
        {
            CachedResult<T>? cached = await _cacheStore.TryGetAsync(query, cancellationToken);
            if (cached?.Value is not null)
            {
                if (cached.CacheState == CacheState.Fresh && fetchPolicy != QueryFetchPolicy.RefreshInBackground)
                {
                    TrackCacheEvent("github.cache.hit", query, cached.CacheState);
                    return cached;
                }

                if (fetchPolicy == QueryFetchPolicy.CacheFirst)
                {
                    TrackCacheEvent("github.cache.hit", query, cached.CacheState);
                    return cached;
                }

                TrackCacheEvent("github.cache.stale", query, cached.CacheState);
                StartBackgroundRefresh(query);
                return cached with { IsRefreshInProgress = true };
            }
        }

        TrackCacheEvent("github.cache.miss", query, CacheState.Miss);
        return await RefreshAsync(query, cancellationToken);
    }

    public async Task<CachedResult<T>> RefreshAsync<T>(
        GitHubQuery<T> query,
        CancellationToken cancellationToken = default)
        where T : class
    {
        _ = GitHubAccountPartition.Require(query.UserId, nameof(query));
        query = WithCanonicalRepresentationKey(query);
        string dedupeKey = GitHubQueryKeys.CreateDedupeKey(
            query.UserId,
            query.Method,
            query.RelativePath,
            query.AcceptMediaType,
            query.JsonTypeInfo.Type);
        using IPerformanceTrace trace = _telemetryService.StartTrace(
            "github.request.completed",
            new Dictionary<string, string?>
            {
                ["resource"] = query.ResourceKind,
                ["priority"] = query.Priority.ToString(),
                ["policy"] = QueryFetchPolicy.NetworkOnly.ToString()
            });

        try
        {
            CachedResult<T>? cached = await _cacheStore.TryGetAsync(query, cancellationToken);
            GitHubRestRequest request = new(
                query.AccessToken,
                query.Method,
                query.RelativePath,
                cached?.ETag,
                cached?.LastModified,
                query.Priority,
                query.AcceptMediaType,
                query.AcceptNotFound);

            QueryRefreshOutcome<T> outcome = await SendWithRetryAsync(
                query,
                request,
                dedupeKey,
                cancellationToken);

            trace.SetProperty("http_status", ((int)outcome.StatusCode).ToString());
            trace.SetProperty("result", TelemetryTaxonomy.Results.Success);
            return outcome.Result;
        }
        catch (OperationCanceledException)
        {
            trace.SetProperty("result", TelemetryTaxonomy.Results.Cancelled);
            trace.SetProperty("error_kind", TelemetryTaxonomy.ErrorKinds.Cancelled);
            throw;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (ex is GitHubRateLimitException rateLimitException)
            {
                TimeSpan? retryAfter = rateLimitException.RetryAfter ??
                    (rateLimitException.RateLimitReset is null ? rateLimitException.RetryDelay : null);
                ObserveRateLimitSafely(
                    query.UserId,
                    rateLimitException.RateLimitRemaining ?? 0,
                    rateLimitException.RateLimitReset,
                    retryAfter,
                    rateLimitException.RateLimitResource);
            }

            trace.SetProperty("result", TelemetryTaxonomy.Results.Error);
            trace.SetProperty("error_kind", ex.GetType().Name);
            _telemetryService.TrackEvent(
                "github.request.failed",
                new Dictionary<string, string?>
                {
                    ["resource"] = query.ResourceKind,
                    ["priority"] = query.Priority.ToString(),
                    ["error_kind"] = ex.GetType().Name
                });
            throw;
        }
    }

    public Task InvalidateAsync(string cacheKey, CancellationToken cancellationToken = default) =>
        _cacheStore.InvalidateAsync(cacheKey, cancellationToken);

    public Task InvalidateTagsAsync(IReadOnlyCollection<string> tags, CancellationToken cancellationToken = default) =>
        _cacheStore.InvalidateTagsAsync(tags, cancellationToken);

    public Task InvalidateTagsAsync(
        string userId,
        IReadOnlyCollection<string> tags,
        CancellationToken cancellationToken = default) =>
        _cacheStore.InvalidateTagsAsync(
            GitHubAccountPartition.Require(userId),
            tags,
            cancellationToken);

    private static GitHubQuery<T> WithCanonicalRepresentationKey<T>(GitHubQuery<T> query)
        where T : class
    {
        string canonicalKey = GitHubQueryKeys.Create(
            query.UserId,
            query.Method,
            query.RelativePath,
            query.AcceptMediaType,
            query.JsonTypeInfo.Type);
        return string.Equals(query.CacheKey, canonicalKey, StringComparison.Ordinal)
            ? query
            : query with { CacheKey = canonicalKey };
    }

    private async Task<QueryRefreshOutcome<T>> SendWithRetryAsync<T>(
        GitHubQuery<T> query,
        GitHubRestRequest request,
        string dedupeKey,
        CancellationToken cancellationToken)
        where T : class
    {
        try
        {
            return await SendOnceAsync(query, request, dedupeKey, cancellationToken);
        }
        catch (GitHubRateLimitException ex) when (ex.RetryDelay <= MaxInlineRetryDelay)
        {
            _telemetryService.TrackEvent(
                "github.request.failed",
                new Dictionary<string, string?>
                {
                    ["resource"] = query.ResourceKind,
                    ["priority"] = query.Priority.ToString(),
                    ["error_kind"] = nameof(GitHubRateLimitException),
                    ["result"] = "retry"
                });
            await _delayAsync(ex.RetryDelay, cancellationToken);
            return await SendOnceAsync(query, request, dedupeKey, cancellationToken);
        }
    }

    private Task<QueryRefreshOutcome<T>> SendOnceAsync<T>(
        GitHubQuery<T> query,
        GitHubRestRequest request,
        string dedupeKey,
        CancellationToken cancellationToken)
        where T : class =>
        _requestQueue.EnqueueForAccountAsync(
            query.UserId,
            dedupeKey,
            query.Priority,
            token => SendAndPersistAsync(query, request, token),
            cancellationToken);

    private async Task<QueryRefreshOutcome<T>> SendAndPersistAsync<T>(
        GitHubQuery<T> query,
        GitHubRestRequest request,
        CancellationToken cancellationToken)
        where T : class
    {
        GitHubRestResponse<T> response = await _transport
            .SendJsonAsync(request, query.JsonTypeInfo, cancellationToken)
            .ConfigureAwait(false);
        ObserveRateLimitSafely(
            query.UserId,
            response.RateLimitRemaining,
            response.RateLimitReset,
            response.RetryAfter,
            response.RateLimitResource);

        if (response.IsNotModified)
        {
            await _cacheStore.MarkRevalidatedAsync(query, response, cancellationToken)
                .ConfigureAwait(false);
            CachedResult<T>? revalidated = await _cacheStore.TryGetAsync(query, cancellationToken)
                .ConfigureAwait(false);
            if (revalidated?.Value is not null)
            {
                return new QueryRefreshOutcome<T>(
                    response.StatusCode,
                    revalidated with { CacheState = CacheState.Fresh });
            }
        }

        if (response.Payload is null && query.EmptyResponseFactory is not null)
        {
            response = response with { Payload = query.EmptyResponseFactory(response.StatusCode) };
        }

        if (response.Payload is null)
        {
            throw new GitHubApiException(response.StatusCode, "GitHub returned an empty payload.");
        }

        await _cacheStore.PutAsync(query, response, cancellationToken).ConfigureAwait(false);
        return new QueryRefreshOutcome<T>(
            response.StatusCode,
            new CachedResult<T>(
                response.Payload,
                CacheState.Fresh,
                response.FetchedAt,
                response.FetchedAt.Add(query.Ttl),
                ETag: response.ETag,
                LastModified: response.LastModified));
    }

    private void ObserveRateLimitSafely(
        string accountPartition,
        int? remaining,
        DateTimeOffset? resetAt,
        TimeSpan? retryAfter,
        string? resource)
    {
        try
        {
            _prefetchPolicy.ObserveRateLimit(accountPartition, remaining, resetAt, retryAfter, resource);
        }
        catch
        {
            // Adaptive admission cannot turn a successful foreground read into a failure.
        }
    }

    private void StartBackgroundRefresh<T>(GitHubQuery<T> query)
        where T : class
    {
        GitHubRequestPriority refreshPriority = query.Priority == GitHubRequestPriority.Prefetch
            ? GitHubRequestPriority.Prefetch
            : GitHubRequestPriority.BackgroundRefresh;
        GitHubQuery<T> backgroundQuery = query with { Priority = refreshPriority };
        _ = _taskCoordinator.RunAsync(
            token => RefreshAsync(backgroundQuery, token),
            new ApplicationTaskOptions("github.query.background_refresh", query.UserId));
    }

    private void TrackCacheEvent<T>(string eventName, GitHubQuery<T> query, CacheState state)
        where T : class
    {
        _telemetryService.TrackEvent(
            eventName,
            new Dictionary<string, string?>
            {
                ["resource"] = query.ResourceKind,
                ["cache_state"] = state.ToString(),
                ["priority"] = query.Priority.ToString()
            });
    }

    private sealed record QueryRefreshOutcome<T>(
        System.Net.HttpStatusCode StatusCode,
        CachedResult<T> Result)
        where T : class;
}
