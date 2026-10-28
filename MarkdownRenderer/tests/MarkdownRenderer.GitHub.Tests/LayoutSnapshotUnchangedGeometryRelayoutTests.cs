using System.Reflection;
using MarkdownRenderer.Document;
using MarkdownRenderer.Layout;
using MarkdownRenderer.Layout.Boxes;
using Microsoft.Graphics.Canvas;
using Microsoft.UI.Xaml;
using Windows.Foundation;
using Xunit;

namespace MarkdownRenderer.GitHub.Tests;

public sealed class LayoutSnapshotUnchangedGeometryRelayoutTests
{
    [Fact]
    public void SameHeightResourceCompletionPreservesTrailingPlacementsAndIndex()
    {
        var first = new MeasuredBlock(24) { BlockIndex = 0 };
        var changed = new MeasuredBlock(40) { BlockIndex = 1 };
        var trailing = new MeasuredBlock(30) { BlockIndex = 2 };
        using LayoutSnapshot snapshot = CreateSnapshot([first, changed, trailing]);
        Rect firstBounds = first.Bounds;
        Rect changedBounds = changed.Bounds;
        Rect trailingBounds = trailing.Bounds;
        Size oldSize = snapshot.Size;
        long oldRevision = snapshot.LayoutRevision;

        changed.SetHeight(40); // New resource/layout state, identical block height.
        snapshot.RelayoutChangedBlocks([changed.BlockIndex], 200, CancellationToken.None);

        Assert.Equal(1, first.MeasureCount);
        Assert.Equal(2, changed.MeasureCount);
        Assert.Equal(1, trailing.MeasureCount);
        Assert.Equal(1, first.ArrangeCount);
        Assert.Equal(2, changed.ArrangeCount);
        Assert.Equal(1, trailing.ArrangeCount);
        Assert.Equal(firstBounds, first.Bounds);
        Assert.Equal(changedBounds, changed.Bounds);
        Assert.Equal(trailingBounds, trailing.Bounds);
        Assert.Equal(oldSize, snapshot.Size);
        Assert.Equal(oldRevision + 1, snapshot.LayoutRevision);

        Assert.True(snapshot.TryBeginInteraction());
        try
        {
            Assert.True(snapshot.TryGetMeasuredTopLevelBlocksInBandForSceneProbe(
                changed.Bounds.Top,
                trailing.Bounds.Bottom,
                out IReadOnlyList<BlockBox> indexed));
            Assert.Collection(
                indexed,
                block => Assert.Same(changed, block),
                block => Assert.Same(trailing, block));
        }
        finally
        {
            snapshot.EndInteraction();
        }
    }

    [Fact]
    public void ChangedHeightUsesExistingSuffixReflowWithoutRemeasuringDirtyBlock()
    {
        var first = new MeasuredBlock(24) { BlockIndex = 0 };
        var changed = new MeasuredBlock(40) { BlockIndex = 1 };
        var trailing = new MeasuredBlock(30) { BlockIndex = 2 };
        using LayoutSnapshot snapshot = CreateSnapshot([first, changed, trailing]);
        double oldHeight = snapshot.Size.Height;
        double oldTrailingTop = trailing.Bounds.Top;

        changed.SetHeight(55);
        snapshot.RelayoutChangedBlocks([changed.BlockIndex], 200, CancellationToken.None);

        Assert.Equal(1, first.MeasureCount);
        Assert.Equal(2, changed.MeasureCount);
        Assert.Equal(1, trailing.MeasureCount);
        Assert.Equal(1, first.ArrangeCount);
        Assert.Equal(2, changed.ArrangeCount);
        Assert.Equal(2, trailing.ArrangeCount);
        Assert.Equal(oldTrailingTop + 15, trailing.Bounds.Top);
        Assert.Equal(oldHeight + 15, snapshot.Size.Height);
        Assert.Equal(1, snapshot.LayoutRevision);
    }

    [Fact]
    public void SameHeightCompletionNormalizesMalformedCommittedPlacement()
    {
        var first = new MeasuredBlock(30) { BlockIndex = 0 };
        var changed = new MeasuredBlock(50) { BlockIndex = 1 };
        var trailing = new MeasuredBlock(70) { BlockIndex = 2 };
        BlockBox[] blocks = [first, changed, trailing];
        float[] priorTops = [10, 20, 30];
        for (int index = 0; index < blocks.Length; index++)
        {
            blocks[index].Measure(200);
            blocks[index].Arrange(0, priorTops[index], 200);
        }

        using LayoutSnapshot snapshot = new(
            blocks,
            new MarkdownSourceMap(string.Empty),
            width: 200,
            height: 180,
            blockSpacing: 8);

        snapshot.RelayoutChangedBlocks([changed.BlockIndex], 200, CancellationToken.None);

        Assert.Equal(2, changed.MeasureCount);
        Assert.Equal(2, changed.ArrangeCount);
        Assert.Equal(2, trailing.ArrangeCount);
        Assert.Equal(first.Bounds.Bottom + 8, changed.Bounds.Top);
        Assert.Equal(changed.Bounds.Bottom + 8, trailing.Bounds.Top);
        Assert.Equal(trailing.Bounds.Bottom, snapshot.Size.Height);
    }

    [Fact]
    public void CancellationDuringMeasureRestoresPlacementWithoutPublishingRevision()
    {
        var first = new MeasuredBlock(24) { BlockIndex = 0 };
        var changed = new MeasuredBlock(40) { BlockIndex = 1 };
        var trailing = new MeasuredBlock(30) { BlockIndex = 2 };
        using LayoutSnapshot snapshot = CreateSnapshot([first, changed, trailing]);
        Rect changedBounds = changed.Bounds;
        Rect trailingBounds = trailing.Bounds;
        Rect[] oldBlockBounds = [first.Bounds, changedBounds, trailingBounds];
        Size oldSize = snapshot.Size;
        long oldRevision = snapshot.LayoutRevision;
        LayoutPlacementGeneration oldIndex = GetViewportIndex(snapshot);
        (double Top, double Bottom)[] oldIndexBounds = Enumerable.Range(0, oldIndex.Count)
            .Select(oldIndex.GetIndexedBounds)
            .ToArray();
        using var cancellation = new CancellationTokenSource();
        changed.AfterMeasure = cancellation.Cancel;

        Assert.Throws<OperationCanceledException>(() => snapshot.RelayoutChangedBlocks(
            [changed.BlockIndex],
            200,
            cancellation.Token));

        Assert.Equal(changedBounds, changed.Bounds);
        Assert.Equal(trailingBounds, trailing.Bounds);
        Assert.Equal(oldBlockBounds, new[] { first, changed, trailing }
            .Select(block => block.Bounds).ToArray());
        Assert.True(changed.IsDirty);
        Assert.Equal(oldSize, snapshot.Size);
        Assert.Equal(oldRevision, snapshot.LayoutRevision);
        Assert.True(snapshot.GeometryRecoveryRequired);
        Assert.True(snapshot.TryClaimGeometryRecoveryRequest());
        Assert.False(snapshot.TryClaimGeometryRecoveryRequest());
        Assert.False(snapshot.TryBeginInteraction());
        Assert.False(snapshot.TryBeginPaint());
        Assert.False(snapshot.TryGetMeasuredTopLevelBlocksInBandForSceneProbe(
            changedBounds.Top,
            trailingBounds.Bottom,
            out IReadOnlyList<BlockBox> unavailable));
        Assert.Empty(unavailable);
        Assert.Same(oldIndex, GetViewportIndex(snapshot));
        Assert.Equal(oldIndexBounds.Length, oldIndex.Count);
        for (int slot = 0; slot < oldIndexBounds.Length; slot++)
        {
            Assert.Equal(slot, oldIndex.GetBlockOrdinal(slot));
            Assert.Equal(oldIndexBounds[slot], oldIndex.GetIndexedBounds(slot));
        }
    }

    [Fact]
    public void MultipleChangedOwnersRetainSuffixRelayoutFallback()
    {
        var first = new MeasuredBlock(24) { BlockIndex = 0 };
        var second = new MeasuredBlock(30) { BlockIndex = 1 };
        var third = new MeasuredBlock(36) { BlockIndex = 2 };
        var fourth = new MeasuredBlock(42) { BlockIndex = 3 };
        using LayoutSnapshot snapshot = CreateSnapshot([first, second, third, fourth]);

        snapshot.RelayoutChangedBlocks(
            [second.BlockIndex, fourth.BlockIndex],
            200,
            CancellationToken.None);

        Assert.Equal(1, first.MeasureCount);
        Assert.Equal(2, second.MeasureCount);
        Assert.Equal(1, third.MeasureCount);
        Assert.Equal(2, fourth.MeasureCount);
        Assert.Equal(2, second.ArrangeCount);
        Assert.Equal(2, third.ArrangeCount);
        Assert.Equal(2, fourth.ArrangeCount);
    }

    [Fact]
    public void UnmeasuredLazyOwnerRetainsEstimatedSuffixFallback()
    {
        var first = new MeasuredBlock(24) { BlockIndex = 0 };
        var changed = new MeasuredBlock(40) { BlockIndex = 1 };
        var trailing = new MeasuredBlock(30) { BlockIndex = 2 };
        using LayoutSnapshot snapshot = CreateSnapshot([first, changed, trailing]);
        snapshot.EnableLazyLayout(200, viewportTop: 0, viewportHeight: 20, overscan: 0,
            CancellationToken.None);
        int changedMeasureCount = changed.MeasureCount;
        int trailingMeasureCount = trailing.MeasureCount;
        Assert.False(snapshot.IsTopLevelBlockMeasured(changed));

        snapshot.RelayoutChangedBlocks([changed.BlockIndex], 200, CancellationToken.None);

        Assert.Equal(changedMeasureCount, changed.MeasureCount);
        Assert.Equal(trailingMeasureCount, trailing.MeasureCount);
        Assert.True(changed.IsDirty);
        Assert.True(trailing.IsDirty);
        Assert.True(snapshot.IsTopLevelBlockMeasured(first));
    }

    [Fact]
    public void WidthChangeRetainsFullMeasuredRelayoutFallback()
    {
        var first = new MeasuredBlock(24) { BlockIndex = 0 };
        var changed = new MeasuredBlock(40) { BlockIndex = 1 };
        var trailing = new MeasuredBlock(30) { BlockIndex = 2 };
        using LayoutSnapshot snapshot = CreateSnapshot([first, changed, trailing]);

        snapshot.RelayoutChangedBlocks([changed.BlockIndex], 250, CancellationToken.None);

        Assert.Equal(2, first.MeasureCount);
        Assert.Equal(2, changed.MeasureCount);
        Assert.Equal(2, trailing.MeasureCount);
        Assert.Equal(250, first.Bounds.Width);
        Assert.Equal(250, changed.Bounds.Width);
        Assert.Equal(250, trailing.Bounds.Width);
    }

    [Fact]
    public void SafeHtmlFlowSpacingChangeRetainsSuffixRelayoutFallback()
    {
        var first = new MeasuredBlock(24) { BlockIndex = 0, SafeHtmlInlineFlowScopeId = 7 };
        var changed = new MeasuredBlock(40) { BlockIndex = 1, SafeHtmlInlineFlowScopeId = 7 };
        var trailing = new MeasuredBlock(30) { BlockIndex = 2, SafeHtmlInlineFlowScopeId = 7 };
        using LayoutSnapshot snapshot = CreateSnapshot([first, changed, trailing]);
        double changedTop = changed.Bounds.Top;
        double trailingTop = trailing.Bounds.Top;
        changed.AfterMeasure = () => changed.SafeHtmlInlineFlowScopeId = 8;

        snapshot.RelayoutChangedBlocks([changed.BlockIndex], 200, CancellationToken.None);

        Assert.Equal(changedTop + 8, changed.Bounds.Top);
        Assert.Equal(trailingTop + 16, trailing.Bounds.Top);
        Assert.Equal(2, trailing.ArrangeCount);
    }

    [Fact]
    public void MarginChangeRetainsSuffixRelayoutFallback()
    {
        var first = new MeasuredBlock(24) { BlockIndex = 0 };
        var changed = new MeasuredBlock(40) { BlockIndex = 1 };
        var trailing = new MeasuredBlock(30) { BlockIndex = 2 };
        using LayoutSnapshot snapshot = CreateSnapshot([first, changed, trailing]);
        changed.AfterMeasure = () => changed.Margin = new Thickness(1, 2, 3, 4);

        snapshot.RelayoutChangedBlocks([changed.BlockIndex], 200, CancellationToken.None);

        Assert.Equal(2, changed.ArrangeCount);
        Assert.Equal(2, trailing.ArrangeCount);
        Assert.Equal(new Thickness(1, 2, 3, 4), changed.Margin);
    }

    [Fact]
    public void SameHeightCompletionDoesNotArrangeThousandsOfTrailingSiblings()
    {
        const int blockCount = 4096;
        var blocks = new MeasuredBlock[blockCount];
        for (int index = 0; index < blocks.Length; index++)
            blocks[index] = new MeasuredBlock(20 + (index % 5)) { BlockIndex = index };

        using LayoutSnapshot snapshot = CreateSnapshot(blocks);
        const int changedOrdinal = 1024;
        Rect trailingBounds = blocks[^1].Bounds;

        snapshot.RelayoutChangedBlocks(
            [blocks[changedOrdinal].BlockIndex],
            200,
            CancellationToken.None);

        Assert.Equal(2, blocks[changedOrdinal].MeasureCount);
        Assert.Equal(2, blocks[changedOrdinal].ArrangeCount);
        Assert.All(blocks.Where((_, index) => index != changedOrdinal), block =>
        {
            Assert.Equal(1, block.MeasureCount);
            Assert.Equal(1, block.ArrangeCount);
        });
        Assert.Equal(trailingBounds, blocks[^1].Bounds);
    }

    private static LayoutSnapshot CreateSnapshot(MeasuredBlock[] blocks)
    {
        const float width = 200;
        const float spacing = 8;
        float y = 0;
        for (int index = 0; index < blocks.Length; index++)
        {
            MeasuredBlock block = blocks[index];
            float height = block.Measure(width);
            block.Arrange(0, y, width);
            y += height;
            if (index < blocks.Length - 1 &&
                !StackBox.SharesSafeHtmlInlineFlow(block, blocks[index + 1]))
                y += spacing;
        }

        return new LayoutSnapshot(
            blocks,
            new MarkdownSourceMap(string.Empty),
            width,
            y,
            blockSpacing: spacing);
    }

    private static LayoutPlacementGeneration GetViewportIndex(LayoutSnapshot snapshot)
        => snapshot.PublishedPlacementGeneration;

    private sealed class MeasuredBlock(float height) : BlockBox
    {
        private float _height = height;

        internal int MeasureCount { get; private set; }
        internal int ArrangeCount { get; private set; }
        internal Action? AfterMeasure { get; set; }

        internal void SetHeight(float value) => _height = value;

        public override float Measure(float availableWidth)
        {
            MeasureCount++;
            Bounds = new Rect(0, 0, availableWidth, _height);
            AfterMeasure?.Invoke();
            return _height;
        }

        public override void Arrange(float x, float y, float width)
        {
            ArrangeCount++;
            base.Arrange(x, y, width);
        }

        public override void Paint(CanvasDrawingSession drawingSession, Rect viewport) { }
    }
}
