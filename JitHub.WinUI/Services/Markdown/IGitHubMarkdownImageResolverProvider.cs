using System;
using System.Threading;
using MarkdownRenderer.Images;
using MarkdownRenderer.Performance;

namespace JitHub.Services.Markdown;

/// <summary>
/// Lets a GitHub service expose only the image-resolver capabilities its configured
/// image source can actually honor.
/// </summary>
internal interface IGitHubMarkdownImageResolverProvider
{
    IMarkdownImageResolver CreateMarkdownImageResolver();
}

internal sealed class GitHubMarkdownImageResolverProvider : IGitHubMarkdownImageResolverProvider
{
    private readonly IMarkdownImageResolver _legacyResolver;
    private readonly Lazy<IMarkdownLeasedImageResolver>? _leasedResolver;

    internal GitHubMarkdownImageResolverProvider(
        IMarkdownImageResolver legacyResolver,
        bool supportsSourceLeases,
        Func<IMarkdownLeasedImageResolver> leasedResolverFactory)
    {
        _legacyResolver = legacyResolver ?? throw new ArgumentNullException(nameof(legacyResolver));
        ArgumentNullException.ThrowIfNull(leasedResolverFactory);
        // Resolver reference identity partitions the shared performance-session
        // source cache/flights. Reuse one facade across views just as the legacy
        // singleton was reused; a new facade per view would defeat deduplication.
        _leasedResolver = supportsSourceLeases
            ? new Lazy<IMarkdownLeasedImageResolver>(
                leasedResolverFactory, LazyThreadSafetyMode.ExecutionAndPublication)
            : null;
    }

    public IMarkdownImageResolver CreateMarkdownImageResolver() =>
        _leasedResolver?.Value ?? _legacyResolver;
}
