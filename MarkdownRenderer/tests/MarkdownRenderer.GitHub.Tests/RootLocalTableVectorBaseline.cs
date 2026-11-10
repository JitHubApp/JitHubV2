using MarkdownRenderer.Accessibility.Internal.Peers;
using MarkdownRenderer.Accessibility.Internal.Semantics;
using MarkdownRenderer.Document;
using MarkdownRenderer.Extensions;
using MarkdownRenderer.Images;
using MarkdownRenderer.Layout;
using MarkdownRenderer.Layout.Boxes;
using MarkdownRenderer.Layout.Internal.Images;
using MarkdownRenderer.Layout.Internal.Images.BitmapCache;
using MarkdownRenderer.Math;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Text;
using Windows.Foundation;
using Xunit;
using static MarkdownRenderer.GitHub.Tests.RootLocalTableQueries;
using static MarkdownRenderer.GitHub.Tests.RootLocalTableRelayoutAssertions;
using static MarkdownRenderer.GitHub.Tests.RootLocalTableVectorQueries;

namespace MarkdownRenderer.GitHub.Tests;

/// <summary>
/// Everything the committed root-table transfer must keep, captured after
/// the images decode: the snapshot's roots and semantic identity, each
/// cell's runs, layout and selection, each vector's scene, drawing and
/// selection, and each image's bitmap, lease and measured size.
/// </summary>
internal sealed class RootLocalTableVectorBaseline
{
    internal required InlineVectorSceneRun[] Vectors { get; init; }
    internal required VectorSceneDrawing[] Drawings { get; init; }
    internal required MarkdownVectorScene[] Scenes { get; init; }
    internal required BlockBox[] Roots { get; init; }
    internal required InlineContainerBox[] Cells { get; init; }
    internal required InlineRun[][] Runs { get; init; }
    internal required CanvasTextLayout[] Layouts { get; init; }
    internal required ImageBox[] Images { get; init; }
    internal required CanvasBitmap[] Bitmaps { get; init; }
    internal required SharedCanvasBitmapCache.Lease[] Leases { get; init; }
    internal required int[] LeaseReferences { get; init; }
    internal required float[] ImageWidths { get; init; }
    internal required float[] ImageHeights { get; init; }
    internal required float[] Columns { get; init; }
    internal required MarkdownSourceMap SourceMap { get; init; }
    internal required MarkdownSemanticDocument Semantic { get; init; }
    internal required string SemanticText { get; init; }
    internal required MarkdownAutomationTextFingerprint TextProjection { get; init; }
    internal required MarkdownAutomationTextFingerprint SemanticRanges { get; init; }
    internal required long Revision { get; init; }
    internal required Rect[][] CellSelections { get; init; }
    internal required Rect[][] VectorSelections { get; init; }

    internal static RootLocalTableVectorBaseline Capture(
        LayoutSnapshot snapshot,
        TableBox table,
        InlineContainerBox[] cells,
        InlineContainerBox[] fallbackCells,
        ImageBox[] images)
    {
        InlineVectorSceneRun[] vectors = EnumerateVectors(cells);
        InlineVectorSceneRun[] fallbackVectors = EnumerateVectors(fallbackCells);
        Assert.Equal(3, vectors.Length);
        Assert.Equal(vectors.Length, fallbackVectors.Length);
        VectorSceneDrawing[] drawings = vectors.Select(GetVectorDrawing).ToArray();
        MarkdownVectorScene[] scenes = vectors
            .Select(vector => Assert.IsType<MarkdownVectorScene>(vector.Scene)).ToArray();
        Assert.All(vectors, static vector => Assert.True(vector.IsNativeResourcesPrepared));
        Assert.All(vectors, static vector => Assert.False(vector.IsMeasuredPlacementOnly));
        SharedCanvasBitmapCache.Lease[] leases = images.Select(GetBitmapLease).ToArray();
        MarkdownSemanticDocument semantic = snapshot.SemanticDocument;
        return new RootLocalTableVectorBaseline
        {
            Vectors = vectors,
            Drawings = drawings,
            Scenes = scenes,
            Roots = snapshot.Blocks.ToArray(),
            Cells = cells,
            Runs = cells.Select(static cell => cell.Runs.ToArray()).ToArray(),
            Layouts = cells.Select(GetInlineLayout).ToArray(),
            Images = images,
            Bitmaps = images.Select(GetBitmap).ToArray(),
            Leases = leases,
            LeaseReferences = leases.Select(GetLeaseReferenceCount).ToArray(),
            ImageWidths = images.Select(static image => image.MeasuredImageWidth).ToArray(),
            ImageHeights = images.Select(static image => image.MeasuredImageHeight).ToArray(),
            Columns = CaptureColumnWidths(table),
            SourceMap = snapshot.SourceMap,
            Semantic = semantic,
            SemanticText = semantic.Text,
            TextProjection = semantic.TextProjectionFingerprint,
            SemanticRanges = semantic.SemanticRangeFingerprint,
            Revision = snapshot.LayoutRevision,
            CellSelections = cells.Select(cell => CaptureCellSelectionRects(snapshot, cell)).ToArray(),
            VectorSelections = vectors.Select(vector =>
                CaptureRunSelectionRects(snapshot, FindOwnerCell(cells, vector), vector)).ToArray(),
        };
    }

    internal void AssertSnapshotMatchesFallback(LayoutSnapshot optimized, TableBox table, LayoutSnapshot fallback)
    {
        Assert.Equal(Revision + 1, optimized.LayoutRevision);
        Assert.Same(SourceMap, optimized.SourceMap);
        Assert.Same(Semantic, optimized.SemanticDocument);
        Assert.Equal(SemanticText, optimized.SemanticDocument.Text);
        Assert.Equal(TextProjection, optimized.SemanticDocument.TextProjectionFingerprint);
        Assert.Equal(SemanticRanges, optimized.SemanticDocument.SemanticRangeFingerprint);
        Assert.Equal(RootLocalTableVectorDocument.AutomationGeneration, optimized.AutomationDocumentGeneration);
        Assert.Equal(fallback.Size, optimized.Size);
        Assert.Same(table, FindTable(optimized));
        Assert.Equal(Roots.Length, optimized.Blocks.Count);
        AssertRootsMatchFallback(Roots, optimized, fallback);
    }

    internal void AssertCellsMatchFallback(
        InlineContainerBox[] cellsAfter,
        InlineContainerBox[] fallbackCells,
        LayoutSnapshot optimized,
        LayoutSnapshot fallback,
        int tableBlockIndex)
    {
        Assert.Equal(Cells.Length, cellsAfter.Length);
        Assert.Equal(Cells.Length, fallbackCells.Length);
        for (int cellIndex = 0; cellIndex < Cells.Length; cellIndex++)
        {
            AssertCellRebuiltInPlace(Cells[cellIndex], Runs[cellIndex], Layouts[cellIndex], cellsAfter[cellIndex]);
            Assert.Equal(
                ToTableLogical(fallback, tableBlockIndex, fallbackCells[cellIndex].Bounds),
                ToTableLogical(optimized, tableBlockIndex, cellsAfter[cellIndex].Bounds));
            Assert.Equal(
                CaptureCellSelectionRects(fallback, fallbackCells[cellIndex]),
                CaptureCellSelectionRects(optimized, cellsAfter[cellIndex]));
        }
    }

    /// <summary>
    /// Each vector run keeps its scene and prepared drawing and takes the
    /// fallback run's measured placement and selection. Returns the runs
    /// after the relayout.
    /// </summary>
    internal InlineVectorSceneRun[] AssertVectorsMatchFallback(
        InlineContainerBox[] cellsAfter,
        InlineContainerBox[] fallbackCells,
        LayoutSnapshot optimized,
        LayoutSnapshot fallback)
    {
        InlineVectorSceneRun[] vectorsAfter = EnumerateVectors(cellsAfter);
        InlineVectorSceneRun[] fallbackVectorsAfter = EnumerateVectors(fallbackCells);
        Assert.Equal(Vectors.Length, vectorsAfter.Length);
        Assert.Equal(Vectors.Length, fallbackVectorsAfter.Length);
        for (int vectorIndex = 0; vectorIndex < Vectors.Length; vectorIndex++)
        {
            InlineVectorSceneRun vector = vectorsAfter[vectorIndex];
            InlineVectorSceneRun expected = fallbackVectorsAfter[vectorIndex];
            Assert.Same(Vectors[vectorIndex], vector);
            Assert.Same(Scenes[vectorIndex], vector.Scene);
            Assert.Same(Drawings[vectorIndex], GetVectorDrawing(vector));
            Assert.True(vector.IsNativeResourcesPrepared);
            Assert.False(vector.IsMeasuredPlacementOnly);
            Assert.Equal(expected.DesiredWidth, vector.DesiredWidth);
            Assert.Equal(expected.DesiredHeight, vector.DesiredHeight);
            Assert.Equal(expected.DesiredBaseline, vector.DesiredBaseline);
            Assert.Equal(expected.LocalBounds, vector.LocalBounds);
            Assert.Equal(expected.Scene.Width, vector.Scene.Width);
            Assert.Equal(expected.Scene.Height, vector.Scene.Height);
            InlineContainerBox owner = FindOwnerCell(cellsAfter, vector);
            InlineContainerBox fallbackOwner = FindOwnerCell(fallbackCells, expected);
            Assert.Equal(
                CaptureRunSelectionRects(fallback, fallbackOwner, expected),
                CaptureRunSelectionRects(optimized, owner, vector));
        }
        return vectorsAfter;
    }

    internal void AssertSelectionsRelocated(
        LayoutSnapshot optimized,
        InlineContainerBox[] cellsAfter,
        InlineVectorSceneRun[] vectorsAfter)
    {
        Rect[][] finalCellSelections = cellsAfter
            .Select(cell => CaptureCellSelectionRects(optimized, cell)).ToArray();
        Assert.Equal(CellSelections.Length, finalCellSelections.Length);
        Assert.True(CellSelections.Where((rects, index) =>
                !rects.SequenceEqual(finalCellSelections[index])).Any(),
            "Measured image placement must update table-cell selection geometry.");
        Rect[][] finalVectorSelections = vectorsAfter.Select(vector =>
            CaptureRunSelectionRects(optimized, FindOwnerCell(cellsAfter, vector), vector)).ToArray();
        Assert.True(VectorSelections.Where((rects, index) =>
                !rects.SequenceEqual(finalVectorSelections[index])).Any(),
            "At least one vector selection range must be relocated with its measured cell.");
    }

    internal void AssertImagesMatchFallback(TableBox table, LayoutSnapshot optimized, LayoutSnapshot fallback)
    {
        ImageBox[] imagesAfter = EnumerateTableImages(table).ToArray();
        ImageBox[] fallbackImagesAfter = EnumerateTableImages(FindTable(fallback)).ToArray();
        Assert.Equal(Images.Length, imagesAfter.Length);
        Assert.Equal(Images.Length, fallbackImagesAfter.Length);
        for (int imageIndex = 0; imageIndex < Images.Length; imageIndex++)
        {
            ImageBox image = imagesAfter[imageIndex];
            ImageBox expected = fallbackImagesAfter[imageIndex];
            Assert.Same(Images[imageIndex], image);
            Assert.Same(Bitmaps[imageIndex], image.Bitmap);
            Assert.Same(Leases[imageIndex], GetBitmapLease(image));
            Assert.Equal(LeaseReferences[imageIndex], GetLeaseReferenceCount(Leases[imageIndex]));
            Assert.Equal(
                ToTableLogical(fallback, table.BlockIndex, expected.Bounds),
                ToTableLogical(optimized, table.BlockIndex, image.Bounds));
            Assert.Equal(expected.MeasuredImageWidth, image.MeasuredImageWidth);
            Assert.Equal(expected.MeasuredImageHeight, image.MeasuredImageHeight);
        }
        Assert.True(AnyMeasuredImageSizeChanged(ImageWidths, ImageHeights, imagesAfter),
            "Decoded image dimensions must be remeasured before table placement is transferred.");
    }

    internal void AssertColumnsAndViewportMatchFallback(TableBox table, LayoutSnapshot fallback)
    {
        Assert.Equal(CaptureColumnWidths(FindTable(fallback)), CaptureColumnWidths(table));
        Assert.True(AnyColumnWidthChanged(Columns, CaptureColumnWidths(table)),
            "Decoded image dimensions must participate in table column rebalancing.");
        AssertViewportMatches(FindTable(fallback), table);
    }
}
