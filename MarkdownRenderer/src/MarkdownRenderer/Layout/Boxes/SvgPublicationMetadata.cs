using System;
using Windows.Foundation;
using MarkdownRenderer.Images;

namespace MarkdownRenderer.Layout.Boxes;

/// <summary>
/// The source, provider and render identity a completed SVG render must still
/// match before an image box publishes its bitmap and metadata.
/// </summary>
internal sealed class SvgPublicationMetadata(
    WeakReference<byte[]> SourceIdentity,
    long SourceByteLength,
    IMarkdownSvgRenderer Renderer,
    long RendererIdentity,
    long ProviderCacheGeneration,
    int RenderGeneration,
    bool MetadataWasKnown,
    string ResolvedCacheKey,
    string SvgFailureCacheKey,
    Size Intrinsic,
    string? Title,
    string? Description,
    string ContentHash,
    string SecurityPartitionHash,
    MarkdownSvgDocumentInfo Info,
    int TargetWidthPixels,
    int TargetHeightPixels,
    string BitmapIdentity,
    long WorkId,
    long PublishRequestedAt,
    long TargetPixels,
    MarkdownSvgRenderDemand? Demand = null,
    IMarkdownSvgAdmission? HostAdmission = null,
    long SourceReservationBytes = 0,
    SvgHostOwnerSlot? OwnerSlot = null,
    MarkdownSvgPhysicalViewport? PhysicalViewport = null)
{
    internal readonly WeakReference<byte[]> SourceIdentity = SourceIdentity;
    internal readonly long SourceByteLength = SourceByteLength;
    internal readonly IMarkdownSvgRenderer Renderer = Renderer;
    internal readonly long RendererIdentity = RendererIdentity;
    internal readonly long ProviderCacheGeneration = ProviderCacheGeneration;
    internal readonly int RenderGeneration = RenderGeneration;
    internal readonly bool MetadataWasKnown = MetadataWasKnown;
    internal readonly string ResolvedCacheKey = ResolvedCacheKey;
    internal readonly string SvgFailureCacheKey = SvgFailureCacheKey;
    internal readonly Size Intrinsic = Intrinsic;
    internal readonly string? Title = Title;
    internal readonly string? Description = Description;
    internal readonly string ContentHash = ContentHash;
    internal readonly string SecurityPartitionHash = SecurityPartitionHash;
    internal readonly MarkdownSvgDocumentInfo Info = Info;
    internal readonly int TargetWidthPixels = TargetWidthPixels;
    internal readonly int TargetHeightPixels = TargetHeightPixels;
    internal readonly string BitmapIdentity = BitmapIdentity;
    internal readonly long WorkId = WorkId;
    internal readonly long PublishRequestedAt = PublishRequestedAt;
    internal readonly long TargetPixels = TargetPixels;
    internal readonly MarkdownSvgRenderDemand? Demand = Demand;
    internal readonly IMarkdownSvgAdmission? HostAdmission = HostAdmission;
    internal readonly long SourceReservationBytes = SourceReservationBytes;
    internal readonly SvgHostOwnerSlot? OwnerSlot = OwnerSlot;
    internal readonly MarkdownSvgPhysicalViewport? PhysicalViewport = PhysicalViewport;
}
