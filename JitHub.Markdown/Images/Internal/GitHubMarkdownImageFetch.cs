using System;
using System.Threading;
using System.Threading.Tasks;
using JitHub.Markdown.Images.Caching;
using MarkdownRenderer.Images;
using MarkdownRenderer.Performance;

namespace JitHub.Markdown.Images.Internal;

/// <summary>
/// Reads Markdown image bytes through <see cref="IGitHubMarkdownImageCache"/>, choosing
/// the admitted, cache-only or leased overload a resolver stage asks for.
/// </summary>
internal sealed class GitHubMarkdownImageFetch
{
    private readonly IGitHubMarkdownImageCache _gitHubImageService;

    /// <param name="gitHubImageService">The image service that owns the cache and transport.</param>
    public GitHubMarkdownImageFetch(IGitHubMarkdownImageCache gitHubImageService)
    {
        _gitHubImageService = gitHubImageService;
    }

    /// <summary>Gets the image service.</summary>
    public IGitHubMarkdownImageCache ImageService => _gitHubImageService;

    /// <summary>Gets the image service as a leased service; throws when it is not one.</summary>
    public IGitHubLeasedImageService LeasedImageService => (IGitHubLeasedImageService)_gitHubImageService;

    public Task<GitHubCachedImage?> GetMarkdownImageAsync(
        string sourceUrl,
        GitHubImageFetchScope scope,
        IMarkdownImageSourceByteAdmission? admission,
        CancellationToken cancellationToken) => admission is null
        ? _gitHubImageService.GetAsync(sourceUrl, scope, cancellationToken)
        : _gitHubImageService.GetAsync(sourceUrl, scope, admission, cancellationToken);

    public Task<GitHubCachedImage?> TryGetCachedMarkdownImageAsync(
        string sourceUrl,
        GitHubImageFetchScope scope,
        IMarkdownImageSourceByteAdmission? admission,
        CancellationToken cancellationToken) => admission is null
        ? _gitHubImageService.TryGetCachedAsync(sourceUrl, scope, cancellationToken)
        : _gitHubImageService.TryGetCachedAsync(sourceUrl, scope, admission, cancellationToken);

    public async Task<GitHubMarkdownImageCandidate?> GetMarkdownImageCandidateAsync(
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

    public static Action<Task>? GetResolverWorkRetirementObserver(
        IMarkdownImageSourceByteAdmission? admission) =>
        admission is IMarkdownImageSourceWorkTracker tracker
            ? tracker.TrackWork
            : null;
}
