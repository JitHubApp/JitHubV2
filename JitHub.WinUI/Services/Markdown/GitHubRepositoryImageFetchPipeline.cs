using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace JitHub.Services.Markdown;

/// <summary>
/// Tries GitHub's credential-free raw CDN before a repository image consumes
/// an authenticated Contents API request. A miss remains eligible for the
/// caller's authenticated private/LFS fallback.
/// </summary>
internal static class GitHubRepositoryImageFetchPipeline
{
    internal static async Task<GitHubCachedImage?> TryGetRawAsync(
        IGitHubImageService imageService,
        Uri rawUri,
        Action<Exception> reportUnexpectedFailure,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(imageService);
        ArgumentNullException.ThrowIfNull(rawUri);
        ArgumentNullException.ThrowIfNull(reportUnexpectedFailure);
        if (rawUri.Scheme != Uri.UriSchemeHttps ||
            !rawUri.Host.Equals("raw.githubusercontent.com", StringComparison.OrdinalIgnoreCase) ||
            !rawUri.IsDefaultPort || rawUri.UserInfo.Length != 0)
        {
            throw new ArgumentException("The repository image is not a trusted raw GitHub URL.", nameof(rawUri));
        }

        try
        {
            return await imageService.GetAsync(
                rawUri.AbsoluteUri,
                GitHubImageFetchScope.TrustedGitHub,
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (HttpRequestException exception) when (
            exception.StatusCode is HttpStatusCode.Unauthorized or
                HttpStatusCode.Forbidden or HttpStatusCode.NotFound)
        {
            // Private assets and missing public objects are expected misses.
            return null;
        }
        catch (InvalidDataException)
        {
            // The raw endpoint can return an LFS pointer, not image bytes.
            return null;
        }
        catch (Exception exception)
        {
            reportUnexpectedFailure(exception);
            return null;
        }
    }
}
