namespace MarkdownRenderer.Layout;

internal readonly struct LazyLayoutCommit(
    bool Changed,
    int MeasuredBlocks,
    double OldHeight,
    double NewHeight,
    long LayoutRevision,
    bool NativeSceneResourcesChanged = false)
{
    internal readonly bool Changed = Changed;
    internal readonly int MeasuredBlocks = MeasuredBlocks;
    internal readonly double OldHeight = OldHeight;
    internal readonly double NewHeight = NewHeight;
    internal readonly long LayoutRevision = LayoutRevision;
    internal bool NativeSceneResourcesChanged { get; init; } = NativeSceneResourcesChanged;
    internal bool SceneGraphRebindRequired { get; init; }

    public static LazyLayoutCommit Unchanged(double height, long layoutRevision)
        => new(false, 0, height, height, layoutRevision);
}
