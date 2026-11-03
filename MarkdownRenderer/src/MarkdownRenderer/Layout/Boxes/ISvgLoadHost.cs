using System;
using System.Threading;
using Windows.Foundation;
using MarkdownRenderer.Images;

namespace MarkdownRenderer.Layout.Boxes;

/// <summary>
/// The image box as seen by its SVG open and render owners. The accepted SVG
/// state, render generation, document uses and UI publication stay with the
/// box; members that take the image-work lock say so.
/// </summary>
internal interface ISvgLoadHost : IImageLoadHost
{
    /// <summary>The box's latest visual-demand epoch.</summary>
    long SvgHostVisualDemandEpoch { get; }

    /// <summary>Takes the image-work lock: starts and returns a new SVG render generation.</summary>
    int AdvanceSvgRenderGeneration();

    /// <summary>
    /// The resolver's security partition, or this box's anonymous local
    /// partition when the resolved asset has no key.
    /// </summary>
    string GetSvgSourceSecurityPartitionKey(string resolvedCacheKey);

    /// <summary>True when <paramref name="rawBytes"/> is the box's accepted SVG source.</summary>
    bool IsKnownSvgSource(byte[] rawBytes, string securityPartitionHash);

    /// <summary>
    /// When <paramref name="rawBytes"/> is the accepted source, returns its
    /// accepted preflight metadata with <paramref name="intrinsicHint"/>.
    /// </summary>
    bool TryGetKnownSvgPreflight(
        byte[] rawBytes,
        string securityPartitionHash,
        Size intrinsicHint,
        out (Size Intrinsic, string? Title, string? Desc, string ContentHash, string SecurityPartitionHash) preflight);

    /// <summary>Publishes an SVG failure for the render generation on the UI thread.</summary>
    void PublishSvgFailure(
        MarkdownSvgException exception, string failureCacheKey, bool cachePermanently, int renderGeneration);

    /// <summary>Publishes an SVG failure for the render generation on the UI thread.</summary>
    void PublishSvgFailure(
        MarkdownSvgFailureReason reason, string description, string failureCacheKey,
        bool cachePermanently, int renderGeneration);

    /// <summary>The theme's body foreground as the SVG current color.</summary>
    MarkdownSvgColor GetSemanticSvgColor();

    /// <summary>The theme's SVG color scheme.</summary>
    MarkdownSvgColorScheme GetSvgColorScheme();

    /// <summary>Publishes an exact content-cache hit; true when it did.</summary>
    bool TryPublishContentCachedSvg(
        string contentCacheKey, string resolvedCacheKey, string? builtInSourceAliasKey,
        bool layoutInvalidated, int renderGeneration, BitmapOutputWorkContext bitmapOutputWork);

    /// <summary>Takes the image-work lock: reserves a candidate use of an opened document.</summary>
    SvgDocumentUseLease? TryAcquireCandidateSvgDocumentUse(
        IMarkdownSvgDocument document, out bool ownershipConflict);

    /// <summary>True while the renderer and its cache generation are still current.</summary>
    bool IsSvgProviderCurrent(IMarkdownSvgRenderer renderer, long providerCacheGeneration, bool hasText);

    /// <summary>Asks the box to reload after a provider generation change.</summary>
    void PublishSvgGenerationInvalidated(int renderGeneration);

    /// <summary>The raster size, tiling decision and physical viewport for an intrinsic size.</summary>
    (int Width, int Height, bool UsesTiles, MarkdownSvgPhysicalViewport PhysicalViewport) PickRasterDimensions(
        Size intrinsic);

    /// <summary>The device bitmap cache identity of one SVG raster.</summary>
    string BuildSvgBitmapIdentity(
        string contentHash, string securityPartitionHash, MarkdownSvgDocumentInfo info, int width, int height,
        MarkdownSvgColorScheme colorScheme, MarkdownSvgColor semanticColor, long rendererIdentity,
        long providerCacheGeneration, MarkdownSvgPhysicalViewport physicalViewport);

    /// <summary>Publishes the outcome of an SVG open or render failure.</summary>
    void HandleSvgWorkFailure(
        Exception exception, CancellationToken cancellationToken, CancellationToken sessionLifetimeToken,
        string resolvedCacheKey, string svgFailureCacheKey, int renderGeneration);

    /// <summary>Takes the image-work lock: retires an opened document the box never accepted.</summary>
    void RetireUncommittedSvgDocument(IMarkdownSvgDocument document);
}
