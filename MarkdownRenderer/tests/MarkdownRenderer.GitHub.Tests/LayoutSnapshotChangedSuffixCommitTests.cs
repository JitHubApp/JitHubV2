using System.Reflection;
using System.Threading;
using MarkdownRenderer.Document;
using MarkdownRenderer.Layout;
using MarkdownRenderer.Layout.Boxes;
using Microsoft.Graphics.Canvas;
using Microsoft.UI.Xaml;
using Windows.Foundation;
using Xunit;

namespace MarkdownRenderer.GitHub.Tests;

public sealed class LayoutSnapshotChangedSuffixCommitTests
{
    private const float Width = 200;
    private const float Spacing = 8;

    [Fact]
    public void ChangedMeasuredRootCommitsExactMeasuredAndEstimatedSuffixGeometry()
    {
        var blocks = new[]
        {
            new MeasuredBlock(24.5f) { BlockIndex = 0 },
            new MeasuredBlock(33.25f) { BlockIndex = 1 },
            new MeasuredBlock(28.75f) { BlockIndex = 2 },
            new MeasuredBlock(34f) { BlockIndex = 3 },
            new MeasuredBlock(42.5f) { BlockIndex = 4 },
            new MeasuredBlock(23f) { BlockIndex = 5 },
            new MeasuredBlock(27f) { BlockIndex = 6 },
        };
        using LayoutSnapshot snapshot = CreateSnapshot(blocks);

        snapshot.EnableLazyLayout(Width, viewportTop: 0, viewportHeight: 1, overscan: 0,
            cancellationToken: CancellationToken.None);
        Assert.True(snapshot.IsTopLevelBlockMeasured(blocks[0]));
        Assert.False(snapshot.IsTopLevelBlockMeasured(blocks[4]));
        Assert.False(snapshot.IsTopLevelBlockMeasured(blocks[5]));

        double targetTop = blocks[4].Bounds.Top;
        snapshot.EnsureMeasuredViewport(targetTop, viewportHeight: 1, overscan: 0,
            cancellationToken: CancellationToken.None);
        Assert.True(snapshot.IsTopLevelBlockMeasured(blocks[4]));
        Assert.False(snapshot.IsTopLevelBlockMeasured(blocks[5]));

        long oldRevision = snapshot.LayoutRevision;
        blocks[4].SetHeight(51.25f);
        snapshot.RelayoutChangedBlocks([blocks[4].BlockIndex], Width, CancellationToken.None);

        Assert.Equal(oldRevision + 1, snapshot.LayoutRevision);
        Assert.Equal(2, snapshot.MeasuredTopLevelBlockCount);

        float[] committedHeights = [24.5f, 32f, 32f, 32f, 51.25f, 32f, 32f];
        float y = 0;
        for (int ordinal = 0; ordinal < blocks.Length; ordinal++)
        {
            Assert.Equal(new Rect(0, y, Width, committedHeights[ordinal]), blocks[ordinal].Bounds);
            y += committedHeights[ordinal];
            if (ordinal < blocks.Length - 1)
                y += Spacing;
        }

        Assert.Equal(new Size(Width, y), snapshot.Size);
        AssertIndexedBand(snapshot, blocks[0], expectedMeasured: true);
        AssertIndexedBand(snapshot, blocks[4], expectedMeasured: true);
        AssertIndexedBand(snapshot, blocks[5], expectedMeasured: false);
    }

    [Fact]
    public void ChangedSuffixUsesFullIndexFallbackWhenPriorCustomOrderIsNotDocumentOrder()
    {
        var first = new MeasuredBlock(20) { BlockIndex = 0 };
        var changed = new MeasuredBlock(30) { BlockIndex = 1 };
        var trailing = new MeasuredBlock(40) { BlockIndex = 2 };
        first.Measure(Width);
        changed.Measure(Width);
        trailing.Measure(Width);

        // A custom placement is legal at snapshot construction. The initial
        // viewport index is visual-order sorted, not ordinal order, so a suffix
        // update must preserve the established full-index defensive fallback.
        first.SetRawPlacement(0, 0, Width);
        changed.SetRawPlacement(0, 200, Width);
        trailing.SetRawPlacement(0, 100, Width);
        using LayoutSnapshot snapshot = new(
            [first, changed, trailing],
            new MarkdownSourceMap(string.Empty),
            Width,
            height: 300,
            blockSpacing: Spacing);

        snapshot.RelayoutChangedBlocks([changed.BlockIndex], Width, CancellationToken.None);

        Assert.Equal(new Rect(0, 0, Width, 20), first.Bounds);
        Assert.Equal(new Rect(0, 28, Width, 30), changed.Bounds);
        Assert.Equal(new Rect(0, 66, Width, 40), trailing.Bounds);
        Assert.Equal(new Size(Width, 106), snapshot.Size);
        AssertIndexedBand(snapshot, changed, expectedMeasured: true);
        AssertIndexedBand(snapshot, trailing, expectedMeasured: true);
    }

    [Fact]
    public void CancellationBeforeSuffixCommitPreservesBoundsViewportIndexAndMetadata()
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
        object oldMetadata = ReadPrivateField(snapshot, "_committedMetadata");
        LayoutPlacementGeneration oldIndex = snapshot.PublishedPlacementGeneration;
        (double Top, double Bottom)[] oldIndexBounds = Enumerable.Range(0, oldIndex.Count)
            .Select(oldIndex.GetIndexedBounds)
            .ToArray();
        using var cancellation = new CancellationTokenSource();
        changed.SetHeight(55);
        changed.AfterMeasure = cancellation.Cancel;

        Assert.ThrowsAny<OperationCanceledException>(() => snapshot.RelayoutChangedBlocks(
            [changed.BlockIndex], Width, cancellation.Token));

        Assert.Equal(firstBounds, first.Bounds);
        Assert.Equal(changedBounds, changed.Bounds);
        Assert.Equal(trailingBounds, trailing.Bounds);
        Assert.Equal(oldSize, snapshot.Size);
        Assert.Equal(oldRevision, snapshot.LayoutRevision);
        Assert.Same(oldMetadata, ReadPrivateField(snapshot, "_committedMetadata"));
        Assert.True(snapshot.GeometryRecoveryRequired);
        Assert.True(snapshot.TryClaimGeometryRecoveryRequest());
        Assert.False(snapshot.TryClaimGeometryRecoveryRequest());
        Assert.False(snapshot.TryBeginInteraction());
        Assert.False(snapshot.TryBeginPaint());
        Assert.False(snapshot.TryGetMeasuredTopLevelBlocksInBandForSceneProbe(
            firstBounds.Top,
            trailingBounds.Bottom,
            out IReadOnlyList<BlockBox> unavailable));
        Assert.Empty(unavailable);
        Assert.Same(oldIndex, snapshot.PublishedPlacementGeneration);
        Assert.Equal(oldIndexBounds.Length, oldIndex.Count);
        for (int slot = 0; slot < oldIndexBounds.Length; slot++)
        {
            Assert.Equal(slot, oldIndex.GetBlockOrdinal(slot));
            Assert.Equal(oldIndexBounds[slot], oldIndex.GetIndexedBounds(slot));
        }
        AssertIndexedBand(snapshot, first, expectedMeasured: true, expectedRecoveryRequired: true);
        AssertIndexedBand(snapshot, changed, expectedMeasured: true, expectedRecoveryRequired: true);
        AssertIndexedBand(snapshot, trailing, expectedMeasured: true, expectedRecoveryRequired: true);
    }

    [Fact]
    public void ArrangeFailureLeavesSnapshotTaintedWithoutPublishingCandidateMetadata()
    {
        var first = new MeasuredBlock(24) { BlockIndex = 0 };
        var changed = new MeasuredBlock(40) { BlockIndex = 1 };
        var trailing = new MeasuredBlock(30) { BlockIndex = 2 };
        using LayoutSnapshot snapshot = CreateSnapshot([first, changed, trailing]);
        Size oldSize = snapshot.Size;
        long oldRevision = snapshot.LayoutRevision;
        object oldMetadata = ReadPrivateField(snapshot, "_committedMetadata");
        changed.SetHeight(55);
        trailing.ThrowAfterArrange = true;

        Assert.Throws<InvalidOperationException>(() => snapshot.RelayoutChangedBlocks(
            [changed.BlockIndex], Width, CancellationToken.None));

        Assert.Equal(oldSize, snapshot.Size);
        Assert.Equal(oldRevision, snapshot.LayoutRevision);
        Assert.Same(oldMetadata, ReadPrivateField(snapshot, "_committedMetadata"));
        Assert.True(ReadPrivateField<bool>(snapshot, "_paintOnlyGeometryTainted"));
    }

    private static LayoutSnapshot CreateSnapshot(MeasuredBlock[] blocks)
    {
        float y = 0;
        for (int ordinal = 0; ordinal < blocks.Length; ordinal++)
        {
            float height = blocks[ordinal].Measure(Width);
            blocks[ordinal].Arrange(0, y, Width);
            y += height;
            if (ordinal < blocks.Length - 1)
                y += Spacing;
        }

        return new LayoutSnapshot(
            blocks,
            new MarkdownSourceMap(string.Empty),
            Width,
            y,
            blockSpacing: Spacing);
    }

    private static void AssertIndexedBand(
        LayoutSnapshot snapshot,
        MeasuredBlock block,
        bool expectedMeasured,
        bool expectedRecoveryRequired = false)
    {
        Assert.Equal(expectedRecoveryRequired, snapshot.GeometryRecoveryRequired);
        if (expectedRecoveryRequired)
        {
            Assert.False(snapshot.TryBeginPaint());
            Assert.False(snapshot.TryBeginInteraction());
            Assert.False(snapshot.TryGetMeasuredTopLevelBlocksInBandForSceneProbe(
                block.Bounds.Top + 1,
                block.Bounds.Bottom - 1,
                out IReadOnlyList<BlockBox> unavailable));
            Assert.Empty(unavailable);
            return;
        }

        Assert.True(snapshot.TryBeginInteraction());
        try
        {
            double top = block.Bounds.Top + 1;
            double bottom = block.Bounds.Bottom - 1;
            bool isIndexedAndMeasured = snapshot.TryGetMeasuredTopLevelBlocksInBandForSceneProbe(
                top,
                bottom,
                out IReadOnlyList<BlockBox> indexed);
            if (expectedMeasured)
            {
                Assert.True(isIndexedAndMeasured);
                Assert.Collection(indexed, candidate => Assert.Same(block, candidate));
            }
            else
            {
                // A false result here means the viewport index found this
                // unmeasured estimated root in its committed geometry.
                Assert.False(isIndexedAndMeasured);
            }
        }
        finally
        {
            snapshot.EndInteraction();
        }
    }

    private static object ReadPrivateField(object instance, string name)
        => instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(instance)!;

    private static T ReadPrivateField<T>(object instance, string name)
        => (T)ReadPrivateField(instance, name);

    private sealed class MeasuredBlock(float height) : BlockBox
    {
        private float _height = height;

        internal Action? AfterMeasure { get; set; }
        internal bool ThrowAfterArrange { get; set; }
        internal void SetHeight(float value) => _height = value;

        public override float Measure(float availableWidth)
        {
            Bounds = new Rect(0, 0, availableWidth, _height);
            AfterMeasure?.Invoke();
            return _height;
        }

        public override void Arrange(float x, float y, float width)
        {
            base.Arrange(x, y, width);
            if (ThrowAfterArrange)
                throw new InvalidOperationException("Injected arrangement failure.");
        }

        internal void SetRawPlacement(float x, float y, float width)
            => base.Arrange(x, y, width);

        public override void Paint(CanvasDrawingSession drawingSession, Rect viewport) { }
    }
}
