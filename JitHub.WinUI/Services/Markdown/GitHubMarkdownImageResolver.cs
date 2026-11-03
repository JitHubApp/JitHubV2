using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using JitHub.WinUI.Helpers;
using MarkdownRenderer.Images;
using MarkdownRenderer.Performance;
using RestGitHubBlob = JitHub.Models.GitHub.GitHubBlob;
using RestGitHubRepositoryContent = JitHub.Models.GitHub.GitHubRepositoryContent;

namespace JitHub.Services.Markdown;

/// <summary>
/// Resolves Markdown image sources for JitHub: repository-relative and raw GitHub
/// assets, authenticated Contents and Blob reads, Git LFS media, public HTTPS
/// images and GitHub Camo fallbacks. Bytes come from <see cref="IGitHubImageService"/>,
/// which owns the cache and the transport; this class owns the GitHub-specific
/// mapping, the remote-image policy decisions and the fallback order.
/// </summary>
internal sealed class GitHubMarkdownImageResolver :
    IMarkdownImageSourceByteAdmittedResolver,
    IMarkdownImagePrefetcher,
    IGitHubMarkdownImageResolverProvider
{
    private readonly IGitHubClientService _gitHubClientService;
    private readonly IGitHubImageService _gitHubImageService;
    private readonly IMarkdownRemoteImagePolicy _markdownRemoteImagePolicy;
    private readonly IGitHubMarkdownImageResolverProvider _markdownImageResolverProvider;
    private readonly GitHubRenderedReadmeImageMaps _renderedReadmeImageMaps;
    private string? _accessToken;

    public GitHubMarkdownImageResolver(
        IGitHubClientService gitHubClientService,
        IGitHubImageService gitHubImageService,
        IMarkdownRemoteImagePolicy markdownRemoteImagePolicy)
    {
        _gitHubClientService = gitHubClientService;
        _gitHubImageService = gitHubImageService;
        _markdownRemoteImagePolicy = markdownRemoteImagePolicy;
        _renderedReadmeImageMaps = new GitHubRenderedReadmeImageMaps(gitHubClientService, GetAccessTokenOrThrow);
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
            MarkdownImageSourceDisposition disposition =
                MarkdownImageSourcePolicy.ClassifyUnownedSource(source, out Uri? publicUri);
            if (disposition == MarkdownImageSourceDisposition.NotHandled)
            {
                return GitHubMarkdownImageCandidate.Unowned(MarkdownImageResolution.NotHandled);
            }

            if (disposition == MarkdownImageSourceDisposition.BlockedInsecureRemote)
            {
                // Never fetch plaintext content directly. GitHub's rendered
                // README representation may map the same authored HTTP URL
                // to its trusted HTTPS Camo proxy; use that exact server-
                // supplied mapping when available, otherwise preserve the
                // insecure-content rejection.
                try
                {
                    GitHubMarkdownImageCandidate? camoCandidate = await TryResolveViaGitHubCamoAsync(
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
                    HandledFailureReporter.Report(ex, "markdown-image-http-camo-fallback");
                }

                return GitHubMarkdownImageCandidate.Unowned(MarkdownImageResolution.Blocked(
                    MarkdownImageUnavailableReason.InsecureRemoteContent));
            }

            System.Diagnostics.Debug.Assert(publicUri is not null);

            MarkdownRemoteImageDecision publicDecision = _markdownRemoteImagePolicy.Evaluate(
                publicUri!,
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
                    GitHubCamoImageMapParser.TryDecodeCanonicalSource(publicUri!, out _))
                {
                    // A Camo HTTP request can consume all three 20-second
                    // transport attempts before its origin fallback begins.
                    // Hedge only canonical Camo URLs, and fetch the decoded
                    // origin under the untrusted third-party policy below.
                    GitHubMarkdownImageCandidate? camoCandidate = await MarkdownImageFallbackPipeline
                        .FirstSuccessfulHedgedAsync(
                            token => GetMarkdownImageCandidateAsync(
                                publicUri!.AbsoluteUri,
                                scope,
                                admission,
                                publicUri!,
                                useLeasedSources,
                                cacheOnly: false,
                                token),
                            token => TryResolveViaGitHubCamoOriginAsync(
                                publicUri!, context, admission, useLeasedSources, token),
                            static (attempt, exception) =>
                            {
                                if (exception is not MarkdownImageSourceDeferredException)
                                    HandledFailureReporter.Report(
                                        exception,
                                        attempt == 0
                                            ? "markdown-image-camo-fetch"
                                            : "markdown-image-camo-origin-fallback");
                            },
                            TimeSpan.FromMilliseconds(750),
                            cancellationToken,
                            disposeAbandonedResult: static result => result.Dispose(),
                            retirementObserver: GetResolverWorkRetirementObserver(admission))
                        .ConfigureAwait(false);
                    return camoCandidate ?? GitHubMarkdownImageCandidate.Unowned(
                        MarkdownImageResolution.Unavailable);
                }

                GitHubMarkdownImageCandidate? publicCandidate = await GetMarkdownImageCandidateAsync(
                        publicUri!.ToString(),
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
                    publicCandidate = await TryResolvePublicImageFallbacksAsync(
                        publicUri!,
                        source,
                        context,
                        admission,
                        useLeasedSources,
                        cancellationToken).ConfigureAwait(false);
                }
                return publicCandidate ?? GitHubMarkdownImageCandidate.Unowned(
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
                HandledFailureReporter.Report(ex, "markdown-image-public-fetch");
                GitHubMarkdownImageCandidate? fallbackCandidate = await TryResolvePublicImageFallbacksAsync(
                    publicUri!,
                    source,
                    context,
                    admission,
                    useLeasedSources,
                    cancellationToken).ConfigureAwait(false);
                return fallbackCandidate ?? GitHubMarkdownImageCandidate.Unowned(
                    MarkdownImageResolution.Unavailable);
            }
        }

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
                // Public repository media is already served by GitHub's raw
                // CDN. Fetching every badge through the authenticated
                // Contents API first exhausts its shared rate budget on
                // image-heavy READMEs. This request carries no account
                // credential; private/LFS/missing assets still fall back
                // to the authenticated repository path below.
                GitHubMarkdownImageCandidate? rawCandidate;
                if (useLeasedSources)
                {
                    GitHubImageSourceLease? rawLease = await GitHubRepositoryImageFetchPipeline
                        .TryGetRawLeasedAsync(
                            (IGitHubLeasedImageService)_gitHubImageService,
                            rawUri,
                            static exception => HandledFailureReporter.Report(
                                exception, "markdown-image-raw-cdn-fallback"),
                            admission!,
                            cancellationToken).ConfigureAwait(false);
                    rawCandidate = await GitHubMarkdownImageAssets.ReadCachedMarkdownImageAsync(
                        rawLease, rawUri, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    GitHubCachedImage? rawImage = await GitHubRepositoryImageFetchPipeline.TryGetRawAsync(
                        _gitHubImageService,
                        rawUri,
                        static exception => HandledFailureReporter.Report(
                            exception, "markdown-image-raw-cdn-fallback"),
                        admission,
                        cancellationToken).ConfigureAwait(false);
                    rawCandidate = await GitHubMarkdownImageAssets.ReadCachedMarkdownImageAsync(
                        rawImage, rawUri, cancellationToken).ConfigureAwait(false);
                }

                if (rawCandidate is not null)
                    return rawCandidate;
            }

            GitHubMarkdownImageCandidate? repositoryCandidate;
            if (repositoryDecision.Access == MarkdownRemoteImageAccess.CacheOnly)
            {
                repositoryCandidate = await GetMarkdownImageCandidateAsync(
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
                try
                {
                    GitHubImageFetcher fetcher = async (_, token) =>
                    {
                        string accessToken = GetAccessTokenOrThrow();

                        // The production client streams raw authenticated Contents
                        // bytes under its exact/staged source reservation. This branch
                        // must precede the legacy worst-case reservation: taking the
                        // full image ceiling here would leave no room for the reader's
                        // own reservation and can deadlock a constrained session.
                        if (useLeasedSources &&
                            _gitHubClientService is IGitHubRepositoryImageClient repositoryImageClient)
                        {
                            GitHubImageDownload? rawDownload = await repositoryImageClient
                                .GetRepositoryImageContentAsync(
                                    accessToken,
                                    imageReference.Owner,
                                    imageReference.Repository,
                                    imageReference.Path,
                                    imageReference.Ref,
                                    admission!,
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
                                        .CreateGitHubRawRouteUri(imageReference);
                                    return await GitHubLeasedImageDownloadTransfer.ResolveLfsDownloadAsync(
                                        pointerReservation,
                                        () => ((IGitHubLeasedImageService)_gitHubImageService).GetLeasedAsync(
                                            lfsRoute.AbsoluteUri,
                                            GitHubImageFetchScope.TrustedGitHub,
                                            admission!,
                                            token)).ConfigureAwait(false);
                                }

                                GitHubImageDownload ownedDownload = rawDownload with
                                {
                                    ContentType = GitHubMarkdownImageAssets.GuessImageContentType(imageReference.Path),
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

                        // Compatibility path for custom/older clients that only
                        // implement JSON Contents/Blob APIs. Their client may have
                        // already materialized base64 text before this reservation;
                        // it is intentionally outside the strict raw-source bound.
                        // Contents and Blob API payloads are materialized by
                        // the authenticated client before decoding. Reserve
                        // the hard image ceiling before that work and keep
                        // the lease through cache storage. An LFS pointer
                        // releases it before the recursive media request.
                        IDisposable? sourceLease = admission is null
                            ? null
                            : await admission.ReserveAsync(
                                GitHubImageService.MaxImageBytes, token).ConfigureAwait(false);
                        try
                        {
                            RestGitHubRepositoryContent file = await _gitHubClientService.GetRepositoryContentAsync(
                                accessToken,
                                imageReference.Owner,
                                imageReference.Repository,
                                imageReference.Path,
                                imageReference.Ref,
                                token).ConfigureAwait(false);
                            byte[] fileBytes = GitHubMarkdownImageAssets.DecodeGitHubContent(file.Content, file.Encoding);

                            if (fileBytes.Length == 0 && !string.IsNullOrWhiteSpace(file.Sha))
                            {
                                RestGitHubBlob blob = await _gitHubClientService.GetBlobAsync(
                                    accessToken,
                                    imageReference.Owner,
                                    imageReference.Repository,
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
                                    .CreateGitHubRawRouteUri(imageReference);
                                if (useLeasedSources)
                                {
                                    IDisposable? pointerReservation = sourceLease;
                                    sourceLease = null;
                                    return await GitHubLeasedImageDownloadTransfer.ResolveLfsDownloadAsync(
                                        pointerReservation,
                                        () => ((IGitHubLeasedImageService)_gitHubImageService).GetLeasedAsync(
                                            lfsRoute.AbsoluteUri,
                                            GitHubImageFetchScope.TrustedGitHub,
                                            admission!,
                                            token)).ConfigureAwait(false);
                                }

                                sourceLease?.Dispose();
                                sourceLease = null;
                                GitHubCachedImage? lfsImage = await GetMarkdownImageAsync(
                                    lfsRoute.AbsoluteUri,
                                    GitHubImageFetchScope.TrustedGitHub,
                                    admission,
                                    token).ConfigureAwait(false);
                                return lfsImage?.Bytes is { Length: > 0 } lfsBytes
                                    ? new GitHubImageDownload(lfsBytes, lfsImage.ContentType)
                                    : null;
                            }

                            if (fileBytes.Length == 0)
                                return null;
                            GitHubImageDownload download = new(
                                fileBytes,
                                GitHubMarkdownImageAssets.GuessImageContentType(imageReference.Path),
                                SourceByteLease: sourceLease);
                            sourceLease = null;
                            return download;
                        }
                        finally
                        {
                            sourceLease?.Dispose();
                        }
                    };
                    if (useLeasedSources)
                    {
                        GitHubImageSourceLease? resolvedLease = await ((IGitHubLeasedImageService)_gitHubImageService)
                            .GetOrFetchLeasedAsync(
                                rawUri.ToString(), fetcher, admission!, cancellationToken)
                            .ConfigureAwait(false);
                        repositoryCandidate = await GitHubMarkdownImageAssets.ReadCachedMarkdownImageAsync(
                            resolvedLease, rawUri, cancellationToken).ConfigureAwait(false);
                    }
                    else
                    {
                        GitHubCachedImage? cachedImage = admission is null
                            ? await _gitHubImageService.GetOrFetchAsync(
                                rawUri.ToString(), fetcher, cancellationToken).ConfigureAwait(false)
                            : await _gitHubImageService.GetOrFetchAsync(
                                rawUri.ToString(), fetcher, admission, cancellationToken).ConfigureAwait(false);
                        repositoryCandidate = await GitHubMarkdownImageAssets.ReadCachedMarkdownImageAsync(
                            cachedImage, rawUri, cancellationToken).ConfigureAwait(false);
                    }
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
                    repositoryCandidate = await GetMarkdownImageCandidateAsync(
                        directRawUri.ToString(),
                        GitHubImageFetchScope.TrustedGitHub,
                        admission,
                        rawUri,
                        useLeasedSources,
                        cacheOnly: false,
                        cancellationToken).ConfigureAwait(false);
                }
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
            HandledFailureReporter.Report(ex, "markdown-image-github-fetch");
            return GitHubMarkdownImageCandidate.Unowned(MarkdownImageResolution.Unavailable);
        }
    }

    private Task<GitHubCachedImage?> GetMarkdownImageAsync(
        string sourceUrl,
        GitHubImageFetchScope scope,
        IMarkdownImageSourceByteAdmission? admission,
        CancellationToken cancellationToken) => admission is null
        ? _gitHubImageService.GetAsync(sourceUrl, scope, cancellationToken)
        : _gitHubImageService.GetAsync(sourceUrl, scope, admission, cancellationToken);

    private Task<GitHubCachedImage?> TryGetCachedMarkdownImageAsync(
        string sourceUrl,
        GitHubImageFetchScope scope,
        IMarkdownImageSourceByteAdmission? admission,
        CancellationToken cancellationToken) => admission is null
        ? _gitHubImageService.TryGetCachedAsync(sourceUrl, scope, cancellationToken)
        : _gitHubImageService.TryGetCachedAsync(sourceUrl, scope, admission, cancellationToken);

    private async Task<GitHubMarkdownImageCandidate?> GetMarkdownImageCandidateAsync(
        string sourceUrl,
        GitHubImageFetchScope scope,
        IMarkdownImageSourceByteAdmission? admission,
        Uri resolvedUri,
        bool useLeasedSources,
        bool cacheOnly,
        CancellationToken cancellationToken)
    {
        if (useLeasedSources)
        {
            if (admission is null || _gitHubImageService is not IGitHubLeasedImageService leased)
            {
                throw new InvalidOperationException(
                    "A leased GitHub image request requires source admission and a leased image service.");
            }

            GitHubImageSourceLease? sourceLease = cacheOnly
                ? await leased.TryGetCachedLeasedAsync(
                    sourceUrl, scope, admission, cancellationToken).ConfigureAwait(false)
                : await leased.GetLeasedAsync(
                    sourceUrl, scope, admission, cancellationToken).ConfigureAwait(false);
            return await GitHubMarkdownImageAssets.ReadCachedMarkdownImageAsync(
                sourceLease, resolvedUri, cancellationToken).ConfigureAwait(false);
        }

        GitHubCachedImage? cachedImage = cacheOnly
            ? await TryGetCachedMarkdownImageAsync(
                sourceUrl, scope, admission, cancellationToken).ConfigureAwait(false)
            : await GetMarkdownImageAsync(
                sourceUrl, scope, admission, cancellationToken).ConfigureAwait(false);
        return await GitHubMarkdownImageAssets.ReadCachedMarkdownImageAsync(
            cachedImage, resolvedUri, cancellationToken).ConfigureAwait(false);
    }

    private async Task<GitHubMarkdownImageCandidate?> TryResolveViaGitHubCamoAsync(
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

        return await GetMarkdownImageCandidateAsync(
            camoUri.AbsoluteUri,
            GitHubImageFetchScope.TrustedGitHub,
            admission,
            camoUri,
            useLeasedSources,
            cacheOnly: false,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<GitHubMarkdownImageCandidate?> TryResolvePublicImageFallbacksAsync(
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
            retirementObserver: GetResolverWorkRetirementObserver(admission)).ConfigureAwait(false);
    }

    private static Action<Task>? GetResolverWorkRetirementObserver(
        IMarkdownImageSourceByteAdmission? admission) =>
        admission is IMarkdownImageSourceWorkTracker tracker
            ? tracker.TrackWork
            : null;

    private async Task<GitHubMarkdownImageCandidate?> TryResolveViaGitHubCamoOriginAsync(
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
        return await GetMarkdownImageCandidateAsync(
            originUri.AbsoluteUri,
            GitHubImageFetchScope.UserApprovedHttps,
            admission,
            originUri,
            useLeasedSources,
            cacheOnly: decision.Access == MarkdownRemoteImageAccess.CacheOnly,
            cancellationToken).ConfigureAwait(false);
    }
}
