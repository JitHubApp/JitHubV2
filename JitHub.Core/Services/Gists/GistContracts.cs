using System;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using JitHub.Models.GitHub;

namespace JitHub.Services;

public enum GistVisibilityFilter
{
    All,
    Public,
    Secret
}

public enum GistLibrarySort
{
    RecentlyUpdated,
    Newest,
    Oldest,
    Title
}

public enum GistMutationDurability
{
    Durable,
    Degraded
}

public sealed record GistMutationResult<T>(
    T Value,
    GistMutationDurability Durability)
{
    public bool IsDurabilityDegraded => Durability == GistMutationDurability.Degraded;
}

public sealed record GistCachedLibrarySnapshot(
    GitHubGist[] Items,
    int CachedPageCount,
    bool IsComplete,
    CacheState CacheState);

internal sealed class GistCachePageIndex
{
    public int PageSize { get; set; }

    public int HighestKnownPage { get; set; }

    public bool IsComplete { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

internal static class GistCacheTagPolicy
{
    public static string List(string accountPartition) =>
        $"gist-cache:account:{Normalize(accountPartition)}:list";

    public static string ListIndex(string accountPartition) =>
        $"gist-cache:account:{Normalize(accountPartition)}:list-index";

    public static string Detail(string accountPartition, string gistId) =>
        $"gist-cache:account:{Normalize(accountPartition)}:detail:{NormalizeIdentity(gistId, nameof(gistId))}";

    public static string Raw(string accountPartition, string rawIdentity) =>
        $"gist-cache:account:{Normalize(accountPartition)}:raw:{NormalizeIdentity(rawIdentity, nameof(rawIdentity))}";

    private static string Normalize(string accountPartition) =>
        GitHubAccountPartition.Require(accountPartition, nameof(accountPartition));

    private static string NormalizeIdentity(string value, string parameterName) =>
        string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException("A stable cache identity is required.", parameterName)
            : value.Trim();
}

public interface IGitHubGistQueryService
{
    Task<GistCachedLibrarySnapshot> GetCachedLibraryAsync(
        string accessToken,
        string userId,
        int pageSize,
        CancellationToken cancellationToken = default);

    Task<CachedResult<GitHubGist[]>> GetPageAsync(
        string accessToken,
        string userId,
        int page,
        int pageSize,
        QueryFetchPolicy fetchPolicy = QueryFetchPolicy.StaleFirst,
        GitHubRequestPriority priority = GitHubRequestPriority.Visible,
        CancellationToken cancellationToken = default);

    Task<CachedResult<GitHubGist>> GetDetailAsync(
        string accessToken,
        string userId,
        string gistId,
        QueryFetchPolicy fetchPolicy = QueryFetchPolicy.StaleFirst,
        GitHubRequestPriority priority = GitHubRequestPriority.Visible,
        CancellationToken cancellationToken = default);

    Task<CachedResult<string>> GetRawFileAsync(
        string userId,
        string rawUrl,
        QueryFetchPolicy fetchPolicy = QueryFetchPolicy.StaleFirst,
        GitHubRequestPriority priority = GitHubRequestPriority.Visible,
        CancellationToken cancellationToken = default);

    Task<string> GetRawFileContentAsync(
        string userId,
        string rawUrl,
        CancellationToken cancellationToken = default);

    Task DrainBackgroundWorkAsync(CancellationToken cancellationToken = default);

    Task<GistMutationResult<GitHubGist>> CreateAsync(
        string accessToken,
        string userId,
        GitHubGistCreateRequest request,
        CancellationToken cancellationToken = default);

    Task<GistMutationResult<GitHubGist>> UpdateAsync(
        string accessToken,
        string userId,
        string gistId,
        GitHubGistUpdateRequest request,
        CancellationToken cancellationToken = default);

    Task<GistMutationResult<bool>> DeleteAsync(
        string accessToken,
        string userId,
        string gistId,
        CancellationToken cancellationToken = default);
}

[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(GistCachePageIndex))]
internal sealed partial class GistCacheJsonSerializerContext : System.Text.Json.Serialization.JsonSerializerContext
{
}
