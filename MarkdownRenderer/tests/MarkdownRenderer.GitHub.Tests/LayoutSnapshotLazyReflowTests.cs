using System.Reflection;
using MarkdownRenderer.Document;
using MarkdownRenderer.Layout;
using MarkdownRenderer.Layout.Boxes;
using Microsoft.Graphics.Canvas;
using Microsoft.UI.Xaml;
using Windows.Foundation;
using Xunit;

namespace MarkdownRenderer.GitHub.Tests;

public sealed class LayoutSnapshotLazyReflowTests
{
    private const float Width = 180f;

    [Fact]
    public void NearEndRealizationInHundredThousandRootsArrangesOnlyChangedSuffix()
    {
        const int blockCount = 100_000;
        const int lastOrdinal = blockCount - 1;
        var blocks = new ProbeBlock[blockCount];
        for (int blockOrdinal = 0; blockOrdinal < blocks.Length; blockOrdinal++)
        {
            // Every estimate is 32. The final block alone measures to a new
            // height, forcing an actual suffix commit instead of the no-op path.
            blocks[blockOrdinal] = new ProbeBlock(blockOrdinal == lastOrdinal ? 40f : 32f)
            {
                BlockIndex = blockOrdinal,
            };
        }

        using var snapshot = CreateUnmeasuredSnapshot(blocks);
        snapshot.EnableLazyLayout(Width, 0, viewportHeight: 1, overscan: 0,
            CancellationToken.None);

        const int prefixCount = lastOrdinal;
        double prefixBottom = (prefixCount * 32d) - 0.25d;
        snapshot.EnsureMeasuredBand(new LazyLayoutBand(0, prefixBottom), CancellationToken.None);
        Assert.Equal(prefixCount, snapshot.MeasuredTopLevelBlockCount);
        Assert.False(snapshot.IsTopLevelBlockMeasured(blocks[lastOrdinal]));

        var prefixRects = new Rect[prefixCount];
        var prefixIndexRects = new (double Top, double Bottom)[prefixCount];
        LayoutPlacementGeneration index = GetViewportIndex(snapshot);
        Assert.True(index.IsDocumentOrder);
        for (int ordinal = 0; ordinal < prefixCount; ordinal++)
        {
            prefixRects[ordinal] = blocks[ordinal].Bounds;
            prefixIndexRects[ordinal] = index.GetIndexedBounds(ordinal);
            Assert.Equal(ordinal, index.GetBlockOrdinal(ordinal));
            blocks[ordinal].ResetCounts();
        }

        Rect lastEstimate = blocks[lastOrdinal].Bounds;
        Size oldSize = snapshot.Size;
        long oldRevision = snapshot.LayoutRevision;
        Assert.Equal(32, lastEstimate.Height);

        snapshot.EnsureMeasuredBand(
            new LazyLayoutBand(lastEstimate.Top, lastEstimate.Top + 0.25d),
            CancellationToken.None);

        Assert.Equal(blockCount, snapshot.MeasuredTopLevelBlockCount);
        Assert.True(snapshot.IsTopLevelBlockMeasured(blocks[lastOrdinal]));
        Assert.Equal(oldRevision + 1, snapshot.LayoutRevision);
        Assert.Equal(oldSize.Height + 8, snapshot.Size.Height);
        Assert.Equal(40, blocks[lastOrdinal].Bounds.Height);
        Assert.Equal(lastEstimate.Top, blocks[lastOrdinal].Bounds.Top);
        Assert.Equal(2, blocks[lastOrdinal].ArrangeCount); // Measure placement + suffix commit.
        LayoutPlacementGeneration committedIndex = GetViewportIndex(snapshot);
        Assert.NotSame(index, committedIndex);
        Assert.Equal(index.Count, committedIndex.Count);
        Assert.True(index.IsDocumentOrder);

        // The root immediately before the changed suffix is a 100k-document
        // fence: it, and every earlier measured sibling, remains untouched.
        for (int ordinal = 0; ordinal < prefixCount; ordinal++)
        {
            Assert.Equal(0, blocks[ordinal].ArrangeCount);
            Assert.Equal(prefixRects[ordinal], blocks[ordinal].Bounds);
            Assert.Equal(prefixIndexRects[ordinal], index.GetIndexedBounds(ordinal));
            Assert.Equal(prefixIndexRects[ordinal], committedIndex.GetIndexedBounds(ordinal));
            Assert.Equal(ordinal, index.GetBlockOrdinal(ordinal));
        }
    }

    [Fact]
    public void ExactMeasuredEstimateDoesNotRearrangeSiblingsAndPublishesMeasuredGeneration()
    {
        var blocks = Enumerable.Range(0, 24)
            .Select(index => new ProbeBlock(32) { BlockIndex = index })
            .ToArray();
        using var snapshot = CreateUnmeasuredSnapshot(blocks);
        snapshot.EnableLazyLayout(Width, 0, 1, 0, CancellationToken.None);

        int targetOrdinal = 12;
        Rect[] oldRects = blocks.Select(block => block.Bounds).ToArray();
        LayoutPlacementGeneration index = GetViewportIndex(snapshot);
        var oldIndexRects = Enumerable.Range(0, blocks.Length)
            .Select(index.GetIndexedBounds)
            .ToArray();
        foreach (ProbeBlock block in blocks)
            block.ResetCounts();
        long oldRevision = snapshot.LayoutRevision;
        Size oldSize = snapshot.Size;

        Rect target = blocks[targetOrdinal].Bounds;
        snapshot.EnsureMeasuredBand(
            new LazyLayoutBand(target.Top + 0.25d, target.Bottom - 0.25d),
            CancellationToken.None);

        Assert.True(snapshot.IsTopLevelBlockMeasured(blocks[targetOrdinal]));
        Assert.Equal(oldRevision + 1, snapshot.LayoutRevision);
        Assert.Equal(oldSize, snapshot.Size);
        Assert.Equal(1, blocks[targetOrdinal].MeasureCount);
        Assert.Equal(1, blocks[targetOrdinal].ArrangeCount);
        LayoutPlacementGeneration committedIndex = GetViewportIndex(snapshot);
        Assert.NotSame(index, committedIndex);
        for (int ordinal = 0; ordinal < blocks.Length; ordinal++)
        {
            Assert.Equal(oldRects[ordinal], blocks[ordinal].Bounds);
            Assert.Equal(oldIndexRects[ordinal], index.GetIndexedBounds(ordinal));
            Assert.Equal(oldIndexRects[ordinal], committedIndex.GetIndexedBounds(ordinal));
            Assert.Equal(ordinal, index.GetBlockOrdinal(ordinal));
            if (ordinal != targetOrdinal)
            {
                Assert.Equal(0, blocks[ordinal].MeasureCount);
                Assert.Equal(0, blocks[ordinal].ArrangeCount);
            }
        }
    }

    [Fact]
    public void ConcurrentSharedHeightHintCannotMoveAlreadyCommittedEstimatedPrefix()
    {
        const int blockCount = 14;
        var metrics = new SharedLayoutMetrics(blockCount, estimatedKeyWeightBytes: 1);
        var blocks = Enumerable.Range(0, blockCount)
            .Select(index => new ProbeBlock(index == blockCount - 1 ? 40f : 32f)
            {
                BlockIndex = index,
            })
            .ToArray();
        using var snapshot = CreateUnmeasuredSnapshot(blocks, sharedLayoutMetrics: metrics);
        snapshot.EnableLazyLayout(Width, 0, 1, 0, CancellationToken.None);

        int prefixOrdinal = 5;
        Rect committedEstimate = blocks[prefixOrdinal].Bounds;
        Assert.Equal(32, committedEstimate.Height);
        metrics.RecordHeight(prefixOrdinal, 96);
        Rect lateTarget = blocks[^1].Bounds;
        Size oldSize = snapshot.Size;
        for (int ordinal = 0; ordinal < blockCount; ordinal++)
            blocks[ordinal].ResetCounts();

        snapshot.EnsureMeasuredBand(
            new LazyLayoutBand(lateTarget.Top + 0.25d, lateTarget.Bottom - 0.25d),
            CancellationToken.None);

        Assert.Equal(committedEstimate, blocks[prefixOrdinal].Bounds);
        Assert.Equal(32, blocks[prefixOrdinal].Bounds.Height);
        Assert.False(snapshot.IsTopLevelBlockMeasured(blocks[prefixOrdinal]));
        Assert.Equal(0, blocks[prefixOrdinal].ArrangeCount);
        Assert.Equal(oldSize.Height + 8, snapshot.Size.Height);
        Assert.Equal(2, snapshot.MeasuredTopLevelBlockCount);
    }

    [Fact]
    public void ChangedOwnerRelayoutKeepsContinuationForLaterLazySuffixReflow()
    {
        const int blockCount = 25;
        const int changedOrdinal = 8;
        const int finalOrdinal = blockCount - 1;
        var blocks = Enumerable.Range(0, blockCount)
            .Select(index => new ProbeBlock(index == finalOrdinal ? 40f : 32f)
            {
                BlockIndex = index,
            })
            .ToArray();
        using var snapshot = CreateUnmeasuredSnapshot(blocks);
        snapshot.EnableLazyLayout(Width, 0, 1, 0, CancellationToken.None);
        snapshot.EnsureMeasuredBand(new LazyLayoutBand(0, (finalOrdinal * 32d) - 0.25d),
            CancellationToken.None);
        Assert.Equal(finalOrdinal, snapshot.MeasuredTopLevelBlockCount);

        Rect[] committedPrefix = blocks.Take(finalOrdinal).Select(block => block.Bounds).ToArray();
        LayoutPlacementGeneration index = GetViewportIndex(snapshot);
        foreach (ProbeBlock block in blocks)
            block.ResetCounts();

        blocks[changedOrdinal].SetMeasuredHeight(40);
        snapshot.RelayoutChangedBlocks([blocks[changedOrdinal].BlockIndex], Width, CancellationToken.None);

        Assert.Equal(808, snapshot.Size.Height);
        Assert.Equal(0, blocks[0].ArrangeCount);
        Assert.Equal(0, blocks[changedOrdinal - 1].ArrangeCount);
        for (int ordinal = 0; ordinal < changedOrdinal; ordinal++)
            Assert.Equal(committedPrefix[ordinal], blocks[ordinal].Bounds);

        // A different code path changed measured geometry/revision. A later
        // lazy reflow must either use the refreshed float continuation or take
        // a correct safe fallback; it must not move/rearrange this prefix.
        Rect[] beforeLateRealization = blocks.Take(finalOrdinal).Select(block => block.Bounds).ToArray();
        for (int ordinal = 0; ordinal < blockCount; ordinal++)
            blocks[ordinal].ResetCounts();
        double anchorOffset = blocks[18].Bounds.Top + 12.5d;
        Assert.True(snapshot.TryCaptureScrollAnchor(anchorOffset, out SnapshotScrollAnchor anchorBefore));
        Rect lateTarget = blocks[finalOrdinal].Bounds;

        snapshot.EnsureMeasuredBand(
            new LazyLayoutBand(lateTarget.Top + 0.25d, lateTarget.Bottom - 0.25d),
            CancellationToken.None);

        Assert.Equal(816, snapshot.Size.Height);
        Assert.Equal(blockCount, snapshot.MeasuredTopLevelBlockCount);
        Assert.Equal(2, blocks[finalOrdinal].ArrangeCount);
        for (int ordinal = 0; ordinal < finalOrdinal; ordinal++)
        {
            Assert.Equal(0, blocks[ordinal].ArrangeCount);
            Assert.Equal(beforeLateRealization[ordinal], blocks[ordinal].Bounds);
        }
        LayoutPlacementGeneration committedIndex = GetViewportIndex(snapshot);
        Assert.NotSame(index, committedIndex);
        Assert.True(snapshot.TryCaptureScrollAnchor(anchorOffset, out SnapshotScrollAnchor anchorAfter));
        Assert.Equal(anchorBefore.BlockIndex, anchorAfter.BlockIndex);
        Assert.Equal(anchorBefore.OffsetFromTop, anchorAfter.OffsetFromTop);
        Assert.Equal(blocks[18].BlockIndex, anchorAfter.BlockIndex);
        Assert.Equal(816, anchorAfter.Height);
    }

    [Fact]
    public void LazyAndEagerLayoutsMatchSequentialFloatGeometryAnchorsAndViewportQueries()
    {
        float[] heights = [21.1f, 30.3f, 11.7f, 46.2f, 17.9f, 23.4f, 39.1f, 14.6f,
            28.8f, 33.3f, 19.2f, 41.7f, 25.5f, 16.4f, 35.8f, 22.6f];
        int?[] flowScopes = [7, 7, null, 11, 11, 11, null, 4, 4, null, null, 5, 5, null, 2, 2];
        const float width = 194.7f;
        const double spacing = 4.35d;
        var padding = new Thickness(3.7, 16_777_217.1d, 5.2, 8.9);

        ProbeBlock[] eagerBlocks = CreateBlocks(heights, flowScopes);
        using LayoutSnapshot eager = CreateEagerSnapshot(eagerBlocks, width, padding, spacing);

        ProbeBlock[] lazyBlocks = CreateBlocks(heights, flowScopes);
        using LayoutSnapshot lazy = CreateUnmeasuredSnapshot(lazyBlocks, width, padding, spacing);
        lazy.EnableLazyLayout(
            width,
            viewportTop: 0,
            viewportHeight: 100_000_000,
            overscan: 0,
            CancellationToken.None);

        Assert.Equal(eager.Size, lazy.Size);
        Assert.Equal(heights.Length, lazy.MeasuredTopLevelBlockCount);
        Assert.Equal(heights.Length, lazy.TopLevelBlockCount);

        float expectedY = (float)padding.Top;
        float contentWidth = (float)System.Math.Max(1, width - padding.Left - padding.Right);
        float contentX = (float)padding.Left;
        for (int ordinal = 0; ordinal < heights.Length; ordinal++)
        {
            Assert.Equal(eagerBlocks[ordinal].Bounds, lazyBlocks[ordinal].Bounds);
            Assert.Equal(new Rect(contentX, expectedY, contentWidth, heights[ordinal]),
                lazyBlocks[ordinal].Bounds);
            expectedY += heights[ordinal];
            if (ordinal < heights.Length - 1 &&
                (flowScopes[ordinal] is not int scope || flowScopes[ordinal + 1] != scope))
                expectedY += (float)spacing;
        }
        expectedY += (float)padding.Bottom;
        Assert.Equal((double)expectedY, lazy.Size.Height);

        double[] anchorOffsets =
        [
            (float)padding.Top + 5.25d,
            eagerBlocks[3].Bounds.Top + 7.4d,
            eagerBlocks[8].Bounds.Bottom - 1.2d,
            eagerBlocks[^1].Bounds.Top + 9.1d,
        ];
        foreach (double offset in anchorOffsets)
        {
            Assert.True(eager.TryCaptureScrollAnchor(offset, out SnapshotScrollAnchor eagerAnchor));
            Assert.True(lazy.TryCaptureScrollAnchor(offset, out SnapshotScrollAnchor lazyAnchor));
            Assert.Equal(eagerAnchor.BlockIndex, lazyAnchor.BlockIndex);
            Assert.Equal(eagerAnchor.OffsetFromTop, lazyAnchor.OffsetFromTop);
            Assert.Equal(eagerAnchor.Height, lazyAnchor.Height);
        }

        (double Top, double Bottom)[] queryBands =
        [
            (eagerBlocks[1].Bounds.Top + 1, eagerBlocks[4].Bounds.Bottom - 1),
            (eagerBlocks[6].Bounds.Top, eagerBlocks[9].Bounds.Top + 2),
            (eagerBlocks[12].Bounds.Bottom, eagerBlocks[15].Bounds.Bottom),
        ];
        foreach ((double top, double bottom) in queryBands)
        {
            Assert.True(eager.TryBeginInteraction());
            try
            {
                Assert.True(lazy.TryBeginInteraction());
                try
                {
                    Assert.True(eager.TryGetMeasuredTopLevelBlocksInBandForSceneProbe(
                        top, bottom, out IReadOnlyList<BlockBox> eagerBlocksInBand));
                    Assert.True(lazy.TryGetMeasuredTopLevelBlocksInBandForSceneProbe(
                        top, bottom, out IReadOnlyList<BlockBox> lazyBlocksInBand));
                    Assert.Equal(
                        eagerBlocksInBand.Select(block => block.BlockIndex),
                        lazyBlocksInBand.Select(block => block.BlockIndex));
                }
                finally
                {
                    lazy.EndInteraction();
                }
            }
            finally
            {
                eager.EndInteraction();
            }
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MeasureOrArrangeFailureBeforeMaskCommitRestoresCommittedLazyState(bool failArrange)
    {
        var blocks = Enumerable.Range(0, 8)
            .Select(index => new ProbeBlock(32) { BlockIndex = index })
            .ToArray();
        using var snapshot = CreateUnmeasuredSnapshot(blocks);
        snapshot.EnableLazyLayout(Width, 0, 1, 0, CancellationToken.None);

        ProbeBlock failing = blocks[3];
        if (failArrange)
        {
            failing.SetMeasuredHeight(47);
            failing.ThrowAfterNextArrange = true;
        }
        else
        {
            failing.ThrowAfterMeasureMutation = true;
        }

        Rect[] oldRects = blocks.Select(block => block.Bounds).ToArray();
        bool[] oldMeasured = GetMeasuredMask(snapshot).ToArray();
        bool[] measuredMask = GetMeasuredMask(snapshot);
        Size oldSize = snapshot.Size;
        long oldRevision = snapshot.LayoutRevision;
        LayoutPlacementGeneration index = GetViewportIndex(snapshot);
        var oldIndexRects = Enumerable.Range(0, blocks.Length)
            .Select(index.GetIndexedBounds)
            .ToArray();

        InvalidOperationException failure = Assert.Throws<InvalidOperationException>(() =>
            snapshot.EnsureMeasuredBand(new LazyLayoutBand(96.25d, 97), CancellationToken.None));
        Assert.Equal(failArrange ? ProbeBlock.ArrangeFailure : ProbeBlock.MeasureFailure, failure.Message);

        AssertGeometryRecoveryReadGate(snapshot, blocks[3].Bounds.Top, blocks[3].Bounds.Bottom);
        Assert.Equal(oldSize, snapshot.Size);
        Assert.Equal(oldRevision, snapshot.LayoutRevision);
        Assert.Equal(oldMeasured.Count(value => value), snapshot.MeasuredTopLevelBlockCount);
        Assert.Same(measuredMask, GetMeasuredMask(snapshot));
        Assert.Equal(oldMeasured, GetMeasuredMask(snapshot));
        Assert.Same(index, GetViewportIndex(snapshot));
        // Per-root queries fail closed after a live layout failure. The
        // complete retained mask is compared above without asking the public
        // query to reveal state from a tainted tree.
        for (int ordinal = 0; ordinal < blocks.Length; ordinal++)
        {
            Assert.Equal(oldRects[ordinal], blocks[ordinal].Bounds);
            Assert.Equal(oldIndexRects[ordinal], index.GetIndexedBounds(ordinal));
            Assert.Equal(ordinal, index.GetBlockOrdinal(ordinal));
        }
    }

    [Fact]
    public void ShiftedViewportBandMeasuresRootsNewlyEnteringToFixedPoint()
    {
        var blocks = Enumerable.Range(0, 7)
            .Select(index => new ProbeBlock(index == 3 ? 5f : 32f) { BlockIndex = index })
            .ToArray();
        using var snapshot = CreateUnmeasuredSnapshot(blocks);
        snapshot.EnableLazyLayout(Width, 0, 1, 0, CancellationToken.None);
        long oldRevision = snapshot.LayoutRevision;

        // Only ordinal 3 intersects initially. Its shorter measured height
        // moves ordinal 4 from [128,160] into this band [96.25,110].
        snapshot.EnsureMeasuredBand(new LazyLayoutBand(96.25, 110), CancellationToken.None);

        Assert.True(snapshot.IsTopLevelBlockMeasured(blocks[3]));
        Assert.True(snapshot.IsTopLevelBlockMeasured(blocks[4]));
        Assert.False(snapshot.IsTopLevelBlockMeasured(blocks[5]));
        Assert.Equal(3, snapshot.MeasuredTopLevelBlockCount);
        Assert.Equal(oldRevision + 1, snapshot.LayoutRevision);
        Assert.Equal(new Rect(0, 96, Width, 5), blocks[3].Bounds);
        Assert.Equal(new Rect(0, 101, Width, 32), blocks[4].Bounds);
        Assert.Equal(new Rect(0, 133, Width, 32), blocks[5].Bounds);
        Assert.Equal(197, snapshot.Size.Height);
    }

    [Fact]
    public void CancellationAfterHeightShiftRestoresMaskBoundsIndexAndMetadata()
    {
        using var cancellation = new CancellationTokenSource();
        var blocks = Enumerable.Range(0, 7)
            .Select(index => new ProbeBlock(index == 3 ? 5f : 32f) { BlockIndex = index })
            .ToArray();
        using var snapshot = CreateUnmeasuredSnapshot(blocks);
        snapshot.EnableLazyLayout(Width, 0, 1, 0, CancellationToken.None);
        blocks[3].AfterMeasure = cancellation.Cancel;

        Rect[] oldRects = blocks.Select(block => block.Bounds).ToArray();
        bool[] oldMeasured = GetMeasuredMask(snapshot).ToArray();
        bool[] measuredMask = GetMeasuredMask(snapshot);
        Size oldSize = snapshot.Size;
        long oldRevision = snapshot.LayoutRevision;
        LayoutPlacementGeneration index = GetViewportIndex(snapshot);
        var oldIndexRects = Enumerable.Range(0, blocks.Length)
            .Select(index.GetIndexedBounds)
            .ToArray();

        Assert.ThrowsAny<OperationCanceledException>(() => snapshot.EnsureMeasuredBand(
            new LazyLayoutBand(96.25, 110), cancellation.Token));

        Assert.True(cancellation.IsCancellationRequested);
        AssertGeometryRecoveryReadGate(snapshot, 96.25, 110);
        Assert.Equal(oldSize, snapshot.Size);
        Assert.Equal(oldRevision, snapshot.LayoutRevision);
        Assert.Equal(oldMeasured.Count(value => value), snapshot.MeasuredTopLevelBlockCount);
        Assert.Same(measuredMask, GetMeasuredMask(snapshot));
        Assert.Equal(oldMeasured, GetMeasuredMask(snapshot));
        Assert.Same(index, GetViewportIndex(snapshot));
        for (int ordinal = 0; ordinal < blocks.Length; ordinal++)
        {
            Assert.Equal(oldRects[ordinal], blocks[ordinal].Bounds);
            Assert.Equal(oldIndexRects[ordinal], index.GetIndexedBounds(ordinal));
            Assert.Equal(ordinal, index.GetBlockOrdinal(ordinal));
        }
    }

    [Fact]
    public void RollbackArrangeRunsWithCancellationSuppressedForPreviouslyMeasuredSuffix()
    {
        using var cancellation = new CancellationTokenSource();
        var blocks = Enumerable.Range(0, 8)
            .Select(index => new ProbeBlock(32) { BlockIndex = index })
            .ToArray();
        using var snapshot = CreateUnmeasuredSnapshot(blocks);
        snapshot.EnableLazyLayout(Width, 0, 1, 0, CancellationToken.None);
        snapshot.EnsureMeasuredBand(new LazyLayoutBand(96.25d, 97), CancellationToken.None);
        Assert.True(snapshot.IsTopLevelBlockMeasured(blocks[3]));
        blocks[2].AfterMeasure = cancellation.Cancel;
        blocks[3].ThrowIfAmbientCancellationRequestedDuringArrange = true;

        Rect[] oldRects = blocks.Select(block => block.Bounds).ToArray();
        bool[] oldMeasured = GetMeasuredMask(snapshot).ToArray();
        bool[] measuredMask = GetMeasuredMask(snapshot);
        Size oldSize = snapshot.Size;
        long oldRevision = snapshot.LayoutRevision;
        LayoutPlacementGeneration index = GetViewportIndex(snapshot);
        var oldIndexRects = Enumerable.Range(0, blocks.Length)
            .Select(index.GetIndexedBounds)
            .ToArray();

        using (LayoutPassCancellation.Push(cancellation.Token))
        {
            Assert.ThrowsAny<OperationCanceledException>(() => snapshot.EnsureMeasuredBand(
                new LazyLayoutBand(64.25d, 64.5d), CancellationToken.None));
        }

        Assert.True(cancellation.IsCancellationRequested);
        AssertGeometryRecoveryReadGate(snapshot, 64.25d, 64.5d);
        Assert.Equal(oldSize, snapshot.Size);
        Assert.Equal(oldRevision, snapshot.LayoutRevision);
        Assert.Equal(oldMeasured.Count(value => value), snapshot.MeasuredTopLevelBlockCount);
        Assert.Same(measuredMask, GetMeasuredMask(snapshot));
        Assert.Equal(oldMeasured, GetMeasuredMask(snapshot));
        Assert.Same(index, GetViewportIndex(snapshot));
        for (int ordinal = 0; ordinal < blocks.Length; ordinal++)
        {
            Assert.Equal(oldRects[ordinal], blocks[ordinal].Bounds);
            Assert.Equal(oldIndexRects[ordinal], index.GetIndexedBounds(ordinal));
            Assert.Equal(ordinal, index.GetBlockOrdinal(ordinal));
        }
    }

    [Fact]
    public async Task PersistentMeasuredSuffixArrangeFailureFailsClosedAndRetiresCleanly()
    {
        var blocks = Enumerable.Range(0, 8)
            .Select(index => new ProbeBlock(32) { BlockIndex = index })
            .ToArray();
        var snapshot = CreateUnmeasuredSnapshot(blocks);
        snapshot.EnableLazyLayout(Width, 0, 1, 0, CancellationToken.None);
        snapshot.EnsureMeasuredBand(new LazyLayoutBand(96.25d, 97), CancellationToken.None);
        Assert.True(snapshot.IsTopLevelBlockMeasured(blocks[3]));
        blocks[2].SetMeasuredHeight(40);
        blocks[3].ThrowOnEveryArrange = true;

        AggregateException failure = Assert.Throws<AggregateException>(() =>
            snapshot.EnsureMeasuredBand(new LazyLayoutBand(64.25d, 64.5d), CancellationToken.None));
        Assert.Contains(failure.InnerExceptions, exception =>
            exception is InvalidOperationException { Message: ProbeBlock.ArrangeFailure });
        Assert.True(failure.InnerExceptions.Count >= 2);
        Assert.False(snapshot.TryBeginInteraction());
        Assert.False(snapshot.TryBeginPaint());

        await snapshot.Retire().WaitAsync(TimeSpan.FromSeconds(3));
        Assert.All(blocks, block => Assert.Equal(1, block.DisposeCount));
        snapshot.Dispose();
    }

    [Fact]
    public void NonDocumentOrderCustomPlacementRetainsFullRelayoutFallback()
    {
        var blocks = Enumerable.Range(0, 5)
            .Select(index => index == 0
                ? new OffsetProbeBlock(32, 64) { BlockIndex = index }
                : new OffsetProbeBlock(32, 0) { BlockIndex = index })
            .ToArray();
        using var snapshot = CreateEagerSnapshot(blocks, Width, default, 0);

        LayoutPlacementGeneration index = GetViewportIndex(snapshot);
        Assert.False(index.IsDocumentOrder);
        int firstArrangeCount = blocks[0].ArrangeCount;
        blocks[0].SetMeasuredHeight(40);

        snapshot.RelayoutChangedBlocks([blocks[0].BlockIndex], Width, CancellationToken.None);

        Assert.True(blocks[0].ArrangeCount > firstArrangeCount);
        Assert.False(GetViewportIndex(snapshot).IsDocumentOrder);
    }

    private static LayoutSnapshot CreateUnmeasuredSnapshot(
        ProbeBlock[] blocks,
        float width = Width,
        Thickness padding = default,
        double spacing = 0,
        SharedLayoutMetrics? sharedLayoutMetrics = null)
    {
        for (int index = 0; index < blocks.Length; index++)
            blocks[index].BlockIndex = index;

        return new LayoutSnapshot(
            blocks,
            new MarkdownSourceMap(string.Empty),
            width,
            height: 0,
            sharedLayoutMetrics: sharedLayoutMetrics,
            documentPadding: padding,
            blockSpacing: spacing);
    }

    private static LayoutSnapshot CreateEagerSnapshot(
        ProbeBlock[] blocks,
        float width,
        Thickness padding,
        double spacing)
    {
        float contentWidth = (float)System.Math.Max(1, width - padding.Left - padding.Right);
        float x = (float)padding.Left;
        float y = (float)padding.Top;
        float blockSpacing = (float)spacing;
        for (int ordinal = 0; ordinal < blocks.Length; ordinal++)
        {
            ProbeBlock block = blocks[ordinal];
            block.Measure(contentWidth);
            block.Arrange(x, y, contentWidth);
            y += (float)block.Bounds.Height;
            if (ordinal < blocks.Length - 1 &&
                !StackBox.SharesSafeHtmlInlineFlow(blocks[ordinal], blocks[ordinal + 1]))
            {
                y += blockSpacing;
            }
        }

        y += (float)padding.Bottom;
        return new LayoutSnapshot(
            blocks,
            new MarkdownSourceMap(string.Empty),
            width,
            y,
            documentPadding: padding,
            blockSpacing: spacing);
    }

    private static ProbeBlock[] CreateBlocks(float[] heights, int?[] flowScopes)
    {
        var blocks = new ProbeBlock[heights.Length];
        for (int index = 0; index < heights.Length; index++)
        {
            blocks[index] = new ProbeBlock(heights[index])
            {
                BlockIndex = index,
                SafeHtmlInlineFlowScopeId = flowScopes[index],
            };
        }

        return blocks;
    }

    private static LayoutPlacementGeneration GetViewportIndex(LayoutSnapshot snapshot)
        => snapshot.PublishedPlacementGeneration;

    private static bool[] GetMeasuredMask(LayoutSnapshot snapshot)
    {
        FieldInfo field = typeof(LayoutSnapshot).GetField(
            "_measuredTopLevelBlocks",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        return Assert.IsType<bool[]>(field.GetValue(snapshot));
    }

    private static void AssertGeometryRecoveryReadGate(LayoutSnapshot snapshot, double top, double bottom)
    {
        Assert.True(snapshot.GeometryRecoveryRequired);
        Assert.True(snapshot.TryClaimGeometryRecoveryRequest());
        Assert.False(snapshot.TryClaimGeometryRecoveryRequest());
        Assert.False(snapshot.TryBeginInteraction());
        Assert.False(snapshot.TryBeginPaint());
        Assert.False(snapshot.TryGetMeasuredTopLevelBlocksInBandForSceneProbe(
            top,
            bottom,
            out IReadOnlyList<BlockBox> blocks));
        Assert.Empty(blocks);
    }

    private class ProbeBlock(float height) : BlockBox
    {
        internal const string MeasureFailure = "probe measure failure";
        internal const string ArrangeFailure = "probe arrange failure";

        private float _measuredHeight = height;

        internal int MeasureCount { get; private set; }
        internal int ArrangeCount { get; private set; }
        internal int DisposeCount { get; private set; }
        internal bool ThrowAfterMeasureMutation { get; set; }
        internal bool ThrowAfterNextArrange { get; set; }
        internal bool ThrowOnEveryArrange { get; set; }
        internal bool ThrowIfAmbientCancellationRequestedDuringArrange { get; set; }
        internal Action? AfterMeasure { get; set; }

        internal void SetMeasuredHeight(float height) => _measuredHeight = height;

        internal void ResetCounts()
        {
            MeasureCount = 0;
            ArrangeCount = 0;
        }

        public override float Measure(float availableWidth)
        {
            MeasureCount++;
            Bounds = new Rect(13, 17, availableWidth, _measuredHeight + 19);
            if (ThrowAfterMeasureMutation)
                throw new InvalidOperationException(MeasureFailure);

            Bounds = new Rect(0, 0, availableWidth, _measuredHeight);
            AfterMeasure?.Invoke();
            return _measuredHeight;
        }

        public override void Arrange(float x, float y, float width)
        {
            ArrangeCount++;
            if (ThrowIfAmbientCancellationRequestedDuringArrange)
                LayoutPassCancellation.GetEffectiveToken(CancellationToken.None).ThrowIfCancellationRequested();

            base.Arrange(x, y, width);
            if (ThrowAfterNextArrange)
            {
                ThrowAfterNextArrange = false;
                Bounds = new Rect(x + 9, y + 11, width, Bounds.Height + 13);
                throw new InvalidOperationException(ArrangeFailure);
            }
            if (ThrowOnEveryArrange)
                throw new InvalidOperationException(ArrangeFailure);
        }

        public override void Paint(CanvasDrawingSession drawingSession, Rect viewport) { }

        public override void Dispose()
        {
            DisposeCount++;
        }
    }

    private sealed class OffsetProbeBlock(float height, float verticalOffset) : ProbeBlock(height)
    {
        public override void Arrange(float x, float y, float width)
        {
            base.Arrange(x, y + verticalOffset, width);
        }
    }
}
