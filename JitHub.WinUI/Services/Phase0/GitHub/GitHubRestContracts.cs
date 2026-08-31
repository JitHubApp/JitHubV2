using System;
using System.Net;
using System.Net.Http;
using System.Text.Json.Serialization.Metadata;
using System.Threading;
using System.Threading.Tasks;

namespace JitHub.Services;

public sealed record GitHubRestRequest(
    string AccessToken,
    HttpMethod Method,
    string RelativePath,
    string? ETag = null,
    DateTimeOffset? LastModified = null,
    GitHubRequestPriority Priority = GitHubRequestPriority.Visible,
    string? AcceptMediaType = null,
    bool AcceptNotFound = false);

public sealed record GitHubRestResponse<T>(
    HttpStatusCode StatusCode,
    T? Payload,
    bool IsNotModified,
    string? ETag,
    DateTimeOffset? LastModified,
    string? Link,
    int? RateLimitRemaining,
    DateTimeOffset? RateLimitReset,
    TimeSpan? RetryAfter,
    DateTimeOffset FetchedAt,
    string? RateLimitResource = null)
    where T : class;

public interface IGitHubRestTransport
{
    Task<GitHubRestResponse<T>> SendJsonAsync<T>(
        GitHubRestRequest request,
        JsonTypeInfo<T> jsonTypeInfo,
        CancellationToken cancellationToken = default)
        where T : class;
}
