using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace JitHub.Services;

public sealed record GitHubGraphQlQuery<T>(
    GitHubQuery<T> CacheQuery,
    GitHubGraphQlRequest Request)
    where T : class;

public interface IGitHubGraphQlQueryService
{
    Task<CachedResult<T>> GetAsync<T>(
        GitHubGraphQlQuery<T> query,
        QueryFetchPolicy fetchPolicy,
        CancellationToken cancellationToken = default)
        where T : class;

    Task<CachedResult<T>> RefreshAsync<T>(
        GitHubGraphQlQuery<T> query,
        CancellationToken cancellationToken = default)
        where T : class;
}

public sealed class GitHubGraphQlQueryService : IGitHubGraphQlQueryService
{
    private readonly IGitHubCacheStore _cacheStore;
    private readonly IGitHubGraphQlTransport _transport;
    private readonly IGitHubRequestQueue _requestQueue;
    private readonly ITelemetryService _telemetryService;
    private readonly IApplicationTaskCoordinator _taskCoordinator;
    private readonly IAdaptivePrefetchPolicy _prefetchPolicy;
    private readonly Func<TimeSpan, CancellationToken, Task> _delayAsync;

    public GitHubGraphQlQueryService(
        IGitHubCacheStore cacheStore,
        IGitHubGraphQlTransport transport,
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

    public GitHubGraphQlQueryService(
        IGitHubCacheStore cacheStore,
        IGitHubGraphQlTransport transport,
        IGitHubRequestQueue requestQueue,
        ITelemetryService telemetryService,
        IApplicationTaskCoordinator taskCoordinator,
        IAdaptivePrefetchPolicy prefetchPolicy)
        : this(cacheStore, transport, requestQueue, telemetryService, taskCoordinator, prefetchPolicy, Task.Delay)
    {
    }

    internal GitHubGraphQlQueryService(
        IGitHubCacheStore cacheStore,
        IGitHubGraphQlTransport transport,
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

    internal GitHubGraphQlQueryService(
        IGitHubCacheStore cacheStore,
        IGitHubGraphQlTransport transport,
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
        _prefetchPolicy = prefetchPolicy ?? throw new ArgumentNullException(nameof(prefetchPolicy));
        _delayAsync = delayAsync;
    }

    public async Task<CachedResult<T>> GetAsync<T>(
        GitHubGraphQlQuery<T> query,
        QueryFetchPolicy fetchPolicy,
        CancellationToken cancellationToken = default)
        where T : class
    {
        Validate(query);
        GitHubQuery<T> cacheQuery = WithCanonicalRepresentationKey(query.CacheQuery);
        query = query with { CacheQuery = cacheQuery };
        if (fetchPolicy != QueryFetchPolicy.NetworkOnly)
        {
            CachedResult<T>? cached = await _cacheStore.TryGetAsync(cacheQuery, cancellationToken);
            if (cached?.Value is not null)
            {
                if ((cached.CacheState == CacheState.Fresh && fetchPolicy != QueryFetchPolicy.RefreshInBackground) ||
                    fetchPolicy == QueryFetchPolicy.CacheFirst)
                {
                    TrackCacheEvent("github.cache.hit", cacheQuery, cached.CacheState);
                    return cached;
                }

                TrackCacheEvent("github.cache.stale", cacheQuery, cached.CacheState);
                StartBackgroundRefresh(query);
                return cached with { IsRefreshInProgress = true };
            }
        }

        TrackCacheEvent("github.cache.miss", cacheQuery, CacheState.Miss);
        return await RefreshAsync(query, cancellationToken);
    }

    public async Task<CachedResult<T>> RefreshAsync<T>(
        GitHubGraphQlQuery<T> query,
        CancellationToken cancellationToken = default)
        where T : class
    {
        Validate(query);
        GitHubQuery<T> cacheQuery = WithCanonicalRepresentationKey(query.CacheQuery);
        query = query with { CacheQuery = cacheQuery };
        string dedupeKey = GitHubQueryKeys.CreateDedupeKey(
            cacheQuery.UserId,
            HttpMethod.Post,
            cacheQuery.RelativePath,
            cacheQuery.AcceptMediaType,
            cacheQuery.JsonTypeInfo.Type);
        using IPerformanceTrace trace = _telemetryService.StartTrace(
            "github.request.completed",
            new Dictionary<string, string?>
            {
                ["resource"] = cacheQuery.ResourceKind,
                ["priority"] = cacheQuery.Priority.ToString(),
                ["policy"] = QueryFetchPolicy.NetworkOnly.ToString()
            });

        try
        {
            CachedResult<T> result = await SendWithRetryAsync(query, dedupeKey, cancellationToken);
            trace.SetProperty("http_status", ((int)HttpStatusCode.OK).ToString());
            trace.SetProperty("result", TelemetryTaxonomy.Results.Success);
            return result;
        }
        catch (OperationCanceledException)
        {
            trace.SetProperty("result", TelemetryTaxonomy.Results.Cancelled);
            trace.SetProperty("error_kind", TelemetryTaxonomy.ErrorKinds.Cancelled);
            throw;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            trace.SetProperty("result", TelemetryTaxonomy.Results.Error);
            trace.SetProperty("error_kind", ex.GetType().Name);
            _telemetryService.TrackEvent(
                "github.request.failed",
                new Dictionary<string, string?>
                {
                    ["resource"] = cacheQuery.ResourceKind,
                    ["priority"] = cacheQuery.Priority.ToString(),
                    ["error_kind"] = ex.GetType().Name
                });
            throw;
        }
    }

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

    private async Task<CachedResult<T>> SendWithRetryAsync<T>(
        GitHubGraphQlQuery<T> query,
        string dedupeKey,
        CancellationToken cancellationToken)
        where T : class
    {
        try
        {
            return await SendOnceAsync(query, dedupeKey, cancellationToken);
        }
        catch (GitHubRateLimitException ex)
        {
            ObserveRateLimitSafely(query.CacheQuery.UserId, ex);
            if (ex.RetryDelay > GitHubQueryService.MaxInlineRetryDelay)
            {
                throw;
            }

            _telemetryService.TrackEvent(
                "github.request.failed",
                new Dictionary<string, string?>
                {
                    ["resource"] = query.CacheQuery.ResourceKind,
                    ["priority"] = query.CacheQuery.Priority.ToString(),
                    ["error_kind"] = nameof(GitHubRateLimitException),
                    ["result"] = "retry"
                });
            await _delayAsync(ex.RetryDelay, cancellationToken);
            try
            {
                return await SendOnceAsync(query, dedupeKey, cancellationToken);
            }
            catch (GitHubRateLimitException retryException)
            {
                ObserveRateLimitSafely(query.CacheQuery.UserId, retryException);
                throw;
            }
        }
    }

    private Task<CachedResult<T>> SendOnceAsync<T>(
        GitHubGraphQlQuery<T> query,
        string dedupeKey,
        CancellationToken cancellationToken)
        where T : class =>
        _requestQueue.EnqueueForAccountAsync(
            query.CacheQuery.UserId,
            dedupeKey,
            query.CacheQuery.Priority,
            token => SendAndPersistAsync(query, token),
            cancellationToken);

    private async Task<CachedResult<T>> SendAndPersistAsync<T>(
        GitHubGraphQlQuery<T> query,
        CancellationToken cancellationToken)
        where T : class
    {
        GitHubGraphQlResponse<T> response = await _transport.SendAsync<T>(
            query.CacheQuery.AccessToken,
            query.Request,
            cancellationToken).ConfigureAwait(false);
        ObserveRateLimitSafely(
            query.CacheQuery.UserId,
            response.RateLimitRemaining,
            response.RateLimitReset,
            response.RetryAfter,
            response.RateLimitResource);
        T payload = response.Data
            ?? throw new GitHubApiException(HttpStatusCode.OK, "GitHub GraphQL returned an empty payload.");
        DateTimeOffset fetchedAt = DateTimeOffset.UtcNow;
        await _cacheStore.PutAsync(
            query.CacheQuery,
            new GitHubRestResponse<T>(
                HttpStatusCode.OK,
                payload,
                IsNotModified: false,
                ETag: null,
                LastModified: null,
                Link: null,
                response.RateLimitRemaining,
                response.RateLimitReset,
                response.RetryAfter,
                fetchedAt,
                response.RateLimitResource),
            cancellationToken).ConfigureAwait(false);
        return new CachedResult<T>(
            payload,
            CacheState.Fresh,
            fetchedAt,
            fetchedAt.Add(query.CacheQuery.Ttl));
    }

    private void StartBackgroundRefresh<T>(GitHubGraphQlQuery<T> query)
        where T : class
    {
        GitHubRequestPriority backgroundPriority = query.CacheQuery.Priority == GitHubRequestPriority.Prefetch
            ? GitHubRequestPriority.Prefetch
            : GitHubRequestPriority.BackgroundRefresh;
        GitHubGraphQlQuery<T> backgroundQuery = query with
        {
            CacheQuery = query.CacheQuery with { Priority = backgroundPriority }
        };
        _ = _taskCoordinator.RunAsync(
            token => RefreshAsync(backgroundQuery, token),
            new ApplicationTaskOptions("github.graphql.background_refresh", query.CacheQuery.UserId));
    }

    private void ObserveRateLimitSafely(string accountPartition, GitHubRateLimitException exception)
    {
        TimeSpan? retryAfter = exception.RetryAfter;
        DateTimeOffset? resetAt = exception.RateLimitReset;
        if (retryAfter is null && resetAt is null)
        {
            retryAfter = exception.RetryDelay;
        }

        ObserveRateLimitSafely(
            accountPartition,
            exception.RateLimitRemaining,
            resetAt,
            retryAfter,
            exception.RateLimitResource);
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
            // Adaptive prefetch admission is best-effort and cannot break foreground reads.
        }
    }

    private void TrackCacheEvent<T>(string eventName, GitHubQuery<T> query, CacheState state)
        where T : class =>
        _telemetryService.TrackEvent(
            eventName,
            new Dictionary<string, string?>
            {
                ["resource"] = query.ResourceKind,
                ["cache_state"] = state.ToString(),
                ["priority"] = query.Priority.ToString()
            });

    private static void Validate<T>(GitHubGraphQlQuery<T> query)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(query);
        _ = GitHubAccountPartition.Require(query.CacheQuery.UserId, nameof(query));
        if (query.CacheQuery.Method != HttpMethod.Post)
        {
            throw new ArgumentException("GraphQL cache queries must use POST semantics.", nameof(query));
        }

        if (string.IsNullOrWhiteSpace(query.Request.Query))
        {
            throw new ArgumentException("A GraphQL document is required.", nameof(query));
        }
    }
}
