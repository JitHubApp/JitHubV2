using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Text;
using MarkdownRenderer.CodeBlocks;
using MarkdownRenderer.Document;
using MarkdownRenderer.Layout;
using MarkdownRenderer.Layout.Boxes;
using MarkdownRenderer.Parsing;
using MarkdownRenderer.Selection;
using MarkdownRenderer.Theming;
using Windows.Foundation;
using Microsoft.UI.Xaml;
using Windows.UI;
using Xunit.Abstractions;
using Xunit;

namespace MarkdownRenderer.GitHub.Tests;

public sealed class SelectionAdornerViewportParityTests
{
    private static readonly double[] RasterizationScales = [1, 1.25, 1.5, 1.75, 2, 2.25, 3, 4];
    private readonly ITestOutputHelper _output;

    public SelectionAdornerViewportParityTests(ITestOutputHelper output)
        => _output = output;

    [Fact]
    public void CodeBlockClipPreservesUntouchedAxisAndReconstructsClippedAxis()
    {
        var source = new Rect(62, 114, 142.94922, 18.199999);

        Rect horizontal = CodeBlockBox.Intersect(source, new Rect(0, 100, 176, 100));

        Assert.Equal(source.Y, horizontal.Y);
        Assert.Equal(
            BitConverter.DoubleToInt64Bits(source.Height),
            BitConverter.DoubleToInt64Bits(horizontal.Height));
        Assert.Equal(new Rect(62, 114, 114, source.Height), horizontal);

        Rect vertical = CodeBlockBox.Intersect(source, new Rect(0, 120, 256, 20));

        Assert.Equal(120, vertical.Y);
        Assert.Equal(source.Bottom - vertical.Top, vertical.Height);
        Assert.True(vertical.Height < source.Height);
    }

    [Fact]
    public void ViewportSelectionMatchesFullTreeOracleForCustomVisualOrderAndAllScaleDirections()
    {
        foreach (FlowDirection flowDirection in new[]
                 {
                     FlowDirection.LeftToRight,
                     FlowDirection.RightToLeft,
                 })
        {
            CountingSelectionBox[] blocks =
            [
                CreateBox(0, top: 40.25, x: 8.25, flowDirection),
                CreateBox(1, top: 0.25, x: 28.5, flowDirection),
                CreateBox(2, top: 20.5, x: 48.75, flowDirection),
                CreateBox(3, top: 80.75, x: 68.25, flowDirection),
                CreateBox(4, top: 60.25, x: 88.5, flowDirection),
            ];
            using var snapshot = new LayoutSnapshot(
                blocks,
                new MarkdownSourceMap(string.Empty),
                width: 200,
                height: 120);
            SelectionController selection = SelectEveryBlock(blocks.Length);
            var viewport = new Rect(0, 20.5, 180, 60);

            List<Rect> legacy = GetLegacyViewportRects(selection, snapshot, viewport);
            int[] expectedOrdinals = blocks
                .Where(block => Intersects(block.Bounds, viewport))
                .Select(block => block.BlockIndex)
                .ToArray();
            Assert.Equal(new[] { 0, 2, 4 }, expectedOrdinals);
            ResetCounts(blocks);

            List<Rect> actual = CollectVisible(selection, snapshot, viewport);
            Assert.Equal(legacy, actual);
            Assert.Equal(
                expectedOrdinals,
                actual.Select(rect => blocks.Single(block => block.SelectionRect == rect).BlockIndex));

            foreach (double scale in RasterizationScales)
            {
                Rect[] expectedPixels = legacy.Select(rect => SnapToPhysicalPixels(rect, scale)).ToArray();
                Rect[] actualPixels = actual.Select(rect => SnapToPhysicalPixels(rect, scale)).ToArray();
                Assert.Equal(expectedPixels, actualPixels);
            }

            // A viewport index sorts by visual Y. Selection handles and paint
            // order retain the old document-order sequence, including custom
            // boxes whose visual order differs from their ordinal.
        }
    }

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
    public void TouchAndRightTapHitTestingUsesTheAdmittedSnapshotAndCurrentSelection()
    {
        string control = ReadRepositorySource(
            "MarkdownRenderer", "MarkdownRenderer", "Controls", "MarkdownRendererControl.cs");
        string pointerInput = ReadRepositorySource(
            "MarkdownRenderer", "MarkdownRenderer", "Controls", "MarkdownRendererControl.PointerInput.cs");

        string pointQuery = GetMethodBody(control, "private bool IsPointInsideSelection(LayoutSnapshot snapshot, Point point)");
        Assert.Contains("_selection.ContainsPointUnderReadLease(", pointQuery, StringComparison.Ordinal);
        Assert.Contains("snapshot,", pointQuery, StringComparison.Ordinal);
        Assert.DoesNotContain("_selectionAdornerRects", pointQuery, StringComparison.Ordinal);

        string tapped = GetMethodBody(control, "private void ProcessTapped(ref PointerInput e)");
        string doubleTapped = GetMethodBody(control, "private void ProcessDoubleTapped(ref PointerInput e)");
        string holding = GetMethodBody(control, "private void ProcessHolding(ref PointerInput e)");
        string rightTapped = GetMethodBody(control, "private void ProcessRightTapped(ref PointerInput e)");
        Assert.Contains("e.InteractionSnapshot is not { } snapshot", tapped, StringComparison.Ordinal);
        Assert.Contains("IsPointInsideSelection(snapshot, point)", tapped, StringComparison.Ordinal);
        Assert.DoesNotContain("_selectionAdornerRects", tapped, StringComparison.Ordinal);
        Assert.Contains("e.InteractionSnapshot is not { } snapshot", rightTapped, StringComparison.Ordinal);
        Assert.Contains("IsPointInsideSelection(snapshot, pt)", rightTapped, StringComparison.Ordinal);
        Assert.DoesNotContain("_selectionAdornerRects", rightTapped, StringComparison.Ordinal);
        Assert.DoesNotMatch(@"\b_snapshot\b", rightTapped);
        int rightTapEmbedGuard = rightTapped.IndexOf("IsPointOverEmbed(pt)", StringComparison.Ordinal);
        int rightTapCodeActionGuard = rightTapped.IndexOf("IsPointOverCodeBlockAction(pt)", StringComparison.Ordinal);
        int rightTapSelectionHit = rightTapped.IndexOf("IsPointInsideSelection(snapshot, pt)", StringComparison.Ordinal);
        Assert.True(rightTapEmbedGuard >= 0 && rightTapCodeActionGuard > rightTapEmbedGuard);
        Assert.True(rightTapSelectionHit > rightTapCodeActionGuard);
        Assert.Contains("IsPointOverEmbed(point)", doubleTapped, StringComparison.Ordinal);
        Assert.Contains("IsPointOverCodeBlockAction(point)", doubleTapped, StringComparison.Ordinal);
        Assert.Contains("IsPointOverEmbed(point)", holding, StringComparison.Ordinal);
        Assert.Contains("IsPointOverCodeBlockAction(point)", holding, StringComparison.Ordinal);

        string touchHitTest = GetMethodBody(control,
            "private bool TryHitTestTouchSelection(");
        int directSelectableHit = touchHitTest.IndexOf("IsSelectableTouchPoint(gesturePoint, snapshot, out position)", StringComparison.Ordinal);
        int sharedContactPolicy = touchHitTest.IndexOf("IsBoundedGestureContact(gesturePoint, contact)", directSelectableHit, StringComparison.Ordinal);
        int contactProbes = touchHitTest.IndexOf("IsSelectableTouchPoint(center, snapshot, out position)", sharedContactPolicy, StringComparison.Ordinal);
        Assert.True(directSelectableHit >= 0 && sharedContactPolicy > directSelectableHit);
        Assert.True(contactProbes > sharedContactPolicy);
        Assert.Contains("IsBoundedGestureContact(gestureCanvas, capturedContact)",
            GetMethodBody(pointerInput, "internal static bool TryCreateGestureHitTestBand("),
            StringComparison.Ordinal);
        string sharedContactValidator = GetMethodBody(pointerInput, "private static bool IsBoundedGestureContact(");
        Assert.Contains("contact.Width > 256 || contact.Height > 256", sharedContactValidator, StringComparison.Ordinal);
        Assert.Contains("dx * dx + dy * dy <= 256 * 256", sharedContactValidator, StringComparison.Ordinal);

        string recognizedGesture = GetMethodBody(pointerInput, "private bool DispatchRecognizedGesture(");
        string pointerDispatch = GetMethodBody(pointerInput, "private void DispatchPointerInput(");
        AssertInteractionLeasePrecedesHitDispatch(recognizedGesture);
        AssertInteractionLeasePrecedesHitDispatch(pointerDispatch);
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

    [Theory]
    [InlineData(FlowDirection.LeftToRight, 1f)]
    [InlineData(FlowDirection.RightToLeft, 1f)]
    [InlineData(FlowDirection.LeftToRight, 1.5f)]
    [InlineData(FlowDirection.RightToLeft, 1.5f)]
    [InlineData(FlowDirection.LeftToRight, 2f)]
    [InlineData(FlowDirection.RightToLeft, 2f)]
    public async Task NativeWarpTableAndMultichunkCodeSelectionMatchesLegacyAfterHorizontalScroll(
        FlowDirection flowDirection,
        float rasterizationScale)
    {
        using var device = new CanvasDevice(forceSoftwareRenderer: true);
        Assert.True(device.ForceSoftwareRenderer);
        await using NativeOverflowFixture fixture = CreateNativeOverflowFixture(
            device,
            flowDirection,
            rasterizationScale);

        Assert.True(fixture.Table.CanScrollHorizontally);
        Assert.True(fixture.Code.CanScrollHorizontally);
        Assert.True(fixture.Table.SetHorizontalOffset(fixture.Table.HorizontalExtent * 0.37));
        Assert.True(fixture.Code.SetHorizontalOffset(fixture.Code.HorizontalExtent * 0.29));
        Assert.True(fixture.Table.HorizontalOffset > 0);
        Assert.True(fixture.Code.HorizontalOffset > 0);

        var tableViewport = new Rect(
            0,
            fixture.Table.Bounds.Top,
            fixture.Width,
            fixture.Table.Bounds.Height);
        var codeViewport = new Rect(
            0,
            fixture.Code.Bounds.Top + fixture.Code.Bounds.Height * 0.32,
            fixture.Width,
            fixture.Code.Bounds.Height * 0.45);

        Assert.True(fixture.Code.Chunks.Count >= 3);
        Assert.True(Intersects(fixture.CodeViewportBounds, codeViewport));
        Assert.Equal(flowDirection == FlowDirection.RightToLeft, fixture.Table.IsRightToLeft);
        Assert.Equal(flowDirection == FlowDirection.RightToLeft, fixture.Code.IsRightToLeft);

        AssertVisibleParityAndPixels(fixture, tableViewport, rasterizationScale);
        AssertVisibleParityAndPixels(fixture, codeViewport, rasterizationScale);
    }

    [Theory]
    [InlineData(1f)]
    [InlineData(1.5f)]
    [InlineData(2f)]
    public async Task RtlUnequalWidthCodeChunksMatchDirectWriteAtZeroMiddleAndMaximumScroll(
        float rasterizationScale)
    {
        using var device = new CanvasDevice(forceSoftwareRenderer: true);
        Assert.True(device.ForceSoftwareRenderer);
        await using RtlNativeCodeFixture fixture = CreateRtlNativeCodeFixture(
            device,
            rasterizationScale,
            CanvasWordWrapping.NoWrap,
            includeUnbreakableToken: true);

        Assert.True(fixture.Code.CanScrollHorizontally);
        Assert.True(fixture.Code.IsRightToLeft);
        Assert.True(fixture.Code.Chunks.Select(chunk => chunk.CodeBlockTextLength).Distinct().Count() > 1);
        Assert.True(fixture.Code.Chunks.Select(chunk => chunk.ContentWidth).Distinct().Count() > 1);

        double maximum = fixture.Code.HorizontalExtent - fixture.Code.HorizontalViewport;
        Assert.True(maximum > 0.5);
        foreach (double offset in new[] { 0, maximum / 2, maximum })
        {
            fixture.Code.SetHorizontalOffset(offset);
            Assert.Equal(offset, fixture.Code.HorizontalOffset);
            AssertCodeChunksMatchDirectWrite(fixture, rasterizationScale);
        }
    }

    [Theory]
    [InlineData(1f)]
    [InlineData(1.5f)]
    [InlineData(2f)]
    public async Task LtrUnequalWidthCodeChunksMatchDirectWriteAtZeroMiddleAndMaximumScroll(
        float rasterizationScale)
    {
        using var device = new CanvasDevice(forceSoftwareRenderer: true);
        Assert.True(device.ForceSoftwareRenderer);
        await using RtlNativeCodeFixture fixture = CreateRtlNativeCodeFixture(
            device,
            rasterizationScale,
            CanvasWordWrapping.NoWrap,
            includeUnbreakableToken: true,
            flowDirection: FlowDirection.LeftToRight);

        Assert.True(fixture.Code.CanScrollHorizontally);
        Assert.False(fixture.Code.IsRightToLeft);
        Assert.True(fixture.Code.Chunks.Select(chunk => chunk.CodeBlockTextLength).Distinct().Count() > 1);
        Assert.True(fixture.Code.Chunks.Select(chunk => chunk.ContentWidth).Distinct().Count() > 1);

        double maximum = fixture.Code.HorizontalExtent - fixture.Code.HorizontalViewport;
        Assert.True(maximum > 0.5);
        foreach (double offset in new[] { 0, maximum / 2, maximum })
        {
            fixture.Code.SetHorizontalOffset(offset);
            Assert.Equal(offset, fixture.Code.HorizontalOffset);
            AssertCodeChunksMatchDirectWrite(fixture, rasterizationScale);
        }
    }

    [Theory]
    [InlineData(true, 1f)]
    [InlineData(true, 1.5f)]
    [InlineData(true, 2f)]
    [InlineData(false, 1f)]
    [InlineData(false, 1.5f)]
    [InlineData(false, 2f)]
    public async Task RtlWrappedCodeWithLongTokenAndOrdinaryLinesMatchesDirectWriteViewport(
        bool includeUnbreakableToken,
        float rasterizationScale)
    {
        using var device = new CanvasDevice(forceSoftwareRenderer: true);
        Assert.True(device.ForceSoftwareRenderer);
        await using RtlNativeCodeFixture fixture = CreateRtlNativeCodeFixture(
            device,
            rasterizationScale,
            CanvasWordWrapping.Wrap,
            includeUnbreakableToken);

        Assert.All(fixture.Code.Chunks, chunk => Assert.Equal(CanvasWordWrapping.Wrap, chunk.WordWrapping));
        if (!includeUnbreakableToken)
            Assert.False(fixture.Code.CanScrollHorizontally);

        double maximum = global::System.Math.Max(
            0,
            fixture.Code.HorizontalExtent - fixture.Code.HorizontalViewport);
        double[] offsets = maximum > 0.5
            ? [0, maximum / 2, maximum]
            : [0];
        foreach (double offset in offsets)
        {
            fixture.Code.SetHorizontalOffset(offset);
            Assert.Equal(offset, fixture.Code.HorizontalOffset);
            AssertCodeChunksMatchDirectWrite(fixture, rasterizationScale);
        }
    }

    [Fact]
    public async Task OffscreenAtomicImageEndpointUsesItsIndexedOwnerNotFirstVisibleSelectionRect()
    {
        using var device = new CanvasDevice(forceSoftwareRenderer: true);
        Assert.True(device.ForceSoftwareRenderer);
        MarkdownLayoutContext context = CreateEndpointContext(device);
        var image = new ImageBox(context, string.Empty, "offscreen image") { BlockIndex = 1 };
        image.Measure(176);
        image.Arrange(0, 0, 176);
        Assert.True(image.Bounds.Height > 0);

        var visible = new CountingSelectionBox(2, new Rect(0, 110, 176, 20));
        visible.Measure(176);
        visible.Arrange(0, 110, 176);
        using var snapshot = new LayoutSnapshot(
            new BlockBox[] { image, visible },
            context.SourceMap,
            width: 176,
            height: 160);
        var selection = new SelectionController();
        selection.SetAnchor(new DocumentPosition(1, 0, 0));
        selection.ExtendTo(new DocumentPosition(2, 0, 1));
        var viewport = new Rect(0, 100, 176, 44);

        Assert.False(Intersects(image.Bounds, viewport));
        List<Rect> visibleRects = CollectVisible(selection, snapshot, viewport);
        Assert.Equal(new[] { visible.SelectionRect }, visibleRects);

        Assert.True(snapshot.TryBeginPaint());
        try
        {
            Assert.True(selection.TryGetSelectionEndpointGeometry(
                snapshot,
                new DocumentPosition(1, 0, 0),
                selection.Range,
                viewport,
                verticalSearchMargin: 64,
                rangeStart: true,
                out _,
                out Rect endpointRect,
                out bool hasCaret));
            Assert.False(hasCaret);
            Assert.Equal(image.Bounds, endpointRect);
            Assert.NotEqual(visibleRects[0], endpointRect);
        }
        finally
        {
            snapshot.EndPaint();
        }

        await snapshot.Retire().WaitAsync(TimeSpan.FromSeconds(10));
        await context.DrainOwnedRetirementsAsync().WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Theory]
    [InlineData(FlowDirection.LeftToRight)]
    [InlineData(FlowDirection.RightToLeft)]
    public void SafeHtmlStackSelectionUsesChildViewportRangeAndMatchesFullTraversal(FlowDirection flowDirection)
    {
        const int childCount = 256;
        var children = new CountingSelectionBox[childCount];
        var stack = new StackBox
        {
            BlockIndex = 0,
            FlowDirection = flowDirection,
            SafeHtmlInlineFlowScopeId = 31,
            HasResolvedSafeHtmlAlignment = true,
        };

        for (int index = 0; index < childCount; index++)
        {
            var child = new CountingSelectionBox(
                index + 1,
                new Rect(0, 0, 240, 9.5));
            children[index] = child;
            stack.Add(child);
        }

        stack.Measure(240);
        stack.Arrange(0, 0, 240);
        using var snapshot = new LayoutSnapshot(
            new BlockBox[] { stack },
            new MarkdownSourceMap(string.Empty),
            width: 240,
            height: (float)stack.Bounds.Height);
        SelectionController selection = SelectEveryBlock(childCount, firstBlockIndex: 1);
        double viewportTop = children[childCount / 2].Bounds.Top + 1;
        var viewport = new Rect(0, viewportTop, 240, 34);

        List<Rect> expected = GetLegacyViewportRects(selection, snapshot, viewport);
        ResetCounts(children);

        List<Rect> actual = CollectVisible(selection, snapshot, viewport);

        Assert.Equal(expected, actual);
        Assert.Equal(expected.Count, children.Sum(child => child.SelectionQueryCount));
        Assert.All(
            children.Where(child => !Intersects(child.Bounds, viewport)),
            child => Assert.Equal(0, child.SelectionQueryCount));
    }

    private static CountingSelectionBox CreateBox(int index, double top, double x, FlowDirection flowDirection)
    {
        if (flowDirection == FlowDirection.RightToLeft)
            x = 176 - x - 15.75;

        return new CountingSelectionBox(
            index,
            new Rect(x, top, 15.75, 7.25));
    }

    private static SelectionController SelectEveryBlock(int blockCount, int firstBlockIndex = 0)
    {
        var selection = new SelectionController();
        selection.SetAnchor(new DocumentPosition(firstBlockIndex, 0, 0));
        selection.ExtendTo(new DocumentPosition(firstBlockIndex + blockCount - 1, 0, 1));
        return selection;
    }

    private static void SetSingleBlockSelection(SelectionController selection, int blockIndex)
    {
        selection.SetAnchor(new DocumentPosition(blockIndex, 0, 0));
        selection.ExtendTo(new DocumentPosition(blockIndex, 0, 1));
    }

    private static bool ContainsPointUnderReadLease(
        SelectionController selection,
        LayoutSnapshot snapshot,
        Point point,
        Rect viewport,
        double scale)
    {
        Assert.True(snapshot.TryBeginPaint());
        try
        {
            return selection.ContainsPointUnderReadLease(snapshot, point, viewport, scale);
        }
        finally
        {
            snapshot.EndPaint();
        }
    }

    private static bool LegacyContainsPoint(
        SelectionController selection,
        LayoutSnapshot snapshot,
        Point point,
        double scale)
    {
        foreach (Rect rectangle in selection.GetHighlightRects(snapshot))
        {
            if (SnapToPhysicalPixels(rectangle, scale).Contains(point))
                return true;
        }

        return false;
    }

    private static void AssertInteractionLeasePrecedesHitDispatch(string method)
    {
        int lease = method.IndexOf("snapshot.TryBeginInteraction()", StringComparison.Ordinal);
        int snapshotCapture = method.IndexOf("input.InteractionSnapshot = snapshot", StringComparison.Ordinal);
        int dispatch = method.IndexOf("ProcessPointerInput(ref input)", StringComparison.Ordinal);
        Assert.True(lease >= 0 && snapshotCapture > lease && dispatch > snapshotCapture);
    }

    private static List<Rect> GetLegacyViewportRects(
        SelectionController selection,
        LayoutSnapshot snapshot,
        Rect viewport)
    {
        var result = new List<Rect>();
        foreach (Rect rect in selection.GetHighlightRects(snapshot))
        {
            if (Intersects(rect, viewport))
                result.Add(rect);
        }

        return result;
    }

    private static List<Rect> CollectVisible(
        SelectionController selection,
        LayoutSnapshot snapshot,
        Rect viewport)
    {
        Assert.True(snapshot.TryBeginPaint());
        try
        {
            var result = new List<Rect>();
            selection.AppendVisibleHighlightRects(snapshot, viewport, result);
            return result;
        }
        finally
        {
            snapshot.EndPaint();
        }
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
    {
        var method = typeof(SelectionController).GetMethod(
            "AppendVisibleHighlightRects",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic,
            binder: null,
            types: [typeof(LayoutSnapshot), typeof(Rect), typeof(List<Rect>)],
            modifiers: null);
        return method is null
            ? null
            : (VisibleAppendDelegate)method.CreateDelegate(typeof(VisibleAppendDelegate), selection);
    }

    private static void AssertVisibleParityAndPixels(
        NativeOverflowFixture fixture,
        Rect viewport,
        float rasterizationScale)
    {
        List<Rect> legacy = GetLegacyViewportRects(fixture.Selection, fixture.Snapshot, viewport);
        List<Rect> actual = CollectVisible(fixture.Selection, fixture.Snapshot, viewport);
        Assert.True(
            legacy.Count > 0,
            legacy.Count == 0
                ? DescribeEmptyLegacyRectResult(fixture, viewport, rasterizationScale)
                : string.Empty);
        Assert.Equal(legacy, actual);

        Color[] expectedPixels = RasterizeSelectionRects(
            fixture.Device,
            legacy,
            viewport,
            rasterizationScale,
            fixture.Snapshot,
            fixture.Selection.Range);
        Color[] actualPixels = RasterizeSelectionRects(
            fixture.Device,
            actual,
            viewport,
            rasterizationScale,
            fixture.Snapshot,
            fixture.Selection.Range);
        Assert.Equal(expectedPixels, actualPixels);

        int checkedPoints = 0;
        foreach (Rect rectangle in legacy)
        {
            Rect snapped = SnapToPhysicalPixels(rectangle, rasterizationScale);
            Rect visible = IntersectForTest(snapped, viewport);
            if (visible.Width <= 0 || visible.Height <= 0)
                continue;

            Point point = new(visible.X + visible.Width / 2, visible.Y + visible.Height / 2);
            bool expectedHit = LegacyContainsPoint(fixture.Selection, fixture.Snapshot, point, rasterizationScale);
            bool actualHit = ContainsPointUnderReadLease(
                fixture.Selection,
                fixture.Snapshot,
                point,
                viewport,
                rasterizationScale);
            Assert.Equal(expectedHit, actualHit);
            Assert.True(actualHit);
            checkedPoints++;
        }

        Assert.True(checkedPoints > 0);
    }

    private static string DescribeEmptyLegacyRectResult(
        NativeOverflowFixture fixture,
        Rect viewport,
        float rasterizationScale)
    {
        string cellDetails = string.Join(
            " || ",
            fixture.Table.GetCellBoxes().Select(cell =>
            {
                Rect[] raw = cell.GetSelectionRects(fixture.Selection.Range).ToArray();
                Rect[] clipped = raw
                    .Select(rectangle => IntersectForTest(rectangle, fixture.Table.HorizontalViewportBounds))
                    .ToArray();
                return $"block={cell.BlockIndex};bounds={cell.Bounds};raw=[{string.Join(",", raw)}];" +
                    $"tableClip=[{string.Join(",", clipped)}]";
            }));
        string codeChunkDetails = string.Join(
            " || ",
            fixture.Code.Chunks.Select(chunk =>
            {
                Rect[] raw = chunk.GetSelectionRects(fixture.Selection.Range).ToArray();
                Rect[] clipped = raw
                    .Select(rectangle => IntersectForTest(rectangle, fixture.Code.HorizontalViewportBounds))
                    .ToArray();
                string nativeRequestedSize = typeof(InlineContainerBox)
                    .GetField("_layout", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                    ?.GetValue(chunk) is CanvasTextLayout nativeLayout
                        ? nativeLayout.RequestedSize.ToString()
                        : "<no-layout>";
                return $"block={chunk.BlockIndex};bounds={chunk.Bounds};offset={chunk.CodeBlockTextOffset};" +
                    $"length={chunk.CodeBlockTextLength};requested={nativeRequestedSize};" +
                    $"raw=[{string.Join(",", raw)}];codeClip=[{string.Join(",", clipped)}]";
            }));

        return $"Expected a visible legacy selection rectangle, but none intersected the viewport. " +
            $"scale={rasterizationScale};range={fixture.Selection.Range};viewport={viewport};" +
            $"tableBounds={fixture.Table.Bounds};tableClip={fixture.Table.HorizontalViewportBounds};" +
            $"rtl={fixture.Table.IsRightToLeft};offset={fixture.Table.HorizontalOffset};" +
            $"extent={fixture.Table.HorizontalExtent};viewportWidth={fixture.Table.HorizontalViewport};" +
            $"cells={cellDetails};codeBounds={fixture.Code.Bounds};" +
            $"codeClip={fixture.Code.HorizontalViewportBounds};rtl={fixture.Code.IsRightToLeft};" +
            $"codeOffset={fixture.Code.HorizontalOffset};codeExtent={fixture.Code.HorizontalExtent};" +
            $"codeViewportWidth={fixture.Code.HorizontalViewport};canonicalLength={fixture.Code.CodeText.Length};" +
            $"chunks={codeChunkDetails}";
    }

    private static void AssertCodeChunksMatchDirectWrite(
        RtlNativeCodeFixture fixture,
        float rasterizationScale)
    {
        CodeBlockBox code = fixture.Code;
        Rect viewport = code.HorizontalViewportBounds;
        float horizontalDirection = code.IsRightToLeft ? 1f : -1f;
        float expectedChunkX = (float)viewport.Left + horizontalDirection * (float)code.HorizontalOffset;
        Assert.All(code.Chunks, chunk =>
        {
            Assert.InRange(global::System.Math.Abs((float)chunk.Bounds.X - expectedChunkX), 0, 0.01f);
            Assert.Equal(default(Thickness), chunk.Margin);

            // The ordinary inline hit-test remains constrained by its own X
            // bounds; CodeBlockBox alone may route clipped overflow hits.
            var outsideInlineBounds = new Point(
                chunk.Bounds.Right + global::System.Math.Max(1, 1 / rasterizationScale),
                chunk.Bounds.Top + chunk.Bounds.Height / 2);
            Assert.False(chunk.HitTest(outsideInlineBounds, out _));
        });

        Rect[] directWriteOracle = GetDirectWriteCodeSelectionRects(fixture, rasterizationScale);
        Rect[] actualCodeRects = code.GetSelectionRects(fixture.Selection.Range).ToArray();
        Assert.NotEmpty(directWriteOracle);
        Assert.True(
            directWriteOracle.SequenceEqual(actualCodeRects),
            DescribeFirstCodeSelectionMismatch(fixture, rasterizationScale, directWriteOracle, actualCodeRects));

        List<Rect> legacyViewportRects = GetLegacyViewportRects(
            fixture.Selection,
            fixture.Snapshot,
            viewport);
        List<Rect> visibleViewportRects = CollectVisible(
            fixture.Selection,
            fixture.Snapshot,
            viewport);
        Assert.NotEmpty(legacyViewportRects);
        Assert.Equal(legacyViewportRects, visibleViewportRects);
        Assert.Equal(directWriteOracle, visibleViewportRects);

        Color[] legacyPixels = RasterizeSelectionRects(
            fixture.Device,
            legacyViewportRects,
            viewport,
            rasterizationScale,
            fixture.Snapshot,
            fixture.Selection.Range);
        Color[] viewportPixels = RasterizeSelectionRects(
            fixture.Device,
            visibleViewportRects,
            viewport,
            rasterizationScale,
            fixture.Snapshot,
            fixture.Selection.Range);
        Assert.Equal(legacyPixels, viewportPixels);

        Color[] foregroundOnly = RasterizeSelectionForegroundOnly(
            fixture.Device,
            fixture.Snapshot,
            fixture.Selection.Range,
            viewport,
            rasterizationScale);
        Assert.Contains(foregroundOnly, pixel => pixel.A != 0);

        Assert.True(fixture.Snapshot.TryBeginInteraction());
        try
        {
            foreach (Rect rect in directWriteOracle)
            {
                Point point = new(rect.Left + rect.Width / 2, rect.Top + rect.Height / 2);
                Assert.True(code.HitTest(point, out DocumentPosition hit));
                InlineContainerBox chunk = Assert.Single(code.Chunks, item => item.BlockIndex == hit.BlockIndex);
                Assert.InRange(hit.CharacterOffset, 0, chunk.CodeBlockTextLength);
                Assert.True(chunk.TryGetCaretPoint(hit, rangeStart: false, out Point caret));
                Assert.True(double.IsFinite(caret.X) && double.IsFinite(caret.Y));
                Assert.InRange(global::System.Math.Abs(caret.Y - point.Y), 0, global::System.Math.Max(rect.Height, 16));
            }
        }
        finally
        {
            fixture.Snapshot.EndInteraction();
        }
    }

    private static Rect[] GetDirectWriteCodeSelectionRects(
        RtlNativeCodeFixture fixture,
        float rasterizationScale)
    {
        const System.Reflection.BindingFlags privateInstance =
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        var layoutField = typeof(InlineContainerBox).GetField("_layout", privateInstance);
        Assert.NotNull(layoutField);
        Rect viewport = fixture.Code.HorizontalViewportBounds;
        float scale = (float)fixture.Code.Context.RasterizationScale;
        if (scale <= 0)
            scale = 1;

        var result = new List<Rect>();
        foreach (InlineContainerBox chunk in fixture.Code.Chunks)
        {
            CanvasTextLayout layout = Assert.IsType<CanvasTextLayout>(layoutField!.GetValue(chunk));
            CanvasTextLayoutRegion[] regions = layout.GetCharacterRegions(0, chunk.CodeBlockTextLength);
            float originX = SnapIndependentTextOrigin((float)(chunk.Bounds.X + chunk.Margin.Left), scale);
            float originY = SnapIndependentTextOrigin((float)(chunk.Bounds.Y + chunk.Margin.Top), scale);
            foreach (CanvasTextLayoutRegion region in regions)
            {
                Rect native = new(
                    originX + region.LayoutBounds.X,
                    originY + region.LayoutBounds.Y,
                    region.LayoutBounds.Width,
                    region.LayoutBounds.Height);
                Rect clipped = IntersectForTest(native, viewport);
                if (clipped.Width > 0 && clipped.Height > 0)
                    result.Add(clipped);
            }
        }

        Assert.Equal(rasterizationScale, (float)fixture.Code.Context.RasterizationScale);
        return result.ToArray();
    }

    private static string DescribeFirstCodeSelectionMismatch(
        RtlNativeCodeFixture fixture,
        float rasterizationScale,
        IReadOnlyList<Rect> expected,
        IReadOnlyList<Rect> actual)
    {
        int count = System.Math.Min(expected.Count, actual.Count);
        int mismatch = 0;
        while (mismatch < count && expected[mismatch].Equals(actual[mismatch]))
            mismatch++;
        if (mismatch == count)
            return $"Selection rectangle count differs: expected={expected.Count}; actual={actual.Count}.";

        Rect expectedRect = expected[mismatch];
        Rect actualRect = actual[mismatch];
        string chunks = string.Join(
            " || ",
            fixture.Code.Chunks.Select(chunk =>
            {
                CanvasTextLayout layout = (CanvasTextLayout)typeof(InlineContainerBox)
                    .GetField("_layout", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                    .GetValue(chunk)!;
                float oracleOriginY = SnapIndependentTextOrigin(
                    (float)(chunk.Bounds.Y + chunk.Margin.Top),
                    (float)fixture.Code.Context.RasterizationScale);
                int from = GetDiagnosticBufferOffset(fixture.Selection.Range.Start, chunk);
                int to = GetDiagnosticBufferOffset(fixture.Selection.Range.End, chunk);
                CanvasTextLayoutRegion[] regions = layout.GetCharacterRegions(0, chunk.CodeBlockTextLength);
                CanvasTextLayoutRegion[] selectedRegions = layout.GetCharacterRegions(from, to - from);
                return $"block={chunk.BlockIndex}; bounds={chunk.Bounds}; oracleOriginY={oracleOriginY:R}; " +
                    $"query={from}+{to - from}; regionRects=[{string.Join(";", regions.Select(region => FormatRect(region.LayoutBounds)))}]; " +
                    $"selectedRects=[{string.Join(";", selectedRegions.Select(region => FormatRect(region.LayoutBounds)))}]; " +
                    $"actualRaw=[{string.Join(";", chunk.GetSelectionRects(fixture.Selection.Range).Select(FormatRect))}]";
            }));
        return $"DirectWrite selection rectangles differ at {mismatch}/{count}; " +
            $"expected={expectedRect}; actual={actualRect}; " +
            $"expectedBottom={expectedRect.Bottom:R}; actualBottom={actualRect.Bottom:R}; " +
            $"scale={rasterizationScale:R}; clip={fixture.Code.HorizontalViewportBounds}; chunks={chunks}";
    }

    private static int GetDiagnosticBufferOffset(DocumentPosition position, InlineContainerBox chunk)
    {
        if (position.BlockIndex < chunk.BlockIndex)
            return 0;
        if (position.BlockIndex > chunk.BlockIndex)
            return chunk.CodeBlockTextLength;
        return System.Math.Clamp(position.CharacterOffset, 0, chunk.CodeBlockTextLength);
    }

    private static string FormatRect(Rect rect)
        => $"{rect.X:R},{rect.Y:R},{rect.Width:R},{rect.Height:R}";

    private static float SnapIndependentTextOrigin(float value, float scale)
        => global::System.MathF.Round(value * scale) / scale;

    private static Color[] RasterizeSelectionForegroundOnly(
        CanvasDevice device,
        LayoutSnapshot snapshot,
        DocumentRange range,
        Rect viewport,
        float rasterizationScale)
    {
        if (!snapshot.TryBeginPaint())
            throw new InvalidOperationException("The native code-selection paint snapshot was unexpectedly busy.");
        try
        {
            using var target = new CanvasRenderTarget(
                device,
                (float)viewport.Width,
                (float)viewport.Height,
                96 * rasterizationScale);
            using (CanvasDrawingSession drawing = target.CreateDrawingSession())
            {
                drawing.Clear(Color.FromArgb(0, 0, 0, 0));
                drawing.Transform = System.Numerics.Matrix3x2.CreateTranslation(
                    (float)-viewport.X,
                    (float)-viewport.Y);
                snapshot.PaintSelectionForeground(
                    drawing,
                    range,
                    Color.FromArgb(255, 225, 238, 251),
                    viewport);
            }

            return target.GetPixelColors();
        }
        finally
        {
            snapshot.EndPaint();
        }
    }

    private static RtlNativeCodeFixture CreateRtlNativeCodeFixture(
        CanvasDevice device,
        float rasterizationScale,
        CanvasWordWrapping wrapping,
        bool includeUnbreakableToken,
        FlowDirection flowDirection = FlowDirection.RightToLeft)
    {
        const float width = 208.25f;
        string longToken = new('W', 220);
        string[] lines = includeUnbreakableToken
            ?
            [
                "x",
                "var mediumName = 17;",
                "var bidiValue = \"שלום אבג 123\";",
                "var token = " + longToken + ";",
                "tail;",
            ]
            :
            [
                "var firstValue = \"ordinary words wrap within the view\";",
                "var secondValue = \"more words for a natural wrap\";",
                "int finalColumn = 42;",
            ];
        string codeText = string.Join("\n", lines);
        var codeStyle = new ElementStyle
        {
            FontFamily = "Consolas",
            FontSize = 13,
            Foreground = Color.FromArgb(255, 24, 25, 26),
            Background = Color.FromArgb(255, 246, 247, 249),
            Padding = new Thickness(4, 2, 6, 2),
        };
        var lineNumberStyle = new ElementStyle
        {
            FontFamily = "Consolas",
            FontSize = 11,
            Foreground = Color.FromArgb(255, 86, 93, 101),
        };
        Color transparent = Color.FromArgb(0, 0, 0, 0);
        var theme = new ThemeSnapshot(
            new Dictionary<string, ElementStyle>
            {
                [MarkdownElementKeys.Body] = codeStyle,
                [MarkdownElementKeys.CodeBlock] = codeStyle,
                [MarkdownElementKeys.CodeBlockHeader] = new ElementStyle(),
                [MarkdownElementKeys.CodeBlockLanguage] = codeStyle,
                [MarkdownElementKeys.CodeBlockGutter] = new ElementStyle(),
                [MarkdownElementKeys.CodeBlockLineNumber] = lineNumberStyle,
            },
            new Dictionary<string, ElementStyleOverride>(),
            transparent,
            transparent,
            transparent,
            transparent,
            isDark: false,
            isHighContrast: false,
            textScaleFactor: 1);
        var context = new MarkdownLayoutContext(
            device,
            theme,
            new MarkdownSourceMap(codeText),
            new MarkdownExtensionRegistry(),
            flowDirection,
            language: "en-US")
        {
            RasterizationScale = rasterizationScale,
            IsCodeBlockCopyEnabled = true,
        };
        var metadata = CodeBlockMetadata.FromDeclarative(
            new SourceSpan(0, codeText.Length),
            codeText,
            "csharp",
            new Dictionary<string, string> { ["startline"] = "7" });
        var code = new CodeBlockBox(context, metadata, codeText, isCopyButtonEnabled: true, showLineNumbers: true)
        {
            BlockIndex = 0,
        };
        int nextBlockIndex = 1;
        int textOffset = 0;
        foreach (string line in lines)
        {
            string chunkText = textOffset + line.Length < codeText.Length ? line + "\n" : line;
            var chunk = new InlineContainerBox(context, MarkdownElementKeys.CodeBlock)
            {
                BlockIndex = nextBlockIndex++,
                CodeLanguage = metadata.Language,
                CodeBlockTextOffset = textOffset,
                CodeBlockTextLength = chunkText.Length,
                WordWrapping = wrapping,
            };
            chunk.Add(new TextRun(chunkText));
            code.AddChunk(chunk);
            textOffset += chunkText.Length;
        }
        Assert.Equal(codeText.Length, textOffset);

        code.Measure(width);
        code.Arrange(11.25f, 37.375f, width);
        var snapshot = new LayoutSnapshot(
            new BlockBox[] { code },
            context.SourceMap,
            width,
            (float)code.Bounds.Bottom + 12);
        var selection = new SelectionController();
        selection.SetAnchor(new DocumentPosition(code.Chunks[0].BlockIndex, 0, 0));
        selection.ExtendTo(new DocumentPosition(code.Chunks[^1].BlockIndex, 0, int.MaxValue));
        return new RtlNativeCodeFixture(device, context, snapshot, code, selection);
    }

    private static Color[] RasterizeSelectionRects(
        CanvasDevice device,
        IReadOnlyList<Rect> rectangles,
        Rect viewport,
        float rasterizationScale,
        LayoutSnapshot snapshot,
        DocumentRange selectionRange)
    {
        if (!snapshot.TryBeginPaint())
            throw new InvalidOperationException("The pixel-oracle snapshot was unexpectedly busy.");
        try
        {
            using var target = new CanvasRenderTarget(
                device,
                (float)viewport.Width,
                (float)viewport.Height,
                96 * rasterizationScale);
            using (CanvasDrawingSession drawing = target.CreateDrawingSession())
            {
                drawing.Clear(Color.FromArgb(0, 0, 0, 0));
                drawing.Transform = System.Numerics.Matrix3x2.CreateTranslation(
                    (float)-viewport.X,
                    (float)-viewport.Y);
                foreach (Rect rectangle in rectangles)
                    drawing.FillRectangle(
                        SnapToPhysicalPixels(rectangle, rasterizationScale),
                        Color.FromArgb(255, 32, 112, 196));
                snapshot.PaintSelectionForeground(
                    drawing,
                    selectionRange,
                    Color.FromArgb(255, 225, 238, 251),
                    viewport);
            }

            return target.GetPixelColors();
        }
        finally
        {
            snapshot.EndPaint();
        }
    }

    private static NativeOverflowFixture CreateNativeOverflowFixture(
        CanvasDevice device,
        FlowDirection flowDirection,
        float rasterizationScale)
    {
        const float width = 176;
        var codeStyle = new ElementStyle
        {
            FontFamily = "Consolas",
            FontSize = 13,
            Foreground = Color.FromArgb(255, 24, 25, 26),
            Background = Color.FromArgb(255, 246, 247, 249),
        };
        var bodyStyle = new ElementStyle
        {
            FontFamily = "Segoe UI",
            FontSize = 13,
            Foreground = Color.FromArgb(255, 25, 28, 31),
        };
        var tableStyle = new ElementStyle { Padding = new Thickness(2) };
        var styles = new Dictionary<string, ElementStyle>
        {
            [MarkdownElementKeys.Body] = bodyStyle,
            [MarkdownElementKeys.Table] = tableStyle,
            [MarkdownElementKeys.TableHeader] = bodyStyle,
            [MarkdownElementKeys.TableCell] = bodyStyle,
            [MarkdownElementKeys.CodeBlock] = codeStyle,
            [MarkdownElementKeys.CodeBlockHeader] = new ElementStyle(),
            [MarkdownElementKeys.CodeBlockLanguage] = bodyStyle,
            [MarkdownElementKeys.CodeBlockGutter] = new ElementStyle(),
            [MarkdownElementKeys.CodeBlockLineNumber] = new ElementStyle(),
        };
        Color transparent = Color.FromArgb(0, 0, 0, 0);
        var theme = new ThemeSnapshot(
            styles,
            new Dictionary<string, ElementStyleOverride>(),
            transparent,
            transparent,
            transparent,
            transparent,
            isDark: false,
            isHighContrast: false,
            textScaleFactor: 1);
        string code = string.Join(
            "\n",
            Enumerable.Range(0, 36).Select(index =>
                $"var horizontalColumn_{index:D2} = sourceValue_{index:D2} + additionalOverflowPayload_{index:D2};"));
        string source = "wide table and code selection\n" + code;
        var context = new MarkdownLayoutContext(
            device,
            theme,
            new MarkdownSourceMap(source),
            new MarkdownExtensionRegistry(),
            flowDirection,
            language: "en-US")
        {
            RasterizationScale = rasterizationScale,
            IsCodeBlockCopyEnabled = true,
        };

        InlineContainerBox MakeCell(int blockIndex, string text)
        {
            var cell = new InlineContainerBox(context, MarkdownElementKeys.TableCell)
            {
                BlockIndex = blockIndex,
            };
            cell.Add(new TextRun(text));
            return cell;
        }

        InlineContainerBox[][] headers =
        [
            [
                MakeCell(1, "header-column-with-long-content-one"),
                MakeCell(2, "header-column-with-long-content-two"),
                MakeCell(3, "header-column-with-long-content-three"),
            ],
        ];
        InlineContainerBox[][] rows =
        [
            [
                MakeCell(4, "body-column-with-long-content-alpha"),
                MakeCell(5, "body-column-with-long-content-bravo"),
                MakeCell(6, "body-column-with-long-content-charlie"),
            ],
            [
                MakeCell(7, "second-row-long-content-delta"),
                MakeCell(8, "second-row-long-content-echo"),
                MakeCell(9, "second-row-long-content-foxtrot"),
            ],
        ];
        var table = new TableBox(context, headers, rows) { BlockIndex = 0 };
        table.Measure(width);
        table.Arrange(0, 8, width);

        CodeBlockMetadata metadata = CodeBlockMetadata.FromDeclarative(
            new SourceSpan(0, code.Length),
            code,
            "csharp",
            new Dictionary<string, string> { ["startline"] = "1" });
        var codeBlock = new CodeBlockBox(
            context,
            metadata,
            code,
            isCopyButtonEnabled: true,
            showLineNumbers: true)
        {
            BlockIndex = 10,
        };
        string canonicalCode = codeBlock.CodeText;
        string[] lines = canonicalCode.Split('\n');
        int nextLine = 0;
        int chunkIndex = 11;
        int textOffset = 0;
        const int linesPerChunk = 9;
        while (nextLine < lines.Length)
        {
            int count = global::System.Math.Min(linesPerChunk, lines.Length - nextLine);
            string chunkText = string.Join("\n", lines.Skip(nextLine).Take(count));
            if (nextLine + count < lines.Length)
                chunkText += "\n";
            var chunk = new InlineContainerBox(context, MarkdownElementKeys.CodeBlock)
            {
                BlockIndex = chunkIndex++,
                CodeLanguage = metadata.Language,
                CodeBlockTextOffset = textOffset,
                CodeBlockTextLength = chunkText.Length,
                WordWrapping = CanvasWordWrapping.NoWrap,
            };
            chunk.Add(new TextRun(chunkText));
            codeBlock.AddChunk(chunk);
            textOffset += chunkText.Length;
            nextLine += count;
        }

        codeBlock.Measure(width);
        codeBlock.Arrange(0, (float)table.Bounds.Bottom + 20, width);
        var snapshot = new LayoutSnapshot(
            new BlockBox[] { table, codeBlock },
            context.SourceMap,
            width,
            (float)codeBlock.Bounds.Bottom + 16);
        var selection = new SelectionController();
        selection.SetAnchor(new DocumentPosition(1, 0, 0));
        selection.ExtendTo(new DocumentPosition(chunkIndex - 1, 0, int.MaxValue));
        return new NativeOverflowFixture(device, context, snapshot, table, codeBlock, selection, width);
    }

    private static MarkdownLayoutContext CreateEndpointContext(CanvasDevice device)
    {
        var body = new ElementStyle
        {
            FontFamily = "Segoe UI",
            FontSize = 13,
            Foreground = Color.FromArgb(255, 24, 25, 26),
        };
        var theme = new ThemeSnapshot(
            new Dictionary<string, ElementStyle>
            {
                [MarkdownElementKeys.Body] = body,
                [MarkdownElementKeys.ImageCaption] = body,
            },
            new Dictionary<string, ElementStyleOverride>(),
            Color.FromArgb(0, 0, 0, 0),
            Color.FromArgb(0, 0, 0, 0),
            Color.FromArgb(0, 0, 0, 0),
            Color.FromArgb(0, 0, 0, 0),
            isDark: false,
            isHighContrast: false,
            textScaleFactor: 1);
        return new MarkdownLayoutContext(
            device,
            theme,
            new MarkdownSourceMap("offscreen image endpoint"),
            new MarkdownExtensionRegistry(),
            FlowDirection.LeftToRight,
            language: "en-US");
    }

    private static void ResetCounts(IEnumerable<CountingSelectionBox> boxes)
    {
        foreach (CountingSelectionBox box in boxes)
            box.SelectionQueryCount = 0;
    }

    private static bool Intersects(Rect left, Rect right)
        => left.Right >= right.Left && left.Left <= right.Right &&
           left.Bottom >= right.Top && left.Top <= right.Bottom;

    private static Rect SnapToPhysicalPixels(Rect rect, double scale)
    {
        double pixelLeft = System.Math.Floor(rect.X * scale);
        double pixelTop = System.Math.Floor(rect.Y * scale);
        double pixelRight = System.Math.Ceiling((rect.X + rect.Width) * scale);
        double pixelBottom = System.Math.Ceiling((rect.Y + rect.Height) * scale);
        return new Rect(
            pixelLeft / scale,
            pixelTop / scale,
            (pixelRight - pixelLeft) / scale,
            (pixelBottom - pixelTop) / scale);
    }

    private static Rect IntersectForTest(Rect left, Rect right)
    {
        double x = System.Math.Max(left.Left, right.Left);
        double y = System.Math.Max(left.Top, right.Top);
        double rightEdge = System.Math.Min(left.Right, right.Right);
        double bottomEdge = System.Math.Min(left.Bottom, right.Bottom);
        if (rightEdge <= x || bottomEdge <= y)
            return Rect.Empty;

        double width = x == left.Left && rightEdge == left.Right
            ? left.Width
            : rightEdge - x;
        double height = y == left.Top && bottomEdge == left.Bottom
            ? left.Height
            : bottomEdge - y;
        return new Rect(x, y, width, height);
    }

    private sealed class CountingSelectionBox : BlockBox
    {
        internal CountingSelectionBox(int blockIndex, Rect bounds)
        {
            BlockIndex = blockIndex;
            Bounds = bounds;
        }

        internal Rect SelectionRect => Bounds;
        internal int SelectionQueryCount { get; set; }

        public override float Measure(float availableWidth)
        {
            Bounds = new Rect(Bounds.X, Bounds.Y, availableWidth, Bounds.Height);
            return (float)Bounds.Height;
        }

        public override void Arrange(float x, float y, float width)
        {
            Bounds = new Rect(x, y, width, Bounds.Height);
            IsDirty = false;
        }

        public override IEnumerable<Rect> GetSelectionRects(DocumentRange range)
        {
            SelectionQueryCount++;
            DocumentRange normalized = range.Normalized();
            if (BlockIndex >= normalized.Start.BlockIndex && BlockIndex <= normalized.End.BlockIndex &&
                !normalized.IsEmpty)
            {
                yield return Bounds;
            }
        }

        public override void Paint(CanvasDrawingSession ds, Rect viewport) { }
    }

    private delegate void VisibleAppendDelegate(
        LayoutSnapshot snapshot,
        Rect viewport,
        List<Rect> destination);

    private static string GetMethodBody(string source, string signature)
    {
        int member = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(member >= 0, $"Could not find method '{signature}'.");
        int bodyStart = source.IndexOf('{', member + signature.Length);
        Assert.True(bodyStart >= 0, $"Could not find a body for '{signature}'.");

        int depth = 0;
        for (int index = bodyStart; index < source.Length; index++)
        {
            if (source[index] == '{')
                depth++;
            else if (source[index] == '}' && --depth == 0)
                return source[bodyStart..(index + 1)];
        }

        throw new InvalidOperationException($"Could not find the end of '{signature}'.");
    }

    private static string ReadRepositorySource(params string[] pathParts)
    {
        for (System.IO.DirectoryInfo? directory = new(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            string path = directory.FullName;
            foreach (string part in pathParts)
                path = System.IO.Path.Combine(path, part);
            if (System.IO.File.Exists(path))
                return System.IO.File.ReadAllText(path);
        }

        throw new System.IO.FileNotFoundException(
            $"Could not locate repository source '{System.IO.Path.Combine(pathParts)}' from '{AppContext.BaseDirectory}'.");
    }

    private sealed class RtlNativeCodeFixture(
        CanvasDevice device,
        MarkdownLayoutContext context,
        LayoutSnapshot snapshot,
        CodeBlockBox code,
        SelectionController selection) : IAsyncDisposable
    {
        internal CanvasDevice Device { get; } = device;
        internal MarkdownLayoutContext Context { get; } = context;
        internal LayoutSnapshot Snapshot { get; } = snapshot;
        internal CodeBlockBox Code { get; } = code;
        internal SelectionController Selection { get; } = selection;

        public async ValueTask DisposeAsync()
        {
            await Snapshot.Retire().WaitAsync(TimeSpan.FromSeconds(10));
            await Context.DrainOwnedRetirementsAsync().WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    private sealed class NativeOverflowFixture(
        CanvasDevice device,
        MarkdownLayoutContext context,
        LayoutSnapshot snapshot,
        TableBox table,
        CodeBlockBox code,
        SelectionController selection,
        float width) : IAsyncDisposable
    {
        internal CanvasDevice Device { get; } = device;
        internal MarkdownLayoutContext Context { get; } = context;
        internal LayoutSnapshot Snapshot { get; } = snapshot;
        internal TableBox Table { get; } = table;
        internal CodeBlockBox Code { get; } = code;
        internal SelectionController Selection { get; } = selection;
        internal float Width { get; } = width;
        internal Rect CodeViewportBounds => Code.HorizontalViewportBounds;

        public async ValueTask DisposeAsync()
        {
            await Snapshot.Retire().WaitAsync(TimeSpan.FromSeconds(10));
            await Context.DrainOwnedRetirementsAsync().WaitAsync(TimeSpan.FromSeconds(10));
        }
    }
}
