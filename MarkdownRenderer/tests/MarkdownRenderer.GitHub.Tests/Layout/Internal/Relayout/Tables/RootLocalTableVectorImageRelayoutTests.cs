using MarkdownRenderer.GitHub.Tests.Layout.Internal.Images.BitmapCache;
using MarkdownRenderer.GitHub.Tests.Layout.Internal.Inlines.Placement;
using MarkdownRenderer.GitHub.Tests.TestSupport.Assertions;
using MarkdownRenderer.Images;
using MarkdownRenderer.Layout.Internal.Context;
using MarkdownRenderer.Layout.Internal.Images;
using MarkdownRenderer.Layout.Internal.Images.BitmapCache;
using MarkdownRenderer.Layout.Internal.Inlines;
using MarkdownRenderer.Layout.Internal.Inlines.Runs;
using MarkdownRenderer.Layout.Internal.Relayout;
using MarkdownRenderer.Layout.Internal.Tables;
using MarkdownRenderer.Layout.Internal.Vectors.Drawing;
using MarkdownRenderer.Math;
using MarkdownRenderer.Performance;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.UI.Xaml;
using Windows.Foundation;
using Xunit;
using static MarkdownRenderer.GitHub.Tests.TestSupport.Assertions.RootLocalTableQueries;
using static MarkdownRenderer.GitHub.Tests.TestSupport.Assertions.RootLocalTableRelayoutAssertions;
using static MarkdownRenderer.GitHub.Tests.Layout.Internal.Relayout.Tables.RootLocalTableVectorQueries;

namespace MarkdownRenderer.GitHub.Tests.Layout.Internal.Relayout.Tables;

[Collection(SharedCanvasBitmapCacheTestCollection.Name)]
public sealed class RootLocalTableVectorImageRelayoutTests
{
    private const float InitialWidth = RootLocalTableVectorDocument.InitialWidth;
    private const float NarrowWidth = 320f;

    [Theory]
    [InlineData(false, 1f, false)]
    [InlineData(true, 1f, false)]
    [InlineData(false, 1.25f, false)]
    [InlineData(true, 1.25f, false)]
    [InlineData(false, 1.5f, false)]
    [InlineData(true, 1.5f, false)]
    [InlineData(false, 1.75f, false)]
    [InlineData(true, 1.75f, false)]
    [InlineData(false, 2f, false)]
    [InlineData(true, 2f, false)]
    [InlineData(false, 2.25f, false)]
    [InlineData(true, 2.25f, false)]
    [InlineData(false, 3f, false)]
    [InlineData(true, 3f, false)]
    [InlineData(false, 4f, false)]
    [InlineData(true, 4f, false)]
    [InlineData(false, 1f, true)]
    [InlineData(true, 1f, true)]
    [InlineData(false, 1.25f, true)]
    [InlineData(true, 1.25f, true)]
    [InlineData(false, 1.5f, true)]
    [InlineData(true, 1.5f, true)]
    [InlineData(false, 1.75f, true)]
    [InlineData(true, 1.75f, true)]
    [InlineData(false, 2f, true)]
    [InlineData(true, 2f, true)]
    [InlineData(false, 2.25f, true)]
    [InlineData(true, 2.25f, true)]
    [InlineData(false, 3f, true)]
    [InlineData(true, 3f, true)]
    [InlineData(false, 4f, true)]
    [InlineData(true, 4f, true)]
    public async Task RootTableTransfersImageAndMeasuredVectorPlacementsWithoutReplacingOwners(
        bool rightToLeft,
        float rasterizationScale,
        bool forceSoftwareRenderer)
    {
        await using var seed = new CompletedRasterPlacementCloneTests.PlacementFixture(
            forceSoftwareRenderer: forceSoftwareRenderer);
        Assert.Equal(forceSoftwareRenderer, seed.Device.ForceSoftwareRenderer);
        using var session = new MarkdownPerformanceSession(MarkdownPerformanceOptions.Progressive);
        var optimizedResolver = new RootLocalTableVectorResolver();
        var fallbackResolver = new RootLocalTableVectorResolver();
        FlowDirection direction = rightToLeft ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
        await using RootLocalTableVectorDocument optimized = await RootLocalTableVectorDocument.BuildAsync(
            seed, session, optimizedResolver, CreateTableSource(optimizedResolver), direction, rasterizationScale);
        await using RootLocalTableVectorDocument fallback = await RootLocalTableVectorDocument.BuildAsync(
            seed, session, fallbackResolver, CreateTableSource(fallbackResolver), direction, rasterizationScale);

        TableBox table = FindTable(optimized.Snapshot);
        TableBox fallbackTable = FindTable(fallback.Snapshot);
        InlineContainerBox[] cellsBefore = table.GetCellBoxes().ToArray();
        InlineContainerBox[] fallbackCellsBefore = fallbackTable.GetCellBoxes().ToArray();
        Assert.Equal(cellsBefore.Length, fallbackCellsBefore.Length);
        Assert.Contains(cellsBefore, static cell =>
            cell.Runs.OfType<InlineImageRun>().Any() &&
            cell.Runs.OfType<InlineVectorSceneRun>().Any());
        Assert.Contains(cellsBefore, static cell =>
            !cell.Runs.OfType<InlineImageRun>().Any() &&
            cell.Runs.OfType<InlineVectorSceneRun>().Any());

        ImageBox[] images = EnumerateTableImages(table).ToArray();
        ImageBox[] fallbackImages = EnumerateTableImages(fallbackTable).ToArray();
        Assert.Equal(2, images.Length);
        Assert.Equal(images.Length, fallbackImages.Length);
        optimizedResolver.ReleaseImages();
        fallbackResolver.ReleaseImages();
        await LoadImagesAsync(images.Concat(fallbackImages));

        var before = RootLocalTableVectorBaseline.Capture(optimized.Snapshot, table, cellsBefore, fallbackCellsBefore, images);
        int[] resolveCountsBefore = optimizedResolver.CaptureResolveCounts();

        // The full table rebuild remains the geometry and raster oracle. A
        // completed-image remeasure keeps the snapshot's available width fixed;
        // intrinsic image measurements may still rebalance its columns.
        fallback.Snapshot.RelayoutChangedBlocks(
            [fallbackTable.BlockIndex], InitialWidth, CancellationToken.None);

        MarkdownLayoutContext? constructionContext = null;
        try
        {
            PreparedImageRelayoutResult result = optimized.Snapshot.TryRelayoutCompletedImageRoots(
                [table.BlockIndex], InitialWidth, CancellationToken.None, out constructionContext,
                expectedPointerInputEpoch: optimized.Snapshot.PointerInputEpoch);

            Assert.Equal(PreparedImageRelayoutResult.Committed, result);
            Assert.NotNull(constructionContext);
            // Construction image clones release their borrowed bitmap leases
            // asynchronously. Stable owner/refcount assertions belong after
            // the registered retirement barrier, not before its forced yield.
            await constructionContext.DrainOwnedRetirementsAsync().WaitAsync(TimeSpan.FromSeconds(10));
            before.AssertSnapshotMatchesFallback(optimized.Snapshot, table, fallback.Snapshot);
            InlineContainerBox[] cellsAfter = table.GetCellBoxes().ToArray();
            InlineContainerBox[] fallbackCells = FindTable(fallback.Snapshot).GetCellBoxes().ToArray();
            before.AssertCellsMatchFallback(cellsAfter, fallbackCells, optimized.Snapshot, fallback.Snapshot, table.BlockIndex);
            InlineVectorSceneRun[] vectorsAfter =
                before.AssertVectorsMatchFallback(cellsAfter, fallbackCells, optimized.Snapshot, fallback.Snapshot);
            before.AssertSelectionsRelocated(optimized.Snapshot, cellsAfter, vectorsAfter);
            before.AssertImagesMatchFallback(table, optimized.Snapshot, fallback.Snapshot);
            Assert.Equal(resolveCountsBefore, optimizedResolver.CaptureResolveCounts());
            before.AssertColumnsAndViewportMatchFallback(table, fallback.Snapshot);
            AssertSemanticTextAndSpanGeometryEqual(optimized.Snapshot, fallback.Snapshot);
            Assert.Equal(Paint(optimized.Snapshot, seed.Device, rasterizationScale),
                Paint(fallback.Snapshot, seed.Device, rasterizationScale));
        }
        finally
        {
            await DrainConstructionAsync(constructionContext);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OverflowingVectorImageTablePreservesClampedHorizontalViewport(bool rightToLeft)
    {
        await using var seed = new CompletedRasterPlacementCloneTests.PlacementFixture();
        using var session = new MarkdownPerformanceSession(MarkdownPerformanceOptions.Progressive);
        var optimizedResolver = new RootLocalTableVectorResolver();
        var fallbackResolver = new RootLocalTableVectorResolver();
        FlowDirection direction = rightToLeft ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
        string optimizedSource = CreateOverflowTableSource(optimizedResolver);
        string fallbackSource = CreateOverflowTableSource(fallbackResolver);
        await using RootLocalTableVectorDocument optimized = await RootLocalTableVectorDocument.BuildAsync(
            seed, session, optimizedResolver, optimizedSource, direction, 1.5f, NarrowWidth);
        await using RootLocalTableVectorDocument fallback = await RootLocalTableVectorDocument.BuildAsync(
            seed, session, fallbackResolver, fallbackSource, direction, 1.5f, NarrowWidth);

        TableBox table = FindTable(optimized.Snapshot);
        TableBox fallbackTable = FindTable(fallback.Snapshot);
        ImageBox[] images = EnumerateTableImages(table).ToArray();
        ImageBox[] fallbackImages = EnumerateTableImages(fallbackTable).ToArray();
        Assert.Equal(2, images.Length);
        optimizedResolver.ReleaseImages();
        fallbackResolver.ReleaseImages();
        await LoadImagesAsync(images.Concat(fallbackImages));
        Assert.True(table.CanScrollHorizontally);
        Assert.True(fallbackTable.CanScrollHorizontally);
        double previousMaximum = System.Math.Max(0, table.HorizontalExtent - table.HorizontalViewport);
        Assert.True(previousMaximum > 0);
        Assert.True(table.SetHorizontalOffset(previousMaximum));
        Assert.True(fallbackTable.SetHorizontalOffset(previousMaximum));

        InlineContainerBox[] cellsBefore = table.GetCellBoxes().ToArray();
        InlineRun[][] runsBefore = cellsBefore.Select(static cell => cell.Runs.ToArray()).ToArray();
        VectorSceneDrawing[] drawingsBefore = EnumerateVectors(cellsBefore).Select(GetVectorDrawing).ToArray();
        Assert.Equal(3, drawingsBefore.Length);
        CanvasBitmap[] bitmapsBefore = images.Select(GetBitmap).ToArray();
        SharedCanvasBitmapCache.Lease[] leasesBefore = images.Select(GetBitmapLease).ToArray();
        fallback.Snapshot.RelayoutChangedBlocks([fallbackTable.BlockIndex], NarrowWidth, CancellationToken.None);
        MarkdownLayoutContext? constructionContext = null;
        try
        {
            PreparedImageRelayoutResult result = optimized.Snapshot.TryRelayoutCompletedImageRoots(
                [table.BlockIndex], NarrowWidth, CancellationToken.None, out constructionContext,
                expectedPointerInputEpoch: optimized.Snapshot.PointerInputEpoch);
            Assert.Equal(PreparedImageRelayoutResult.Committed, result);
            Assert.NotNull(constructionContext);
            await constructionContext.DrainOwnedRetirementsAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Same(table, FindTable(optimized.Snapshot));
            Assert.Equal(fallback.Snapshot.Size, optimized.Snapshot.Size);
            TableBox expected = FindTable(fallback.Snapshot);
            Assert.Equal(expected.HorizontalExtent, table.HorizontalExtent);
            Assert.Equal(expected.HorizontalViewport, table.HorizontalViewport);
            Assert.Equal(expected.HorizontalOffset, table.HorizontalOffset);
            Assert.InRange(table.HorizontalOffset, 0, System.Math.Max(0,
                table.HorizontalExtent - table.HorizontalViewport));
            InlineContainerBox[] cellsAfter = table.GetCellBoxes().ToArray();
            AssertCellsAndRunsPreserved(cellsBefore, runsBefore, cellsAfter);
            AssertVectorDrawingsPreparedAndPreserved(drawingsBefore, EnumerateVectors(cellsAfter));
            for (int index = 0; index < images.Length; index++)
            {
                Assert.Same(images[index], EnumerateTableImages(table).ElementAt(index));
                Assert.Same(bitmapsBefore[index], images[index].Bitmap);
                Assert.Same(leasesBefore[index], GetBitmapLease(images[index]));
            }
            Assert.Equal(Paint(optimized.Snapshot, seed.Device, 1.5f),
                Paint(fallback.Snapshot, seed.Device, 1.5f));
        }
        finally
        {
            await DrainConstructionAsync(constructionContext);
        }
    }

    [Fact]
    public async Task StaleLaterTableVectorRejectsWholeCandidateBeforeEarlierImageTransfer()
    {
        await using var seed = new CompletedRasterPlacementCloneTests.PlacementFixture();
        using var session = new MarkdownPerformanceSession(MarkdownPerformanceOptions.Progressive);
        var resolver = new RootLocalTableVectorResolver();
        await using RootLocalTableVectorDocument document = await RootLocalTableVectorDocument.BuildAsync(
            seed, session, resolver, CreateTableSource(resolver), FlowDirection.LeftToRight, 1.5f);
        TableBox table = FindTable(document.Snapshot);
        InlineContainerBox[] cells = table.GetCellBoxes().ToArray();
        InlineVectorSceneRun vector = cells.SelectMany(static cell => cell.Runs.OfType<InlineVectorSceneRun>())
            .Last();
        ImageBox image = EnumerateTableImages(table).First();
        resolver.ReleaseImages();
        await LoadImagesAsync(EnumerateTableImages(table));
        Rect imageBounds = image.Bounds;
        CanvasBitmap bitmap = GetBitmap(image);
        SharedCanvasBitmapCache.Lease lease = GetBitmapLease(image);
        InlineContainerBox owner = FindOwnerCell(cells, vector);
        CanvasTextLayout layout = GetInlineLayout(owner);
        InlineRun[][] runs = cells.Select(static cell => cell.Runs.ToArray()).ToArray();
        Rect tableBounds = table.Bounds;
        Rect oldVectorBounds = vector.LocalBounds;
        VectorSceneDrawing oldDrawing = GetVectorDrawing(vector);
        Rect mutatedBounds = new(oldVectorBounds.X + 3, oldVectorBounds.Y + 1,
            oldVectorBounds.Width + 2, oldVectorBounds.Height);
        float mutatedWidth = vector.DesiredWidth + 2;
        MarkdownLayoutContext? constructionContext = null;
        try
        {
            PreparedImageRelayoutResult result = document.Snapshot.TryRelayoutCompletedImageRoots(
                [table.BlockIndex], InitialWidth, CancellationToken.None, out constructionContext,
                phaseHooks: CompletedImageRelayoutPhaseHooks.BeforeMeasure(() => vector.RestoreLayoutState(
                    mutatedWidth, vector.DesiredHeight, vector.DesiredBaseline, mutatedBounds)),
                expectedPointerInputEpoch: document.Snapshot.PointerInputEpoch);
            Assert.Equal(PreparedImageRelayoutResult.Retry, result);
            Assert.NotNull(constructionContext);
            await constructionContext.DrainOwnedRetirementsAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Same(table, FindTable(document.Snapshot));
            Assert.Equal(tableBounds, table.Bounds);
            for (int cellIndex = 0; cellIndex < cells.Length; cellIndex++)
            {
                Assert.Same(cells[cellIndex], table.GetCellBoxes().ElementAt(cellIndex));
                Assert.Same(layout, GetInlineLayout(owner));
                AssertRunsPreserved(runs[cellIndex], cells[cellIndex].Runs);
            }
            Assert.Equal(mutatedBounds, vector.LocalBounds);
            Assert.Equal(mutatedWidth, vector.DesiredWidth);
            Assert.Same(oldDrawing, GetVectorDrawing(vector));
            Assert.True(vector.IsNativeResourcesPrepared);
            Assert.Equal(imageBounds, image.Bounds);
            Assert.Same(bitmap, image.Bitmap);
            Assert.Same(lease, GetBitmapLease(image));
        }
        finally
        {
            await DrainConstructionAsync(constructionContext);
        }
    }

    [Fact]
    public async Task CancellationAfterTableCandidateCaptureLeavesImageAndVectorOwnersUntouched()
    {
        await using var seed = new CompletedRasterPlacementCloneTests.PlacementFixture();
        using var session = new MarkdownPerformanceSession(MarkdownPerformanceOptions.Progressive);
        var resolver = new RootLocalTableVectorResolver();
        await using RootLocalTableVectorDocument document = await RootLocalTableVectorDocument.BuildAsync(
            seed, session, resolver, CreateTableSource(resolver), FlowDirection.RightToLeft, 1.5f);
        TableBox table = FindTable(document.Snapshot);
        InlineContainerBox[] cells = table.GetCellBoxes().ToArray();
        InlineRun[][] runs = cells.Select(static cell => cell.Runs.ToArray()).ToArray();
        InlineVectorSceneRun vector = cells.SelectMany(static cell => cell.Runs.OfType<InlineVectorSceneRun>())
            .First();
        ImageBox image = EnumerateTableImages(table).First();
        resolver.ReleaseImages();
        await LoadImagesAsync(EnumerateTableImages(table));
        CanvasTextLayout layout = GetInlineLayout(FindOwnerCell(cells, vector));
        VectorSceneDrawing drawing = GetVectorDrawing(vector);
        CanvasBitmap bitmap = GetBitmap(image);
        SharedCanvasBitmapCache.Lease lease = GetBitmapLease(image);
        Rect bounds = table.Bounds;
        Rect imageBounds = image.Bounds;
        Rect vectorBounds = vector.LocalBounds;
        float vectorWidth = vector.DesiredWidth;
        long revision = document.Snapshot.LayoutRevision;
        using var cancellation = new CancellationTokenSource();
        MarkdownLayoutContext? constructionContext = null;
        try
        {
            Assert.ThrowsAny<OperationCanceledException>(() =>
                document.Snapshot.TryRelayoutCompletedImageRoots(
                [table.BlockIndex], InitialWidth, cancellation.Token, out constructionContext,
                    phaseHooks: CompletedImageRelayoutPhaseHooks.BeforeMeasure(cancellation.Cancel),
                    expectedPointerInputEpoch: document.Snapshot.PointerInputEpoch));
            Assert.NotNull(constructionContext);
            await constructionContext.DrainOwnedRetirementsAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(revision, document.Snapshot.LayoutRevision);
            Assert.Same(table, FindTable(document.Snapshot));
            Assert.Equal(bounds, table.Bounds);
            Assert.Same(layout, GetInlineLayout(FindOwnerCell(cells, vector)));
            Assert.Same(drawing, GetVectorDrawing(vector));
            Assert.True(vector.IsNativeResourcesPrepared);
            Assert.Equal(vectorBounds, vector.LocalBounds);
            Assert.Equal(vectorWidth, vector.DesiredWidth);
            Assert.Equal(imageBounds, image.Bounds);
            Assert.Same(bitmap, image.Bitmap);
            Assert.Same(lease, GetBitmapLease(image));
            for (int cellIndex = 0; cellIndex < cells.Length; cellIndex++)
            {
                Assert.Same(cells[cellIndex], table.GetCellBoxes().ElementAt(cellIndex));
                AssertRunsPreserved(runs[cellIndex], cells[cellIndex].Runs);
            }
        }
        finally
        {
            await DrainConstructionAsync(constructionContext);
        }
    }

    [Fact]
    public async Task MalformedOrCappedVectorCellFailsClosedWithoutImageLeaseTransfer()
    {
        await using var seed = new CompletedRasterPlacementCloneTests.PlacementFixture();
        using var session = new MarkdownPerformanceSession(MarkdownPerformanceOptions.Progressive);

        var malformedResolver = new RootLocalTableVectorResolver();
        await using RootLocalTableVectorDocument malformed = await RootLocalTableVectorDocument.BuildAsync(
            seed, session, malformedResolver, CreateTableSource(malformedResolver),
            FlowDirection.LeftToRight, 1f);
        await AssertMalformedVectorBoundsFailClosedAsync(malformed, malformedResolver);

        var cappedResolver = new RootLocalTableVectorResolver();
        await using RootLocalTableVectorDocument capped = await RootLocalTableVectorDocument.BuildAsync(
            seed, session, cappedResolver, CreateCappedVectorCellSource(cappedResolver), FlowDirection.LeftToRight, 1f);
        await AssertCappedVectorCellFailsClosedAsync(capped, cappedResolver);
    }

    /// <summary>
    /// A vector run with NaN bounds makes the table unavailable before any
    /// construction context exists; its owners and image lease stay put.
    /// </summary>
    private static async Task AssertMalformedVectorBoundsFailClosedAsync(
        RootLocalTableVectorDocument malformed,
        RootLocalTableVectorResolver malformedResolver)
    {
        TableBox malformedTable = FindTable(malformed.Snapshot);
        InlineContainerBox[] malformedCells = malformedTable.GetCellBoxes().ToArray();
        InlineVectorSceneRun malformedVector = EnumerateVectors(malformedCells).First();
        ImageBox malformedImage = EnumerateTableImages(malformedTable).First();
        malformedResolver.ReleaseImages();
        await LoadImagesAsync(EnumerateTableImages(malformedTable));
        Rect malformedBounds = malformedTable.Bounds;
        CanvasTextLayout malformedLayout = GetInlineLayout(FindOwnerCell(malformedCells, malformedVector));
        CanvasBitmap malformedBitmap = GetBitmap(malformedImage);
        SharedCanvasBitmapCache.Lease malformedLease = GetBitmapLease(malformedImage);
        Rect validVectorBounds = malformedVector.LocalBounds;
        malformedVector.RestoreLayoutState(malformedVector.DesiredWidth, malformedVector.DesiredHeight,
            malformedVector.DesiredBaseline, validVectorBounds with { X = double.NaN });
        MarkdownLayoutContext? malformedContext = null;
        try
        {
            PreparedImageRelayoutResult result = malformed.Snapshot.TryRelayoutCompletedImageRoots(
                [malformedTable.BlockIndex], InitialWidth, CancellationToken.None, out malformedContext,
                expectedPointerInputEpoch: malformed.Snapshot.PointerInputEpoch);
            Assert.Equal(PreparedImageRelayoutResult.Unavailable, result);
            Assert.Null(malformedContext);
            Assert.Equal(malformedBounds, malformedTable.Bounds);
            Assert.Same(malformedLayout, GetInlineLayout(FindOwnerCell(malformedCells, malformedVector)));
            Assert.True(double.IsNaN(malformedVector.LocalBounds.X));
            Assert.True(malformedVector.IsNativeResourcesPrepared);
            Assert.Same(malformedBitmap, malformedImage.Bitmap);
            Assert.Same(malformedLease, GetBitmapLease(malformedImage));
        }
        finally
        {
            malformedVector.RestoreLayoutState(malformedVector.DesiredWidth, malformedVector.DesiredHeight,
                malformedVector.DesiredBaseline, validVectorBounds);
            await DrainConstructionAsync(malformedContext);
        }
    }

    /// <summary>
    /// A vector cell with more runs than private raster placement allows makes
    /// the table unavailable before any lease is taken or resolve is repeated.
    /// </summary>
    private static async Task AssertCappedVectorCellFailsClosedAsync(
        RootLocalTableVectorDocument capped,
        RootLocalTableVectorResolver cappedResolver)
    {
        TableBox cappedTable = FindTable(capped.Snapshot);
        InlineContainerBox cappedCell = Assert.Single(cappedTable.GetCellBoxes(), static cell =>
            cell.Runs.OfType<InlineVectorSceneRun>().Any());
        Assert.True(cappedCell.Runs.Count > InlineContainerBox.MaximumPrivateRasterPlacementRuns);
        Assert.InRange(cappedCell.PrivateRasterPlacementTextLength, 0,
            InlineContainerBox.MaximumPrivateRasterPlacementTextLength);
        Assert.Contains(cappedCell.Runs, static run => run is InlineVectorSceneRun);
        ImageBox cappedImage = Assert.Single(EnumerateTableImages(cappedTable));
        cappedResolver.ReleaseImages();
        await LoadImageAsync(cappedImage);
        CanvasBitmap cappedBitmap = GetBitmap(cappedImage);
        SharedCanvasBitmapCache.Lease cappedLease = GetBitmapLease(cappedImage);
        int leaseReferences = GetLeaseReferenceCount(cappedLease);
        int[] resolveCounts = cappedResolver.CaptureResolveCounts();
        Rect cappedBounds = cappedTable.Bounds;
        MarkdownLayoutContext? cappedContext = null;
        try
        {
            PreparedImageRelayoutResult cappedResult = capped.Snapshot.TryRelayoutCompletedImageRoots(
                [cappedTable.BlockIndex], InitialWidth, CancellationToken.None, out cappedContext,
                expectedPointerInputEpoch: capped.Snapshot.PointerInputEpoch);
            Assert.Equal(PreparedImageRelayoutResult.Unavailable, cappedResult);
            Assert.Null(cappedContext);
            Assert.Equal(cappedBounds, cappedTable.Bounds);
            Assert.Same(cappedBitmap, cappedImage.Bitmap);
            Assert.Same(cappedLease, GetBitmapLease(cappedImage));
            Assert.Equal(leaseReferences, GetLeaseReferenceCount(cappedLease));
            Assert.Equal(resolveCounts, cappedResolver.CaptureResolveCounts());
        }
        finally
        {
            await DrainConstructionAsync(cappedContext);
        }
    }

    private static string CreateTableSource(RootLocalTableVectorResolver resolver) =>
        "| Item | Preview | Details |\n" +
        "| --- | --- | --- |\n" +
        "| Primary | ![wide preview](" + resolver.WideUrl + ") then $x^2$ | [primary link](https://example.test/primary) and **strong** |\n" +
        "| Vector only | $y^2$ plus *emphasis* | text without an image |\n" +
        "| Secondary | ![tall preview](" + resolver.TallUrl + ") with $z^2$ | [details](https://example.test/details) |\n";

    private static string CreateOverflowTableSource(RootLocalTableVectorResolver resolver) =>
        "| Item | Preview | Details |\n" +
        "| --- | --- | --- |\n" +
        "| Primary | ![wide preview](" + resolver.WideUrl + ") and $x^2$ | " +
        "UnbreakableColumnMinimumTokenABCDEFGHIJKLMNOPQRSTUVWXYZABCDEFGHIJKLMNOPQRSTUVWXYZABCDEFGHIJKLMNOPQRSTUVWXYZABCDEFGHIJKLMNOPQRSTUVWXYZABCDEFGHIJKLMNOPQRSTUVWXYZABCDEFGHIJKLMNOPQRSTUVWXYZ |\n" +
        "| Vector only | $y^2$ | text |\n" +
        "| Secondary | ![tall preview](" + resolver.TallUrl + ") with $z^2$ | notes |\n";

    private static string CreateCappedVectorCellSource(RootLocalTableVectorResolver resolver)
    {
        string manyStyledRuns = string.Join(" ", Enumerable.Range(0, 140)
            .Select(static index => index % 2 == 0 ? "**x**" : "*y*"));
        return "| Kind | Preview | Details |\n| --- | --- | --- |\n" +
            "| media | ![capped image](" + resolver.WideUrl + ") | image cell |\n" +
            "| vector | " + manyStyledRuns + " $x^2$ | text |\n";
    }

    private static void AssertCellsAndRunsPreserved(
        InlineContainerBox[] cellsBefore,
        InlineRun[][] runsBefore,
        InlineContainerBox[] cellsAfter)
    {
        Assert.Equal(cellsBefore.Length, cellsAfter.Length);
        for (int cellIndex = 0; cellIndex < cellsBefore.Length; cellIndex++)
        {
            Assert.Same(cellsBefore[cellIndex], cellsAfter[cellIndex]);
            AssertRunsPreserved(runsBefore[cellIndex], cellsAfter[cellIndex].Runs);
        }
    }

    private static void AssertVectorDrawingsPreparedAndPreserved(
        VectorSceneDrawing[] drawingsBefore,
        InlineVectorSceneRun[] vectorsAfter)
    {
        Assert.Equal(drawingsBefore.Length, vectorsAfter.Length);
        for (int index = 0; index < vectorsAfter.Length; index++)
        {
            Assert.Same(drawingsBefore[index], GetVectorDrawing(vectorsAfter[index]));
            Assert.True(vectorsAfter[index].IsNativeResourcesPrepared);
        }
    }
}
