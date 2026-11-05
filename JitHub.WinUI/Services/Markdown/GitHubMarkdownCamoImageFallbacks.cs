using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using JitHub.Markdown;
using JitHub.Markdown.Images;
using JitHub.WinUI.Helpers;
using MarkdownRenderer.Images;

namespace JitHub.Services.Markdown;

/// <summary>
/// GitHub's Camo-based alternatives for an image the authored URL cannot
/// serve: the server-rendered README's source-to-Camo map, and the canonical
/// HTTPS origin a Camo URL encodes.
/// </summary>
internal sealed class GitHubMarkdownCamoImageFallbacks
{
    private readonly IMarkdownRemoteImagePolicy _markdownRemoteImagePolicy;
    private readonly GitHubRenderedReadmeImageMaps _renderedReadmeImageMaps;
    private readonly GitHubMarkdownImageFetch _fetch;

    /// <param name="markdownRemoteImagePolicy">The remote-image policy for decoded origins.</param>
    /// <param name="renderedReadmeImageMaps">The credential-scoped rendered README maps.</param>
    /// <param name="fetch">The image fetch helper.</param>
    public GitHubMarkdownCamoImageFallbacks(
        IMarkdownRemoteImagePolicy markdownRemoteImagePolicy,
        GitHubRenderedReadmeImageMaps renderedReadmeImageMaps,
        GitHubMarkdownImageFetch fetch)
    {
        _markdownRemoteImagePolicy = markdownRemoteImagePolicy;
        _renderedReadmeImageMaps = renderedReadmeImageMaps;
        _fetch = fetch;
    }

    public async Task<GitHubMarkdownImageCandidate?> TryResolveViaGitHubCamoAsync(
        string source,
        MarkdownImageResolveContext context,
        IMarkdownImageSourceByteAdmission? admission,
        bool useLeasedSources,
        CancellationToken cancellationToken)
    {
        MarkdownDocumentSource? document = context.DocumentSource;
        if (document is null ||
            !document.HasRepositoryContext ||
            !Uri.TryCreate(source, UriKind.Absolute, out Uri? sourceUri) ||
            (sourceUri.Scheme != Uri.UriSchemeHttp &&
                sourceUri.Scheme != Uri.UriSchemeHttps) ||
            MarkdownRemoteImagePolicy.IsTrustedGitHubHost(sourceUri.Host))
        {
            return null;
        }

        IReadOnlyDictionary<string, string> map = await _renderedReadmeImageMaps.GetAsync(
            document,
            cancellationToken).ConfigureAwait(false);
        if (!map.TryGetValue(sourceUri.AbsoluteUri, out string? camoSource) ||
            !Uri.TryCreate(camoSource, UriKind.Absolute, out Uri? camoUri) ||
            !camoUri.Host.Equals("camo.githubusercontent.com", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return await _fetch.GetMarkdownImageCandidateAsync(
            camoUri.AbsoluteUri,
            GitHubImageFetchScope.TrustedGitHub,
            admission,
            camoUri,
            useLeasedSources,
            cacheOnly: false,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<GitHubMarkdownImageCandidate?> TryResolvePublicImageFallbacksAsync(
        Uri publicUri,
        string authoredSource,
        MarkdownImageResolveContext context,
        IMarkdownImageSourceByteAdmission? admission,
        bool useLeasedSources,
        CancellationToken cancellationToken)
    {
        // A Camo endpoint, its canonical HTTPS origin, and GitHub's
        // server-rendered source map are independent transports. A timeout
        // or reset on one must not suppress the remaining safe alternative.
        return await MarkdownImageFallbackPipeline.FirstSuccessfulAsync(
            token => TryResolveViaGitHubCamoOriginAsync(
                publicUri,
                context,
                admission,
                useLeasedSources,
                token),
            token => TryResolveViaGitHubCamoAsync(
                authoredSource,
                context,
                admission,
                useLeasedSources,
                token),
            static (attempt, exception) =>
            {
                if (exception is not MarkdownImageSourceDeferredException)
                    HandledFailureReporter.Report(
                        exception,
                        attempt == 0
                            ? "markdown-image-camo-origin-fallback"
                            : "markdown-image-github-camo-fallback");
            },
            cancellationToken,
            disposeAbandonedResult: static result => result.Dispose(),
            retirementObserver: GitHubMarkdownImageFetch.GetResolverWorkRetirementObserver(admission))
            .ConfigureAwait(false);
    }

    public async Task<GitHubMarkdownImageCandidate?> TryResolveViaGitHubCamoOriginAsync(
        Uri sourceUri,
        MarkdownImageResolveContext context,
        IMarkdownImageSourceByteAdmission? admission,
        bool useLeasedSources,
        CancellationToken cancellationToken)
    {
        if (!GitHubCamoImageMapParser.TryDecodeCanonicalSource(sourceUri, out Uri originUri))
            return null;

        MarkdownRemoteImageDecision decision = _markdownRemoteImagePolicy.Evaluate(
            originUri,
            context.AllowThirdPartyRemoteImages);
        if (decision.Access == MarkdownRemoteImageAccess.Block)
            return null;

        // The decoded origin is untrusted even when its hostname resembles
        // a GitHub host. Never attach credentials or inherit Camo's trusted
        // fetch scope to this fallback request.
        return await _fetch.GetMarkdownImageCandidateAsync(
            originUri.AbsoluteUri,
            GitHubImageFetchScope.UserApprovedHttps,
            admission,
            originUri,
            useLeasedSources,
            cacheOnly: decision.Access == MarkdownRemoteImageAccess.CacheOnly,
            cancellationToken).ConfigureAwait(false);
    }
}
