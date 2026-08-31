using System.Threading;
using System.Threading.Tasks;
using JitHub.Models.GitHub;

namespace JitHub.Services.CodeViewer;

public interface IGitHubRepoCodeQueryService
{
    Task<CachedResult<GitHubTree>> GetTreeAsync(
        string accessToken,
        string userId,
        string owner,
        string repositoryName,
        string gitRef,
        QueryFetchPolicy fetchPolicy = QueryFetchPolicy.StaleFirst,
        CancellationToken cancellationToken = default);

    Task<CachedResult<GitHubRepositoryContent[]>> GetDirectoryAsync(
        string accessToken,
        string userId,
        string owner,
        string repositoryName,
        string path,
        string gitRef,
        QueryFetchPolicy fetchPolicy = QueryFetchPolicy.StaleFirst,
        CancellationToken cancellationToken = default);

    Task<CachedResult<GitHubBlob>> GetBlobAsync(
        string accessToken,
        string userId,
        string owner,
        string repositoryName,
        string sha,
        QueryFetchPolicy fetchPolicy = QueryFetchPolicy.StaleFirst,
        CancellationToken cancellationToken = default);

    Task<CachedResult<GitHubBlob>> GetBlobAsync(
        string accessToken,
        string userId,
        string owner,
        string repositoryName,
        string sha,
        GitHubRequestPriority priority,
        QueryFetchPolicy fetchPolicy = QueryFetchPolicy.StaleFirst,
        CancellationToken cancellationToken = default) =>
        GetBlobAsync(
            accessToken,
            userId,
            owner,
            repositoryName,
            sha,
            fetchPolicy,
            cancellationToken);
}
