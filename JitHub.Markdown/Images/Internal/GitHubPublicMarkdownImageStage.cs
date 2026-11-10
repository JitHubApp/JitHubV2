using System;
using System.Threading;
using System.Threading.Tasks;
using JitHub.Markdown.Images.Caching;
using JitHub.Markdown.Images.Internal.Camo;
using JitHub.Markdown.Images.RemoteContent;
using MarkdownRenderer.Images;

namespace JitHub.Markdown.Images.Internal;

/// <summary>
/// Resolves a Markdown image that does not belong to a GitHub repository:
/// unhandled sources, plaintext HTTP (served only through GitHub's own Camo
/// mapping), and public HTTPS images under the remote-image policy, with the
/// Camo fallbacks a failed fetch may use.
/// </summary>
internal sealed class GitHubPublicMarkdownImageStage
{
    private readonly IMarkdownRemoteImagePolicy _markdownRemoteImagePolicy;
    private readonly GitHubMarkdownImageFetch _fetch;
    private readonly GitHubMarkdownCamoImageFallbacks _camo;
    private readonly Action<Exception, string> _reportFailure;

    /// <param name="markdownRemoteImagePolicy">The remote-image policy.</param>
    /// <param name="fetch">The image fetch helper.</param>
    /// <param name="camo">The Camo fallbacks.</param>
    /// <param name="reportFailure">Receives handled failures and their category.</param>
    public GitHubPublicMarkdownImageStage(
        IMarkdownRemoteImagePolicy markdownRemoteImagePolicy,
        GitHubMarkdownImageFetch fetch,
        GitHubMarkdownCamoImageFallbacks camo,
        Action<Exception, string> reportFailure)
    {
        _markdownRemoteImagePolicy = markdownRemoteImagePolicy;
        _fetch = fetch;
        _camo = camo;
        _reportFailure = reportFailure;
    }

    /// <summary>Resolves an image source that is not a repository image.</summary>
    public async Task<GitHubMarkdownImageCandidate> ResolveAsync(
        string source,
        MarkdownImageResolveContext context,
        IMarkdownImageSourceByteAdmission? admission,
        bool useLeasedSources,
        CancellationToken cancellationToken)
    {
        MarkdownImageSourceDisposition disposition =
            MarkdownImageSourcePolicy.ClassifyUnownedSource(source, out Uri? publicUri);
        if (disposition == MarkdownImageSourceDisposition.NotHandled)
        {
            return GitHubMarkdownImageCandidate.Unowned(MarkdownImageResolution.NotHandled);
        }

        if (disposition == MarkdownImageSourceDisposition.BlockedInsecureRemote)
        {
            return await ResolveInsecureRemoteAsync(
                source, context, admission, useLeasedSources, cancellationToken).ConfigureAwait(false);
        }

        System.Diagnostics.Debug.Assert(publicUri is not null);

        return await ResolvePublicHttpsAsync(
            publicUri!, source, context, admission, useLeasedSources, cancellationToken).ConfigureAwait(false);
    }

    private async Task<GitHubMarkdownImageCandidate> ResolveInsecureRemoteAsync(
        string source,
        MarkdownImageResolveContext context,
        IMarkdownImageSourceByteAdmission? admission,
        bool useLeasedSources,
        CancellationToken cancellationToken)
    {
        // Never fetch plaintext content directly. GitHub's rendered
        // README representation may map the same authored HTTP URL
        // to its trusted HTTPS Camo proxy; use that exact server-
        // supplied mapping when available, otherwise preserve the
        // insecure-content rejection.
        try
        {
            GitHubMarkdownImageCandidate? camoCandidate = await _camo.TryResolveViaGitHubCamoAsync(
                source,
                context,
                admission,
                useLeasedSources,
                cancellationToken).ConfigureAwait(false);
            if (camoCandidate is not null)
                return camoCandidate;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (MarkdownImageSourceDeferredException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _reportFailure(ex, "markdown-image-http-camo-fallback");
        }

        return GitHubMarkdownImageCandidate.Unowned(MarkdownImageResolution.Blocked(
            MarkdownImageUnavailableReason.InsecureRemoteContent));
    }

    private async Task<GitHubMarkdownImageCandidate> ResolvePublicHttpsAsync(
        Uri publicUri,
        string source,
        MarkdownImageResolveContext context,
        IMarkdownImageSourceByteAdmission? admission,
        bool useLeasedSources,
        CancellationToken cancellationToken)
    {
        MarkdownRemoteImageDecision publicDecision = _markdownRemoteImagePolicy.Evaluate(
            publicUri,
            context.AllowThirdPartyRemoteImages);
        if (publicDecision.Access == MarkdownRemoteImageAccess.Block)
        {
            return GitHubMarkdownImageCandidate.Unowned(
                MarkdownImageResolution.Blocked(publicDecision.UnavailableReason));
        }

        try
        {
            GitHubImageFetchScope scope = publicDecision.IsThirdParty
                ? GitHubImageFetchScope.UserApprovedHttps
                : GitHubImageFetchScope.TrustedGitHub;
            if (publicDecision.Access == MarkdownRemoteImageAccess.AllowNetwork &&
                GitHubCamoImageMapParser.TryDecodeCanonicalSource(publicUri, out _))
            {
                return await ResolveCanonicalCamoAsync(
                    publicUri, scope, context, admission, useLeasedSources, cancellationToken).ConfigureAwait(false);
            }

            return await FetchPublicAsync(
                publicUri, source, publicDecision, scope, context, admission, useLeasedSources, cancellationToken)
                .ConfigureAwait(false);
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
            _reportFailure(ex, "markdown-image-public-fetch");
            GitHubMarkdownImageCandidate? fallbackCandidate = await _camo.TryResolvePublicImageFallbacksAsync(
                publicUri,
                source,
                context,
                admission,
                useLeasedSources,
                cancellationToken).ConfigureAwait(false);
            return fallbackCandidate ?? GitHubMarkdownImageCandidate.Unowned(
                MarkdownImageResolution.Unavailable);
        }
    }

    private async Task<GitHubMarkdownImageCandidate> ResolveCanonicalCamoAsync(
        Uri publicUri,
        GitHubImageFetchScope scope,
        MarkdownImageResolveContext context,
        IMarkdownImageSourceByteAdmission? admission,
        bool useLeasedSources,
        CancellationToken cancellationToken)
    {
        // A Camo HTTP request can consume all three 20-second
        // transport attempts before its origin fallback begins.
        // Hedge only canonical Camo URLs, and fetch the decoded
        // origin under the untrusted third-party policy below.
        GitHubMarkdownImageCandidate? camoCandidate = await MarkdownImageFallbackPipeline
            .FirstSuccessfulHedgedAsync(
                token => _fetch.GetMarkdownImageCandidateAsync(
                    publicUri.AbsoluteUri,
                    scope,
                    admission,
                    publicUri,
                    useLeasedSources,
                    cacheOnly: false,
                    token),
                token => _camo.TryResolveViaGitHubCamoOriginAsync(
                    publicUri, context, admission, useLeasedSources, token),
                (attempt, exception) =>
                {
                    if (exception is not MarkdownImageSourceDeferredException)
                        _reportFailure(
                            exception,
                            attempt == 0
                                ? "markdown-image-camo-fetch"
                                : "markdown-image-camo-origin-fallback");
                },
                TimeSpan.FromMilliseconds(750),
                cancellationToken,
                disposeAbandonedResult: static result => result.Dispose(),
                retirementObserver: GitHubMarkdownImageFetch.GetResolverWorkRetirementObserver(admission))
            .ConfigureAwait(false);
        return camoCandidate ?? GitHubMarkdownImageCandidate.Unowned(
            MarkdownImageResolution.Unavailable);
    }

    private async Task<GitHubMarkdownImageCandidate> FetchPublicAsync(
        Uri publicUri,
        string source,
        MarkdownRemoteImageDecision publicDecision,
        GitHubImageFetchScope scope,
        MarkdownImageResolveContext context,
        IMarkdownImageSourceByteAdmission? admission,
        bool useLeasedSources,
        CancellationToken cancellationToken)
    {
        GitHubMarkdownImageCandidate? publicCandidate = await _fetch.GetMarkdownImageCandidateAsync(
                publicUri.ToString(),
                scope,
                admission,
                publicUri,
                useLeasedSources,
                cacheOnly: publicDecision.Access == MarkdownRemoteImageAccess.CacheOnly,
                cancellationToken)
            .ConfigureAwait(false);
        if (publicCandidate is null && publicDecision.Access == MarkdownRemoteImageAccess.CacheOnly)
        {
            return GitHubMarkdownImageCandidate.Unowned(
                MarkdownImageResolution.Blocked(publicDecision.UnavailableReason));
        }

        if (publicCandidate is null && publicDecision.Access == MarkdownRemoteImageAccess.AllowNetwork)
        {
            publicCandidate = await _camo.TryResolvePublicImageFallbacksAsync(
                publicUri,
                source,
                context,
                admission,
                useLeasedSources,
                cancellationToken).ConfigureAwait(false);
        }
        return publicCandidate ?? GitHubMarkdownImageCandidate.Unowned(
            MarkdownImageResolution.Unavailable);
    }
}
