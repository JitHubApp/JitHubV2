namespace MarkdownRenderer.Layout.Boxes;

/// <summary>
/// One source/prepared image pair in a measured-placement transfer batch,
/// with the identities and widths the commit must still find.
/// </summary>
internal readonly struct ImageMeasuredPlacementTransfer
{
    /// <summary>The most image pairs one transfer batch may commit.</summary>
    internal const int MaximumBatchCount = 32;

    internal ImageBox Source { get; init; }
    internal ImageBox Prepared { get; init; }
    internal SharedCanvasBitmapCache.Lease ExpectedSourceLease { get; init; }
    internal CompletedSvgPlacementMetadataFence? ExpectedSvgMetadataFence { get; init; }
    internal bool ExpectedInlineLayout { get; init; }
    internal float ExpectedAvailableWidth { get; init; }
    internal float ExpectedPreparedAvailableWidth { get; init; }
    internal NativeLayoutPlacementFence ExpectedSourceNativeLayouts { get; init; }
    internal NativeLayoutPlacementFence? ExpectedPreparedNativeLayouts { get; init; }

    internal ImageMeasuredPlacementTransfer(
        ImageBox Source,
        ImageBox Prepared,
        SharedCanvasBitmapCache.Lease ExpectedSourceLease,
        CompletedSvgPlacementMetadataFence? ExpectedSvgMetadataFence,
        bool ExpectedInlineLayout = false,
        float ExpectedAvailableWidth = 0f,
        float ExpectedPreparedAvailableWidth = 0f)
    {
        this.Source = Source;
        this.Prepared = Prepared;
        this.ExpectedSourceLease = ExpectedSourceLease;
        this.ExpectedSvgMetadataFence = ExpectedSvgMetadataFence;
        this.ExpectedInlineLayout = ExpectedInlineLayout;
        this.ExpectedAvailableWidth = ExpectedAvailableWidth;
        this.ExpectedPreparedAvailableWidth = ExpectedPreparedAvailableWidth;
        ExpectedSourceNativeLayouts = Source is not null &&
            Source.TryCaptureNativeLayoutPlacementFence(out var fence)
            ? fence
            : default;
        ExpectedPreparedNativeLayouts = Prepared is not null &&
            Prepared.TryCaptureNativeLayoutPlacementFence(out var preparedFence)
            ? preparedFence
            : null;
    }
}
