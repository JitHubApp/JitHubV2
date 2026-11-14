using System;
using MarkdownRenderer.Images.Svg;
using Windows.Foundation;

namespace MarkdownRenderer.Layout.Internal.Images.Placement;

/// <summary>
/// Immutable accepted-SVG state used only to validate a private bitmap
/// placement. The source identity is weak; this value never owns source bytes,
/// a provider/document, tile plan, work owner, or cancellation state.
/// </summary>
internal readonly struct CompletedSvgPlacementMetadataFence
{
    internal CompletedSvgPlacementMetadataFence(
        WeakReference<byte[]> sourceIdentity,
        long sourceByteLength,
        string? anonymousPartitionKey,
        string? preResolutionCacheKey,
        Size intrinsicSize,
        string? title,
        string? description,
        string contentHash,
        string securityPartitionHash,
        MarkdownSvgDocumentInfo info,
        string? activeCacheKey,
        long rendererIdentity,
        long providerCacheGeneration,
        int renderGeneration,
        int loadDemandGeneration,
        int bitmapWidthPixels,
        int bitmapHeightPixels,
        MarkdownSvgPhysicalViewport? physicalViewport,
        MarkdownSvgPhysicalViewport? bitmapPhysicalViewport)
    {
        SourceIdentity = sourceIdentity;
        SourceByteLength = sourceByteLength;
        AnonymousPartitionKey = anonymousPartitionKey;
        PreResolutionCacheKey = preResolutionCacheKey;
        IntrinsicSize = intrinsicSize;
        Title = title;
        Description = description;
        ContentHash = contentHash;
        SecurityPartitionHash = securityPartitionHash;
        Info = info;
        ActiveCacheKey = activeCacheKey;
        RendererIdentity = rendererIdentity;
        ProviderCacheGeneration = providerCacheGeneration;
        RenderGeneration = renderGeneration;
        LoadDemandGeneration = loadDemandGeneration;
        BitmapWidthPixels = bitmapWidthPixels;
        BitmapHeightPixels = bitmapHeightPixels;
        PhysicalViewport = physicalViewport;
        BitmapPhysicalViewport = bitmapPhysicalViewport;
    }

    internal WeakReference<byte[]> SourceIdentity { get; }
    internal long SourceByteLength { get; }
    internal string? AnonymousPartitionKey { get; }
    internal string? PreResolutionCacheKey { get; }
    internal Size IntrinsicSize { get; }
    internal string? Title { get; }
    internal string? Description { get; }
    internal string ContentHash { get; }
    internal string SecurityPartitionHash { get; }
    internal MarkdownSvgDocumentInfo Info { get; }
    internal string? ActiveCacheKey { get; }
    internal long RendererIdentity { get; }
    internal long ProviderCacheGeneration { get; }
    internal int RenderGeneration { get; }
    internal int LoadDemandGeneration { get; }
    internal int BitmapWidthPixels { get; }
    internal int BitmapHeightPixels { get; }
    internal MarkdownSvgPhysicalViewport? PhysicalViewport { get; }
    internal MarkdownSvgPhysicalViewport? BitmapPhysicalViewport { get; }
}
