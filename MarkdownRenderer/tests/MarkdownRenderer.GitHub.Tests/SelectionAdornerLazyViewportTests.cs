using MarkdownRenderer.Document;
using MarkdownRenderer.GitHub.Tests.TestSupport.Fakes;
using MarkdownRenderer.GitHub.Tests.TestSupport.Fixtures;
using MarkdownRenderer.Layout.Internal.LazyLayout;
using MarkdownRenderer.Layout.Internal.Snapshots;
using MarkdownRenderer.Selection;
using MarkdownRenderer.Selection.Internal;
using Windows.Foundation;
using Xunit.Abstractions;
using Xunit;
using static MarkdownRenderer.GitHub.Tests.TestSupport.Fixtures.SelectionAdornerViewportFixture;

namespace MarkdownRenderer.GitHub.Tests;

/// <summary>
/// Checks that viewport selection and point hit testing visit only
/// measured lazy roots in their viewport or point band, including in a
/// hundred-thousand-root document, and report the traversal diagnostic.
/// </summary>
public sealed class SelectionAdornerLazyViewportTests
{
    private readonly ITestOutputHelper _output;

    public SelectionAdornerLazyViewportTests(ITestOutputHelper output)
        => _output = output;

    [Fact]
    public void LazyViewportSelectionExcludesRootsThatHaveNotBeenMeasured()
    {
        var blocks = Enumerable.Range(0, 8)
            .Select(index => new CountingSelectionBox(
                index,
                new Rect(0, index * 20, 100, 12)))
            .ToArray();
        using var snapshot = new LayoutSnapshot(
            blocks,
            new MarkdownSourceMap(string.Empty),
            width: 100,
            height: 180);
        snapshot.EnableLazyLayout(
            availableWidth: 100,
            viewportTop: 10_000,
            viewportHeight: 60,
            overscan: 0,
            CancellationToken.None);

        SelectionController selection = SelectEveryBlock(blocks.Length);
        var viewport = new Rect(0, 18, 100, 38);
        List<Rect> beforeRealization = CollectVisible(selection, snapshot, viewport);
        Assert.Empty(beforeRealization);
        Assert.All(blocks, block => Assert.Equal(0, block.SelectionQueryCount));

        snapshot.EnsureMeasuredBand(new LazyLayoutBand(viewport.Top, viewport.Bottom), CancellationToken.None);
        List<Rect> expected = GetLegacyViewportRects(selection, snapshot, viewport);
        ResetCounts(blocks);

        List<Rect> afterRealization = CollectVisible(selection, snapshot, viewport);

        Assert.Equal(expected, afterRealization);
        Assert.InRange(afterRealization.Count, 1, 4);
        Assert.Equal(afterRealization.Count, blocks.Sum(block => block.SelectionQueryCount));
    }

    [Fact]
    public void PointHitExcludesUnmeasuredLazyRootsUntilTheyAreRealized()
    {
        var blocks = Enumerable.Range(0, 8)
            .Select(index => new CountingSelectionBox(
                index,
                new Rect(0, index * 20, 100, 12)))
            .ToArray();
        using var snapshot = new LayoutSnapshot(
            blocks,
            new MarkdownSourceMap(string.Empty),
            width: 100,
            height: 180);
        snapshot.EnableLazyLayout(
            availableWidth: 100,
            viewportTop: 10_000,
            viewportHeight: 60,
            overscan: 0,
            CancellationToken.None);
        SelectionController selection = SelectEveryBlock(blocks.Length);
        var viewport = new Rect(0, 18, 100, 38);
        Point initialPoint = new(50, 25);

        Assert.False(ContainsPointUnderReadLease(selection, snapshot, initialPoint, viewport, scale: 1));
        Assert.All(blocks, block => Assert.Equal(0, block.SelectionQueryCount));

        snapshot.EnsureMeasuredBand(new LazyLayoutBand(viewport.Top, viewport.Bottom), CancellationToken.None);
        Rect realizedTarget = blocks[1].Bounds;
        Point realizedPoint = new(
            realizedTarget.X + realizedTarget.Width / 2,
            realizedTarget.Y + realizedTarget.Height / 2);
        bool expected = LegacyContainsPoint(selection, snapshot, realizedPoint, scale: 1);
        Assert.True(expected);
        ResetCounts(blocks);

        bool actual = ContainsPointUnderReadLease(selection, snapshot, realizedPoint, viewport, scale: 1);

        Assert.Equal(expected, actual);
        Assert.Equal(1, blocks.Sum(block => block.SelectionQueryCount));
        Assert.Equal(1, blocks[1].SelectionQueryCount);
        Assert.All(
            blocks.Where(block => block.BlockIndex != 1),
            block => Assert.Equal(0, block.SelectionQueryCount));
    }

    [Fact]
    public void HundredThousandRootSelectionVisitsOnlyMeasuredViewportCandidates()
    {
        const int blockCount = 100_000;
        const double blockPitch = 18;
        var blocks = new CountingSelectionBox[blockCount];
        for (int index = 0; index < blocks.Length; index++)
        {
            double x = 1.25 + (index % 7) * 9.5;
            double y = index * blockPitch + 0.25;
            blocks[index] = new CountingSelectionBox(index, new Rect(x, y, 50.5, 11.25));
        }

        using var snapshot = new LayoutSnapshot(
            blocks,
            new MarkdownSourceMap(string.Empty),
            width: 256,
            height: (float)(blockCount * blockPitch + 20));
        SelectionController selection = SelectEveryBlock(blockCount);
        var viewport = new Rect(0, (blockCount / 2) * blockPitch + 1, 256, 126);

        List<Rect> expected = GetLegacyViewportRects(selection, snapshot, viewport);
        Assert.InRange(expected.Count, 1, 10);
        ResetCounts(blocks);

        List<Rect> actual = CollectVisible(selection, snapshot, viewport);

        Assert.Equal(expected, actual);
        Assert.Equal(expected.Count, blocks.Sum(block => block.SelectionQueryCount));
        Assert.All(
            blocks.Where(block => !Intersects(block.Bounds, viewport)),
            block => Assert.Equal(0, block.SelectionQueryCount));
    }

    [Fact]
    public void PointHitUsesCurrentRangeAndMatchesOutwardPixelSnapBeforeNextAdornerFrame()
    {
        CountingSelectionBox[] blocks =
        [
            new CountingSelectionBox(0, new Rect(10.13, 14.17, 20.13, 8.13)),
            new CountingSelectionBox(1, new Rect(47.23, 53.19, 18.27, 7.31)),
        ];
        using var snapshot = new LayoutSnapshot(
            blocks,
            new MarkdownSourceMap(string.Empty),
            width: 100,
            height: 80);
        var selection = new SelectionController();
        var viewport = new Rect(0, 0, 100, 80);

        foreach (double scale in RasterizationScales)
        {
            SetSingleBlockSelection(selection, blocks[0].BlockIndex);
            Rect rawFirst = blocks[0].Bounds;
            Rect snappedFirst = SnapToPhysicalPixels(rawFirst, scale);
            double fringeX = snappedFirst.Left + (rawFirst.Left - snappedFirst.Left) / 2;
            Point snappedFringePoint = new(fringeX, rawFirst.Top + rawFirst.Height / 2);
            Assert.True(fringeX < rawFirst.Left);
            Assert.True(snappedFirst.Contains(snappedFringePoint));

            // This is the rectangle set that a just-completed adorner frame
            // would have published. The next paint is deliberately not run.
            List<Rect> previouslyPaintedRects = GetLegacyViewportRects(selection, snapshot, viewport)
                .Select(rectangle => SnapToPhysicalPixels(rectangle, scale))
                .ToList();
            Assert.Equal(new[] { snappedFirst }, previouslyPaintedRects);
            Assert.True(previouslyPaintedRects[0].Contains(snappedFringePoint));
            Assert.Equal(
                LegacyContainsPoint(selection, snapshot, snappedFringePoint, scale),
                ContainsPointUnderReadLease(selection, snapshot, snappedFringePoint, viewport, scale));
            Assert.True(ContainsPointUnderReadLease(selection, snapshot, snappedFringePoint, viewport, scale));

            // A selection may straddle the viewport edge; a point on the
            // visible edge still hits when outward pixel snapping includes it.
            var clippedViewport = new Rect(15, 14, 20, 20);
            Point visibleTopEdge = new(clippedViewport.Left, clippedViewport.Top);
            Assert.True(snappedFirst.Contains(visibleTopEdge));
            Assert.Equal(
                LegacyContainsPoint(selection, snapshot, visibleTopEdge, scale),
                ContainsPointUnderReadLease(selection, snapshot, visibleTopEdge, clippedViewport, scale));
            Assert.True(ContainsPointUnderReadLease(selection, snapshot, visibleTopEdge, clippedViewport, scale));

            SetSingleBlockSelection(selection, blocks[1].BlockIndex);
            Rect rawSecond = blocks[1].Bounds;
            Rect snappedSecond = SnapToPhysicalPixels(rawSecond, scale);
            Point secondPoint = new(rawSecond.Left + rawSecond.Width / 2, rawSecond.Top + rawSecond.Height / 2);
            List<Rect> nextPaintRects = GetLegacyViewportRects(selection, snapshot, viewport)
                .Select(rectangle => SnapToPhysicalPixels(rectangle, scale))
                .ToList();

            Assert.Equal(new[] { snappedSecond }, nextPaintRects);
            Assert.Equal(new[] { snappedFirst }, previouslyPaintedRects);
            Assert.False(previouslyPaintedRects[0].Contains(secondPoint));
            Assert.False(ContainsPointUnderReadLease(selection, snapshot, snappedFringePoint, viewport, scale));
            Assert.Equal(
                LegacyContainsPoint(selection, snapshot, secondPoint, scale),
                ContainsPointUnderReadLease(selection, snapshot, secondPoint, viewport, scale));
            Assert.True(ContainsPointUnderReadLease(selection, snapshot, secondPoint, viewport, scale));

            // The right edge remains exclusive exactly as Rect.Contains and
            // the committed pixel-snapped adorner have always behaved.
            Point rightEdge = new(snappedSecond.Right, secondPoint.Y);
            Assert.Equal(
                LegacyContainsPoint(selection, snapshot, rightEdge, scale),
                ContainsPointUnderReadLease(selection, snapshot, rightEdge, viewport, scale));
        }
    }

    [Fact]
    public void PointHitVisitsOnlyItsMeasuredPointBandInAHundredThousandRootDocument()
    {
        const int blockCount = 100_000;
        const double blockPitch = 18;
        var blocks = new CountingSelectionBox[blockCount];
        for (int index = 0; index < blocks.Length; index++)
        {
            double x = 1.25 + (index % 7) * 9.5;
            double y = index * blockPitch + 0.25;
            blocks[index] = new CountingSelectionBox(index, new Rect(x, y, 50.5, 11.25));
        }

        using var snapshot = new LayoutSnapshot(
            blocks,
            new MarkdownSourceMap(string.Empty),
            width: 256,
            height: (float)(blockCount * blockPitch + 20));
        SelectionController selection = SelectEveryBlock(blockCount);
        const double scale = 1;
        const int targetIndex = blockCount / 2;
        Rect target = blocks[targetIndex].Bounds;
        Point point = new(target.X + target.Width / 2, target.Y + target.Height / 2);
        var viewport = new Rect(0, point.Y - 63, 256, 126);

        bool expected = LegacyContainsPoint(selection, snapshot, point, scale);
        Assert.True(expected);
        ResetCounts(blocks);

        bool actual = ContainsPointUnderReadLease(selection, snapshot, point, viewport, scale);

        Assert.Equal(expected, actual);
        Assert.Equal(1, blocks[targetIndex].SelectionQueryCount);
        Assert.Equal(1, blocks.Sum(block => block.SelectionQueryCount));
        Assert.All(
            blocks.Where(block => block.BlockIndex != targetIndex),
            block => Assert.Equal(0, block.SelectionQueryCount));
    }

    [Fact]
    public void HundredThousandRootViewportTraversalReportsCounterorderedAlgorithmDiagnostic()
    {
        const int blockCount = 100_000;
        const int iterations = 12;
        const double blockPitch = 18;
        var blocks = new CountingSelectionBox[blockCount];
        for (int index = 0; index < blocks.Length; index++)
        {
            double x = 1.25 + (index % 7) * 9.5;
            double y = index * blockPitch + 0.25;
            blocks[index] = new CountingSelectionBox(index, new Rect(x, y, 50.5, 11.25));
        }

        using var snapshot = new LayoutSnapshot(
            blocks,
            new MarkdownSourceMap(string.Empty),
            width: 256,
            height: (float)(blockCount * blockPitch + 20));
        SelectionController selection = SelectEveryBlock(blockCount);
        var viewport = new Rect(0, (blockCount / 2) * blockPitch + 1, 256, 126);
        List<Rect> expected = GetLegacyViewportRects(selection, snapshot, viewport);
        var destination = new List<Rect>(expected.Count + 2);
        VisibleAppendDelegate? append = TryCreateVisibleAppendDelegate(selection);
        bool forceLegacy = string.Equals(
            Environment.GetEnvironmentVariable("MARKDOWN_SELECTION_ADORNER_DIAGNOSTIC_LEGACY"),
            "1",
            StringComparison.Ordinal);
        string algorithm = append is not null && !forceLegacy ? "viewport-index" : "full-tree-filter";

        for (int warmup = 0; warmup < 2; warmup++)
            CollectDiagnosticIteration(selection, snapshot, viewport, destination, append, algorithm);
        ResetCounts(blocks);

        long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        for (int iteration = 0; iteration < iterations; iteration++)
            CollectDiagnosticIteration(selection, snapshot, viewport, destination, append, algorithm);
        stopwatch.Stop();
        long allocatedBytes = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;

        Assert.Equal(expected, destination);
        int selectionRectQueries = blocks.Sum(block => block.SelectionQueryCount);
        int viewportCandidateCount = expected.Count;
        if (algorithm == "viewport-index")
            Assert.Equal(iterations * viewportCandidateCount, selectionRectQueries);
        else
            Assert.Equal(iterations * blockCount, selectionRectQueries);

        _output.WriteLine(
            "selection-adorners;roots={0};iterations={1};algorithm={2};visibleRects={3};" +
            "leafQueries={4};allocatedBytes={5};elapsedMilliseconds={6:F3};releaseEvidence=false",
            blockCount,
            iterations,
            algorithm,
            expected.Count,
            selectionRectQueries,
            allocatedBytes,
            stopwatch.Elapsed.TotalMilliseconds);
    }

    private static void SetSingleBlockSelection(SelectionController selection, int blockIndex)
    {
        selection.SetAnchor(new DocumentPosition(blockIndex, 0, 0));
        selection.ExtendTo(new DocumentPosition(blockIndex, 0, 1));
    }

    private static void CollectDiagnosticIteration(
        SelectionController selection,
        LayoutSnapshot snapshot,
        Rect viewport,
        List<Rect> destination,
        VisibleAppendDelegate? append,
        string algorithm)
    {
        destination.Clear();
        if (algorithm == "viewport-index" && append is not null)
        {
            if (!snapshot.TryBeginPaint())
                throw new InvalidOperationException("The diagnostic snapshot was unexpectedly busy.");
            try
            {
                append(snapshot, viewport, destination);
            }
            finally
            {
                snapshot.EndPaint();
            }
            return;
        }

        foreach (Rect rectangle in selection.GetHighlightRects(snapshot))
        {
            if (Intersects(rectangle, viewport))
                destination.Add(rectangle);
        }
    }

    private static VisibleAppendDelegate? TryCreateVisibleAppendDelegate(SelectionController selection)
        => selection.AppendVisibleHighlightRects;

    private delegate void VisibleAppendDelegate(
        LayoutSnapshot snapshot,
        Rect viewport,
        List<Rect> destination);
}
