namespace MarkdownRenderer.Layout.Internal.Snapshots;

internal readonly struct SnapshotBlockScrollTarget(
    int BlockIndex,
    double TargetTop,
    double OwnerTop,
    double OwnerBottom,
    double OwnerHeight,
    bool OwnerMeasured,
    long LayoutRevision)
{
    internal readonly int BlockIndex = BlockIndex;
    internal readonly double TargetTop = TargetTop;
    internal readonly double OwnerTop = OwnerTop;
    internal readonly double OwnerBottom = OwnerBottom;
    internal readonly double OwnerHeight = OwnerHeight;
    internal readonly bool OwnerMeasured = OwnerMeasured;
    internal readonly long LayoutRevision = LayoutRevision;
}
