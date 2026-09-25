using System;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using JitHub.Services.Markdown;
using MarkdownRenderer.Images;

namespace JitHub.Services;

public sealed record GitHubImageDownload(
    byte[]? Bytes,
    string? ContentType,
    string? ETag = null,
    DateTimeOffset? LastModified = null,
    bool IsNotModified = false,
    IDisposable? SourceByteLease = null);

public sealed record GitHubCachedImage(
    string FilePath,
    byte[] Bytes,
    string? ContentType,
    bool IsFromCache,
    bool IsStale = false,
    Task<GitHubCachedImage?>? RefreshTask = null);

public delegate Task<GitHubImageDownload?> GitHubImageFetcher(
    GitHubImageCacheEntry? cachedEntry,
    CancellationToken cancellationToken);

public enum GitHubImageFetchScope
{
    TrustedGitHub,
    UserApprovedHttps,
}

public interface IGitHubImageService
{
    Task<GitHubCachedImage?> GetAsync(string sourceUrl, CancellationToken cancellationToken = default);

    Task<GitHubCachedImage?> GetAsync(
        string sourceUrl,
        GitHubImageFetchScope scope,
        CancellationToken cancellationToken = default);

    Task<GitHubCachedImage?> GetAsync(
        string sourceUrl,
        GitHubImageFetchScope scope,
        IMarkdownImageSourceByteAdmission admission,
        CancellationToken cancellationToken = default);

    Task<GitHubCachedImage?> TryGetCachedAsync(
        string sourceUrl,
        GitHubImageFetchScope scope,
        CancellationToken cancellationToken = default);

    Task<GitHubCachedImage?> GetOrFetchAsync(
        string sourceUrl,
        GitHubImageFetcher fetcher,
        CancellationToken cancellationToken = default);

    Task<GitHubCachedImage?> GetOrFetchAsync(
        string sourceUrl,
        GitHubImageFetcher fetcher,
        IMarkdownImageSourceByteAdmission admission,
        CancellationToken cancellationToken = default);

    Task InvalidateAsync(string sourceUrl, CancellationToken cancellationToken = default);
}

public sealed partial class GitHubImageService : IGitHubImageService, IDisposable
{
    // Large project READMEs commonly contain optimized screenshots and animated
    // demonstrations above 10 MiB. Compressed bytes remain bounded here while
    // RasterImageResourceBudget independently caps dimensions, frames, pixels,
    // and decoded memory before the renderer admits the payload.
    internal const int MaxImageBytes = 32 * 1024 * 1024;
    // Two consecutive CDN/HTTP2 stream failures were observed in the live
    // README corpus. A third idempotent GET is bounded and only reached for
    // errors classified as transient; policy and permanent responses fail fast.
    private const int MaxHttpAttempts = 3;
    private const long MemoryCacheByteBudget = 64L * 1024 * 1024;
    private const int MemoryCacheEntryBudget = 2048;
    private static readonly TimeSpan TransientRetryDelay = TimeSpan.FromMilliseconds(125);
    private static readonly TimeSpan Freshness =
        GitHubCachePolicy.TtlForResource(GitHubCachePolicy.AvatarImageResource);

    private readonly IGitHubImageCacheStore _cacheStore;
    private readonly HttpClient _httpClient;
    private readonly Func<long> _partitionProvider;
    private readonly Func<string, CancellationToken, Task<IPAddress[]>> _hostAddressResolver;
    private readonly bool _validateDnsBeforeRequest;
    private readonly bool _ownsHttpClient;
    private readonly IAccountWorkQuiescence? _accountWork;
    private readonly MemoryImageCache _memoryCache = new(
        MemoryCacheByteBudget,
        MemoryCacheEntryBudget);
    private readonly GitHubImageCacheStore? _observableCacheStore;
    private readonly ConcurrentDictionary<string, SharedImageRequest> _misses =
        new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, SharedImageRequest> _refreshes =
        new(StringComparer.Ordinal);

    public GitHubImageService(
        IGitHubImageCacheStore cacheStore,
        IAccountService accountService,
        IAccountWorkQuiescence accountWork)
        : this(
            cacheStore,
            CreateHttpClient(),
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
        IAccountWorkQuiescence? accountWork = null)
    {
        _cacheStore = cacheStore;
        _httpClient = httpClient;
        _ownsHttpClient = ownsHttpClient;
        _partitionProvider = partitionProvider ?? (() => 0);
        _hostAddressResolver = hostAddressResolver ?? ResolveHostAddressesAsync;
        // The production client validates every physical connection in ConnectCallback.
        // Resolving the same host again before every logical HTTP/2 stream adds hundreds
        // of redundant DNS calls on avatar-heavy READMEs without improving rebinding
        // protection. Injected handlers still use the request-level validator when one is
        // supplied because they do not own the connection callback.
        _validateDnsBeforeRequest = !ownsHttpClient && hostAddressResolver is not null;
        _accountWork = accountWork;
        _observableCacheStore = cacheStore as GitHubImageCacheStore;
        if (_observableCacheStore is not null)
        {
            _observableCacheStore.CacheContentInvalidated += ClearMemoryCache;
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
            (cached, token) => FetchHttpAsync(sourceUrl, cached, scope, null, token),
            scope,
            null,
            cancellationToken);

    public Task<GitHubCachedImage?> GetAsync(
        string sourceUrl,
        GitHubImageFetchScope scope,
        IMarkdownImageSourceByteAdmission admission,
        CancellationToken cancellationToken = default) =>
        GetOrFetchCoreAsync(
            sourceUrl,
            (cached, token) => FetchHttpAsync(sourceUrl, cached, scope, admission, token),
            scope,
            admission,
            cancellationToken);

    public async Task<GitHubCachedImage?> TryGetCachedAsync(
        string sourceUrl,
        GitHubImageFetchScope scope,
        CancellationToken cancellationToken = default)
    {
        if (!IsAllowedSource(sourceUrl, scope))
        {
            return null;
        }

        long accountId = _partitionProvider();
        using IAccountWorkLease? lease = EnterAccountWork(accountId, cancellationToken);
        CancellationToken operationToken = lease?.CancellationToken ?? cancellationToken;
        string cacheKey = GetCacheKey(accountId, sourceUrl);
        GitHubImageCacheRead? cached;
        if (!_memoryCache.TryGet(cacheKey, out cached))
        {
            cached = await _cacheStore.TryReadAsync(cacheKey, operationToken).ConfigureAwait(false);
            if (cached is not null)
            {
                _memoryCache.Set(cacheKey, cached);
            }
        }
        else if (cached is not null)
        {
            cached = RefreshCachedTimestamp(cached);
        }
        if (cached is null)
        {
            return null;
        }

        bool isStale = DateTimeOffset.UtcNow - cached.Entry.CachedAt > Freshness;
        return ToResult(cached, isFromCache: true) with { IsStale = isStale };
    }

    public async Task<GitHubCachedImage?> GetOrFetchAsync(
        string sourceUrl,
        GitHubImageFetcher fetcher,
        CancellationToken cancellationToken = default)
        => await GetOrFetchCoreAsync(
            sourceUrl,
            (cached, token) => FetchWithRetryAsync(fetcher, cached, token),
            GitHubImageFetchScope.UserApprovedHttps,
            null,
            cancellationToken).ConfigureAwait(false);

    public async Task<GitHubCachedImage?> GetOrFetchAsync(
        string sourceUrl,
        GitHubImageFetcher fetcher,
        IMarkdownImageSourceByteAdmission admission,
        CancellationToken cancellationToken = default)
        => await GetOrFetchCoreAsync(
            sourceUrl,
            (cached, token) => FetchWithRetryAsync(fetcher, cached, token),
            GitHubImageFetchScope.UserApprovedHttps,
            admission,
            cancellationToken).ConfigureAwait(false);

    private async Task<GitHubCachedImage?> GetOrFetchCoreAsync(
        string sourceUrl,
        GitHubImageFetcher fetcher,
        GitHubImageFetchScope scope,
        IMarkdownImageSourceByteAdmission? admission,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(fetcher);
        if (!IsAllowedSource(sourceUrl, scope))
        {
            return null;
        }

        long accountId = _partitionProvider();
        using IAccountWorkLease? readLease = EnterAccountWork(accountId, cancellationToken);
        CancellationToken readToken = readLease?.CancellationToken ?? cancellationToken;
        string cacheKey = GetCacheKey(accountId, sourceUrl);
        // Distinct in-flight identities keep a speculative or unbudgeted
        // downloader from blocking a visible admitted request. Successful
        // downloads still share the same account-partitioned cache entry.
        string flightKey = admission is null
            ? cacheKey
            : admission.IsSpeculative ? cacheKey + "#source-speculative" : cacheKey + "#source-visible";
        GitHubImageCacheRead? cached;
        if (!_memoryCache.TryGet(cacheKey, out cached))
        {
            cached = await _cacheStore.TryReadAsync(cacheKey, readToken).ConfigureAwait(false);
            if (cached is not null)
            {
                _memoryCache.Set(cacheKey, cached);
            }
        }
        else if (cached is not null)
        {
            cached = RefreshCachedTimestamp(cached);
        }
        if (cached is not null)
        {
            if (DateTimeOffset.UtcNow - cached.Entry.CachedAt <= Freshness)
            {
                return ToResult(cached, isFromCache: true);
            }

            Task<GitHubCachedImage?> refreshTask = GetSharedTask(
                _refreshes,
                flightKey,
                token => FetchAndStoreWithLeaseAsync(
                    accountId,
                    cacheKey,
                    sourceUrl,
                    cached,
                    fetcher,
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

        Task<GitHubCachedImage?> missTask = GetSharedTask(
            _misses,
            flightKey,
            token => FetchAndStoreWithLeaseAsync(
                accountId,
                cacheKey,
                sourceUrl,
                null,
                fetcher,
                token),
            cancellationToken);
        return await missTask.ConfigureAwait(false);
    }

    public async Task InvalidateAsync(string sourceUrl, CancellationToken cancellationToken = default)
    {
        long accountId = _partitionProvider();
        using IAccountWorkLease? lease = EnterAccountWork(accountId, cancellationToken);
        string cacheKey = GetCacheKey(accountId, sourceUrl);
        _memoryCache.Remove(cacheKey);
        await _cacheStore.InvalidateAsync(
            cacheKey,
            lease?.CancellationToken ?? cancellationToken).ConfigureAwait(false);
    }

    private void ClearMemoryCache() => _memoryCache.Clear();

    private static GitHubImageCacheRead RefreshCachedTimestamp(GitHubImageCacheRead cached)
    {
        try
        {
            DateTime lastWrite = File.GetLastWriteTimeUtc(cached.Entry.FilePath);
            if (lastWrite != DateTime.MinValue)
            {
                return cached with
                {
                    Entry = cached.Entry with { CachedAt = new DateTimeOffset(lastWrite) }
                };
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }

        return cached;
    }

    public void Dispose()
    {
        if (_observableCacheStore is not null)
        {
            _observableCacheStore.CacheContentInvalidated -= ClearMemoryCache;
        }

        _memoryCache.Clear();
        foreach (SharedImageRequest request in _misses.Values)
        {
            request.Cancel();
        }

        foreach (SharedImageRequest request in _refreshes.Values)
        {
            request.Cancel();
        }

        if (_ownsHttpClient)
        {
            _httpClient.Dispose();
        }
    }

    private async Task<GitHubCachedImage?> FetchAndStoreAsync(
        string cacheKey,
        string sourceUrl,
        GitHubImageCacheRead? cached,
        GitHubImageFetcher fetcher,
        CancellationToken cancellationToken)
    {
        try
        {
            GitHubImageDownload? download = await fetcher(cached?.Entry, cancellationToken).ConfigureAwait(false);
            using IDisposable? sourceByteLease = download?.SourceByteLease;
            if (download?.IsNotModified == true && cached is not null)
            {
                await _cacheStore.MarkFreshAsync(cacheKey, cancellationToken).ConfigureAwait(false);
                GitHubImageCacheRead? refreshed = await _cacheStore.TryReadAsync(cacheKey, cancellationToken)
                    .ConfigureAwait(false);
                if (refreshed is not null)
                {
                    _memoryCache.Set(cacheKey, refreshed);
                }
                return refreshed is null ? null : ToResult(refreshed, isFromCache: true);
            }

            if (download?.Bytes is not { Length: > 0 } bytes ||
                bytes.Length > MaxImageBytes ||
                !TryDetectSupportedImageContentType(bytes, download.ContentType, out string contentType))
            {
                throw new InvalidDataException("Remote content is not a supported image.");
            }

            // GitHub's image CDN occasionally returns raster bytes under a stale or
            // incorrect image media type. The decoder and cache must follow the
            // validated signature, not the untrusted response label.
            string extension = GetExtension(contentType, sourceUrl);
            GitHubImageCacheEntry stored = await _cacheStore.PutAsync(
                    cacheKey,
                    bytes,
                    extension,
                    new GitHubImageCacheWriteMetadata(download.ETag, download.LastModified, contentType),
                    cancellationToken)
                .ConfigureAwait(false);
            _memoryCache.Set(cacheKey, new GitHubImageCacheRead(stored, bytes));
            return ToResult(stored, bytes, isFromCache: false);
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

    private async Task<GitHubCachedImage?> FetchAndStoreWithLeaseAsync(
        long accountId,
        string cacheKey,
        string sourceUrl,
        GitHubImageCacheRead? cached,
        GitHubImageFetcher fetcher,
        CancellationToken cancellationToken)
    {
        using IAccountWorkLease? lease = EnterAccountWork(accountId, cancellationToken);
        return await FetchAndStoreAsync(
            cacheKey,
            sourceUrl,
            cached,
            fetcher,
            lease?.CancellationToken ?? cancellationToken).ConfigureAwait(false);
    }

    private async Task<GitHubImageDownload?> FetchHttpAsync(
        string sourceUrl,
        GitHubImageCacheEntry? cached,
        GitHubImageFetchScope scope,
        IMarkdownImageSourceByteAdmission? admission,
        CancellationToken cancellationToken)
    {
        for (int attempt = 1; attempt <= MaxHttpAttempts; attempt++)
        {
            try
            {
                return await FetchHttpAttemptAsync(
                    sourceUrl,
                    cached,
                    scope,
                    admission,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (
                attempt < MaxHttpAttempts &&
                !cancellationToken.IsCancellationRequested &&
                IsTransientHttpFailure(exception))
            {
                // Only transient failures reach this point. Back off the second
                // retry to avoid immediately repeating a throttled CDN request.
                await Task.Delay(TransientRetryDelay * attempt, cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        throw new InvalidOperationException("The bounded image retry loop completed unexpectedly.");
    }

    private static async Task<GitHubImageDownload?> FetchWithRetryAsync(
        GitHubImageFetcher fetcher,
        GitHubImageCacheEntry? cached,
        CancellationToken cancellationToken)
    {
        for (int attempt = 1; attempt <= MaxHttpAttempts; attempt++)
        {
            try
            {
                return await fetcher(cached, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (
                attempt < MaxHttpAttempts &&
                !cancellationToken.IsCancellationRequested &&
                IsTransientFetchFailure(exception))
            {
                // Repository-relative images use an authenticated GitHub API
                // fetcher rather than FetchHttpAsync. Give that idempotent GET
                // the same single bounded recovery opportunity as CDN images.
                await Task.Delay(TransientRetryDelay * attempt, cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        throw new InvalidOperationException("The bounded image fetch retry loop completed unexpectedly.");
    }

    private async Task<GitHubImageDownload?> FetchHttpAttemptAsync(
        string sourceUrl,
        GitHubImageCacheEntry? cached,
        GitHubImageFetchScope scope,
        IMarkdownImageSourceByteAdmission? admission,
        CancellationToken cancellationToken)
    {
        Uri currentUri = new(sourceUrl, UriKind.Absolute);
        const int maxRedirects = 3;
        for (int redirectCount = 0; redirectCount <= maxRedirects; redirectCount++)
        {
            if (_validateDnsBeforeRequest)
            {
                await EnsurePublicNetworkDestinationAsync(currentUri, cancellationToken).ConfigureAwait(false);
            }

            using HttpRequestMessage request = new(HttpMethod.Get, currentUri);
            if (redirectCount == 0 &&
                !string.IsNullOrWhiteSpace(cached?.ETag) &&
                EntityTagHeaderValue.TryParse(cached.ETag, out EntityTagHeaderValue? entityTag))
            {
                request.Headers.IfNoneMatch.Add(entityTag);
            }

            if (redirectCount == 0 && cached?.LastModified is DateTimeOffset lastModified)
            {
                request.Headers.IfModifiedSince = lastModified;
            }

            using HttpResponseMessage response = await _httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.NotModified)
            {
                return new GitHubImageDownload(null, cached?.ContentType, IsNotModified: true);
            }

            if (IsRedirect(response.StatusCode))
            {
                if (redirectCount == maxRedirects || response.Headers.Location is not Uri location)
                {
                    throw new InvalidDataException("Remote image exceeded the redirect limit.");
                }

                Uri nextUri = location.IsAbsoluteUri ? location : new Uri(currentUri, location);
                if (!IsAllowedRedirect(currentUri, nextUri, scope))
                {
                    throw new InvalidDataException("Remote image redirect violates the source policy.");
                }

                currentUri = nextUri;
                continue;
            }

            response.EnsureSuccessStatusCode();
            string? contentType = response.Content.Headers.ContentType?.MediaType;
            (byte[] bytes, IDisposable? sourceByteLease) = await ReadBoundedAsync(
                response.Content, admission, cancellationToken).ConfigureAwait(false);
            return new GitHubImageDownload(
                bytes,
                contentType,
                response.Headers.ETag?.ToString(),
                response.Content.Headers.LastModified,
                SourceByteLease: sourceByteLease);
        }

        throw new InvalidDataException("Remote image exceeded the redirect limit.");
    }

    private static bool IsTransientHttpFailure(Exception exception)
    {
        if (exception is OperationCanceledException)
        {
            // The caller token is checked by the catch filter. Reaching this path
            // therefore means HttpClient's own per-attempt timeout elapsed.
            return true;
        }

        if (exception is IOException)
        {
            return true;
        }

        if (exception is not HttpRequestException requestException)
        {
            return false;
        }

        if (requestException.StatusCode is not HttpStatusCode statusCode)
        {
            // Connection resets, DNS races, and HTTP/2 stream failures do not
            // carry a response status but are safe to retry for an idempotent GET.
            return true;
        }

        int status = (int)statusCode;
        return status is 408 or 425 or 429 ||
            (status >= 500 && status <= 599 && status is not (501 or 505));
    }

    private static bool IsTransientFetchFailure(Exception exception)
    {
        if (IsTransientHttpFailure(exception))
        {
            return true;
        }

        if (exception is not GitHubApiException apiException ||
            exception is GitHubAuthenticationException or GitHubRateLimitException)
        {
            return false;
        }

        int status = (int)apiException.StatusCode;
        return status is 408 or 425 or 429 ||
            (status >= 500 && status <= 599 && status is not (501 or 505));
    }

    private async Task<(byte[] Bytes, IDisposable? Lease)> ReadBoundedAsync(
        HttpContent content,
        IMarkdownImageSourceByteAdmission? admission,
        CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength is long contentLength && contentLength > MaxImageBytes)
        {
            throw new InvalidDataException($"Remote image exceeds the {MaxImageBytes}-byte limit.");
        }

        await using Stream input = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        if (content.Headers.ContentLength is long knownLength)
        {
            IDisposable? lease = null;
            try
            {
                if (admission is not null && knownLength > 0)
                    lease = await admission.ReserveAsync(knownLength, cancellationToken).ConfigureAwait(false);
                // Most image responses provide a length. Read directly into the final
                // array instead of growing a MemoryStream and copying it with ToArray.
                byte[] bytes = new byte[checked((int)knownLength)];
                await input.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
                if (await input.ReadAsync(new byte[1], cancellationToken).ConfigureAwait(false) != 0)
                    throw new InvalidDataException("Remote image exceeds its declared content length.");
                IDisposable? transferred = lease;
                lease = null;
                return (bytes, transferred);
            }
            finally
            {
                lease?.Dispose();
            }
        }

        if (admission is not null)
            return await ReadUnknownLengthAdmittedAsync(input, admission, cancellationToken)
                .ConfigureAwait(false);

        using MemoryStream output = new();
        byte[] buffer = new byte[81920];
        while (true)
        {
            int read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                return (output.ToArray(), null);
            }

            if (output.Length + read > MaxImageBytes)
            {
                throw new InvalidDataException($"Remote image exceeds the {MaxImageBytes}-byte limit.");
            }

            output.Write(buffer, 0, read);
        }
    }

    private static async Task<(byte[] Bytes, IDisposable Lease)> ReadUnknownLengthAdmittedAsync(
        Stream input,
        IMarkdownImageSourceByteAdmission admission,
        CancellationToken cancellationToken)
    {
        // Most chunked badges fit in memory. Reserve both the initial buffer
        // and its exact-size result before allocating either one. Larger
        // streams spill to a delete-on-close file instead of growing multiple
        // unadmitted buffers or holding a partial byte lease while waiting for
        // more capacity (which could deadlock other chunked readers).
        int initialCapacity = checked((int)Math.Min(1024L * 1024, admission.MaximumReservationBytes / 2));
        if (initialCapacity < 1)
            throw new InvalidDataException("The source-byte admission cannot hold an image chunk.");
        IDisposable? initialLease = await admission.ReserveAsync(
            initialCapacity * 2L, cancellationToken).ConfigureAwait(false);
        try
        {
            byte[] buffer = new byte[initialCapacity];
            int length = 0;
            while (length < buffer.Length)
            {
                int read = await input.ReadAsync(buffer.AsMemory(length), cancellationToken)
                    .ConfigureAwait(false);
                if (read == 0)
                {
                    byte[] exact = buffer.AsSpan(0, length).ToArray();
                    IDisposable transferred = initialLease;
                    initialLease = null;
                    return (exact, transferred);
                }
                length += read;
            }

            byte[] one = new byte[1];
            if (await input.ReadAsync(one, cancellationToken).ConfigureAwait(false) == 0)
            {
                // A full buffer already has the exact length; the reservation
                // covers it without an additional array copy.
                IDisposable transferred = initialLease;
                initialLease = null;
                return (buffer, transferred);
            }

            string temporaryPath = Path.Combine(
                Path.GetTempPath(), $"jithub-image-{Guid.NewGuid():N}.tmp");
            await using FileStream spool = new(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.ReadWrite,
                FileShare.None,
                81920,
                FileOptions.Asynchronous | FileOptions.DeleteOnClose);
            await spool.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
            await spool.WriteAsync(one, cancellationToken).ConfigureAwait(false);
            int total = checked(length + 1);
            while (true)
            {
                int read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (read == 0)
                    break;
                if (total > MaxImageBytes - read)
                    throw new InvalidDataException($"Remote image exceeds the {MaxImageBytes}-byte limit.");
                await spool.WriteAsync(buffer.AsMemory(0, read), cancellationToken)
                    .ConfigureAwait(false);
                total += read;
            }

            // The scratch buffer no longer participates in the read. Release
            // its lease before waiting for the final array's weighted grant.
            initialLease.Dispose();
            initialLease = null;
            IDisposable finalLease = await admission.ReserveAsync(total, cancellationToken)
                .ConfigureAwait(false);
            try
            {
                byte[] bytes = new byte[total];
                spool.Position = 0;
                await spool.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
                return (bytes, finalLease);
            }
            catch
            {
                finalLease.Dispose();
                throw;
            }
        }
        catch
        {
            initialLease?.Dispose();
            throw;
        }
    }

    private static Task<GitHubCachedImage?> GetSharedTask(
        ConcurrentDictionary<string, SharedImageRequest> tasks,
        string cacheKey,
        Func<CancellationToken, Task<GitHubCachedImage?>> taskFactory,
        CancellationToken callerCancellationToken)
    {
        while (true)
        {
            SharedImageRequest request = tasks.GetOrAdd(cacheKey, _ => new SharedImageRequest(taskFactory));
            if (request.TryAddWaiter())
            {
                return AwaitSharedAsync(tasks, cacheKey, request, callerCancellationToken);
            }

            tasks.TryRemove(new KeyValuePair<string, SharedImageRequest>(cacheKey, request));
        }
    }

    private static async Task<GitHubCachedImage?> AwaitSharedAsync(
        ConcurrentDictionary<string, SharedImageRequest> tasks,
        string cacheKey,
        SharedImageRequest request,
        CancellationToken callerCancellationToken)
    {
        Task<GitHubCachedImage?> task = request.Task;
        try
        {
            return await task.WaitAsync(callerCancellationToken).ConfigureAwait(false);
        }
        finally
        {
            SharedImageRequestRelease release = request.ReleaseWaiter(task.IsCompleted);
            if (release.ShouldCancel)
            {
                request.Cancel();
            }

            if (release.ShouldRetire)
            {
                tasks.TryRemove(new KeyValuePair<string, SharedImageRequest>(cacheKey, request));
                request.DisposeWhenComplete();
            }
        }
    }

    private IAccountWorkLease? EnterAccountWork(long accountId, CancellationToken cancellationToken) =>
        accountId > 0
            ? _accountWork?.Enter(accountId.ToString(CultureInfo.InvariantCulture), cancellationToken)
            : null;

    private static string GetCacheKey(long accountId, string sourceUrl) =>
        $"{accountId.ToString(CultureInfo.InvariantCulture)}:{NormalizeCacheIdentity(sourceUrl)}";

    internal static string NormalizeCacheIdentity(string sourceUrl)
    {
        if (!Uri.TryCreate(sourceUrl, UriKind.Absolute, out Uri? uri))
        {
            return sourceUrl.Trim();
        }

        UriBuilder builder = new(uri)
        {
            Scheme = uri.Scheme.ToLowerInvariant(),
            Host = uri.IdnHost.ToLowerInvariant(),
            Fragment = string.Empty,
            Port = uri.IsDefaultPort ? -1 : uri.Port
        };
        return builder.Uri.GetComponents(
            UriComponents.SchemeAndServer | UriComponents.PathAndQuery,
            UriFormat.UriEscaped);
    }

    private static bool IsAllowedSource(string sourceUrl, GitHubImageFetchScope scope)
    {
        if (!Uri.TryCreate(sourceUrl, UriKind.Absolute, out Uri? sourceUri) ||
            sourceUri.Scheme != Uri.UriSchemeHttps ||
            !string.IsNullOrEmpty(sourceUri.UserInfo) ||
            !IsGloballyRoutableDestination(sourceUri))
        {
            return false;
        }

        return scope == GitHubImageFetchScope.UserApprovedHttps ||
            MarkdownRemoteImagePolicy.IsTrustedGitHubHost(sourceUri.Host);
    }

    private static bool IsAllowedRedirect(
        Uri currentUri,
        Uri nextUri,
        GitHubImageFetchScope scope)
    {
        if (IsAllowedSource(nextUri.ToString(), scope))
            return true;

        // GitHub serves README user attachments through a short-lived,
        // query-signed S3 URL. Keep the normal trusted-host boundary intact
        // and admit only this exact GitHub-owned transition; subsequent
        // redirects are evaluated independently and cannot inherit it.
        return scope == GitHubImageFetchScope.TrustedGitHub &&
            currentUri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) &&
            IsGitHubUserAssetPath(currentUri.AbsolutePath) &&
            nextUri.Scheme == Uri.UriSchemeHttps &&
            string.IsNullOrEmpty(nextUri.UserInfo) &&
            nextUri.Host.Equals(
                "github-production-user-asset-6210df.s3.amazonaws.com",
                StringComparison.OrdinalIgnoreCase) &&
            nextUri.Query.Contains("X-Amz-Credential=", StringComparison.OrdinalIgnoreCase) &&
            nextUri.Query.Contains("X-Amz-Signature=", StringComparison.OrdinalIgnoreCase) &&
            IsGloballyRoutableDestination(nextUri);
    }

    private static bool IsGitHubUserAssetPath(string absolutePath)
    {
        if (absolutePath.StartsWith(
                "/user-attachments/assets/",
                StringComparison.OrdinalIgnoreCase))
        {
            return Guid.TryParse(absolutePath["/user-attachments/assets/".Length..], out _);
        }

        // GitHub's legacy README attachment form is
        // /{owner}/{repository}/assets/{numeric-user-id}/{asset-id}. It is
        // still used by many high-traffic repositories and redirects through
        // the same query-signed, GitHub-owned S3 endpoint as the newer form.
        string[] segments = absolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return segments.Length == 5 &&
            segments[0].Length > 0 &&
            segments[1].Length > 0 &&
            segments[2].Equals("assets", StringComparison.OrdinalIgnoreCase) &&
            long.TryParse(segments[3], NumberStyles.None, CultureInfo.InvariantCulture, out _) &&
            Guid.TryParse(segments[4], out _);
    }

    private static bool IsGloballyRoutableDestination(Uri uri)
    {
        string host = uri.Host.TrimEnd('.');
        if (uri.IsLoopback || GloballyRoutableAddressPolicy.IsSpecialUseHost(host))
        {
            return false;
        }

        return !IPAddress.TryParse(host, out IPAddress? address) ||
            GloballyRoutableAddressPolicy.IsGloballyRoutable(address);
    }

    private async Task EnsurePublicNetworkDestinationAsync(Uri uri, CancellationToken cancellationToken)
    {
        IPAddress[] addresses;
        try
        {
            addresses = await _hostAddressResolver(uri.DnsSafeHost, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is SocketException or ArgumentException)
        {
            throw new InvalidDataException("Remote image host could not be resolved safely.", ex);
        }

        if (addresses.Length == 0 || addresses.Any(static address => !GloballyRoutableAddressPolicy.IsGloballyRoutable(address)))
        {
            throw new InvalidDataException("Remote image host does not resolve exclusively to globally routable destinations.");
        }
    }

    private static bool IsRedirect(HttpStatusCode statusCode) =>
        statusCode is HttpStatusCode.MovedPermanently or
            HttpStatusCode.Redirect or
            HttpStatusCode.RedirectMethod or
            HttpStatusCode.TemporaryRedirect or
            HttpStatusCode.PermanentRedirect;

    private static GitHubCachedImage ToResult(GitHubImageCacheRead read, bool isFromCache) =>
        new(read.Entry.FilePath, read.Bytes, read.Entry.ContentType, isFromCache);

    private static GitHubCachedImage ToResult(
        GitHubImageCacheEntry entry,
        byte[] bytes,
        bool isFromCache) =>
        new(entry.FilePath, bytes, entry.ContentType, isFromCache);

    private static bool TryDetectSupportedImageContentType(
        byte[] bytes,
        string? declaredContentType,
        out string contentType)
    {
        if (bytes.Length >= 8 &&
            bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47 &&
            bytes[4] == 0x0D && bytes[5] == 0x0A && bytes[6] == 0x1A && bytes[7] == 0x0A)
        {
            contentType = "image/png";
            return true;
        }

        if (bytes.Length >= 3 && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF)
        {
            contentType = "image/jpeg";
            return true;
        }

        if (bytes.Length >= 6 &&
            bytes[0] == (byte)'G' && bytes[1] == (byte)'I' && bytes[2] == (byte)'F' &&
            bytes[3] == (byte)'8' && (bytes[4] == (byte)'7' || bytes[4] == (byte)'9') &&
            bytes[5] == (byte)'a')
        {
            contentType = "image/gif";
            return true;
        }

        if (bytes.Length >= 12 &&
            bytes[0] == (byte)'R' && bytes[1] == (byte)'I' && bytes[2] == (byte)'F' && bytes[3] == (byte)'F' &&
            bytes[8] == (byte)'W' && bytes[9] == (byte)'E' && bytes[10] == (byte)'B' && bytes[11] == (byte)'P')
        {
            contentType = "image/webp";
            return true;
        }

        if (IsStillAvif(bytes))
        {
            contentType = "image/avif";
            return true;
        }

        if (bytes.Length >= 2 && bytes[0] == (byte)'B' && bytes[1] == (byte)'M')
        {
            contentType = "image/bmp";
            return true;
        }

        if (bytes.Length >= 4 && bytes[0] == 0 && bytes[1] == 0 && bytes[2] == 1 && bytes[3] == 0)
        {
            contentType = "image/x-icon";
            return true;
        }

        if (bytes.Length >= 4 &&
            ((bytes[0] == (byte)'I' && bytes[1] == (byte)'I' && bytes[2] == 0x2A && bytes[3] == 0) ||
             (bytes[0] == (byte)'M' && bytes[1] == (byte)'M' && bytes[2] == 0 && bytes[3] == 0x2A)))
        {
            contentType = "image/tiff";
            return true;
        }

        // XML is not self-identifying enough to admit under an arbitrary image
        // label. SVG retains the stricter declared-type check before its existing
        // sandboxed parser and resource policy run.
        if (string.Equals(declaredContentType, "image/svg+xml", StringComparison.OrdinalIgnoreCase))
        {
            string prefix = System.Text.Encoding.UTF8.GetString(bytes, 0, Math.Min(bytes.Length, 4096));
            if (prefix.Contains("<svg", StringComparison.OrdinalIgnoreCase))
            {
                contentType = "image/svg+xml";
                return true;
            }
        }

        contentType = string.Empty;
        return false;
    }

    private static bool IsStillAvif(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 16 || !bytes.Slice(4, 4).SequenceEqual("ftyp"u8))
        {
            return false;
        }

        uint boxSize = BinaryPrimitives.ReadUInt32BigEndian(bytes);
        if (boxSize < 16 || boxSize > bytes.Length || (boxSize & 3) != 0)
        {
            return false;
        }

        bool hasStillBrand = false;
        for (int offset = 8; offset <= boxSize - 4; offset += 4)
        {
            ReadOnlySpan<byte> brand = bytes.Slice(offset, 4);
            if (brand.SequenceEqual("avis"u8))
            {
                // Animated AVIF has a different resource and decode budget. Do
                // not admit it through the bounded still-image path.
                return false;
            }

            hasStillBrand |= brand.SequenceEqual("avif"u8);
        }

        return hasStillBrand;
    }

    private static HttpClient CreateHttpClient()
    {
        SocketsHttpHandler handler = new()
        {
            AllowAutoRedirect = false,
            ConnectCallback = ConnectValidatedAsync,
            PooledConnectionLifetime = TimeSpan.FromMinutes(2)
        };
        HttpClient client = new(handler)
        {
            Timeout = TimeSpan.FromSeconds(20),
            DefaultRequestVersion = HttpVersion.Version20,
            DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrLower
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("JitHub/1.0");
        return client;
    }

    private static async ValueTask<Stream> ConnectValidatedAsync(
        SocketsHttpConnectionContext context,
        CancellationToken cancellationToken)
    {
        IPAddress[] addresses = await ResolveHostAddressesAsync(context.DnsEndPoint.Host, cancellationToken).ConfigureAwait(false);
        if (addresses.Length == 0 || addresses.Any(static address => !GloballyRoutableAddressPolicy.IsGloballyRoutable(address)))
        {
            throw new InvalidDataException("Remote image host does not resolve exclusively to globally routable destinations.");
        }

        Exception? lastError = null;
        foreach (IPAddress address in addresses)
        {
            Socket socket = new(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
            try
            {
                await socket.ConnectAsync(new IPEndPoint(address, context.DnsEndPoint.Port), cancellationToken).ConfigureAwait(false);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (Exception ex) when (ex is SocketException or OperationCanceledException)
            {
                socket.Dispose();
                lastError = ex;
                if (ex is OperationCanceledException)
                {
                    throw;
                }
            }
        }

        throw new HttpRequestException("Remote image host could not be reached through a validated address.", lastError);
    }

    private static Task<IPAddress[]> ResolveHostAddressesAsync(string host, CancellationToken cancellationToken) =>
        Dns.GetHostAddressesAsync(host, cancellationToken);

    private static string GetExtension(string mediaType, string sourceUrl)
    {
        string? extension = mediaType.ToLowerInvariant() switch
        {
            "image/png" => ".png",
            "image/jpeg" => ".jpg",
            "image/gif" => ".gif",
            "image/webp" => ".webp",
            "image/avif" => ".avif",
            "image/svg+xml" => ".svg",
            "image/bmp" => ".bmp",
            "image/x-icon" or "image/vnd.microsoft.icon" => ".ico",
            "image/tiff" => ".tiff",
            _ => null
        };

        if (extension is not null)
        {
            return extension;
        }

        string pathExtension = Path.GetExtension(new Uri(sourceUrl).AbsolutePath);
        return string.IsNullOrWhiteSpace(pathExtension) || pathExtension.Length > 12
            ? ".img"
            : pathExtension;
    }

    internal readonly record struct SharedImageRequestRelease(bool ShouldRetire, bool ShouldCancel);

    private sealed class MemoryImageCache
    {
        private readonly long _byteBudget;
        private readonly int _entryBudget;
        private readonly object _gate = new();
        private readonly Dictionary<string, LinkedListNode<Entry>> _entries = new(StringComparer.Ordinal);
        private readonly LinkedList<Entry> _lru = new();
        private long _bytes;

        public MemoryImageCache(long byteBudget, int entryBudget)
        {
            _byteBudget = Math.Max(1, byteBudget);
            _entryBudget = Math.Max(1, entryBudget);
        }

        public bool TryGet(string key, out GitHubImageCacheRead? value)
        {
            lock (_gate)
            {
                if (!_entries.TryGetValue(key, out LinkedListNode<Entry>? node))
                {
                    value = null;
                    return false;
                }

                _lru.Remove(node);
                _lru.AddFirst(node);
                value = node.Value.Value;
                return true;
            }
        }

        public void Set(string key, GitHubImageCacheRead value)
        {
            long byteLength = value.Bytes.LongLength;
            if (byteLength <= 0 || byteLength > _byteBudget)
            {
                return;
            }

            lock (_gate)
            {
                if (_entries.Remove(key, out LinkedListNode<Entry>? existing))
                {
                    _lru.Remove(existing);
                    _bytes -= existing.Value.ByteLength;
                }

                LinkedListNode<Entry> node = new(new Entry(key, value, byteLength));
                _lru.AddFirst(node);
                _entries[key] = node;
                _bytes += byteLength;
                while (_entries.Count > _entryBudget || _bytes > _byteBudget)
                {
                    LinkedListNode<Entry>? tail = _lru.Last;
                    if (tail is null)
                    {
                        break;
                    }

                    _lru.RemoveLast();
                    _entries.Remove(tail.Value.Key);
                    _bytes -= tail.Value.ByteLength;
                }
            }
        }

        public void Remove(string key)
        {
            lock (_gate)
            {
                if (_entries.Remove(key, out LinkedListNode<Entry>? node))
                {
                    _lru.Remove(node);
                    _bytes -= node.Value.ByteLength;
                }
            }
        }

        public void Clear()
        {
            lock (_gate)
            {
                _entries.Clear();
                _lru.Clear();
                _bytes = 0;
            }
        }

        private sealed record Entry(string Key, GitHubImageCacheRead Value, long ByteLength);
    }

    internal sealed class SharedImageRequest
    {
        private readonly object _gate = new();
        private readonly CancellationTokenSource _cancellationTokenSource = new();
        private readonly Lazy<Task<GitHubCachedImage?>> _task;
        private int _waiterCount;
        private int _disposeScheduled;
        private bool _retired;

        public SharedImageRequest(Func<CancellationToken, Task<GitHubCachedImage?>> taskFactory)
        {
            _task = new Lazy<Task<GitHubCachedImage?>>(
                () => taskFactory(_cancellationTokenSource.Token),
                LazyThreadSafetyMode.ExecutionAndPublication);
        }

        public Task<GitHubCachedImage?> Task => _task.Value;

        public bool TryAddWaiter()
        {
            lock (_gate)
            {
                if (_retired)
                {
                    return false;
                }

                _waiterCount++;
                return true;
            }
        }

        public SharedImageRequestRelease ReleaseWaiter(bool taskCompleted)
        {
            lock (_gate)
            {
                if (_waiterCount <= 0)
                {
                    throw new InvalidOperationException("Shared image request waiter count is already zero.");
                }

                _waiterCount--;
                if (_waiterCount > 0)
                {
                    return new SharedImageRequestRelease(ShouldRetire: false, ShouldCancel: false);
                }

                // Retire while holding the same lock used by TryAddWaiter. A late caller can no
                // longer join this transfer between the zero-count decision and cancellation.
                _retired = true;
                return new SharedImageRequestRelease(
                    ShouldRetire: true,
                    ShouldCancel: !taskCompleted);
            }
        }

        public void Cancel()
        {
            try
            {
                _cancellationTokenSource.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
        }

        public void DisposeWhenComplete()
        {
            if (Interlocked.Exchange(ref _disposeScheduled, 1) != 0)
            {
                return;
            }

            _ = DisposeRequestWhenCompleteAsync();
        }

        private async Task DisposeRequestWhenCompleteAsync()
        {
            try
            {
                await Task.ConfigureAwait(false);
            }
            catch
            {
                // Waiters own user-visible failures; disposal only observes completion.
            }
            finally
            {
                _cancellationTokenSource.Dispose();
            }
        }
    }
}
