using System.Reflection;
using System.Threading;
using MarkdownRenderer.Document;
using MarkdownRenderer.Layout;
using Microsoft.Graphics.Canvas;
using Microsoft.UI.Xaml;
using Windows.Foundation;
using Xunit;

namespace MarkdownRenderer.GitHub.Tests;

public sealed class LayoutSnapshotFusedViewportIndexTests
{
    private const float Width = 223.75f;
    private const float Spacing = 0.375f;

    [Fact]
    public void ChangedHeightRefreshesFullViewportIndexForMeasuredAndEstimatedLazySuffix()
    {
        const int blockCount = 4_096;
        const int prefixCount = 80;
        const int changedOrdinal = 3_000;
        const float changedHeight = 38.25f;
        var blocks = Enumerable.Range(0, blockCount)
            .Select(index => new ProbeBlock(32f) { BlockIndex = index })
            .ToArray();
        using LayoutSnapshot snapshot = CreateUnmeasuredSnapshot(blocks, blockSpacing: Spacing);
        snapshot.EnableLazyLayout(Width, viewportTop: 0, viewportHeight: 1, overscan: 0,
            CancellationToken.None);

        // Realize a sizeable prefix while leaving the rest estimated.
        double prefixBottom = blocks[prefixCount].Bounds.Top - 0.125d;
        snapshot.EnsureMeasuredBand(new LazyLayoutBand(0, prefixBottom), CancellationToken.None);
        Assert.True(snapshot.IsTopLevelBlockMeasured(blocks[prefixCount - 1]));
        Assert.False(snapshot.IsTopLevelBlockMeasured(blocks[prefixCount]));

        // Also realize two distant adjacent roots. The following suffix remains
        // estimated, so the later relayout validates one mixed-state index update.
        Rect targetEstimate = blocks[changedOrdinal].Bounds;
        Rect nextEstimate = blocks[changedOrdinal + 1].Bounds;
        snapshot.EnsureMeasuredBand(
            new LazyLayoutBand(targetEstimate.Top + 0.125d, nextEstimate.Bottom - 0.125d),
            CancellationToken.None);
        Assert.True(snapshot.IsTopLevelBlockMeasured(blocks[changedOrdinal]));
        Assert.True(snapshot.IsTopLevelBlockMeasured(blocks[changedOrdinal + 1]));
        Assert.False(snapshot.IsTopLevelBlockMeasured(blocks[changedOrdinal + 2]));
        Assert.Equal(prefixCount + 2, snapshot.MeasuredTopLevelBlockCount);

        Rect[] priorBounds = blocks.Select(block => block.Bounds).ToArray();
        Size oldSize = snapshot.Size;
        long oldRevision = snapshot.LayoutRevision;
        LayoutPlacementGeneration index = GetViewportIndex(snapshot);
        Assert.True(index.IsDocumentOrder);

        blocks[changedOrdinal].SetHeight(changedHeight);
        snapshot.RelayoutChangedBlocks([blocks[changedOrdinal].BlockIndex], Width, CancellationToken.None);

        Assert.Equal(oldRevision + 1, snapshot.LayoutRevision);
        Assert.Equal(prefixCount + 2, snapshot.MeasuredTopLevelBlockCount);
        Assert.Equal(oldSize.Height + (changedHeight - priorBounds[changedOrdinal].Height),
            snapshot.Size.Height);
        Assert.Equal(priorBounds[changedOrdinal].Top, blocks[changedOrdinal].Bounds.Top);
        Assert.Equal(priorBounds[changedOrdinal + 1].Top + (changedHeight - priorBounds[changedOrdinal].Height),
            blocks[changedOrdinal + 1].Bounds.Top);
        Assert.Equal(priorBounds[changedOrdinal + 2].Top + (changedHeight - priorBounds[changedOrdinal].Height),
            blocks[changedOrdinal + 2].Bounds.Top);
        Assert.False(snapshot.IsTopLevelBlockMeasured(blocks[changedOrdinal + 2]));

        // Prefix geometry is an exact fence; the changed measured roots and all
        // estimated suffix entries must agree with a full rebuild from the
        // authoritative block bounds, including the index's ordinal ordering.
        for (int ordinal = 0; ordinal < changedOrdinal; ordinal++)
            Assert.Equal(priorBounds[ordinal], blocks[ordinal].Bounds);

        int[] sampleOrdinals =
        [0, prefixCount - 1, changedOrdinal - 1, changedOrdinal,
            changedOrdinal + 1, changedOrdinal + 2, blockCount - 1];
        AssertMatchesFullBoundsRebuild(snapshot, blocks, sampleOrdinals);
    }

    [Fact]
    public void ChangedSuffixUsesFullViewportIndexFallbackForCustomVisualOrder()
    {
        float[] offsets = [84f, 0f, 23f, -41f, 62f, -17f, 31f, -33f];
        var blocks = offsets.Select((offset, ordinal) =>
            new OffsetProbeBlock(24f + ordinal, offset) { BlockIndex = ordinal }).ToArray();
        float y = 0;
        foreach (OffsetProbeBlock block in blocks)
        {
            float height = block.Measure(Width);
            block.Arrange(0, y, Width);
            y += height + 7.25f;
        }

        using LayoutSnapshot snapshot = new(
            blocks,
            new MarkdownSourceMap(string.Empty),
            Width,
            height: y,
            blockSpacing: 7.25f);
        LayoutPlacementGeneration index = GetViewportIndex(snapshot);
        Assert.False(index.IsDocumentOrder);

        Rect[] priorBounds = blocks.Select(block => block.Bounds).ToArray();
        blocks[2].SetHeight(41.5f);
        snapshot.RelayoutChangedBlocks([blocks[2].BlockIndex], Width, CancellationToken.None);

        Assert.False(index.IsDocumentOrder);
        Assert.NotEqual(priorBounds[2].Height, blocks[2].Bounds.Height);
        AssertMatchesFullBoundsRebuild(snapshot, blocks, Enumerable.Range(0, blocks.Length).ToArray());
    }

    [Fact]
    public void ArrangeFailureDoesNotPublishCandidateViewportIndexOrMetadata()
    {
        var blocks = Enumerable.Range(0, 40)
            .Select(index => new ProbeBlock(28f + (index % 5)) { BlockIndex = index })
            .ToArray();
        using LayoutSnapshot snapshot = CreateEagerSnapshot(blocks, blockSpacing: 3.5f);
        LayoutPlacementGeneration index = GetViewportIndex(snapshot);
        IndexSlot[] committedSlots = CaptureSlots(index);
        (double Top, double Bottom)[] exactBoundaryQueries =
        [
            (blocks[0].Bounds.Top, blocks[0].Bounds.Top),
            (blocks[6].Bounds.Bottom, blocks[6].Bounds.Bottom),
            (blocks[15].Bounds.Top, blocks[19].Bounds.Bottom),
            (blocks[^1].Bounds.Bottom, blocks[^1].Bounds.Bottom),
        ];
        ViewportRange[] committedRanges = exactBoundaryQueries
            .Select(query => index.Find(query.Top, query.Bottom))
            .ToArray();
        Size oldSize = snapshot.Size;
        long oldRevision = snapshot.LayoutRevision;
        object oldMetadata = ReadPrivateField(snapshot, "_committedMetadata");

        blocks[3].SetHeight(45.25f);
        blocks[21].ThrowAfterArrange = true;
        Assert.Throws<InvalidOperationException>(() => snapshot.RelayoutChangedBlocks(
            [blocks[3].BlockIndex], Width, CancellationToken.None));

        Assert.True(ReadPrivateField<bool>(snapshot, "_paintOnlyGeometryTainted"));
        Assert.Equal(oldSize, snapshot.Size);
        Assert.Equal(oldRevision, snapshot.LayoutRevision);
        Assert.Same(oldMetadata, ReadPrivateField(snapshot, "_committedMetadata"));
        Assert.Same(index, GetViewportIndex(snapshot));
        Assert.Equal(committedSlots, CaptureSlots(index));
        for (int queryIndex = 0; queryIndex < exactBoundaryQueries.Length; queryIndex++)
        {
            (double top, double bottom) = exactBoundaryQueries[queryIndex];
            Assert.Equal(committedRanges[queryIndex], index.Find(top, bottom));
        }

    }

    private static LayoutSnapshot CreateUnmeasuredSnapshot(
        ProbeBlock[] blocks,
        double blockSpacing)
    {
        for (int ordinal = 0; ordinal < blocks.Length; ordinal++)
            blocks[ordinal].BlockIndex = ordinal;

        return new LayoutSnapshot(
            blocks,
            new MarkdownSourceMap(string.Empty),
            Width,
            height: 0,
            blockSpacing: blockSpacing);
    }

    private static LayoutSnapshot CreateEagerSnapshot(
        ProbeBlock[] blocks,
        double blockSpacing)
    {
        float y = 0;
        for (int ordinal = 0; ordinal < blocks.Length; ordinal++)
        {
            float height = blocks[ordinal].Measure(Width);
            blocks[ordinal].Arrange(0, y, Width);
            y += height;
            if (ordinal < blocks.Length - 1)
                y += (float)blockSpacing;
        }

        return new LayoutSnapshot(
            blocks,
            new MarkdownSourceMap(string.Empty),
            Width,
            y,
            blockSpacing: blockSpacing);
    }

    private static void AssertMatchesFullBoundsRebuild(
        LayoutSnapshot snapshot,
        ProbeBlock[] blocks,
        int[] sampleOrdinals)
    {
        LayoutPlacementGeneration actual = GetViewportIndex(snapshot);
        var expected = new ViewportBandIndex(blocks.Length);
        for (int ordinal = 0; ordinal < blocks.Length; ordinal++)
        {
            Rect bounds = blocks[ordinal].Bounds;
            expected.SetEntry(ordinal, ordinal, bounds.Top, bounds.Bottom);
        }
        expected.Commit();

        Assert.Equal(expected.Count, actual.Count);
        Assert.Equal(expected.IsDocumentOrder, actual.IsDocumentOrder);
        for (int entry = 0; entry < expected.Count; entry++)
        {
            Assert.Equal(expected.GetBlockOrdinal(entry), actual.GetBlockOrdinal(entry));
            Assert.Equal(expected.GetBounds(entry), actual.GetIndexedBounds(entry));
        }

        var queryBands = new List<(double Top, double Bottom)>(sampleOrdinals.Length * 3);
        foreach (int ordinal in sampleOrdinals)
        {
            Rect bounds = blocks[ordinal].Bounds;
            queryBands.Add((bounds.Top, bounds.Top));
            queryBands.Add((bounds.Bottom, bounds.Bottom));
            queryBands.Add((bounds.Top, bounds.Bottom));
        }

        queryBands.Add((blocks[0].Bounds.Bottom, blocks[^1].Bounds.Top));
        foreach ((double top, double bottom) in queryBands)
        {
            ViewportRange expectedRange = expected.Find(top, bottom);
            ViewportRange actualRange = actual.Find(top, bottom);
            Assert.Equal(expectedRange, actualRange);
            for (int entry = expectedRange.Start; entry < expectedRange.End; entry++)
                Assert.Equal(expected.GetBlockOrdinal(entry), actual.GetBlockOrdinal(entry));
        }
    }

    private static IndexSlot[] CaptureSlots(LayoutPlacementGeneration index)
    {
        var slots = new IndexSlot[index.Count];
        for (int entry = 0; entry < slots.Length; entry++)
        {
            (double top, double bottom) = index.GetIndexedBounds(entry);
            slots[entry] = new IndexSlot(index.GetBlockOrdinal(entry), top, bottom);
        }
        return slots;
    }

    private static LayoutPlacementGeneration GetViewportIndex(LayoutSnapshot snapshot)
        => snapshot.PublishedPlacementGeneration;

    private static object ReadPrivateField(object instance, string name)
    {
        FieldInfo field = instance.GetType().GetField(
            name, BindingFlags.Instance | BindingFlags.NonPublic)!;
        return field.GetValue(instance)!;
    }

    private static T ReadPrivateField<T>(object instance, string name)
        => (T)ReadPrivateField(instance, name);

    private readonly record struct IndexSlot(int Ordinal, double Top, double Bottom);

    private class ProbeBlock(float height) : BlockBox
    {
        private float _height = height;

        internal bool ThrowAfterArrange { get; set; }
        internal void SetHeight(float height) => _height = height;

        public override float Measure(float availableWidth)
        {
            Bounds = new Rect(0, 0, availableWidth, _height);
            return _height;
        }

        public override void Arrange(float x, float y, float width)
        {
            base.Arrange(x, y, width);
            if (ThrowAfterArrange)
                throw new InvalidOperationException("Injected suffix arrange failure.");
        }

        public override void Paint(CanvasDrawingSession drawingSession, Rect viewport) { }
    }

    private sealed class OffsetProbeBlock(float height, float offset) : ProbeBlock(height)
    {
        public override void Arrange(float x, float y, float width)
            => base.Arrange(x, y + offset, width);
    }
}
