using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using JitHub.Markdown.Images;

namespace JitHub.Services;

/// <summary>
/// Bounded retry for idempotent image GETs. Only failures classified as
/// transient are retried; policy and permanent responses fail fast.
/// </summary>
internal static class GitHubImageFetchRetryPolicy
{
    // Two consecutive CDN/HTTP2 stream failures were observed on real README
    // images. A third idempotent GET is bounded and only reached for
    // errors classified as transient; policy and permanent responses fail fast.
    internal const int MaxAttempts = 3;
    private static readonly TimeSpan TransientRetryDelay = TimeSpan.FromMilliseconds(125);

    /// <summary>Delays before the next attempt; the second retry backs off further.</summary>
    internal static Task DelayBeforeRetryAsync(int attempt, CancellationToken cancellationToken) =>
        Task.Delay(TransientRetryDelay * attempt, cancellationToken);

    internal static async Task<GitHubImageDownload?> FetchWithRetryAsync(
        GitHubImageFetcher fetcher,
        GitHubImageCacheEntry? cached,
        CancellationToken cancellationToken)
    {
        for (int attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            try
            {
                return await fetcher(cached, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (
                attempt < MaxAttempts &&
                !cancellationToken.IsCancellationRequested &&
                IsTransientFetchFailure(exception))
            {
                // Repository-relative images use an authenticated GitHub API
                // fetcher rather than the HTTP fetcher. Give that idempotent GET
                // the same single bounded recovery opportunity as CDN images.
                await DelayBeforeRetryAsync(attempt, cancellationToken).ConfigureAwait(false);
            }
        }

        throw new InvalidOperationException("The bounded image fetch retry loop completed unexpectedly.");
    }

    internal static bool IsTransientHttpFailure(Exception exception)
    {
        if (exception is TransientCamoSvgPayloadException)
            return true;

        if (exception is OperationCanceledException)
        {
            // The caller token is checked by the catch filter. Reaching this path
            // therefore means HttpClient's own per-attempt timeout elapsed.
            return true;
        }

        if (exception is IOException)
        {
            return true;
        }

        if (exception is not HttpRequestException requestException)
        {
            return false;
        }

        if (requestException.StatusCode is not HttpStatusCode statusCode)
        {
            // Connection resets, DNS races, and HTTP/2 stream failures do not
            // carry a response status but are safe to retry for an idempotent GET.
            return true;
        }

        return IsTransientStatus((int)statusCode);
    }

    private static bool IsTransientFetchFailure(Exception exception)
    {
        if (IsTransientHttpFailure(exception))
        {
            return true;
        }

        if (exception is not GitHubApiException apiException ||
            exception is GitHubAuthenticationException or GitHubRateLimitException)
        {
            return false;
        }

        return IsTransientStatus((int)apiException.StatusCode);
    }

    private static bool IsTransientStatus(int status) =>
        status is 408 or 425 or 429 ||
        (status >= 500 && status <= 599 && status is not (501 or 505));
}
