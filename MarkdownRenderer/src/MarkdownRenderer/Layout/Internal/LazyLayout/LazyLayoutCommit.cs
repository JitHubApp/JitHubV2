namespace MarkdownRenderer.Layout.Internal.LazyLayout;

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

    /// <summary>
    /// Combines two consecutive commits of one snapshot into the commit a
    /// single pass over both bands would have produced.
    /// </summary>
    internal static LazyLayoutCommit Merge(LazyLayoutCommit earlier, LazyLayoutCommit later)
        => new(
            earlier.Changed || later.Changed,
            earlier.MeasuredBlocks + later.MeasuredBlocks,
            earlier.OldHeight,
            later.NewHeight,
            later.LayoutRevision,
            earlier.NativeSceneResourcesChanged || later.NativeSceneResourcesChanged)
        {
            // The residency policy reports its current graph state at the end
            // of each pass, so the later pass is authoritative.
            SceneGraphRebindRequired = later.SceneGraphRebindRequired,
        };
}
