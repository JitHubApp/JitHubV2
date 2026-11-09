using System;
using System.Net;

namespace JitHub.Services;

public static class GitHubRetryPolicy
{
    public static readonly TimeSpan DefaultSecondaryRateLimitDelay = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan MaximumRetryDelay = TimeSpan.FromHours(1);

    public static TimeSpan? CalculateRetryDelay(
        HttpStatusCode statusCode,
        int? rateLimitRemaining,
        DateTimeOffset? rateLimitReset,
        TimeSpan? retryAfter,
        DateTimeOffset now)
    {
        if (retryAfter is { } retryAfterDelay)
        {
            return Clamp(retryAfterDelay);
        }

        bool isRateLimitStatus = statusCode == HttpStatusCode.Forbidden ||
            statusCode == HttpStatusCode.TooManyRequests ||
            (int)statusCode == 429;
        if (!isRateLimitStatus)
        {
            return null;
        }

        if (rateLimitRemaining == 0 && rateLimitReset is DateTimeOffset resetAt)
        {
            return Clamp(resetAt - now + TimeSpan.FromSeconds(1));
        }

        if (statusCode == HttpStatusCode.Forbidden || statusCode == HttpStatusCode.TooManyRequests)
        {
            return DefaultSecondaryRateLimitDelay;
        }

        return null;
    }

    private static TimeSpan Clamp(TimeSpan delay)
    {
        if (delay < TimeSpan.Zero)
        {
            return TimeSpan.Zero;
        }

        return delay > MaximumRetryDelay
            ? MaximumRetryDelay
            : delay;
    }
}
