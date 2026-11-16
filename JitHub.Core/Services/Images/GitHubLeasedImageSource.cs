using System;
using System.Threading;
using System.Threading.Tasks;
using JitHub.Core.Services.Accounts;
using JitHub.Core.Services.Images.Caching;
using JitHub.Core.Services.Images.Fetching;
using JitHub.Core.Services.Images.Flights;
using JitHub.Markdown.Images.Caching;
using MarkdownRenderer.Images;
using MarkdownRenderer.Performance;

namespace JitHub.Core.Services.Images;

/// <summary>
/// The leased read path of <see cref="GitHubImageService"/>. Every returned
/// lease owns a byte reservation taken before its source bytes were
/// materialized, so source-byte budgets stay exact.
/// </summary>
internal sealed class GitHubLeasedImageSource
{
    private readonly GitHubImageAccountScope _accounts;
    private readonly GitHubImageCacheTier _cache;
    private readonly GitHubImageServiceLifetime _lifetime;
    private readonly GitHubLeasedImageFlights _misses = new();
    private readonly GitHubLeasedImageFlights _refreshes = new();

    internal GitHubLeasedImageSource(
        GitHubImageAccountScope accounts,
        GitHubImageCacheTier cache,
        GitHubImageServiceLifetime lifetime)
    {
        _accounts = accounts;
        _cache = cache;
        _lifetime = lifetime;
    }

    internal async Task<GitHubImageSourceLease?> TryGetCachedAsync(
        string sourceUrl,
        GitHubImageFetchScope scope,
        IMarkdownImageSourceByteAdmission admission,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(admission);
        cancellationToken.ThrowIfCancellationRequested();
        if (!GitHubImageSourcePolicy.IsAllowedSource(sourceUrl, scope))
            return null;

        long accountId = _accounts.CurrentAccountId();
        using IAccountWorkLease? accountLease = _accounts.Enter(accountId, cancellationToken);
        CancellationToken operationToken = accountLease?.CancellationToken ?? cancellationToken;
        string cacheKey = GitHubImageAccountScope.GetCacheKey(accountId, sourceUrl);
        if (await _cache.GetEntryAsync(cacheKey, operationToken).ConfigureAwait(false) is null)
        {
            return null;
        }

        return await ReadCachedImageLeaseAsync(cacheKey, admission, operationToken)
            .ConfigureAwait(false);
    }

    internal async Task<GitHubImageSourceLease?> GetOrFetchAsync(
        string sourceUrl,
        GitHubImageFetcher fetcher,
        GitHubImageFetchScope scope,
        IMarkdownImageSourceByteAdmission admission,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(fetcher);
        ArgumentNullException.ThrowIfNull(admission);
        cancellationToken.ThrowIfCancellationRequested();
        if (!GitHubImageSourcePolicy.IsAllowedSource(sourceUrl, scope))
            return null;

        long accountId = _accounts.CurrentAccountId();
        using IAccountWorkLease? accountLease = _accounts.Enter(accountId, cancellationToken);
        CancellationToken operationToken = accountLease?.CancellationToken ?? cancellationToken;
        string cacheKey = GitHubImageAccountScope.GetCacheKey(accountId, sourceUrl);
        GitHubImageCacheEntry? cachedEntry = await _cache.GetEntryAsync(cacheKey, operationToken)
            .ConfigureAwait(false);

        if (cachedEntry is not null && GitHubImageCacheTier.IsFresh(cachedEntry.CachedAt))
        {
            GitHubImageSourceLease? cached = await ReadCachedImageLeaseAsync(
                    cacheKey,
                    admission,
                    operationToken)
                .ConfigureAwait(false);
            if (cached is not null)
                return cached;

            // The generation disappeared between metadata lookup and source admission.
            // Fall through to an ordinary miss rather than returning a stale array.
            cachedEntry = null;
        }

        GitHubLeasedImageFlights flights = cachedEntry is null ? _misses : _refreshes;
        GitHubImageSourceLease? resolved = await flights.JoinAsync(
                new GitHubLeasedImageFlightKey(cacheKey, admission),
                token => FetchAndStoreWithAccountLeaseAsync(
                    accountId,
                    cacheKey,
                    sourceUrl,
                    cachedEntry,
                    fetcher,
                    admission,
                    token),
                admission as IMarkdownImageSourceWorkTracker,
                cancellationToken)
            .ConfigureAwait(false);
        if (resolved is not null)
            return resolved;

        // A failed stale revalidation is a cache miss from the flight's perspective.
        // Re-open the currently committed generation under a fresh exact reservation;
        // the old cache array was never kept alive under a released source lease.
        return cachedEntry is null
            ? null
            : await ReadCachedImageLeaseAsync(cacheKey, admission, operationToken)
                .ConfigureAwait(false);
    }

    internal void CancelAll()
    {
        _misses.CancelAll();
        _refreshes.CancelAll();
    }

    private async Task<GitHubImageSourceLease?> ReadCachedImageLeaseAsync(
        string cacheKey,
        IMarkdownImageSourceByteAdmission admission,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            (bool retry, GitHubImageSourceLease? lease) =
                TryGetMemoryEntry(cacheKey) is { } memoryEntry
                    ? await TryLeaseFromMemoryAsync(cacheKey, memoryEntry, admission, cancellationToken)
                        .ConfigureAwait(false)
                    : await TryLeaseFromStoreAsync(cacheKey, admission, cancellationToken)
                        .ConfigureAwait(false);
            if (!retry)
                return lease;
        }
    }

    /// <summary>
    /// Returns only the scalar identity of the memory generation. If the cache
    /// evicts it during the admission wait, this request must not become an
    /// uncharged owner of its byte array.
    /// </summary>
    private GitHubImageCacheEntry? TryGetMemoryEntry(string cacheKey) =>
        _cache.Memory.TryGet(cacheKey, out GitHubImageCacheRead? memoryRead) && memoryRead is not null
            ? memoryRead.Entry
            : null;

    private async Task<(bool Retry, GitHubImageSourceLease? Lease)> TryLeaseFromMemoryAsync(
        string cacheKey,
        GitHubImageCacheEntry expected,
        IMarkdownImageSourceByteAdmission admission,
        CancellationToken cancellationToken)
    {
        long expectedLength = expected.ByteLength;
        if (expectedLength <= 0 || expectedLength > GitHubImageService.MaxImageBytes)
        {
            _cache.Memory.Remove(cacheKey);
            return (true, null);
        }

        IDisposable? reservation = await admission.ReserveAsync(expectedLength, cancellationToken)
            .ConfigureAwait(false);
        GitHubImageCacheRead? current = null;
        try
        {
            // Waiting for weighted admission yields. Do not return a generation
            // that was replaced or invalidated while the caller waited.
            if (!_cache.Memory.TryGet(cacheKey, out current) ||
                current is null ||
                current.Entry.ByteLength != expectedLength ||
                !string.Equals(current.Entry.FilePath, expected.FilePath, StringComparison.Ordinal) ||
                current.Bytes.LongLength != expectedLength)
            {
                if (current is not null && current.Bytes.LongLength != current.Entry.ByteLength)
                    _cache.Memory.Remove(cacheKey);
                current = null;
                return (true, null);
            }

            GitHubImageCacheRead currentRead = GitHubImageCacheTier.RefreshCachedTimestamp(current);
            GitHubImageSourceLease result = new(ToCachedImage(currentRead), reservation);
            reservation = null;
            current = null;
            return (false, result);
        }
        finally
        {
            current = null;
            reservation?.Dispose();
        }
    }

    private async Task<(bool Retry, GitHubImageSourceLease? Lease)> TryLeaseFromStoreAsync(
        string cacheKey,
        IMarkdownImageSourceByteAdmission admission,
        CancellationToken cancellationToken)
    {
        if (_cache.AdmittedStore is not { } concreteStore)
        {
            // An injected cache store does not expose an admitted-read contract.
            // Its TryReadAsync implementation may allocate or retain bytes before
            // returning, so a reservation made from scalar metadata here would be
            // post-allocation accounting, not strict preallocation admission.
            // Fail closed for this leased read; the concrete production store
            // has the required atomic metadata/admission/read path.
            return (false, null);
        }

        AdmittedGitHubImageCacheRead? admitted = await concreteStore.TryReadAdmittedAsync(
                cacheKey,
                admission,
                GitHubImageService.MaxImageBytes,
                cancellationToken)
            .ConfigureAwait(false);
        if (admitted is null)
            return (false, null);

        try
        {
            GitHubImageCacheRead read = admitted.Value;
            if (read.Bytes.Length == 0 || read.Bytes.Length > GitHubImageService.MaxImageBytes ||
                read.Bytes.LongLength != read.Entry.ByteLength)
            {
                admitted.Dispose();
                admitted = null;
                return (true, null);
            }

            _cache.Memory.Set(cacheKey, read);
            GitHubImageSourceLease result = new(ToCachedImage(read), admitted);
            admitted = null;
            return (false, result);
        }
        finally
        {
            admitted?.Dispose();
        }
    }

    private static GitHubCachedImage ToCachedImage(GitHubImageCacheRead read) => new(
        read.Entry.FilePath,
        read.Bytes,
        read.Entry.ContentType,
        IsFromCache: true,
        IsStale: !GitHubImageCacheTier.IsFresh(read.Entry.CachedAt));

    private async Task<GitHubImageSourceLease?> FetchAndStoreWithAccountLeaseAsync(
        long accountId,
        string cacheKey,
        string sourceUrl,
        GitHubImageCacheEntry? cachedEntry,
        GitHubImageFetcher fetcher,
        IMarkdownImageSourceByteAdmission admission,
        CancellationToken cancellationToken)
    {
        using IAccountWorkLease? accountLease = _accounts.Enter(accountId, cancellationToken);
        return await FetchAndStoreAsync(
                cacheKey,
                sourceUrl,
                cachedEntry,
                fetcher,
                admission,
                accountLease?.CancellationToken ?? cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<GitHubImageSourceLease?> FetchAndStoreAsync(
        string cacheKey,
        string sourceUrl,
        GitHubImageCacheEntry? cachedEntry,
        GitHubImageFetcher fetcher,
        IMarkdownImageSourceByteAdmission admission,
        CancellationToken cancellationToken)
    {
        IDisposable? sourceReservation = null;
        try
        {
            GitHubImageDownload? download = await fetcher(cachedEntry, cancellationToken)
                .ConfigureAwait(false);
            sourceReservation = download?.SourceByteLease;
            cancellationToken.ThrowIfCancellationRequested();

            if (download?.IsNotModified == true && cachedEntry is not null)
            {
                sourceReservation?.Dispose();
                sourceReservation = null;
                await _cache.Store.MarkFreshAsync(cacheKey, cancellationToken).ConfigureAwait(false);
                return await ReadCachedImageLeaseAsync(cacheKey, admission, cancellationToken)
                    .ConfigureAwait(false);
            }

            GitHubValidatedImage image = GitHubValidatedImage.From(download);
            if (sourceReservation is null)
            {
                throw new InvalidOperationException(
                    "A leased image fetcher must transfer the reservation acquired before materializing its bytes.");
            }

            if (_lifetime.IsDisposed)
                throw new OperationCanceledException("The GitHub image service is disposed.", cancellationToken);

            GitHubImageCacheEntry stored = await _cache.StoreAsync(
                    cacheKey,
                    sourceUrl,
                    image,
                    download!,
                    cancellationToken)
                .ConfigureAwait(false);
            var result = new GitHubImageSourceLease(
                new GitHubCachedImage(stored.FilePath, image.Bytes, stored.ContentType, IsFromCache: false),
                sourceReservation);
            sourceReservation = null;
            return result;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch when (cachedEntry is not null)
        {
            // Preserve stale-while-revalidate behavior without returning a raw cached
            // array. The caller reopens the cache generation under its own lease.
            return null;
        }
        finally
        {
            sourceReservation?.Dispose();
        }
    }
}
