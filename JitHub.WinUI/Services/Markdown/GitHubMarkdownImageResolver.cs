using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using JitHub.Markdown;
using JitHub.Markdown.Images;
using JitHub.WinUI.Helpers;
using MarkdownRenderer.Images;
using MarkdownRenderer.Performance;

namespace JitHub.Services.Markdown;

/// <summary>
/// Resolves Markdown image sources for JitHub: repository-relative and raw GitHub
/// assets, authenticated Contents and Blob reads, Git LFS media, public HTTPS
/// images and GitHub Camo fallbacks. Bytes come from <see cref="IGitHubImageService"/>,
/// which owns the cache and the transport; this class owns the GitHub-specific
/// mapping, the remote-image policy decisions and the fallback order, split
/// into <see cref="GitHubPublicMarkdownImageStage"/> and
/// <see cref="GitHubRepositoryMarkdownImageStage"/>.
/// </summary>
internal sealed class GitHubMarkdownImageResolver :
    IMarkdownImageSourceByteAdmittedResolver,
    IMarkdownImagePrefetcher,
    IGitHubMarkdownImageResolverProvider
{
    private readonly IGitHubImageService _gitHubImageService;
    private readonly IGitHubMarkdownImageResolverProvider _markdownImageResolverProvider;
    private readonly GitHubRenderedReadmeImageMaps _renderedReadmeImageMaps;
    private readonly GitHubPublicMarkdownImageStage _publicImages;
    private readonly GitHubRepositoryMarkdownImageStage _repositoryImages;
    private string? _accessToken;

    public GitHubMarkdownImageResolver(
        IGitHubClientService gitHubClientService,
        IGitHubImageService gitHubImageService,
        IMarkdownRemoteImagePolicy markdownRemoteImagePolicy)
    {
        _gitHubImageService = gitHubImageService;
        _renderedReadmeImageMaps = new GitHubRenderedReadmeImageMaps(gitHubClientService, GetAccessTokenOrThrow);
        var fetch = new GitHubMarkdownImageFetch(gitHubImageService);
        _publicImages = new GitHubPublicMarkdownImageStage(
            markdownRemoteImagePolicy,
            fetch,
            new GitHubMarkdownCamoImageFallbacks(markdownRemoteImagePolicy, _renderedReadmeImageMaps, fetch));
        _repositoryImages = new GitHubRepositoryMarkdownImageStage(
            gitHubClientService,
            markdownRemoteImagePolicy,
            fetch,
            GetAccessTokenOrThrow);
        _markdownImageResolverProvider = new GitHubMarkdownImageResolverProvider(
            this,
            gitHubImageService is IGitHubLeasedImageService,
            CreateLeasedMarkdownImageResolver);
    }

    public IMarkdownImageResolver CreateMarkdownImageResolver() =>
        _markdownImageResolverProvider.CreateMarkdownImageResolver();

    private IMarkdownLeasedImageResolver CreateLeasedMarkdownImageResolver() =>
        new LeasedGitHubMarkdownImageResolver(this, ResolveMarkdownImageLeaseAsync);

    /// <summary>
    /// Sets the account credential used for authenticated repository image reads.
    /// The legacy <c>GitHubService</c> forwards its own token changes here.
    /// </summary>
    /// <param name="token">The GitHub access token, or <see langword="null"/> when signed out.</param>
    public void SetAccessToken(string? token)
    {
        string? normalized = string.IsNullOrWhiteSpace(token) ? null : token;
        if (!string.Equals(_accessToken, normalized, StringComparison.Ordinal))
        {
            // Rendered README HTML can originate from a private repository.
            // Never retain its source-to-Camo map across account boundaries.
            _renderedReadmeImageMaps.Clear();
        }

        _accessToken = normalized;
    }

    private string GetAccessTokenOrThrow()
    {
        return string.IsNullOrWhiteSpace(_accessToken)
            ? throw new InvalidOperationException("GitHub access token is not available.")
            : _accessToken;
    }

    public async ValueTask PrefetchAsync(
        IReadOnlyList<string> sources,
        MarkdownImageResolveContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sources);
        List<string> smallAvatars = [];
        List<string> generalImages = [];
        foreach (string source in sources)
        {
            (GitHubMarkdownImageAssets.IsSmallGitHubAvatar(source) ? smallAvatars : generalImages).Add(source);
        }

        // GitHub contributor grids can contain hundreds of independently cached,
        // explicitly thumbnail-sized avatars. They are safe to multiplex widely over
        // HTTP/2. Unknown and potentially 10 MiB resources retain a conservative lane
        // so hostile documents cannot multiply their byte ceiling by the avatar fanout.
        const int avatarConcurrency = 96;
        const int generalImageConcurrency = 16;
        await Task.WhenAll(
            PrefetchGroupAsync(smallAvatars, avatarConcurrency, context, cancellationToken),
            PrefetchGroupAsync(generalImages, generalImageConcurrency, context, cancellationToken))
            .ConfigureAwait(false);
    }

    private async Task PrefetchGroupAsync(
        IReadOnlyList<string> sources,
        int maximumConcurrency,
        MarkdownImageResolveContext context,
        CancellationToken cancellationToken)
    {
        await Parallel.ForEachAsync(
            sources,
            new ParallelOptions
            {
                CancellationToken = cancellationToken,
                MaxDegreeOfParallelism = maximumConcurrency
            },
            async (source, token) =>
            {
                try
                {
                    _ = await ResolveAsync(source, context, token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    // A transport or account-lifetime cancellation for one
                    // speculative resource must not abandon unrelated image
                    // prefetches. A later viewport-owned resolve gets a fresh
                    // bounded attempt.
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    HandledFailureReporter.Report(
                        exception,
                        "markdown-image-prefetch-item");
                }
            }).ConfigureAwait(false);
    }

    public ValueTask<MarkdownImageResolution> ResolveAsync(
        string source,
        MarkdownImageResolveContext context,
        CancellationToken cancellationToken) =>
        ResolveMarkdownImageCoreAsync(source, context, null, cancellationToken);

    public ValueTask<MarkdownImageResolution> ResolveWithSourceByteAdmissionAsync(
        string source,
        MarkdownImageResolveContext context,
        IMarkdownImageSourceByteAdmission admission,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(admission);
        return ResolveMarkdownImageCoreAsync(source, context, admission, cancellationToken);
    }

    private async ValueTask<MarkdownImageResolution> ResolveMarkdownImageCoreAsync(
        string source,
        MarkdownImageResolveContext context,
        IMarkdownImageSourceByteAdmission? admission,
        CancellationToken cancellationToken)
    {
        using GitHubMarkdownImageCandidate candidate = await ResolveMarkdownImageCandidateCoreAsync(
                source, context, admission, useLeasedSources: false, cancellationToken)
            .ConfigureAwait(false);
        return candidate.Resolution;
    }

    private async ValueTask<MarkdownImageResolutionLease> ResolveMarkdownImageLeaseAsync(
        string source,
        MarkdownImageResolveContext context,
        IMarkdownImageSourceByteAdmission admission,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(admission);
        if (_gitHubImageService is not IGitHubLeasedImageService)
        {
            throw new InvalidOperationException(
                "A source-leased Markdown resolver requires a leased GitHub image service.");
        }

        using GitHubMarkdownImageCandidate candidate = await ResolveMarkdownImageCandidateCoreAsync(
                source, context, admission, useLeasedSources: true, cancellationToken)
            .ConfigureAwait(false);
        return candidate.DetachLease();
    }

    private async ValueTask<GitHubMarkdownImageCandidate> ResolveMarkdownImageCandidateCoreAsync(
        string source,
        MarkdownImageResolveContext context,
        IMarkdownImageSourceByteAdmission? admission,
        bool useLeasedSources,
        CancellationToken cancellationToken)
    {
        GitHubMarkdownImageReference imageReference;
        bool repositoryImageResolved = context.DocumentSource is not null
            ? GitHubMarkdownImageUrlResolver.TryResolve(source, context.DocumentSource, out imageReference)
            : GitHubMarkdownImageUrlResolver.TryResolve(
                source,
                context.BaseUri,
                context.DocumentPath,
                out imageReference);
        if (!repositoryImageResolved)
        {
            return await _publicImages.ResolveAsync(
                source, context, admission, useLeasedSources, cancellationToken).ConfigureAwait(false);
        }

        return await _repositoryImages.ResolveAsync(
            source, imageReference, context, admission, useLeasedSources, cancellationToken).ConfigureAwait(false);
    }
}
