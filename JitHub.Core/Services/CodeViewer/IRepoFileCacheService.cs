using System;
using System.Threading;
using System.Threading.Tasks;
using JitHub.Core.Models.CodeViewer;
using JitHub.Core.Services.Storage;

namespace JitHub.Core.Services.CodeViewer;

public interface IRepoFileCacheService
{
    string RootPath { get; }
    long DiskSoftCapBytes { get; }
    TimeSpan Ttl { get; }

    bool TryGet(RepoFileCacheKey key, out RepoFileCacheEntry entry);
    Task<RepoFileCacheEntry?> GetAsync(RepoFileCacheKey key, CancellationToken ct);
    Task PutAsync(RepoFileCacheKey key, RepoFileCacheEntry entry, CancellationToken ct);
    Task PurgeAsync(CancellationToken ct);
    Task<long> GetTotalBytesAsync(CancellationToken ct = default);
    Task ClearAllAsync(CancellationToken ct = default);
    Task ClearPartitionAsync(string userId, CancellationToken ct = default) =>
        Task.FromException(new NotSupportedException(
            "This repository file cache does not support account-partition removal."));
    Task<JitHub.Core.Services.Storage.CacheStoreInspection> InspectAsync(CancellationToken ct = default) =>
        Task.FromResult(JitHub.Core.Services.Storage.CacheStoreInspection.Unavailable(
            "Integrity inspection is not implemented by this repository file cache store."));
}
