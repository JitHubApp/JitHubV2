using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using MarkdownRenderer.Images;

namespace JitHub.Services;

/// <summary>
/// Resolves GitHub and user-approved HTTPS images through an account-partitioned
/// memory and disk cache, deduplicating concurrent fetches and serving stale
/// entries while they revalidate.
/// </summary>
/// <remarks>
/// The service composes its parts: <see cref="GitHubImageHttpFetcher"/> downloads,
/// <see cref="GitHubImageCacheTier"/> caches, <see cref="GitHubImageRequestFlights"/>
/// deduplicates and <see cref="GitHubLeasedImageSource"/> serves the leased path.
/// It owns their shared lifetime and the memory-pressure subscription. The class is
/// <c>partial</c> only so CsWinRT can generate its ABI projection of IDisposable.
/// </remarks>
public sealed partial class GitHubImageService : IGitHubImageService, IGitHubLeasedImageService, IDisposable
{
    // Large project READMEs commonly contain optimized screenshots and animated
    // demonstrations above 10 MiB. Compressed bytes remain bounded here while
    // RasterImageResourceBudget independently caps dimensions, frames, pixels,
    // and decoded memory before the renderer admits the payload.
    internal const int MaxImageBytes = 32 * 1024 * 1024;

    private readonly GitHubImageAccountScope _accounts;
    private readonly GitHubImageCacheTier _cache;
    private readonly GitHubImageHttpFetcher _http;
    private readonly GitHubImageServiceLifetime _lifetime = new();
    private readonly GitHubImageRequestFlights _misses = new();
    private readonly GitHubImageRequestFlights _refreshes = new();
    private readonly GitHubLeasedImageSource _leased;
    private readonly GitHubImageMemoryPressureSubscription _memoryPressure;
    private bool _observableCacheSubscribed;

    public GitHubImageService(
        IGitHubImageCacheStore cacheStore,
        IAccountService accountService,
        IAccountWorkQuiescence accountWork)
        : this(
            cacheStore,
            GitHubImageHttpFetcher.CreateHttpClient(),
            ownsHttpClient: true,
            accountService.GetUser,
            accountWork: accountWork)
    {
    }

    internal GitHubImageService(
        IGitHubImageCacheStore cacheStore,
        HttpClient httpClient,
        bool ownsHttpClient = false,
        Func<long>? partitionProvider = null,
        Func<string, CancellationToken, Task<IPAddress[]>>? hostAddressResolver = null,
        IAccountWorkQuiescence? accountWork = null,
        Action<EventHandler<object>>? subscribeMemoryPressure = null,
        Action<EventHandler<object>>? unsubscribeMemoryPressure = null)
    {
        _memoryPressure = new GitHubImageMemoryPressureSubscription(
            new GitHubImageMemoryPressureRelay(this).OnAppMemoryUsageIncreased,
            subscribeMemoryPressure,
            unsubscribeMemoryPressure);
        _accounts = new GitHubImageAccountScope(partitionProvider, accountWork);
        _cache = new GitHubImageCacheTier(cacheStore);
        // The production client validates every physical connection in ConnectCallback.
        // Resolving the same host again before every logical HTTP/2 stream adds hundreds
        // of redundant DNS calls on avatar-heavy READMEs without improving rebinding
        // protection. Injected handlers still use the request-level validator when one is
        // supplied because they do not own the connection callback.
        _http = new GitHubImageHttpFetcher(
            httpClient,
            ownsHttpClient,
            !ownsHttpClient ? hostAddressResolver : null);
        _leased = new GitHubLeasedImageSource(_accounts, _cache, _lifetime);

        try
        {
            _memoryPressure.Subscribe();
            if (_cache.AdmittedStore is { } observableStore)
            {
                // Mark before invoking add so the rollback also attempts
                // removal if an event accessor throws after partial registration.
                _observableCacheSubscribed = true;
                observableStore.CacheContentInvalidated += ClearMemoryCache;
            }
        }
        catch (Exception registrationFailure)
        {
            RollBackRegistration(registrationFailure);
            throw;
        }
    }

    public Task<GitHubCachedImage?> GetAsync(
        string sourceUrl,
        CancellationToken cancellationToken = default) =>
        GetAsync(sourceUrl, GitHubImageFetchScope.TrustedGitHub, cancellationToken);

    public Task<GitHubCachedImage?> GetAsync(
        string sourceUrl,
        GitHubImageFetchScope scope,
        CancellationToken cancellationToken = default) =>
        GetOrFetchCoreAsync(
            sourceUrl,
            (cached, token) => _http.FetchAsync(sourceUrl, cached, scope, null, token),
            scope,
            null,
            cancellationToken);

    public Task<GitHubCachedImage?> GetAsync(
        string sourceUrl,
        GitHubImageFetchScope scope,
        IMarkdownImageSourceByteAdmission admission,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(admission);
        return GetOrFetchCoreAsync(
            sourceUrl,
            (cached, token) => _http.FetchAsync(sourceUrl, cached, scope, admission, token),
            scope,
            admission,
            cancellationToken);
    }

    public Task<GitHubCachedImage?> TryGetCachedAsync(
        string sourceUrl,
        GitHubImageFetchScope scope,
        CancellationToken cancellationToken = default) =>
        TryGetCachedCoreAsync(sourceUrl, scope, null, cancellationToken);

    public Task<GitHubCachedImage?> TryGetCachedAsync(
        string sourceUrl,
        GitHubImageFetchScope scope,
        IMarkdownImageSourceByteAdmission admission,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(admission);
        return TryGetCachedCoreAsync(sourceUrl, scope, admission, cancellationToken);
    }

    public async Task<GitHubCachedImage?> GetOrFetchAsync(
        string sourceUrl,
        GitHubImageFetcher fetcher,
        CancellationToken cancellationToken = default)
        => await GetOrFetchCoreAsync(
            sourceUrl,
            (cached, token) => GitHubImageFetchRetryPolicy.FetchWithRetryAsync(fetcher, cached, token),
            GitHubImageFetchScope.UserApprovedHttps,
            null,
            cancellationToken).ConfigureAwait(false);

    public async Task<GitHubCachedImage?> GetOrFetchAsync(
        string sourceUrl,
        GitHubImageFetcher fetcher,
        IMarkdownImageSourceByteAdmission admission,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(admission);
        return await GetOrFetchCoreAsync(
            sourceUrl,
            (cached, token) => GitHubImageFetchRetryPolicy.FetchWithRetryAsync(fetcher, cached, token),
            GitHubImageFetchScope.UserApprovedHttps,
            admission,
            cancellationToken).ConfigureAwait(false);
    }

    Task<GitHubImageSourceLease?> IGitHubLeasedImageService.GetLeasedAsync(
        string sourceUrl,
        GitHubImageFetchScope scope,
        IMarkdownImageSourceByteAdmission admission,
        CancellationToken cancellationToken) =>
        _leased.GetOrFetchAsync(
            sourceUrl,
            (cached, token) => _http.FetchAsync(sourceUrl, cached, scope, admission, token),
            scope,
            admission,
            cancellationToken);

    Task<GitHubImageSourceLease?> IGitHubLeasedImageService.TryGetCachedLeasedAsync(
        string sourceUrl,
        GitHubImageFetchScope scope,
        IMarkdownImageSourceByteAdmission admission,
        CancellationToken cancellationToken) =>
        _leased.TryGetCachedAsync(sourceUrl, scope, admission, cancellationToken);

    Task<GitHubImageSourceLease?> IGitHubLeasedImageService.GetOrFetchLeasedAsync(
        string sourceUrl,
        GitHubImageFetcher fetcher,
        IMarkdownImageSourceByteAdmission admission,
        CancellationToken cancellationToken) =>
        _leased.GetOrFetchAsync(
            sourceUrl,
            (cached, token) => GitHubImageFetchRetryPolicy.FetchWithRetryAsync(fetcher, cached, token),
            GitHubImageFetchScope.UserApprovedHttps,
            admission,
            cancellationToken);

    public async Task InvalidateAsync(string sourceUrl, CancellationToken cancellationToken = default)
    {
        long accountId = _accounts.CurrentAccountId();
        using IAccountWorkLease? lease = _accounts.Enter(accountId, cancellationToken);
        string cacheKey = GitHubImageAccountScope.GetCacheKey(accountId, sourceUrl);
        _cache.Memory.Remove(cacheKey);
        await _cache.Store.InvalidateAsync(
            cacheKey,
            lease?.CancellationToken ?? cancellationToken).ConfigureAwait(false);
    }

    public void Dispose()
    {
        if (!_lifetime.TryRetire())
            return;

        Exception? memoryPressureUnsubscribeFailure = _memoryPressure.IsSubscribed
            ? _memoryPressure.TryUnsubscribe()
            : null;

        if (_observableCacheSubscribed && _cache.AdmittedStore is { } observableStore)
        {
            observableStore.CacheContentInvalidated -= ClearMemoryCache;
            _observableCacheSubscribed = false;
        }

        _cache.Memory.Clear();
        _misses.CancelAll();
        _refreshes.CancelAll();
        _leased.CancelAll();
        _http.Dispose();

        if (memoryPressureUnsubscribeFailure is not null &&
            !GitHubImageMemoryPressureSubscription.IsUnavailable(memoryPressureUnsubscribeFailure))
        {
            ExceptionDispatchInfo.Capture(memoryPressureUnsubscribeFailure).Throw();
        }
    }

    internal void OnAppMemoryUsageIncreased(object? sender, object args)
    {
        if (!_lifetime.IsDisposed)
            ClearMemoryCache();
    }

    private async Task<GitHubCachedImage?> TryGetCachedCoreAsync(
        string sourceUrl,
        GitHubImageFetchScope scope,
        IMarkdownImageSourceByteAdmission? admission,
        CancellationToken cancellationToken)
    {
        if (!GitHubImageSourcePolicy.IsAllowedSource(sourceUrl, scope))
        {
            return null;
        }

        long accountId = _accounts.CurrentAccountId();
        using IAccountWorkLease? lease = _accounts.Enter(accountId, cancellationToken);
        CancellationToken operationToken = lease?.CancellationToken ?? cancellationToken;
        string cacheKey = GitHubImageAccountScope.GetCacheKey(accountId, sourceUrl);
        GitHubImageCacheRead? cached = await _cache.ReadAsync(cacheKey, admission, operationToken)
            .ConfigureAwait(false);
        if (cached is null)
        {
            return null;
        }

        return ToResult(cached, isFromCache: true) with { IsStale = !GitHubImageCacheTier.IsFresh(cached.Entry.CachedAt) };
    }

    private async Task<GitHubCachedImage?> GetOrFetchCoreAsync(
        string sourceUrl,
        GitHubImageFetcher fetcher,
        GitHubImageFetchScope scope,
        IMarkdownImageSourceByteAdmission? admission,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(fetcher);
        if (!GitHubImageSourcePolicy.IsAllowedSource(sourceUrl, scope))
        {
            return null;
        }

        long accountId = _accounts.CurrentAccountId();
        using IAccountWorkLease? readLease = _accounts.Enter(accountId, cancellationToken);
        CancellationToken readToken = readLease?.CancellationToken ?? cancellationToken;
        string cacheKey = GitHubImageAccountScope.GetCacheKey(accountId, sourceUrl);
        // Distinct in-flight identities keep a speculative or unbudgeted
        // downloader from blocking a visible admitted request. Successful
        // downloads still share the same account-partitioned cache entry.
        string flightKey = admission is null
            ? cacheKey
            : admission.IsSpeculative ? cacheKey + "#source-speculative" : cacheKey + "#source-visible";
        GitHubImageCacheRead? cached = await _cache.ReadAsync(cacheKey, admission, readToken)
            .ConfigureAwait(false);
        if (cached is not null)
        {
            if (GitHubImageCacheTier.IsFresh(cached.Entry.CachedAt))
            {
                return ToResult(cached, isFromCache: true);
            }

            return await ServeStaleAsync(
                    accountId, cacheKey, flightKey, sourceUrl, cached, fetcher, admission, cancellationToken)
                .ConfigureAwait(false);
        }

        return await _misses.JoinAsync(
                flightKey,
                token => FetchAndStoreWithAccountLeaseAsync(
                    accountId,
                    cacheKey,
                    sourceUrl,
                    null,
                    fetcher,
                    admission,
                    token),
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<GitHubCachedImage?> ServeStaleAsync(
        long accountId,
        string cacheKey,
        string flightKey,
        string sourceUrl,
        GitHubImageCacheRead cached,
        GitHubImageFetcher fetcher,
        IMarkdownImageSourceByteAdmission? admission,
        CancellationToken cancellationToken)
    {
        Task<GitHubCachedImage?> refreshTask = _refreshes.JoinAsync(
            flightKey,
            token => FetchAndStoreWithAccountLeaseAsync(
                accountId,
                cacheKey,
                sourceUrl,
                cached,
                fetcher,
                admission,
                token),
            cancellationToken);
        if (admission is not null)
        {
            // The admission is scoped to the renderer's active resolution.
            // A detached stale-while-revalidate task could outlive that
            // scope and continue downloading after account/session
            // retirement. Keep admitted refreshes inside the caller's
            // tracked lifetime until a separately owned refresh budget
            // exists. FetchAndStoreAsync preserves the stale asset if
            // revalidation fails.
            GitHubCachedImage? refreshed = await refreshTask.ConfigureAwait(false);
            return refreshed ?? (ToResult(cached, isFromCache: true) with { IsStale = true });
        }

        return new GitHubCachedImage(
            cached.Entry.FilePath,
            cached.Bytes,
            cached.Entry.ContentType,
            IsFromCache: true,
            IsStale: true,
            RefreshTask: refreshTask);
    }

    private async Task<GitHubCachedImage?> FetchAndStoreWithAccountLeaseAsync(
        long accountId,
        string cacheKey,
        string sourceUrl,
        GitHubImageCacheRead? cached,
        GitHubImageFetcher fetcher,
        IMarkdownImageSourceByteAdmission? admission,
        CancellationToken cancellationToken)
    {
        using IAccountWorkLease? lease = _accounts.Enter(accountId, cancellationToken);
        return await FetchAndStoreAsync(
            cacheKey,
            sourceUrl,
            cached,
            fetcher,
            admission,
            lease?.CancellationToken ?? cancellationToken).ConfigureAwait(false);
    }

    private async Task<GitHubCachedImage?> FetchAndStoreAsync(
        string cacheKey,
        string sourceUrl,
        GitHubImageCacheRead? cached,
        GitHubImageFetcher fetcher,
        IMarkdownImageSourceByteAdmission? admission,
        CancellationToken cancellationToken)
    {
        try
        {
            GitHubImageDownload? download = await fetcher(cached?.Entry, cancellationToken).ConfigureAwait(false);
            using IDisposable? sourceByteLease = download?.SourceByteLease;
            if (download?.IsNotModified == true && cached is not null)
            {
                await _cache.Store.MarkFreshAsync(cacheKey, cancellationToken).ConfigureAwait(false);
                GitHubImageCacheRead? refreshed = await _cache.ReadStoreAsync(
                    cacheKey, admission, cancellationToken)
                    .ConfigureAwait(false);
                if (refreshed is not null)
                {
                    _cache.Memory.Set(cacheKey, refreshed);
                }
                return refreshed is null ? null : ToResult(refreshed, isFromCache: true);
            }

            GitHubValidatedImage image = GitHubValidatedImage.From(download);
            GitHubImageCacheEntry stored = await _cache.StoreAsync(
                    cacheKey,
                    sourceUrl,
                    image,
                    download!,
                    cancellationToken)
                .ConfigureAwait(false);
            return new GitHubCachedImage(stored.FilePath, image.Bytes, stored.ContentType, IsFromCache: false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch when (cached is not null)
        {
            return ToResult(cached, isFromCache: true) with { IsStale = true };
        }
    }

    /// <summary>
    /// Undoes a partially completed registration. Rethrows the original failure
    /// unless cleanup also failed, in which case every failure is aggregated.
    /// </summary>
    private void RollBackRegistration(Exception registrationFailure)
    {
        List<Exception> cleanupFailures = [];
        if (_memoryPressure.TryUnsubscribe() is { } memoryPressureFailure)
            cleanupFailures.Add(memoryPressureFailure);
        if (_observableCacheSubscribed && _cache.AdmittedStore is { } observableStore)
        {
            try
            {
                observableStore.CacheContentInvalidated -= ClearMemoryCache;
                _observableCacheSubscribed = false;
            }
            catch (Exception cacheUnsubscribeFailure)
            {
                cleanupFailures.Add(cacheUnsubscribeFailure);
            }
        }

        try
        {
            _http.Dispose();
        }
        catch (Exception clientDisposeFailure)
        {
            cleanupFailures.Add(clientDisposeFailure);
        }

        if (cleanupFailures.Count > 0)
        {
            cleanupFailures.Insert(0, registrationFailure);
            throw new AggregateException(
                "GitHub image service subscription failed and rollback was incomplete.",
                cleanupFailures);
        }
    }

    private void ClearMemoryCache() => _cache.Memory.Clear();

    private static GitHubCachedImage ToResult(GitHubImageCacheRead read, bool isFromCache) =>
        new(read.Entry.FilePath, read.Bytes, read.Entry.ContentType, isFromCache);
}
