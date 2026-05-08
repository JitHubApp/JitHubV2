using System.Threading;
using System.Threading.Tasks;
using JitHub.Models.CodeViewer;

namespace JitHub.Services.CodeViewer;

public interface IRepoFileCacheService
{
    bool TryGet(RepoFileCacheKey key, out RepoFileCacheEntry entry);
    Task<RepoFileCacheEntry?> GetAsync(RepoFileCacheKey key, CancellationToken ct);
    Task PutAsync(RepoFileCacheKey key, RepoFileCacheEntry entry, CancellationToken ct);
    Task PurgeAsync(CancellationToken ct);
}
