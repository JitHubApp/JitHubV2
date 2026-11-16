using Windows.Foundation;
using MarkdownRenderer.Layout.Internal.Images;
using MarkdownRenderer.Layout.Internal.Placement;
using MarkdownRenderer.Layout.Internal.Snapshots;

namespace MarkdownRenderer.Controls.Internal.Images;

/// <summary>
/// Immutable membership/index publication for automation readiness. ImageBox
/// objects are borrowed from the stated snapshot and read only while that
/// snapshot's interaction lease is held.
/// </summary>
internal sealed class ImagePlanPublication(
    LayoutSnapshot Snapshot,
    long SnapshotGeneration,
    long LayoutRevision,
    ImageBox[] Images,
    Rect[] ImageBounds,
    ViewportBandIndex? Index)
{
    internal readonly LayoutSnapshot Snapshot = Snapshot;
    internal readonly long SnapshotGeneration = SnapshotGeneration;
    internal readonly long LayoutRevision = LayoutRevision;
    internal readonly ImageBox[] Images = Images;
    internal readonly Rect[] ImageBounds = ImageBounds;
    internal readonly ViewportBandIndex? Index = Index;
}
