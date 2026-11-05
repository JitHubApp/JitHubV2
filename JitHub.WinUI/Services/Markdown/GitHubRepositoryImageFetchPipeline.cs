using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using MarkdownRenderer.Images;

namespace JitHub.Services.Markdown;

/// <summary>
/// Tries GitHub's credential-free raw CDN before a repository image consumes
/// an authenticated Contents API request. A miss remains eligible for the
/// caller's authenticated private/LFS fallback.
/// </summary>
internal static class GitHubRepositoryImageFetchPipeline
{
    internal static async Task<GitHubImageSourceLease?> TryGetRawLeasedAsync(
        IGitHubLeasedImageService imageService,
        Uri rawUri,
        Action<Exception> reportUnexpectedFailure,
        IMarkdownImageSourceByteAdmission admission,
        CancellationToken cancellationToken,
        Action<GitHubRawImageFetchOutcome>? observeOutcome = null)
    {
        ArgumentNullException.ThrowIfNull(imageService);
        ArgumentNullException.ThrowIfNull(rawUri);
        ArgumentNullException.ThrowIfNull(reportUnexpectedFailure);
        ArgumentNullException.ThrowIfNull(admission);
        ValidateRawUri(rawUri);

        return await FetchAsync(
            () => imageService.GetLeasedAsync(
                rawUri.AbsoluteUri,
                GitHubImageFetchScope.TrustedGitHub,
                admission,
                cancellationToken),
            reportUnexpectedFailure,
            observeOutcome,
            cancellationToken).ConfigureAwait(false);
    }

    internal static Task<GitHubCachedImage?> TryGetRawAsync(
        IGitHubImageService imageService,
        Uri rawUri,
        Action<Exception> reportUnexpectedFailure,
        CancellationToken cancellationToken,
        Action<GitHubRawImageFetchOutcome>? observeOutcome = null) =>
        TryGetRawAsync(
            imageService, rawUri, reportUnexpectedFailure, null, cancellationToken, observeOutcome);

    internal static async Task<GitHubCachedImage?> TryGetRawAsync(
        IGitHubImageService imageService,
        Uri rawUri,
        Action<Exception> reportUnexpectedFailure,
        IMarkdownImageSourceByteAdmission? admission,
        CancellationToken cancellationToken,
        Action<GitHubRawImageFetchOutcome>? observeOutcome = null)
    {
        ArgumentNullException.ThrowIfNull(imageService);
        ArgumentNullException.ThrowIfNull(rawUri);
        ArgumentNullException.ThrowIfNull(reportUnexpectedFailure);
        ValidateRawUri(rawUri);

        return await FetchAsync(
            () => admission is null
                ? imageService.GetAsync(
                    rawUri.AbsoluteUri,
                    GitHubImageFetchScope.TrustedGitHub,
                    cancellationToken)
                : imageService.GetAsync(
                    rawUri.AbsoluteUri,
                    GitHubImageFetchScope.TrustedGitHub,
                    admission,
                    cancellationToken),
            reportUnexpectedFailure,
            observeOutcome,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Runs one raw CDN fetch and maps its failures: expected misses and bad payloads
    /// return <see langword="null"/>, caller cancellation and deferral propagate, and
    /// anything else is reported and returns <see langword="null"/>.
    /// </summary>
    private static async Task<TResult?> FetchAsync<TResult>(
        Func<Task<TResult?>> fetch,
        Action<Exception> reportUnexpectedFailure,
        Action<GitHubRawImageFetchOutcome>? observeOutcome,
        CancellationToken cancellationToken)
        where TResult : class
    {
        try
        {
            TResult? result = await fetch().ConfigureAwait(false);
            ObserveOutcome(
                observeOutcome,
                new GitHubRawImageFetchOutcome(
                    result is null
                        ? GitHubRawImageFetchOutcomeKind.NullResult
                        : GitHubRawImageFetchOutcomeKind.Success));
            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            ObserveOutcome(
                observeOutcome,
                new GitHubRawImageFetchOutcome(GitHubRawImageFetchOutcomeKind.CallerCanceled));
            throw;
        }
        catch (HttpRequestException exception)
        {
            ObserveOutcome(observeOutcome, CreateHttpOutcome(exception));
            if (IsExpectedRawMiss(exception.StatusCode))
            {
                // Private assets and missing public objects are expected misses.
                return null;
            }

            reportUnexpectedFailure(exception);
            return null;
        }
        catch (InvalidDataException)
        {
            // The raw endpoint can return an LFS pointer, not image bytes.
            ObserveOutcome(
                observeOutcome,
                new GitHubRawImageFetchOutcome(GitHubRawImageFetchOutcomeKind.InvalidPayload));
            return null;
        }
        catch (MarkdownImageSourceDeferredException)
        {
            ObserveOutcome(
                observeOutcome,
                new GitHubRawImageFetchOutcome(GitHubRawImageFetchOutcomeKind.SourceDeferred));
            throw;
        }
        catch (OperationCanceledException exception)
        {
            // A non-caller cancellation from HttpClient is usually its request timeout.
            ObserveOutcome(
                observeOutcome,
                new GitHubRawImageFetchOutcome(GitHubRawImageFetchOutcomeKind.TransportFailure));
            reportUnexpectedFailure(exception);
            return null;
        }
        catch (Exception exception)
        {
            ObserveOutcome(
                observeOutcome,
                new GitHubRawImageFetchOutcome(GitHubRawImageFetchOutcomeKind.OtherFailure));
            reportUnexpectedFailure(exception);
            return null;
        }
    }

    private static bool IsExpectedRawMiss(HttpStatusCode? statusCode) =>
        statusCode is HttpStatusCode.Unauthorized or
            HttpStatusCode.Forbidden or HttpStatusCode.NotFound;

    private static GitHubRawImageFetchOutcome CreateHttpOutcome(HttpRequestException exception) =>
        exception.StatusCode is { } statusCode
            ? new GitHubRawImageFetchOutcome(
                GitHubRawImageFetchOutcomeKind.HttpStatus,
                (int)statusCode)
            : new GitHubRawImageFetchOutcome(GitHubRawImageFetchOutcomeKind.TransportFailure);

    private static void ObserveOutcome(
        Action<GitHubRawImageFetchOutcome>? observer,
        GitHubRawImageFetchOutcome outcome)
    {
        if (observer is null)
            return;

        try
        {
            observer(outcome);
        }
        catch (Exception)
        {
            // Diagnostics are advisory and must never change fetch semantics.
        }
    }

    private static void ValidateRawUri(Uri rawUri)
    {
        if (rawUri.Scheme != Uri.UriSchemeHttps ||
            !rawUri.Host.Equals("raw.githubusercontent.com", StringComparison.OrdinalIgnoreCase) ||
            !rawUri.IsDefaultPort || rawUri.UserInfo.Length != 0)
        {
            throw new ArgumentException("The repository image is not a trusted raw GitHub URL.", nameof(rawUri));
        }
    }
}
