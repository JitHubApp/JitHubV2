using MarkdownRenderer.Document;
using MarkdownRenderer.Layout;
using Microsoft.Graphics.Canvas;
using Windows.Foundation;
using Xunit;

namespace MarkdownRenderer.GitHub.Tests;

public sealed class LayoutSnapshotScrollAnchorIndexTests
{
    [Fact]
    public void FirstBottomLookupPreservesInclusiveEdgesAndSelectsNextBlockAcrossGaps()
    {
        AnchorBlock first = CreateBlock(101, top: 0, height: 10);
        AnchorBlock second = CreateBlock(205, top: 20, height: 10);
        AnchorBlock third = CreateBlock(309, top: 50, height: 10);
        using var snapshot = new LayoutSnapshot(
            [first, second, third],
            new MarkdownSourceMap(string.Empty),
            width: 100,
            height: 70);

        Assert.True(snapshot.TryCaptureScrollAnchor(10, out SnapshotScrollAnchor atFirstBottom));
        Assert.Equal(101, atFirstBottom.BlockIndex);
        Assert.Equal(-10, atFirstBottom.OffsetFromTop);

        // The offset lies in the gap after the second block. The anchor remains
        // the first later block whose bottom reaches the offset, not null.
        Assert.True(snapshot.TryCaptureScrollAnchor(31, out SnapshotScrollAnchor inGap));
        Assert.Equal(309, inGap.BlockIndex);
        Assert.Equal(19, inGap.OffsetFromTop);

        Assert.True(snapshot.TryCaptureScrollAnchor(61, out SnapshotScrollAnchor afterDocument));
        Assert.Null(afterDocument.BlockIndex);
        Assert.Equal(0, afterDocument.OffsetFromTop);
    }

    [Fact]
    public void CustomVisualOrderRetainsLegacyDocumentOrderAnchorChoice()
    {
        AnchorBlock firstInDocument = CreateBlock(101, top: 40, height: 10);
        AnchorBlock secondInDocument = CreateBlock(205, top: 0, height: 10);
        using var snapshot = new LayoutSnapshot(
            [firstInDocument, secondInDocument],
            new MarkdownSourceMap(string.Empty),
            width: 100,
            height: 60);

        Assert.True(snapshot.TryCaptureScrollAnchor(5, out SnapshotScrollAnchor anchor));
        Assert.Equal(101, anchor.BlockIndex);
        Assert.Equal(35, anchor.OffsetFromTop);
    }

    [Fact]
    public void ScrollAnchorTargetUsesIndexedTopLevelIdentityAndRejectsMissingIdentity()
    {
        AnchorBlock first = CreateBlock(101, top: 0, height: 10);
        AnchorBlock second = CreateBlock(205, top: 20, height: 10);
        using var snapshot = new LayoutSnapshot(
            [first, second],
            new MarkdownSourceMap(string.Empty),
            width: 100,
            height: 40);

        Assert.True(snapshot.TryCaptureScrollAnchorTarget(205, offsetFromTop: 4, out double target));
        Assert.Equal(16, target);
        Assert.False(snapshot.TryCaptureScrollAnchorTarget(999, offsetFromTop: 4, out _));
    }

    [Fact]
    public void LargeViewportIndexResolvesGapAndEndQueries()
    {
        const int count = 100_000;
        var index = new ViewportBandIndex(count);
        for (int ordinal = 0; ordinal < count; ordinal++)
        {
            double top = ordinal * 3d;
            index.SetEntry(ordinal, ordinal, top, top + 1);
        }
        index.Commit();

        int finalOrdinal = count - 1;
        double gapOffset = finalOrdinal * 3d - 1;
        Assert.True(index.TryFindFirstBottomAtOrAfter(gapOffset, out int nextOrdinal));
        Assert.Equal(finalOrdinal, nextOrdinal);
        Assert.True(index.TryFindFirstBottomAtOrAfter(finalOrdinal * 3d + 1, out int atInclusiveBottom));
        Assert.Equal(finalOrdinal, atInclusiveBottom);
        Assert.False(index.TryFindFirstBottomAtOrAfter(finalOrdinal * 3d + 2, out _));
    }

    private static AnchorBlock CreateBlock(int blockIndex, float top, float height)
    {
        var block = new AnchorBlock(height) { BlockIndex = blockIndex };
        block.Measure(100);
        block.Arrange(0, top, 100);
        return block;
    }

    private sealed class AnchorBlock(float height) : BlockBox
    {
        public override float Measure(float availableWidth)
        {
            Bounds = new Rect(Bounds.X, Bounds.Y, availableWidth, height);
            return height;
        }

        public override void Paint(CanvasDrawingSession drawingSession, Rect viewport) { }
    }
}
