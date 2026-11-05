using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using MarkdownRenderer.Images;

namespace JitHub.Services;

/// <summary>
/// The two image cache levels the image service reads and writes together: a
/// bounded in-memory LRU in front of the persistent cache store.
/// </summary>
internal sealed class GitHubImageCacheTier
{
    private const long MemoryCacheByteBudget = 64L * 1024 * 1024;
    private const int MemoryCacheEntryBudget = 2048;
    private static readonly TimeSpan Freshness =
        GitHubCachePolicy.TtlForResource(GitHubCachePolicy.AvatarImageResource);

    internal GitHubImageCacheTier(IGitHubImageCacheStore store)
    {
        Store = store;
        AdmittedStore = store as GitHubImageCacheStore;
    }

    internal IGitHubImageCacheStore Store { get; }

    /// <summary>
    /// The production store, which can admit, check and read an entry atomically.
    /// Injected stores do not have this contract.
    /// </summary>
    internal GitHubImageCacheStore? AdmittedStore { get; }

    internal GitHubImageMemoryCache Memory { get; } = new(MemoryCacheByteBudget, MemoryCacheEntryBudget);

    internal static bool IsFresh(DateTimeOffset cachedAt) => DateTimeOffset.UtcNow - cachedAt <= Freshness;

    /// <summary>Reads memory first, then the store, and promotes a store hit into memory.</summary>
    internal async Task<GitHubImageCacheRead?> ReadAsync(
        string cacheKey,
        IMarkdownImageSourceByteAdmission? admission,
        CancellationToken cancellationToken)
    {
        if (Memory.TryGet(cacheKey, out GitHubImageCacheRead? cached))
        {
            return cached is null ? null : RefreshCachedTimestamp(cached);
        }

        cached = await ReadStoreAsync(cacheKey, admission, cancellationToken).ConfigureAwait(false);
        if (cached is not null)
        {
            Memory.Set(cacheKey, cached);
        }

        return cached;
    }

    /// <summary>Reads the persistent store, reserving the entry's bytes first when admitted.</summary>
    internal async Task<GitHubImageCacheRead?> ReadStoreAsync(
        string cacheKey,
        IMarkdownImageSourceByteAdmission? admission,
        CancellationToken cancellationToken)
    {
        if (admission is null)
            return await Store.TryReadAsync(cacheKey, cancellationToken).ConfigureAwait(false);

        if (AdmittedStore is { } concreteStore)
        {
            using AdmittedGitHubImageCacheRead? admitted = await concreteStore.TryReadAdmittedAsync(
                cacheKey, admission, GitHubImageService.MaxImageBytes, cancellationToken).ConfigureAwait(false);
            return admitted?.Value;
        }

        // Injected stores may not support an atomic metadata/read operation.
        // Reserve their reported size before asking them to materialize bytes;
        // the production store above also rechecks the immutable generation
        // under its stripe gate before allocating the exact-size array.
        GitHubImageCacheEntry? entry = await Store.TryGetAsync(cacheKey, cancellationToken)
            .ConfigureAwait(false);
        if (entry is null || entry.ByteLength <= 0 || entry.ByteLength > GitHubImageService.MaxImageBytes)
            return null;
        using IDisposable lease = await admission.ReserveAsync(entry.ByteLength, cancellationToken)
            .ConfigureAwait(false);
        GitHubImageCacheRead? read = await Store.TryReadAsync(cacheKey, cancellationToken)
            .ConfigureAwait(false);
        if (read is not null && read.Bytes.Length > entry.ByteLength)
            throw new InvalidDataException("The image cache entry changed during its admitted read.");
        return read;
    }

    /// <summary>Gets the current entry's metadata without materializing its bytes from the store.</summary>
    internal async Task<GitHubImageCacheEntry?> GetEntryAsync(string cacheKey, CancellationToken cancellationToken)
    {
        if (Memory.TryGet(cacheKey, out GitHubImageCacheRead? memoryRead) && memoryRead is not null)
        {
            return RefreshCachedTimestamp(memoryRead).Entry;
        }

        return await Store.TryGetAsync(cacheKey, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Writes a validated download to the store and memory. The decoder and
    /// cache follow the validated signature, not the untrusted response label.
    /// </summary>
    internal async Task<GitHubImageCacheEntry> StoreAsync(
        string cacheKey,
        string sourceUrl,
        GitHubValidatedImage image,
        GitHubImageDownload download,
        CancellationToken cancellationToken)
    {
        // GitHub's image CDN occasionally returns raster bytes under a stale or
        // incorrect image media type.
        string extension = GitHubImageContentSniffer.GetExtension(image.ContentType, sourceUrl);
        GitHubImageCacheEntry stored = await Store.PutAsync(
                cacheKey,
                image.Bytes,
                extension,
                new GitHubImageCacheWriteMetadata(download.ETag, download.LastModified, image.ContentType),
                cancellationToken)
            .ConfigureAwait(false);
        Memory.Set(cacheKey, new GitHubImageCacheRead(stored, image.Bytes));
        return stored;
    }

    internal static GitHubImageCacheRead RefreshCachedTimestamp(GitHubImageCacheRead cached)
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
            // The payload is being replaced or removed; keep the recorded timestamp.
        }
        catch (UnauthorizedAccessException)
        {
            // The payload is unreadable for metadata; keep the recorded timestamp.
        }

        return cached;
    }
}
