using System;
using MarkdownRenderer.Controls.Internal.Embeds;
using MarkdownRenderer.Layout.Internal.Boxes;
using MarkdownRenderer.Layout.Internal.Snapshots;
using Windows.Foundation;

namespace MarkdownRenderer.Controls.Internal.Input.HorizontalOverflow;

/// <summary>
/// What one horizontal overflow scroll changed, captured under the snapshot
/// lease: the region's new offset and bounds, and where its inline hosted
/// elements move. Holds the snapshot and region weakly.
/// </summary>
internal readonly struct HorizontalOverflowVisualState(
    WeakReference<LayoutSnapshot> Snapshot,
    long SnapshotGeneration,
    long LayoutRevision,
    int BlockIndex,
    WeakReference<IHorizontalOverflowBox> Target,
    double OldOffset,
    double NewOffset,
    double Extent,
    double Viewport,
    bool IsRightToLeft,
    Rect ViewportBounds,
    Rect TrackBounds,
    InlineEmbedPlacementUpdate[] EmbedPlacements)
{
    public readonly WeakReference<LayoutSnapshot> Snapshot = Snapshot;
    public readonly long SnapshotGeneration = SnapshotGeneration;
    public readonly long LayoutRevision = LayoutRevision;
    public readonly int BlockIndex = BlockIndex;
    public readonly WeakReference<IHorizontalOverflowBox> Target = Target;
    public readonly double OldOffset = OldOffset;
    public readonly double NewOffset = NewOffset;
    public readonly double Extent = Extent;
    public readonly double Viewport = Viewport;
    public readonly bool IsRightToLeft = IsRightToLeft;
    public readonly Rect ViewportBounds = ViewportBounds;
    public readonly Rect TrackBounds = TrackBounds;
    public readonly InlineEmbedPlacementUpdate[] EmbedPlacements = EmbedPlacements;
}
