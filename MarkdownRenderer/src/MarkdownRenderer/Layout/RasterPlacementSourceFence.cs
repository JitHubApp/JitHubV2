using MarkdownRenderer.Layout.Boxes;

namespace MarkdownRenderer.Layout;

// Borrowed only during descriptor capture. The candidate converts these to weak
// identities before it can be retained for UI publication.
internal readonly struct RasterPlacementSourceFence(
    ImageBox Image, SharedCanvasBitmapCache.Lease? Lease,
    CompletedSvgPlacementMetadataFence? SvgMetadata = null,
    bool IsIdleDescriptor = false,
    int IdleLoadDemandGeneration = 0,
    bool WasSubscribed = true)
{
    public ImageBox Image { get; init; } = Image;
    public SharedCanvasBitmapCache.Lease? Lease { get; init; } = Lease;
    public CompletedSvgPlacementMetadataFence? SvgMetadata { get; init; } = SvgMetadata;
    public bool IsIdleDescriptor { get; init; } = IsIdleDescriptor;
    public int IdleLoadDemandGeneration { get; init; } = IdleLoadDemandGeneration;
    public bool WasSubscribed { get; init; } = WasSubscribed;
}
