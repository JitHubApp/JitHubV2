using System;
using System.Threading;
using System.Threading.Tasks;
using JitHub.Markdown.Images;

namespace JitHub.Services;

public interface IGitHubImageCacheStore
{
    Task<GitHubImageCacheEntry?> TryGetAsync(string cacheKey, CancellationToken cancellationToken = default);

    Task<GitHubImageCacheRead?> TryReadAsync(string cacheKey, CancellationToken cancellationToken = default);

    Task<GitHubImageCacheEntry> PutAsync(
        string cacheKey,
        byte[] bytes,
        string extension,
        CancellationToken cancellationToken = default);

    Task<GitHubImageCacheEntry> PutAsync(
        string cacheKey,
        byte[] bytes,
        string extension,
        GitHubImageCacheWriteMetadata metadata,
        CancellationToken cancellationToken = default);

    Task MarkFreshAsync(string cacheKey, CancellationToken cancellationToken = default);

    Task InvalidateAsync(string cacheKey, CancellationToken cancellationToken = default);

    Task ClearAllAsync(CancellationToken cancellationToken = default);

    Task ClearPartitionAsync(string accountPartition, CancellationToken cancellationToken = default) =>
        Task.FromException(new NotSupportedException(
            "This image cache store does not support account-partition removal."));

    Task EnforceCapAsync(CancellationToken cancellationToken = default);

    Task<long> GetTotalBytesAsync(CancellationToken cancellationToken = default);

    Task<CacheStoreInspection> InspectAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(CacheStoreInspection.Unavailable("Integrity inspection is not implemented by this image cache store."));
}
