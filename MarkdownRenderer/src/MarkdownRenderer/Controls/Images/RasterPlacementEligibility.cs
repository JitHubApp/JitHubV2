using MarkdownRenderer.Layout.Internal.Inlines;
using MarkdownRenderer.Layout.Internal.Snapshots;

namespace MarkdownRenderer.Controls.Images;

/// <summary>
/// When a completed image relayout may build a private raster placement
/// candidate instead of reflowing the published tree in place.
/// </summary>
internal static class RasterPlacementEligibility
{
    /// <summary>
    /// True for an opted-in performance session, a document with at most
    /// <see cref="LayoutSnapshot.MaximumPrivateRasterPlacementRoots"/> top-level
    /// blocks, no hosted elements, and between one and
    /// <see cref="InlineContainerBox.MaximumPrivateRasterPlacementImages"/>
    /// image plans. Code block copy buttons do not prevent it.
    /// </summary>
    internal static bool Allows(bool optedIn, int topLevelBlockCount, int hostedElementPlanCount, int imagePlanCount) =>
        optedIn &&
        topLevelBlockCount is > 0 and <= LayoutSnapshot.MaximumPrivateRasterPlacementRoots &&
        hostedElementPlanCount == 0 &&
        imagePlanCount is > 0 and <= InlineContainerBox.MaximumPrivateRasterPlacementImages;
}
