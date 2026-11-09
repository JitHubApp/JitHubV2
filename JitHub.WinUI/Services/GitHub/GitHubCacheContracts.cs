using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text.Json.Serialization.Metadata;
using System.Threading;
using System.Threading.Tasks;

namespace JitHub.Services;

public enum CacheState
{
    Miss,
    Fresh,
    Stale,
    Refreshing,
    Error
}

public enum QueryFetchPolicy
{
    StaleFirst,
    CacheFirst,
    NetworkOnly,
    RefreshInBackground
}

public enum GitHubRequestPriority
{
    UserInitiated,
    Visible,
    BackgroundRefresh,
    Prefetch,
    Mutation
}

public sealed record CachedResult<T>(
    T? Value,
    CacheState CacheState,
    DateTimeOffset? FetchedAt,
    DateTimeOffset? StaleAfter,
    bool IsRefreshInProgress = false,
    Exception? RefreshError = null,
    string? ETag = null,
    DateTimeOffset? LastModified = null)
    where T : class;

public sealed record GitHubQuery<T>(
    string AccessToken,
    string UserId,
    HttpMethod Method,
    string RelativePath,
    string CacheKey,
    string ResourceKind,
    TimeSpan Ttl,
    JsonTypeInfo<T> JsonTypeInfo,
    IReadOnlyList<string>? Tags = null,
    GitHubRequestPriority Priority = GitHubRequestPriority.Visible,
    string? AcceptMediaType = null,
    bool AcceptNotFound = false,
    Func<HttpStatusCode, T>? EmptyResponseFactory = null)
    where T : class;

public interface IGitHubCacheStore
{
    Task<CachedResult<T>?> TryGetAsync<T>(GitHubQuery<T> query, CancellationToken cancellationToken = default)
        where T : class;

    Task PutAsync<T>(
        GitHubQuery<T> query,
        GitHubRestResponse<T> response,
        CancellationToken cancellationToken = default)
        where T : class;

    Task MarkRevalidatedAsync<T>(
        GitHubQuery<T> query,
        GitHubRestResponse<T> response,
        CancellationToken cancellationToken = default)
        where T : class;

    Task InvalidateAsync(string cacheKey, CancellationToken cancellationToken = default);

    Task InvalidateTagsAsync(IReadOnlyCollection<string> tags, CancellationToken cancellationToken = default);

    Task InvalidateTagsAsync(
        string userId,
        IReadOnlyCollection<string> tags,
        CancellationToken cancellationToken = default) =>
        InvalidateTagsAsync(tags, cancellationToken);

    Task ClearAllAsync(CancellationToken cancellationToken = default);

    Task ClearPartitionAsync(string userId, CancellationToken cancellationToken = default) =>
        Task.FromException(new NotSupportedException(
            "This query cache store does not support account-partition removal."));

    Task<long> GetTotalPayloadBytesAsync(CancellationToken cancellationToken = default);

    Task<long> GetTotalMetadataBytesAsync(CancellationToken cancellationToken = default);

    Task<int> GetSchemaVersionAsync(CancellationToken cancellationToken = default);

    Task EnforceCapsAsync(CancellationToken cancellationToken = default);

    Task<CacheStoreInspection> InspectAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(CacheStoreInspection.Unavailable("Integrity inspection is not implemented by this cache store."));
}
