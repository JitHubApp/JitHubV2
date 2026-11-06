using System;
using System.Threading;
using System.Threading.Tasks;
using MarkdownRenderer.Images;

namespace JitHub.Markdown.Images;

/// <summary>
/// Resolves a Markdown image that belongs to a GitHub repository: the
/// credential-free raw CDN first, then the authenticated repository read (with
/// Git LFS media), and for an explicitly authored raw URL a direct raw fallback.
/// </summary>
internal sealed class GitHubRepositoryMarkdownImageStage
{
    private readonly IGitHubMarkdownContentClient _contentClient;
    private readonly IGitHubRepositoryImageClient? _rawImageClient;
    private readonly IMarkdownRemoteImagePolicy _markdownRemoteImagePolicy;
    private readonly GitHubMarkdownImageFetch _fetch;
    private readonly Func<string> _getAccessToken;
    private readonly Action<Exception, string> _reportFailure;

    /// <param name="contentClient">The client for authenticated repository reads.</param>
    /// <param name="rawImageClient">The raw Contents capability of that client, if it has one.</param>
    /// <param name="markdownRemoteImagePolicy">The remote-image policy.</param>
    /// <param name="fetch">The image fetch helper.</param>
    /// <param name="getAccessToken">Returns the account credential, or throws when signed out.</param>
    /// <param name="reportFailure">Receives handled failures and their category.</param>
    public GitHubRepositoryMarkdownImageStage(
        IGitHubMarkdownContentClient contentClient,
        IGitHubRepositoryImageClient? rawImageClient,
        IMarkdownRemoteImagePolicy markdownRemoteImagePolicy,
        GitHubMarkdownImageFetch fetch,
        Func<string> getAccessToken,
        Action<Exception, string> reportFailure)
    {
        _contentClient = contentClient;
        _rawImageClient = rawImageClient;
        _markdownRemoteImagePolicy = markdownRemoteImagePolicy;
        _fetch = fetch;
        _getAccessToken = getAccessToken;
        _reportFailure = reportFailure;
    }

    /// <summary>Resolves a repository image.</summary>
    public async Task<GitHubMarkdownImageCandidate> ResolveAsync(
        string source,
        GitHubMarkdownImageReference imageReference,
        MarkdownImageResolveContext context,
        IMarkdownImageSourceByteAdmission? admission,
        bool useLeasedSources,
        CancellationToken cancellationToken)
    {
        try
        {
            Uri rawUri = GitHubMarkdownImageUrlResolver.CreateRawUri(imageReference);
            MarkdownRemoteImageDecision repositoryDecision = _markdownRemoteImagePolicy.Evaluate(
                rawUri,
                context.AllowThirdPartyRemoteImages);
            if (repositoryDecision.Access == MarkdownRemoteImageAccess.Block)
            {
                return GitHubMarkdownImageCandidate.Unowned(
                    MarkdownImageResolution.Blocked(repositoryDecision.UnavailableReason));
            }

            if (repositoryDecision.Access == MarkdownRemoteImageAccess.AllowNetwork)
            {
                GitHubMarkdownImageCandidate? rawCandidate = await TryGetFromRawCdnAsync(
                    rawUri, admission, useLeasedSources, cancellationToken).ConfigureAwait(false);
                if (rawCandidate is not null)
                    return rawCandidate;
            }

            GitHubMarkdownImageCandidate? repositoryCandidate;
            if (repositoryDecision.Access == MarkdownRemoteImageAccess.CacheOnly)
            {
                repositoryCandidate = await _fetch.GetMarkdownImageCandidateAsync(
                    rawUri.ToString(),
                    GitHubImageFetchScope.TrustedGitHub,
                    admission,
                    rawUri,
                    useLeasedSources,
                    cacheOnly: true,
                    cancellationToken).ConfigureAwait(false);
                if (repositoryCandidate is null)
                {
                    return GitHubMarkdownImageCandidate.Unowned(
                        MarkdownImageResolution.Blocked(repositoryDecision.UnavailableReason));
                }
            }
            else
            {
                repositoryCandidate = await FetchFromRepositoryAsync(
                    source, imageReference, rawUri, admission, useLeasedSources, cancellationToken)
                    .ConfigureAwait(false);
            }

            return repositoryCandidate ?? GitHubMarkdownImageCandidate.Unowned(
                MarkdownImageResolution.Unavailable);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (MarkdownImageSourceDeferredException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _reportFailure(ex, "markdown-image-github-fetch");
            return GitHubMarkdownImageCandidate.Unowned(MarkdownImageResolution.Unavailable);
        }
    }

    private async Task<GitHubMarkdownImageCandidate?> TryGetFromRawCdnAsync(
        Uri rawUri,
        IMarkdownImageSourceByteAdmission? admission,
        bool useLeasedSources,
        CancellationToken cancellationToken)
    {
        // Public repository media is already served by GitHub's raw
        // CDN. Fetching every badge through the authenticated
        // Contents API first exhausts its shared rate budget on
        // image-heavy READMEs. This request carries no account
        // credential; private/LFS/missing assets still fall back
        // to the authenticated repository path below.
        if (useLeasedSources)
        {
            GitHubImageSourceLease? rawLease = await GitHubRepositoryImageFetchPipeline
                .TryGetRawLeasedAsync(
                    _fetch.LeasedImageService,
                    rawUri,
                    exception => _reportFailure(
                        exception, "markdown-image-raw-cdn-fallback"),
                    admission!,
                    cancellationToken).ConfigureAwait(false);
            return await GitHubMarkdownImageAssets.ReadCachedMarkdownImageAsync(
                rawLease, rawUri, cancellationToken).ConfigureAwait(false);
        }

        GitHubCachedImage? rawImage = await GitHubRepositoryImageFetchPipeline.TryGetRawAsync(
            _fetch.ImageService,
            rawUri,
            exception => _reportFailure(
                exception, "markdown-image-raw-cdn-fallback"),
            admission,
            cancellationToken).ConfigureAwait(false);
        return await GitHubMarkdownImageAssets.ReadCachedMarkdownImageAsync(
            rawImage, rawUri, cancellationToken).ConfigureAwait(false);
    }

    private async Task<GitHubMarkdownImageCandidate?> FetchFromRepositoryAsync(
        string source,
        GitHubMarkdownImageReference imageReference,
        Uri rawUri,
        IMarkdownImageSourceByteAdmission? admission,
        bool useLeasedSources,
        CancellationToken cancellationToken)
    {
        try
        {
            GitHubImageFetcher fetcher = new GitHubRepositoryImageContentFetch(
                _contentClient,
                _rawImageClient,
                _fetch,
                _getAccessToken,
                imageReference,
                admission,
                useLeasedSources).FetchAsync;
            if (useLeasedSources)
            {
                GitHubImageSourceLease? resolvedLease = await _fetch.LeasedImageService
                    .GetOrFetchLeasedAsync(
                        rawUri.ToString(), fetcher, admission!, cancellationToken)
                    .ConfigureAwait(false);
                return await GitHubMarkdownImageAssets.ReadCachedMarkdownImageAsync(
                    resolvedLease, rawUri, cancellationToken).ConfigureAwait(false);
            }

            GitHubCachedImage? cachedImage = admission is null
                ? await _fetch.ImageService.GetOrFetchAsync(
                    rawUri.ToString(), fetcher, cancellationToken).ConfigureAwait(false)
                : await _fetch.ImageService.GetOrFetchAsync(
                    rawUri.ToString(), fetcher, admission, cancellationToken).ConfigureAwait(false);
            return await GitHubMarkdownImageAssets.ReadCachedMarkdownImageAsync(
                cachedImage, rawUri, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (MarkdownImageSourceDeferredException)
        {
            throw;
        }
        catch when (GitHubMarkdownImageAssets.IsAbsoluteRawGitHubSource(source, imageReference))
        {
            // A README may intentionally reference a raw asset through
            // a branch name that no longer resolves through the Contents
            // API even though GitHub's raw CDN still serves the immutable
            // object. Only an explicitly authored, trusted raw URL gets
            // this fallback; relative/private repository assets continue
            // to use the authenticated repository API path exclusively.
            Uri directRawUri = GitHubMarkdownImageAssets.CreateRawFallbackUri(imageReference.SourceUri);
            return await _fetch.GetMarkdownImageCandidateAsync(
                directRawUri.ToString(),
                GitHubImageFetchScope.TrustedGitHub,
                admission,
                rawUri,
                useLeasedSources,
                cacheOnly: false,
                cancellationToken).ConfigureAwait(false);
        }
    }
}
