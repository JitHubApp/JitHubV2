using System;
using System.Net;

namespace JitHub.Services;

public class GitHubApiException : Exception
{
    public GitHubApiException(HttpStatusCode statusCode, string message)
        : base(message)
    {
        StatusCode = statusCode;
    }

    public HttpStatusCode StatusCode { get; }
}

public sealed class GitHubAuthenticationException : GitHubApiException
{
    public GitHubAuthenticationException(string message)
        : base(HttpStatusCode.Unauthorized, message)
    {
    }
}

public sealed class GitHubRateLimitException : GitHubApiException
{
    public GitHubRateLimitException(HttpStatusCode statusCode, string message, TimeSpan retryDelay)
        : this(statusCode, message, retryDelay, null, null, null, null)
    {
    }

    public GitHubRateLimitException(
        HttpStatusCode statusCode,
        string message,
        TimeSpan retryDelay,
        int? rateLimitRemaining,
        DateTimeOffset? rateLimitReset,
        TimeSpan? retryAfter,
        string? rateLimitResource)
        : base(statusCode, message)
    {
        RetryDelay = retryDelay < TimeSpan.Zero ? TimeSpan.Zero : retryDelay;
        RateLimitRemaining = rateLimitRemaining;
        RateLimitReset = rateLimitReset;
        RetryAfter = retryAfter;
        RateLimitResource = rateLimitResource;
    }

    public TimeSpan RetryDelay { get; }

    public int? RateLimitRemaining { get; }

    public DateTimeOffset? RateLimitReset { get; }

    public TimeSpan? RetryAfter { get; }

    public string? RateLimitResource { get; }
}
