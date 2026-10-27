using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using MarkdownRenderer.Images;

namespace JitHub.Services.Markdown;

internal enum GitHubRawImageFetchOutcomeKind
{
    Success,
    NullResult,
    HttpStatus,
    InvalidPayload,
    CallerCanceled,
    SourceDeferred,
    TransportFailure,
    OtherFailure,
}

/// <summary>
/// Fixed, privacy-safe outcome for one raw GitHub image-service operation,
/// which may be fulfilled from cache or include internal transport retries. It intentionally
/// carries no URL, exception message, response body, or credential data.
/// </summary>
internal readonly record struct GitHubRawImageFetchOutcome(
    GitHubRawImageFetchOutcomeKind Kind,
    int? HttpStatusCode = null);

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

        try
        {
            GitHubImageSourceLease? result = await imageService.GetLeasedAsync(
                rawUri.AbsoluteUri,
                GitHubImageFetchScope.TrustedGitHub,
                admission,
                cancellationToken).ConfigureAwait(false);
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

        try
        {
            GitHubCachedImage? result = admission is null
                ? await imageService.GetAsync(
                    rawUri.AbsoluteUri,
                    GitHubImageFetchScope.TrustedGitHub,
                    cancellationToken).ConfigureAwait(false)
                : await imageService.GetAsync(
                    rawUri.AbsoluteUri,
                    GitHubImageFetchScope.TrustedGitHub,
                    admission,
                    cancellationToken).ConfigureAwait(false);
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
