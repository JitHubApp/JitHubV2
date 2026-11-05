using System;
using System.Threading;
using System.Threading.Tasks;
using MarkdownRenderer.Images;
using RestGitHubBlob = JitHub.Models.GitHub.GitHubBlob;
using RestGitHubRepositoryContent = JitHub.Models.GitHub.GitHubRepositoryContent;

namespace JitHub.Services.Markdown;

/// <summary>
/// The authenticated read for one repository image, used as the image cache's
/// fetcher: raw Contents bytes under source admission when the client supports
/// it, otherwise the JSON Contents and Blob APIs, following a Git LFS pointer to
/// its media bytes in both cases. The account credential is read when the cache
/// asks for the bytes, not when the request is created.
/// </summary>
internal sealed class GitHubRepositoryImageContentFetch
{
    private readonly IGitHubClientService _gitHubClientService;
    private readonly GitHubMarkdownImageFetch _fetch;
    private readonly Func<string> _getAccessToken;
    private readonly GitHubMarkdownImageReference _imageReference;
    private readonly IMarkdownImageSourceByteAdmission? _admission;
    private readonly bool _useLeasedSources;

    public GitHubRepositoryImageContentFetch(
        IGitHubClientService gitHubClientService,
        GitHubMarkdownImageFetch fetch,
        Func<string> getAccessToken,
        GitHubMarkdownImageReference imageReference,
        IMarkdownImageSourceByteAdmission? admission,
        bool useLeasedSources)
    {
        _gitHubClientService = gitHubClientService;
        _fetch = fetch;
        _getAccessToken = getAccessToken;
        _imageReference = imageReference;
        _admission = admission;
        _useLeasedSources = useLeasedSources;
    }

    /// <summary>Fetches the image bytes; matches <see cref="GitHubImageFetcher"/>.</summary>
    public async Task<GitHubImageDownload?> FetchAsync(GitHubImageCacheEntry? cachedEntry, CancellationToken token)
    {
        string accessToken = _getAccessToken();

        // The production client streams raw authenticated Contents
        // bytes under its exact/staged source reservation. This branch
        // must precede the legacy worst-case reservation: taking the
        // full image ceiling here would leave no room for the reader's
        // own reservation and can deadlock a constrained session.
        if (_useLeasedSources &&
            _gitHubClientService is IGitHubRepositoryImageClient repositoryImageClient)
        {
            return await FetchRawContentAsync(repositoryImageClient, accessToken, token).ConfigureAwait(false);
        }

        return await FetchContentsApiAsync(accessToken, token).ConfigureAwait(false);
    }

    private async Task<GitHubImageDownload?> FetchRawContentAsync(
        IGitHubRepositoryImageClient repositoryImageClient,
        string accessToken,
        CancellationToken token)
    {
        GitHubImageDownload? rawDownload = await repositoryImageClient
            .GetRepositoryImageContentAsync(
                accessToken,
                _imageReference.Owner,
                _imageReference.Repository,
                _imageReference.Path,
                _imageReference.Ref,
                _admission!,
                token).ConfigureAwait(false);
        if (rawDownload is null)
            return null;

        byte[]? rawBytes = rawDownload.Bytes;
        IDisposable? rawLease = rawDownload.SourceByteLease;
        if (rawBytes is not { Length: > 0 })
        {
            rawLease?.Dispose();
            return null;
        }

        try
        {
            if (GitLfsPointer.IsPointer(rawBytes))
            {
                // Drop the raw response carrier and its payload
                // before recursively fetching the LFS media bytes.
                rawDownload = rawDownload with
                {
                    Bytes = Array.Empty<byte>(),
                    SourceByteLease = null
                };
                rawBytes = Array.Empty<byte>();
                IDisposable? pointerReservation = rawLease;
                rawLease = null;
                Uri lfsRoute = GitHubMarkdownImageUrlResolver
                    .CreateGitHubRawRouteUri(_imageReference);
                return await GitHubLeasedImageDownloadTransfer.ResolveLfsDownloadAsync(
                    pointerReservation,
                    () => _fetch.LeasedImageService.GetLeasedAsync(
                        lfsRoute.AbsoluteUri,
                        GitHubImageFetchScope.TrustedGitHub,
                        _admission!,
                        token)).ConfigureAwait(false);
            }

            GitHubImageDownload ownedDownload = rawDownload with
            {
                ContentType = GitHubMarkdownImageAssets.GuessImageContentType(_imageReference.Path),
                SourceByteLease = rawLease
            };
            rawLease = null;
            return ownedDownload;
        }
        finally
        {
            rawLease?.Dispose();
        }
    }

    private async Task<GitHubImageDownload?> FetchContentsApiAsync(string accessToken, CancellationToken token)
    {
        // Compatibility path for custom/older clients that only
        // implement JSON Contents/Blob APIs. Their client may have
        // already materialized base64 text before this reservation;
        // it is intentionally outside the strict raw-source bound.
        // Contents and Blob API payloads are materialized by
        // the authenticated client before decoding. Reserve
        // the hard image ceiling before that work and keep
        // the lease through cache storage. An LFS pointer
        // releases it before the recursive media request.
        IDisposable? sourceLease = _admission is null
            ? null
            : await _admission.ReserveAsync(
                GitHubImageService.MaxImageBytes, token).ConfigureAwait(false);
        try
        {
            RestGitHubRepositoryContent file = await _gitHubClientService.GetRepositoryContentAsync(
                accessToken,
                _imageReference.Owner,
                _imageReference.Repository,
                _imageReference.Path,
                _imageReference.Ref,
                token).ConfigureAwait(false);
            byte[] fileBytes = GitHubMarkdownImageAssets.DecodeGitHubContent(file.Content, file.Encoding);

            if (fileBytes.Length == 0 && !string.IsNullOrWhiteSpace(file.Sha))
            {
                RestGitHubBlob blob = await _gitHubClientService.GetBlobAsync(
                    accessToken,
                    _imageReference.Owner,
                    _imageReference.Repository,
                    file.Sha,
                    token).ConfigureAwait(false);
                fileBytes = GitHubMarkdownImageAssets.DecodeGitHubContent(blob.Content, blob.Encoding);
            }

            if (GitLfsPointer.IsPointer(fileBytes))
            {
                // Do not keep the pointer payload in this async
                // state machine after returning its admission.
                // The media request owns a different source grant.
                fileBytes = Array.Empty<byte>();
                // The Contents API and raw.githubusercontent.com expose
                // the LFS pointer itself. GitHub's repository /raw route
                // performs the bounded, trusted redirect to the media CDN
                // and yields the same bytes rendered by github.com.
                Uri lfsRoute = GitHubMarkdownImageUrlResolver
                    .CreateGitHubRawRouteUri(_imageReference);
                IDisposable? pointerReservation = sourceLease;
                sourceLease = null;
                return await FetchLfsMediaAsync(lfsRoute, pointerReservation, token).ConfigureAwait(false);
            }

            if (fileBytes.Length == 0)
                return null;
            GitHubImageDownload download = new(
                fileBytes,
                GitHubMarkdownImageAssets.GuessImageContentType(_imageReference.Path),
                SourceByteLease: sourceLease);
            sourceLease = null;
            return download;
        }
        finally
        {
            sourceLease?.Dispose();
        }
    }

    private async Task<GitHubImageDownload?> FetchLfsMediaAsync(
        Uri lfsRoute,
        IDisposable? pointerReservation,
        CancellationToken token)
    {
        if (_useLeasedSources)
        {
            return await GitHubLeasedImageDownloadTransfer.ResolveLfsDownloadAsync(
                pointerReservation,
                () => _fetch.LeasedImageService.GetLeasedAsync(
                    lfsRoute.AbsoluteUri,
                    GitHubImageFetchScope.TrustedGitHub,
                    _admission!,
                    token)).ConfigureAwait(false);
        }

        pointerReservation?.Dispose();
        GitHubCachedImage? lfsImage = await _fetch.GetMarkdownImageAsync(
            lfsRoute.AbsoluteUri,
            GitHubImageFetchScope.TrustedGitHub,
            _admission,
            token).ConfigureAwait(false);
        return lfsImage?.Bytes is { Length: > 0 } lfsBytes
            ? new GitHubImageDownload(lfsBytes, lfsImage.ContentType)
            : null;
    }
}
