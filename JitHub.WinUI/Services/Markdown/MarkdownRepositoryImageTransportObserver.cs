using System;
using System.IO;
using System.Net.Http;
using System.Threading;
using MarkdownRenderer.Images;

namespace JitHub.Services.Markdown;

internal enum MarkdownRepositoryImageTransportStage
{
    RawImageServiceResult,
    RepositoryFetcher,
}

/// <summary>
/// Wraps only actual authenticated repository-fetcher invocations, not cache hits
/// or assumed HTTP attempts. The separate raw-image service hook includes cache
/// results and is not this wrapper. Optional diagnostics never own returned bytes
/// or their source reservation and never change success/failure behavior.
/// </summary>
internal static class MarkdownRepositoryImageTransportObserver
{
    internal static GitHubImageFetcher WrapFetcher(
        GitHubImageFetcher fetcher,
        Action<GitHubRawImageFetchOutcome>? observer)
    {
        ArgumentNullException.ThrowIfNull(fetcher);
        // The ordinary path returns the original delegate, with no wrapper
        // closure, collector, source hash or additional task allocation.
        if (observer is null)
            return fetcher;

        return async (cached, cancellationToken) =>
        {
            try
            {
                GitHubImageDownload? result = await fetcher(cached, cancellationToken).ConfigureAwait(false);
                Observe(observer, new(result?.Bytes is { Length: > 0 }
                    ? GitHubRawImageFetchOutcomeKind.Success
                    : GitHubRawImageFetchOutcomeKind.NullResult, null));
                return result;
            }
            catch (Exception exception)
            {
                Observe(observer, ClassifyFailure(exception, cancellationToken));
                throw;
            }
        };
    }

    private static GitHubRawImageFetchOutcome ClassifyFailure(Exception exception, CancellationToken token) =>
        exception switch
        {
            MarkdownImageSourceDeferredException => new(GitHubRawImageFetchOutcomeKind.SourceDeferred, null),
            OperationCanceledException when token.IsCancellationRequested => new(GitHubRawImageFetchOutcomeKind.CallerCanceled, null),
            OperationCanceledException => new(GitHubRawImageFetchOutcomeKind.TransportFailure, null),
            HttpRequestException { StatusCode: { } status } => new(GitHubRawImageFetchOutcomeKind.HttpStatus, (int)status),
            GitHubApiException api => new(GitHubRawImageFetchOutcomeKind.HttpStatus, (int)api.StatusCode),
            InvalidDataException => new(GitHubRawImageFetchOutcomeKind.InvalidPayload, null),
            HttpRequestException or IOException => new(GitHubRawImageFetchOutcomeKind.TransportFailure, null),
            _ => new(GitHubRawImageFetchOutcomeKind.OtherFailure, null),
        };

    private static void Observe(Action<GitHubRawImageFetchOutcome> observer, GitHubRawImageFetchOutcome outcome)
    {
        try
        {
            observer(outcome);
        }
        catch
        {
            // In particular, a failing observer must not strand a successfully
            // returned download's source lease or replace its original failure.
        }
    }
}
