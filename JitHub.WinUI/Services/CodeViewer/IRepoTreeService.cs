using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using JitHub.Models.CodeViewer;

namespace JitHub.Services.CodeViewer;

public sealed record RepoCodeLoadResult<T>(
    T Value,
    CacheState CacheState,
    bool IsRefreshInProgress = false,
    string? RefreshError = null,
    DateTimeOffset? FetchedAt = null,
    DateTimeOffset? StaleAfter = null)
    where T : class;

public interface IRepoTreeService
{
    Task ClearMemoryCacheAsync(
        string? accountPartition = null,
        CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    async Task PrefetchTreeAsync(
        string owner,
        string name,
        string refOrSha,
        CancellationToken ct)
    {
        _ = await LoadTreeAsync(owner, name, refOrSha, ct).ConfigureAwait(false);
    }

    Task<RepoCodeLoadResult<RepoTree>> LoadTreeAsync(
        string owner,
        string name,
        string refOrSha,
        CancellationToken ct,
        QueryFetchPolicy fetchPolicy = QueryFetchPolicy.StaleFirst);

    Task<RepoCodeLoadResult<IReadOnlyList<RepoTreeNode>>> LoadDirectoryAsync(
        string owner,
        string name,
        string path,
        string refOrSha,
        CancellationToken ct,
        QueryFetchPolicy fetchPolicy = QueryFetchPolicy.StaleFirst);

    Task<RepoCodeLoadResult<RepoFileBlob>> LoadBlobAsync(
        string owner,
        string name,
        string sha,
        CancellationToken ct,
        QueryFetchPolicy fetchPolicy = QueryFetchPolicy.StaleFirst);
}
