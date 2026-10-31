using System.Collections.Concurrent;
using System.Reflection;
using MarkdownRenderer.Accessibility;
using MarkdownRenderer.Document;
using MarkdownRenderer.Extensions;
using MarkdownRenderer.Images;
using MarkdownRenderer.Layout;
using MarkdownRenderer.Layout.Boxes;
using MarkdownRenderer.Math;
using MarkdownRenderer.Performance;
using MarkdownRenderer.Parsing;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.UI.Xaml;
using Windows.Foundation;
using Windows.UI;
using Xunit;

namespace MarkdownRenderer.GitHub.Tests;

[Collection(SharedCanvasBitmapCacheTestCollection.Name)]
public sealed class RootLocalTableVectorImageRelayoutTests
{
    private const float InitialWidth = 640f;
    private const float NarrowWidth = 320f;
    private const long AutomationGeneration = 84_221;

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
        var optimizedResolver = new TableImageResolver();
        var fallbackResolver = new TableImageResolver();
        FlowDirection direction = rightToLeft ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
        await using TableDocument optimized = await BuildDocumentAsync(
            seed, session, optimizedResolver, CreateTableSource(optimizedResolver), direction, rasterizationScale);
        await using TableDocument fallback = await BuildDocumentAsync(
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
        foreach (ImageBox image in images.Concat(fallbackImages))
            await LoadImageAsync(image);

        InlineVectorSceneRun[] vectors = cellsBefore
            .SelectMany(static cell => cell.Runs.OfType<InlineVectorSceneRun>()).ToArray();
        InlineVectorSceneRun[] fallbackVectors = fallbackCellsBefore
            .SelectMany(static cell => cell.Runs.OfType<InlineVectorSceneRun>()).ToArray();
        Assert.Equal(3, vectors.Length);
        Assert.Equal(vectors.Length, fallbackVectors.Length);
        VectorSceneDrawing[] drawingsBefore = vectors.Select(GetVectorDrawing).ToArray();
        MarkdownVectorScene[] scenesBefore = vectors
            .Select(vector => Assert.IsType<MarkdownVectorScene>(vector.Scene)).ToArray();
        Assert.All(vectors, static vector => Assert.True(vector.IsNativeResourcesPrepared));
        Assert.All(vectors, static vector => Assert.False(vector.IsMeasuredPlacementOnly));

        BlockBox[] rootsBefore = optimized.Snapshot.Blocks.ToArray();
        InlineRun[][] runsBefore = cellsBefore.Select(static cell => cell.Runs.ToArray()).ToArray();
        CanvasTextLayout[] layoutsBefore = cellsBefore.Select(GetInlineLayout).ToArray();
        CanvasBitmap[] bitmapsBefore = images.Select(image => Assert.IsType<CanvasBitmap>(image.Bitmap)).ToArray();
        SharedCanvasBitmapCache.Lease[] leasesBefore = images.Select(GetBitmapLease).ToArray();
        int[] leaseReferencesBefore = leasesBefore.Select(GetLeaseReferenceCount).ToArray();
        float[] initialImageWidths = images.Select(static image => image.MeasuredImageWidth).ToArray();
        float[] initialImageHeights = images.Select(static image => image.MeasuredImageHeight).ToArray();
        float[] initialColumns = CaptureColumnWidths(table);
        MarkdownSourceMap sourceMapBefore = optimized.Snapshot.SourceMap;
        MarkdownSemanticDocument semanticBefore = optimized.Snapshot.SemanticDocument;
        string semanticTextBefore = semanticBefore.Text;
        var textProjectionBefore = semanticBefore.TextProjectionFingerprint;
        var semanticRangesBefore = semanticBefore.SemanticRangeFingerprint;
        long revisionBefore = optimized.Snapshot.LayoutRevision;
        Rect[][] initialCellSelections = cellsBefore
            .Select(cell => CaptureCellSelectionRects(optimized.Snapshot, cell)).ToArray();
        Rect[][] initialVectorSelections = vectors.Select(vector =>
            CaptureRunSelectionRects(
                optimized.Snapshot, FindOwnerCell(cellsBefore, vector), vector)).ToArray();
        int[] resolveCountsBefore = optimizedResolver.Sources
            .Select(optimizedResolver.ResolveCountFor).ToArray();

        // The full table rebuild remains the geometry and raster oracle. A
        // completed-image remeasure keeps the snapshot's available width fixed;
        // intrinsic image measurements may still rebalance its columns.
        fallback.Snapshot.RelayoutChangedBlocks(
            [fallbackTable.BlockIndex], InitialWidth, CancellationToken.None);

        MarkdownLayoutContext? constructionContext = null;
        try
        {
            PreparedImageRelayoutResult result = optimized.Snapshot.TryRelayoutCompletedImageRoots(
                [table.BlockIndex],
                InitialWidth,
                CancellationToken.None,
                out constructionContext,
                expectedPointerInputEpoch: optimized.Snapshot.PointerInputEpoch);

            Assert.Equal(PreparedImageRelayoutResult.Committed, result);
            Assert.NotNull(constructionContext);
            // Construction image clones release their borrowed bitmap leases
            // asynchronously. Stable owner/refcount assertions belong after
            // the registered retirement barrier, not before its forced yield.
            await constructionContext.DrainOwnedRetirementsAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(revisionBefore + 1, optimized.Snapshot.LayoutRevision);
            Assert.Same(sourceMapBefore, optimized.Snapshot.SourceMap);
            Assert.Same(semanticBefore, optimized.Snapshot.SemanticDocument);
            Assert.Equal(semanticTextBefore, optimized.Snapshot.SemanticDocument.Text);
            Assert.Equal(textProjectionBefore, optimized.Snapshot.SemanticDocument.TextProjectionFingerprint);
            Assert.Equal(semanticRangesBefore, optimized.Snapshot.SemanticDocument.SemanticRangeFingerprint);
            Assert.Equal(AutomationGeneration, optimized.Snapshot.AutomationDocumentGeneration);
            Assert.Equal(fallback.Snapshot.Size, optimized.Snapshot.Size);
            Assert.Same(table, FindTable(optimized.Snapshot));
            Assert.Equal(rootsBefore.Length, optimized.Snapshot.Blocks.Count);
            for (int rootIndex = 0; rootIndex < rootsBefore.Length; rootIndex++)
            {
                Assert.Same(rootsBefore[rootIndex], optimized.Snapshot.Blocks[rootIndex]);
                Assert.Equal(
                    LayoutPlacementTestGeometry.ToLogical(
                        fallback.Snapshot, fallback.Snapshot.Blocks[rootIndex]),
                    LayoutPlacementTestGeometry.ToLogical(
                        optimized.Snapshot, optimized.Snapshot.Blocks[rootIndex]));
            }

            InlineContainerBox[] cellsAfter = table.GetCellBoxes().ToArray();
            InlineContainerBox[] fallbackCells = FindTable(fallback.Snapshot).GetCellBoxes().ToArray();
            Assert.Equal(cellsBefore.Length, cellsAfter.Length);
            Assert.Equal(cellsBefore.Length, fallbackCells.Length);
            for (int cellIndex = 0; cellIndex < cellsBefore.Length; cellIndex++)
            {
                Assert.Same(cellsBefore[cellIndex], cellsAfter[cellIndex]);
                Assert.Equal(runsBefore[cellIndex].Length, cellsAfter[cellIndex].Runs.Count);
                for (int runIndex = 0; runIndex < runsBefore[cellIndex].Length; runIndex++)
                    Assert.Same(runsBefore[cellIndex][runIndex], cellsAfter[cellIndex].Runs[runIndex]);
                Assert.NotSame(layoutsBefore[cellIndex], GetInlineLayout(cellsAfter[cellIndex]));
                Assert.Equal(
                    LayoutPlacementTestGeometry.ToLogical(
                        fallback.Snapshot, table.BlockIndex, [fallbackCells[cellIndex].Bounds])[0],
                    LayoutPlacementTestGeometry.ToLogical(
                        optimized.Snapshot, table.BlockIndex, [cellsAfter[cellIndex].Bounds])[0]);
                Assert.Equal(
                    CaptureCellSelectionRects(fallback.Snapshot, fallbackCells[cellIndex]),
                    CaptureCellSelectionRects(optimized.Snapshot, cellsAfter[cellIndex]));
            }

            InlineVectorSceneRun[] vectorsAfter = cellsAfter
                .SelectMany(static cell => cell.Runs.OfType<InlineVectorSceneRun>()).ToArray();
            InlineVectorSceneRun[] fallbackVectorsAfter = fallbackCells
                .SelectMany(static cell => cell.Runs.OfType<InlineVectorSceneRun>()).ToArray();
            Assert.Equal(vectors.Length, vectorsAfter.Length);
            Assert.Equal(vectors.Length, fallbackVectorsAfter.Length);
            for (int vectorIndex = 0; vectorIndex < vectors.Length; vectorIndex++)
            {
                Assert.Same(vectors[vectorIndex], vectorsAfter[vectorIndex]);
                Assert.Same(scenesBefore[vectorIndex], vectorsAfter[vectorIndex].Scene);
                Assert.Same(drawingsBefore[vectorIndex], GetVectorDrawing(vectorsAfter[vectorIndex]));
                Assert.True(vectorsAfter[vectorIndex].IsNativeResourcesPrepared);
                Assert.False(vectorsAfter[vectorIndex].IsMeasuredPlacementOnly);
                Assert.Equal(fallbackVectorsAfter[vectorIndex].DesiredWidth,
                    vectorsAfter[vectorIndex].DesiredWidth);
                Assert.Equal(fallbackVectorsAfter[vectorIndex].DesiredHeight,
                    vectorsAfter[vectorIndex].DesiredHeight);
                Assert.Equal(fallbackVectorsAfter[vectorIndex].DesiredBaseline,
                    vectorsAfter[vectorIndex].DesiredBaseline);
                Assert.Equal(fallbackVectorsAfter[vectorIndex].LocalBounds,
                    vectorsAfter[vectorIndex].LocalBounds);
                Assert.Equal(fallbackVectorsAfter[vectorIndex].Scene.Width,
                    vectorsAfter[vectorIndex].Scene.Width);
                Assert.Equal(fallbackVectorsAfter[vectorIndex].Scene.Height,
                    vectorsAfter[vectorIndex].Scene.Height);
                InlineContainerBox owner = FindOwnerCell(cellsAfter, vectorsAfter[vectorIndex]);
                InlineContainerBox fallbackOwner = FindOwnerCell(fallbackCells, fallbackVectorsAfter[vectorIndex]);
                Assert.Equal(
                    CaptureRunSelectionRects(fallback.Snapshot, fallbackOwner, fallbackVectorsAfter[vectorIndex]),
                    CaptureRunSelectionRects(optimized.Snapshot, owner, vectorsAfter[vectorIndex]));
            }

            Rect[][] finalCellSelections = cellsAfter
                .Select(cell => CaptureCellSelectionRects(optimized.Snapshot, cell)).ToArray();
            Assert.Equal(initialCellSelections.Length, finalCellSelections.Length);
            Assert.True(initialCellSelections.Where((rects, index) =>
                    !rects.SequenceEqual(finalCellSelections[index])).Any(),
                "Measured image placement must update table-cell selection geometry.");
            Rect[][] finalVectorSelections = vectorsAfter.Select(vector =>
                CaptureRunSelectionRects(
                    optimized.Snapshot, FindOwnerCell(cellsAfter, vector), vector)).ToArray();
            Assert.True(initialVectorSelections.Where((rects, index) =>
                    !rects.SequenceEqual(finalVectorSelections[index])).Any(),
                "At least one vector selection range must be relocated with its measured cell.");

            ImageBox[] imagesAfter = EnumerateTableImages(table).ToArray();
            ImageBox[] fallbackImagesAfter = EnumerateTableImages(FindTable(fallback.Snapshot)).ToArray();
            Assert.Equal(images.Length, imagesAfter.Length);
            Assert.Equal(images.Length, fallbackImagesAfter.Length);
            bool imageResized = false;
            for (int imageIndex = 0; imageIndex < images.Length; imageIndex++)
            {
                Assert.Same(images[imageIndex], imagesAfter[imageIndex]);
                Assert.Same(bitmapsBefore[imageIndex], imagesAfter[imageIndex].Bitmap);
                Assert.Same(leasesBefore[imageIndex], GetBitmapLease(imagesAfter[imageIndex]));
                Assert.Equal(leaseReferencesBefore[imageIndex], GetLeaseReferenceCount(leasesBefore[imageIndex]));
                Assert.Equal(
                    LayoutPlacementTestGeometry.ToLogical(
                        fallback.Snapshot, table.BlockIndex, [fallbackImagesAfter[imageIndex].Bounds])[0],
                    LayoutPlacementTestGeometry.ToLogical(
                        optimized.Snapshot, table.BlockIndex, [imagesAfter[imageIndex].Bounds])[0]);
                Assert.Equal(fallbackImagesAfter[imageIndex].MeasuredImageWidth,
                    imagesAfter[imageIndex].MeasuredImageWidth);
                Assert.Equal(fallbackImagesAfter[imageIndex].MeasuredImageHeight,
                    imagesAfter[imageIndex].MeasuredImageHeight);
                imageResized |= System.Math.Abs(initialImageWidths[imageIndex] -
                    imagesAfter[imageIndex].MeasuredImageWidth) > 0.5f ||
                    System.Math.Abs(initialImageHeights[imageIndex] -
                    imagesAfter[imageIndex].MeasuredImageHeight) > 0.5f;
            }
            Assert.True(imageResized, "Decoded image dimensions must be remeasured before table placement is transferred.");
            Assert.Equal(resolveCountsBefore, optimizedResolver.Sources
                .Select(optimizedResolver.ResolveCountFor).ToArray());
            Assert.Equal(CaptureColumnWidths(FindTable(fallback.Snapshot)), CaptureColumnWidths(table));
            Assert.True(initialColumns.Where((width, index) =>
                    System.Math.Abs(width - CaptureColumnWidths(table)[index]) > 0.5f).Any(),
                "Decoded image dimensions must participate in table column rebalancing.");
            Assert.Equal(FindTable(fallback.Snapshot).HorizontalOffset, table.HorizontalOffset);
            Assert.Equal(FindTable(fallback.Snapshot).HorizontalExtent, table.HorizontalExtent);
            Assert.Equal(FindTable(fallback.Snapshot).HorizontalViewport, table.HorizontalViewport);
            AssertSemanticTextAndSpanGeometryEqual(optimized.Snapshot, fallback.Snapshot);
            Assert.Equal(Paint(optimized.Snapshot, seed.Device, rasterizationScale),
                Paint(fallback.Snapshot, seed.Device, rasterizationScale));
        }
        finally
        {
            if (constructionContext is not null)
                await constructionContext.DrainOwnedRetirementsAsync().WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OverflowingVectorImageTablePreservesClampedHorizontalViewport(bool rightToLeft)
    {
        await using var seed = new CompletedRasterPlacementCloneTests.PlacementFixture();
        using var session = new MarkdownPerformanceSession(MarkdownPerformanceOptions.Progressive);
        var optimizedResolver = new TableImageResolver();
        var fallbackResolver = new TableImageResolver();
        FlowDirection direction = rightToLeft ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
        string optimizedSource = CreateOverflowTableSource(optimizedResolver);
        string fallbackSource = CreateOverflowTableSource(fallbackResolver);
        await using TableDocument optimized = await BuildDocumentAsync(
            seed, session, optimizedResolver, optimizedSource, direction, 1.5f, NarrowWidth);
        await using TableDocument fallback = await BuildDocumentAsync(
            seed, session, fallbackResolver, fallbackSource, direction, 1.5f, NarrowWidth);

        TableBox table = FindTable(optimized.Snapshot);
        TableBox fallbackTable = FindTable(fallback.Snapshot);
        ImageBox[] images = EnumerateTableImages(table).ToArray();
        ImageBox[] fallbackImages = EnumerateTableImages(fallbackTable).ToArray();
        Assert.Equal(2, images.Length);
        optimizedResolver.ReleaseImages();
        fallbackResolver.ReleaseImages();
        foreach (ImageBox image in images.Concat(fallbackImages))
            await LoadImageAsync(image);
        Assert.True(table.CanScrollHorizontally);
        Assert.True(fallbackTable.CanScrollHorizontally);
        double previousMaximum = System.Math.Max(0, table.HorizontalExtent - table.HorizontalViewport);
        Assert.True(previousMaximum > 0);
        Assert.True(table.SetHorizontalOffset(previousMaximum));
        Assert.True(fallbackTable.SetHorizontalOffset(previousMaximum));

        InlineContainerBox[] cellsBefore = table.GetCellBoxes().ToArray();
        InlineRun[][] runsBefore = cellsBefore.Select(static cell => cell.Runs.ToArray()).ToArray();
        VectorSceneDrawing[] drawingsBefore = cellsBefore
            .SelectMany(static cell => cell.Runs.OfType<InlineVectorSceneRun>())
            .Select(GetVectorDrawing).ToArray();
        Assert.Equal(3, drawingsBefore.Length);
        CanvasBitmap[] bitmapsBefore = images.Select(image => Assert.IsType<CanvasBitmap>(image.Bitmap)).ToArray();
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
            Assert.Equal(cellsBefore.Length, cellsAfter.Length);
            for (int cellIndex = 0; cellIndex < cellsBefore.Length; cellIndex++)
            {
                Assert.Same(cellsBefore[cellIndex], cellsAfter[cellIndex]);
                Assert.Equal(runsBefore[cellIndex].Length, cellsAfter[cellIndex].Runs.Count);
                for (int runIndex = 0; runIndex < runsBefore[cellIndex].Length; runIndex++)
                    Assert.Same(runsBefore[cellIndex][runIndex], cellsAfter[cellIndex].Runs[runIndex]);
            }
            InlineVectorSceneRun[] vectorsAfter = cellsAfter
                .SelectMany(static cell => cell.Runs.OfType<InlineVectorSceneRun>()).ToArray();
            Assert.Equal(drawingsBefore.Length, vectorsAfter.Length);
            for (int index = 0; index < vectorsAfter.Length; index++)
            {
                Assert.Same(drawingsBefore[index], GetVectorDrawing(vectorsAfter[index]));
                Assert.True(vectorsAfter[index].IsNativeResourcesPrepared);
            }
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
            if (constructionContext is not null)
                await constructionContext.DrainOwnedRetirementsAsync().WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    [Fact]
    public async Task StaleLaterTableVectorRejectsWholeCandidateBeforeEarlierImageTransfer()
    {
        await using var seed = new CompletedRasterPlacementCloneTests.PlacementFixture();
        using var session = new MarkdownPerformanceSession(MarkdownPerformanceOptions.Progressive);
        var resolver = new TableImageResolver();
        await using TableDocument document = await BuildDocumentAsync(
            seed, session, resolver, CreateTableSource(resolver), FlowDirection.LeftToRight, 1.5f);
        TableBox table = FindTable(document.Snapshot);
        InlineContainerBox[] cells = table.GetCellBoxes().ToArray();
        InlineVectorSceneRun vector = cells.SelectMany(static cell => cell.Runs.OfType<InlineVectorSceneRun>())
            .Last();
        ImageBox image = EnumerateTableImages(table).First();
        resolver.ReleaseImages();
        foreach (ImageBox tableImage in EnumerateTableImages(table))
            await LoadImageAsync(tableImage);
        Rect imageBounds = image.Bounds;
        CanvasBitmap bitmap = Assert.IsType<CanvasBitmap>(image.Bitmap);
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
                beforeMeasureOutsideGateForTests: () => vector.RestoreLayoutState(
                    mutatedWidth, vector.DesiredHeight, vector.DesiredBaseline, mutatedBounds),
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
                Assert.Equal(runs[cellIndex].Length, cells[cellIndex].Runs.Count);
                for (int runIndex = 0; runIndex < runs[cellIndex].Length; runIndex++)
                    Assert.Same(runs[cellIndex][runIndex], cells[cellIndex].Runs[runIndex]);
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
            if (constructionContext is not null)
                await constructionContext.DrainOwnedRetirementsAsync().WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    [Fact]
    public async Task CancellationAfterTableCandidateCaptureLeavesImageAndVectorOwnersUntouched()
    {
        await using var seed = new CompletedRasterPlacementCloneTests.PlacementFixture();
        using var session = new MarkdownPerformanceSession(MarkdownPerformanceOptions.Progressive);
        var resolver = new TableImageResolver();
        await using TableDocument document = await BuildDocumentAsync(
            seed, session, resolver, CreateTableSource(resolver), FlowDirection.RightToLeft, 1.5f);
        TableBox table = FindTable(document.Snapshot);
        InlineContainerBox[] cells = table.GetCellBoxes().ToArray();
        InlineRun[][] runs = cells.Select(static cell => cell.Runs.ToArray()).ToArray();
        InlineVectorSceneRun vector = cells.SelectMany(static cell => cell.Runs.OfType<InlineVectorSceneRun>())
            .First();
        ImageBox image = EnumerateTableImages(table).First();
        resolver.ReleaseImages();
        foreach (ImageBox tableImage in EnumerateTableImages(table))
            await LoadImageAsync(tableImage);
        CanvasTextLayout layout = GetInlineLayout(FindOwnerCell(cells, vector));
        VectorSceneDrawing drawing = GetVectorDrawing(vector);
        CanvasBitmap bitmap = Assert.IsType<CanvasBitmap>(image.Bitmap);
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
                    beforeMeasureOutsideGateForTests: cancellation.Cancel,
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
                Assert.Equal(runs[cellIndex].Length, cells[cellIndex].Runs.Count);
                for (int runIndex = 0; runIndex < runs[cellIndex].Length; runIndex++)
                    Assert.Same(runs[cellIndex][runIndex], cells[cellIndex].Runs[runIndex]);
            }
        }
        finally
        {
            if (constructionContext is not null)
                await constructionContext.DrainOwnedRetirementsAsync().WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    [Fact]
    public async Task MalformedOrCappedVectorCellFailsClosedWithoutImageLeaseTransfer()
    {
        await using var seed = new CompletedRasterPlacementCloneTests.PlacementFixture();
        using var session = new MarkdownPerformanceSession(MarkdownPerformanceOptions.Progressive);

        var malformedResolver = new TableImageResolver();
        await using TableDocument malformed = await BuildDocumentAsync(
            seed, session, malformedResolver, CreateTableSource(malformedResolver),
            FlowDirection.LeftToRight, 1f);
        TableBox malformedTable = FindTable(malformed.Snapshot);
        InlineContainerBox[] malformedCells = malformedTable.GetCellBoxes().ToArray();
        InlineVectorSceneRun malformedVector = malformedCells
            .SelectMany(static cell => cell.Runs.OfType<InlineVectorSceneRun>()).First();
        ImageBox malformedImage = EnumerateTableImages(malformedTable).First();
        malformedResolver.ReleaseImages();
        foreach (ImageBox tableImage in EnumerateTableImages(malformedTable))
            await LoadImageAsync(tableImage);
        Rect malformedBounds = malformedTable.Bounds;
        CanvasTextLayout malformedLayout = GetInlineLayout(FindOwnerCell(malformedCells, malformedVector));
        CanvasBitmap malformedBitmap = Assert.IsType<CanvasBitmap>(malformedImage.Bitmap);
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
            if (malformedContext is not null)
                await malformedContext.DrainOwnedRetirementsAsync().WaitAsync(TimeSpan.FromSeconds(10));
        }

        var cappedResolver = new TableImageResolver();
        string manyStyledRuns = string.Join(" ", Enumerable.Range(0, 140)
            .Select(static index => index % 2 == 0 ? "**x**" : "*y*"));
        string cappedSource =
            "| Kind | Preview | Details |\n| --- | --- | --- |\n" +
            "| media | ![capped image](" + cappedResolver.WideUrl + ") | image cell |\n" +
            "| vector | " + manyStyledRuns + " $x^2$ | text |\n";
        await using TableDocument capped = await BuildDocumentAsync(
            seed, session, cappedResolver, cappedSource, FlowDirection.LeftToRight, 1f);
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
        CanvasBitmap cappedBitmap = Assert.IsType<CanvasBitmap>(cappedImage.Bitmap);
        SharedCanvasBitmapCache.Lease cappedLease = GetBitmapLease(cappedImage);
        int leaseReferences = GetLeaseReferenceCount(cappedLease);
        int[] resolveCounts = cappedResolver.Sources.Select(cappedResolver.ResolveCountFor).ToArray();
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
            Assert.Equal(resolveCounts, cappedResolver.Sources.Select(cappedResolver.ResolveCountFor).ToArray());
        }
        finally
        {
            if (cappedContext is not null)
                await cappedContext.DrainOwnedRetirementsAsync().WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    private static string CreateTableSource(TableImageResolver resolver) =>
        "| Item | Preview | Details |\n" +
        "| --- | --- | --- |\n" +
        "| Primary | ![wide preview](" + resolver.WideUrl + ") then $x^2$ | [primary link](https://example.test/primary) and **strong** |\n" +
        "| Vector only | $y^2$ plus *emphasis* | text without an image |\n" +
        "| Secondary | ![tall preview](" + resolver.TallUrl + ") with $z^2$ | [details](https://example.test/details) |\n";

    private static string CreateOverflowTableSource(TableImageResolver resolver) =>
        "| Item | Preview | Details |\n" +
        "| --- | --- | --- |\n" +
        "| Primary | ![wide preview](" + resolver.WideUrl + ") and $x^2$ | " +
        "UnbreakableColumnMinimumTokenABCDEFGHIJKLMNOPQRSTUVWXYZABCDEFGHIJKLMNOPQRSTUVWXYZABCDEFGHIJKLMNOPQRSTUVWXYZABCDEFGHIJKLMNOPQRSTUVWXYZABCDEFGHIJKLMNOPQRSTUVWXYZABCDEFGHIJKLMNOPQRSTUVWXYZ |\n" +
        "| Vector only | $y^2$ | text |\n" +
        "| Secondary | ![tall preview](" + resolver.TallUrl + ") with $z^2$ | notes |\n";

    private static async Task<TableDocument> BuildDocumentAsync(
        CompletedRasterPlacementCloneTests.PlacementFixture seed,
        MarkdownPerformanceSession session,
        TableImageResolver resolver,
        string source,
        FlowDirection direction,
        float rasterizationScale,
        float width = InitialWidth)
    {
        MarkdownEngine engine = new MarkdownEngineBuilder()
            .UseProfile(MarkdownProfiles.GfmStrict)
            .UseMathematics()
            .WithParseCacheBudgetBytes(0)
            .Build();
        LayoutSnapshot? snapshot = null;
        MarkdownLayoutContext? context = null;
        try
        {
            MarkdownDocument markdown = Assert.IsType<MarkdownDocument>(await engine.ParseAsync(source));
            var registry = new MarkdownExtensionRegistry().ConfigureGitHubReadmeRegistry();
            var sourceMap = new MarkdownSourceMap(markdown.Source);
            context = new MarkdownLayoutContext(
                seed.Device,
                seed.SourceContext.ThemeSnapshot,
                sourceMap,
                registry,
                direction,
                language: "en-US")
            {
                CancellationToken = CancellationToken.None,
                ImageCancellationToken = CancellationToken.None,
                ImagePreparationOwner = seed.ImagePreparationOwnerToken,
                PerformanceDocumentOwner = new object(),
                PerformanceSession = session,
                ImageResolver = resolver,
                AllowThirdPartyRemoteImages = true,
                RasterizationScale = rasterizationScale,
            };
            snapshot = new LayoutBuilder(context, semanticDocument: markdown)
                .Build(markdown.ParsedDocument!, width, CancellationToken.None);
            snapshot.AssignAutomationDocumentGeneration(AutomationGeneration);
            var result = new TableDocument(context, snapshot, engine);
            snapshot = null;
            context = null;
            engine = null!;
            return result;
        }
        catch
        {
            try
            {
                if (snapshot is not null)
                    await snapshot.Retire().ConfigureAwait(false);
            }
            finally
            {
                if (context is not null)
                    await context.DrainOwnedRetirementsAsync().ConfigureAwait(false);
                engine.Dispose();
            }
            throw;
        }
    }

    private static TableBox FindTable(LayoutSnapshot snapshot) =>
        Assert.Single(snapshot.Blocks.OfType<TableBox>());

    private static IEnumerable<ImageBox> EnumerateTableImages(TableBox table)
    {
        foreach (InlineContainerBox cell in table.GetCellBoxes())
        foreach (InlineImageRun run in cell.Runs.OfType<InlineImageRun>())
            yield return run.Image;
    }

    private static async Task LoadImageAsync(ImageBox image)
    {
        await DisplaySizedRasterDecodeTests.WaitForLoadAsync(image).WaitAsync(TimeSpan.FromSeconds(10));
        await DisplaySizedRasterDecodeTests.WaitUntilAsync(() => image.ActiveImageWorkCountForTests == 0);
        Assert.NotNull(image.Bitmap);
    }

    private static float[] CaptureColumnWidths(TableBox table) =>
        table.GetCellInfos().Where(static info => info.IsHeader && info.Row == 0)
            .Select(static info => (float)info.Box.Bounds.Width).ToArray();

    private static Rect[] CaptureCellSelectionRects(LayoutSnapshot snapshot, InlineContainerBox cell)
    {
        (DocumentPosition start, DocumentPosition end) = cell.GetBlockBoundaries();
        return LayoutPlacementTestGeometry.ToLogical(
            snapshot, cell.BlockIndex, cell.GetRangeRects(new DocumentRange(start, end)));
    }

    private static Rect[] CaptureRunSelectionRects(
        LayoutSnapshot snapshot,
        InlineContainerBox cell,
        InlineRun run)
    {
        int runIndex = Enumerable.Range(0, cell.Runs.Count)
            .Single(index => ReferenceEquals(cell.Runs[index], run));
        var range = new DocumentRange(
            new DocumentPosition(cell.BlockIndex, runIndex, 0),
            new DocumentPosition(cell.BlockIndex, runIndex, run.RenderedLength));
        return LayoutPlacementTestGeometry.ToLogical(snapshot, cell.BlockIndex, cell.GetRangeRects(range));
    }

    private static InlineContainerBox FindOwnerCell(
        IReadOnlyList<InlineContainerBox> cells,
        InlineVectorSceneRun vector) =>
        Assert.Single(cells, cell => cell.Runs.Any(run => ReferenceEquals(run, vector)));

    private static CanvasTextLayout GetInlineLayout(InlineContainerBox inline) =>
        Assert.IsType<CanvasTextLayout>(GetRawField(inline, "_layout"));

    private static SharedCanvasBitmapCache.Lease GetBitmapLease(ImageBox image) =>
        Assert.IsType<SharedCanvasBitmapCache.Lease>(GetRawField(image, "_bitmapLease"));

    private static VectorSceneDrawing GetVectorDrawing(InlineVectorSceneRun run) =>
        Assert.IsType<VectorSceneDrawing>(GetRawField(run, "_drawing"));

    private static object? GetRawField(object instance, string name)
    {
        FieldInfo field = instance.GetType().GetField(name,
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Missing private test field: " + name);
        return field.GetValue(instance);
    }

    private static int GetLeaseReferenceCount(SharedCanvasBitmapCache.Lease lease)
    {
        object entry = Assert.IsAssignableFrom<object>(GetRawField(lease, "_entry"));
        FieldInfo references = entry.GetType().GetField("_referenceCount",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Missing bitmap cache reference counter.");
        return Assert.IsType<int>(references.GetValue(entry));
    }

    private static void AssertSemanticTextAndSpanGeometryEqual(
        LayoutSnapshot actual,
        LayoutSnapshot expected)
    {
        Assert.Equal(expected.SemanticDocument.Text, actual.SemanticDocument.Text);
        var expectedSpans = CaptureSemanticSpanGeometry(expected);
        var actualSpans = CaptureSemanticSpanGeometry(actual);
        Assert.Equal(expectedSpans.Length, actualSpans.Length);
        for (int index = 0; index < expectedSpans.Length; index++)
        {
            Assert.Equal(expectedSpans[index].Start, actualSpans[index].Start);
            Assert.Equal(expectedSpans[index].End, actualSpans[index].End);
            Assert.Equal(expectedSpans[index].Text, actualSpans[index].Text);
            Assert.Equal(expectedSpans[index].RunType, actualSpans[index].RunType);
            Assert.Equal(expectedSpans[index].Rects, actualSpans[index].Rects);
        }
    }

    private static SemanticSpanGeometry[] CaptureSemanticSpanGeometry(LayoutSnapshot snapshot) =>
        snapshot.SemanticDocument.TextSpans
            .Where(static span => span.InlineBox is not null)
            .Select(span => new SemanticSpanGeometry(
                span.TextStart,
                span.TextEnd,
                span.InlineRun?.GetType(),
                snapshot.SemanticDocument.Text.Substring(span.TextStart, span.TextEnd - span.TextStart),
                snapshot.SemanticDocument.GetDocumentRects(span.TextStart, span.TextEnd).ToArray()))
            .ToArray();

    private static Color[] Paint(LayoutSnapshot snapshot, CanvasDevice device, float rasterizationScale)
    {
        float width = (float)System.Math.Max(1, System.Math.Ceiling(snapshot.Size.Width + 1));
        float height = (float)System.Math.Max(1, System.Math.Ceiling(snapshot.Size.Height + 1));
        using var target = new CanvasRenderTarget(device, width, height, 96 * rasterizationScale);
        using (CanvasDrawingSession drawing = target.CreateDrawingSession())
        {
            drawing.Clear(Color.FromArgb(0, 0, 0, 0));
            snapshot.Paint(drawing, new Rect(0, 0, snapshot.Size.Width, height));
        }
        return target.GetPixelColors();
    }

    private readonly record struct SemanticSpanGeometry(
        int Start,
        int End,
        Type? RunType,
        string Text,
        Rect[] Rects);

    private sealed class TableDocument : IAsyncDisposable
    {
        internal TableDocument(
            MarkdownLayoutContext context,
            LayoutSnapshot snapshot,
            MarkdownEngine engine)
        {
            Context = context;
            Snapshot = snapshot;
            Engine = engine;
        }

        internal MarkdownLayoutContext Context { get; }
        internal LayoutSnapshot Snapshot { get; }
        private MarkdownEngine Engine { get; }

        public async ValueTask DisposeAsync()
        {
            try
            {
                await Snapshot.Retire().WaitAsync(TimeSpan.FromSeconds(10));
            }
            finally
            {
                try
                {
                    await Context.DrainOwnedRetirementsAsync().WaitAsync(TimeSpan.FromSeconds(10));
                }
                finally
                {
                    Engine.Dispose();
                }
            }
        }
    }

    private sealed class TableImageResolver : IMarkdownImageResolver
    {
        private readonly ConcurrentDictionary<string, MarkdownImageAsset> _assets =
            new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, int> _counts = new(StringComparer.Ordinal);
        private readonly string _identity = Guid.NewGuid().ToString("N");
        private readonly TaskCompletionSource _imagesReady = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal string WideUrl { get; } =
            "https://images.example.test/table-vector-wide-" + Guid.NewGuid().ToString("N") + ".png";
        internal string TallUrl { get; } =
            "https://images.example.test/table-vector-tall-" + Guid.NewGuid().ToString("N") + ".png";
        internal string[] Sources => _assets.Keys.Order(StringComparer.Ordinal).ToArray();
        internal int ResolveCountFor(string source) => _counts.TryGetValue(source, out int count) ? count : 0;
        internal void ReleaseImages() => _imagesReady.TrySetResult();

        internal TableImageResolver()
        {
            AddAsset(WideUrl, 480, 96);
            AddAsset(TallUrl, 96, 240);
        }

        public async ValueTask<MarkdownImageResolution> ResolveAsync(
            string source,
            MarkdownImageResolveContext context,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await _imagesReady.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            _counts.AddOrUpdate(source, 1, static (_, count) => count + 1);
            return _assets.TryGetValue(source, out MarkdownImageAsset? asset)
                ? MarkdownImageResolution.Resolved(asset)
                : MarkdownImageResolution.Unavailable;
        }

        private void AddAsset(string source, int width, int height)
        {
            _assets[source] = new MarkdownImageAsset(
                DisplaySizedRasterDecodeTests.CreatePng(width, height),
                "image/png",
                new Uri(source),
                "root-local-table-vector:" + _identity + ":" + Guid.NewGuid().ToString("N"));
        }
    }
}
