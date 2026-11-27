using MarkdownRenderer.Layout.Internal.LazyLayout;
using Xunit;

namespace MarkdownRenderer.Tests.Layout.Internal.LazyLayout;

public class LazyLayoutCommitTests
{
    [Fact]
    public void Merge_SpansBothCommits()
    {
        var earlier = new LazyLayoutCommit(true, 3, OldHeight: 1000, NewHeight: 1200, LayoutRevision: 4);
        var later = new LazyLayoutCommit(true, 2, OldHeight: 1200, NewHeight: 1350, LayoutRevision: 5)
        {
            SceneGraphRebindRequired = true,
        };

        LazyLayoutCommit merged = LazyLayoutCommit.Merge(earlier, later);

        Assert.True(merged.Changed);
        Assert.Equal(5, merged.MeasuredBlocks);
        Assert.Equal(1000, merged.OldHeight);
        Assert.Equal(1350, merged.NewHeight);
        Assert.Equal(5, merged.LayoutRevision);
        Assert.True(merged.SceneGraphRebindRequired);
    }

    [Fact]
    public void Merge_KeepsNativeSceneChangesFromEitherCommit()
    {
        var earlier = new LazyLayoutCommit(false, 0, 10, 10, 1, NativeSceneResourcesChanged: true);
        LazyLayoutCommit later = LazyLayoutCommit.Unchanged(10, 1);

        LazyLayoutCommit merged = LazyLayoutCommit.Merge(earlier, later);

        Assert.True(merged.NativeSceneResourcesChanged);
        Assert.False(merged.Changed);
        Assert.False(merged.SceneGraphRebindRequired);
    }
}
