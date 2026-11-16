namespace MarkdownRenderer.Layout.Internal.Snapshots;

internal readonly struct SnapshotScrollAnchor(
    int? BlockIndex,
    double OffsetFromTop,
    double Height)
{
    internal readonly int? BlockIndex = BlockIndex;
    internal readonly double OffsetFromTop = OffsetFromTop;
    internal readonly double Height = Height;
}
