using MarkdownRenderer.Document;
using MarkdownRenderer.Images;
using MarkdownRenderer.Layout;
using MarkdownRenderer.Layout.Boxes;
using MarkdownRenderer.Layout.Internal.Images;
using MarkdownRenderer.Layout.Internal.Images.BitmapCache;
using MarkdownRenderer.Selection;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Text;
using Windows.Foundation;
using Xunit;
using static MarkdownRenderer.GitHub.Tests.RootLocalTableQueries;

namespace MarkdownRenderer.GitHub.Tests;

/// <summary>
/// Assertion blocks shared by the root-local table relayout tests. A committed
/// relayout keeps every root, cell, run, image, bitmap and lease owner and
/// matches the full-rebuild fallback's geometry; a rejected one leaves the
/// source owners exactly as they were.
/// </summary>
internal static class RootLocalTableRelayoutAssertions
{
    /// <summary>Waits for a relayout's construction context to retire what it owns, if one was created.</summary>
    internal static async Task DrainConstructionAsync(MarkdownLayoutContext? constructionContext)
    {
        if (constructionContext is not null)
            await constructionContext.DrainOwnedRetirementsAsync().WaitAsync(TimeSpan.FromSeconds(10));
    }

    /// <summary>Each root is the same owner as before and has the fallback root's logical bounds.</summary>
    internal static void AssertRootsMatchFallback(
        BlockBox[] rootsBefore,
        LayoutSnapshot optimized,
        LayoutSnapshot fallback)
    {
        for (int rootIndex = 0; rootIndex < rootsBefore.Length; rootIndex++)
        {
            Assert.Same(rootsBefore[rootIndex], optimized.Blocks[rootIndex]);
            Assert.Equal(LayoutPlacementTestGeometry.ToLogical(fallback, fallback.Blocks[rootIndex]),
                LayoutPlacementTestGeometry.ToLogical(optimized, optimized.Blocks[rootIndex]));
        }
    }

    /// <summary>The snapshot still holds exactly the roots it held before.</summary>
    internal static void AssertRootsPreserved(BlockBox[] rootsBefore, LayoutSnapshot snapshot)
    {
        Assert.Equal(rootsBefore.Length, snapshot.Blocks.Count);
        for (int index = 0; index < rootsBefore.Length; index++)
            Assert.Same(rootsBefore[index], snapshot.Blocks[index]);
    }

    internal static void AssertViewportMatches(TableBox expected, TableBox actual)
    {
        Assert.Equal(expected.HorizontalOffset, actual.HorizontalOffset);
        Assert.Equal(expected.HorizontalExtent, actual.HorizontalExtent);
        Assert.Equal(expected.HorizontalViewport, actual.HorizontalViewport);
    }

    internal static void AssertRunsPreserved(InlineRun[] runsBefore, IReadOnlyList<InlineRun> runsAfter)
    {
        Assert.Equal(runsBefore.Length, runsAfter.Count);
        for (int runIndex = 0; runIndex < runsBefore.Length; runIndex++)
            Assert.Same(runsBefore[runIndex], runsAfter[runIndex]);
    }

    /// <summary>The cell and its runs are the same owners, and only its text layout was rebuilt.</summary>
    internal static void AssertCellRebuiltInPlace(
        InlineContainerBox cellBefore,
        InlineRun[] runsBefore,
        CanvasTextLayout layoutBefore,
        InlineContainerBox cellAfter)
    {
        Assert.Same(cellBefore, cellAfter);
        AssertRunsPreserved(runsBefore, cellAfter.Runs);
        Assert.NotSame(layoutBefore, GetInlineLayout(cellAfter));
    }

    /// <summary>
    /// Every cell was rebuilt in place and matches the fallback cell's logical
    /// bounds and selection rectangles.
    /// </summary>
    internal static void AssertCellsMatchFallback(
        InlineContainerBox[] cellsBefore,
        InlineRun[][] runsBefore,
        CanvasTextLayout[] layoutsBefore,
        InlineContainerBox[] cellsAfter,
        InlineContainerBox[] fallbackCells,
        LayoutSnapshot optimized,
        LayoutSnapshot fallback)
    {
        Assert.Equal(cellsBefore.Length, cellsAfter.Length);
        for (int cellIndex = 0; cellIndex < cellsBefore.Length; cellIndex++)
        {
            AssertCellRebuiltInPlace(
                cellsBefore[cellIndex], runsBefore[cellIndex], layoutsBefore[cellIndex], cellsAfter[cellIndex]);
            Assert.Equal(LayoutPlacementTestGeometry.ToLogical(fallback, fallbackCells[cellIndex]),
                LayoutPlacementTestGeometry.ToLogical(optimized, cellsAfter[cellIndex]));
            Assert.Equal(
                GetCellSelectionRects(fallback, fallbackCells[cellIndex]),
                GetCellSelectionRects(optimized, cellsAfter[cellIndex]));
        }
    }

    /// <summary>
    /// Every image keeps its owner, bitmap and lease, and matches the fallback
    /// image's logical bounds and measured size.
    /// </summary>
    internal static void AssertImagesMatchFallback(
        ImageBox[] images,
        ImageBox[] imagesAfter,
        CanvasBitmap[] bitmapsBefore,
        SharedCanvasBitmapCache.Lease[] leasesBefore,
        ImageBox[] fallbackImages,
        LayoutSnapshot optimized,
        LayoutSnapshot fallback)
    {
        for (int imageIndex = 0; imageIndex < images.Length; imageIndex++)
        {
            Assert.Same(images[imageIndex], imagesAfter[imageIndex]);
            Assert.Same(bitmapsBefore[imageIndex], imagesAfter[imageIndex].Bitmap);
            Assert.Same(leasesBefore[imageIndex], GetBitmapLease(imagesAfter[imageIndex]));
            Assert.Equal(LayoutPlacementTestGeometry.ToLogical(fallback, fallbackImages[imageIndex]),
                LayoutPlacementTestGeometry.ToLogical(optimized, imagesAfter[imageIndex]));
            Assert.Equal(fallbackImages[imageIndex].MeasuredImageWidth,
                imagesAfter[imageIndex].MeasuredImageWidth);
            Assert.Equal(fallbackImages[imageIndex].MeasuredImageHeight,
                imagesAfter[imageIndex].MeasuredImageHeight);
        }
    }

    /// <summary>
    /// Builds the native image plan the control's publication path uses and
    /// checks that it publishes exactly <paramref name="expectedOrder"/>.
    /// Call it before Paint, which can update inline bounds.
    /// </summary>
    internal static RasterPlacementImagePlan AssertPublicationPlan(
        LayoutSnapshot snapshot,
        ImageBox[] expectedOrder,
        int expectedCount)
    {
        var plan = new RasterPlacementImagePlan(snapshot, snapshot.Blocks, expectedCount: expectedCount);
        Assert.Equal(expectedOrder.Length, plan.PublicationImages.Length);
        for (int imageIndex = 0; imageIndex < expectedOrder.Length; imageIndex++)
            Assert.Same(expectedOrder[imageIndex], plan.PublicationImages[imageIndex]);
        return plan;
    }

    internal static void AssertLeaseReferenceCounts(
        SharedCanvasBitmapCache.Lease[] leases,
        int[] referenceCountsBefore)
    {
        for (int index = 0; index < leases.Length; index++)
            Assert.Equal(referenceCountsBefore[index], GetLeaseReferenceCount(leases[index]));
    }

    /// <summary>Each image still holds its lease, and each lease its prior reference count.</summary>
    internal static void AssertLeasesHeld(
        ImageBox[] images,
        SharedCanvasBitmapCache.Lease[] leasesBefore,
        int[] referenceCountsBefore)
    {
        for (int index = 0; index < images.Length; index++)
        {
            Assert.Same(leasesBefore[index], GetBitmapLease(images[index]));
            Assert.Equal(referenceCountsBefore[index], GetLeaseReferenceCount(leasesBefore[index]));
        }
    }

    /// <summary>
    /// Each image is its own child block of the table, distinct from the
    /// table root. Returns those child block indices.
    /// </summary>
    internal static int[] AssertDistinctImageChildIndices(TableBox table, ImageBox[] images)
    {
        int[] childIndices = images.Select(static image => image.BlockIndex).Distinct().ToArray();
        Assert.Equal(images.Length, childIndices.Length);
        Assert.DoesNotContain(table.BlockIndex, childIndices);
        return childIndices;
    }

    /// <summary>After a rejected relayout every cell keeps its owner, layout, selection and runs.</summary>
    internal static void AssertCellsUnchanged(
        RootLocalTableOwners before,
        Rect[][] selectionBefore,
        LayoutSnapshot snapshot,
        InlineContainerBox[] cellsAfter)
    {
        Assert.Equal(before.Cells.Length, cellsAfter.Length);
        for (int cellIndex = 0; cellIndex < before.Cells.Length; cellIndex++)
        {
            Assert.Same(before.Cells[cellIndex], cellsAfter[cellIndex]);
            Assert.Same(before.Layouts[cellIndex], GetInlineLayout(cellsAfter[cellIndex]));
            Assert.Equal(selectionBefore[cellIndex], GetCellSelectionRects(snapshot, cellsAfter[cellIndex]));
            AssertRunsPreserved(before.Runs[cellIndex], cellsAfter[cellIndex].Runs);
        }
    }

    /// <summary>
    /// A committed relayout advanced the revision once, kept the source map,
    /// semantic document, automation generation and every root, and matches
    /// the fallback's size and root geometry. With flexible column space the
    /// size must also have changed.
    /// </summary>
    internal static void AssertCommittedSnapshotKeepsSemanticOwners(
        RootLocalTableOwners before,
        LayoutSnapshot optimized,
        LayoutSnapshot fallback,
        bool hasFlexibleColumnSpace)
    {
        Assert.Equal(before.Revision + 1, optimized.LayoutRevision);
        Assert.Equal(fallback.Size, optimized.Size);
        if (hasFlexibleColumnSpace)
            Assert.NotEqual(before.Size, optimized.Size);
        Assert.Same(before.SourceMap, optimized.SourceMap);
        Assert.Same(before.Semantic, optimized.SemanticDocument);
        Assert.Equal(RootLocalTableDocument.AutomationGeneration, optimized.AutomationDocumentGeneration);
        Assert.Equal(before.Roots.Length, optimized.Blocks.Count);
        AssertRootsMatchFallback(before.Roots, optimized, fallback);
    }

    /// <summary>
    /// Each cell was rebuilt in place and matches the fallback's geometry,
    /// selection, semantic text rectangles and, for the first text cell, its
    /// caret endpoint.
    /// </summary>
    internal static void AssertCellsMatchFallbackSelectionAndCaret(
        RootLocalTableOwners before,
        TableBox table,
        TableBox fallbackTable,
        LayoutSnapshot optimized,
        LayoutSnapshot fallback)
    {
        InlineContainerBox[] cellsAfter = table.GetCellBoxes().ToArray();
        InlineContainerBox[] fallbackCells = fallbackTable.GetCellBoxes().ToArray();
        bool comparedCaret = false;
        for (int cellIndex = 0; cellIndex < before.Cells.Length; cellIndex++)
        {
            InlineContainerBox cell = cellsAfter[cellIndex];
            InlineContainerBox fallbackCell = fallbackCells[cellIndex];
            AssertCellRebuiltInPlace(before.Cells[cellIndex], before.Runs[cellIndex], before.Layouts[cellIndex], cell);
            Assert.Equal(LayoutPlacementTestGeometry.ToLogical(fallback, fallbackCell),
                LayoutPlacementTestGeometry.ToLogical(optimized, cell));
            Rect[] expectedSelectionRects = GetCellSelectionRects(fallback, fallbackCell);
            Rect[] actualSelectionRects = GetCellSelectionRects(optimized, cell);
            AssertUnclippedVerticalSelectionExtentIsPreserved(fallback, fallbackTable, fallbackCell, expectedSelectionRects);
            AssertUnclippedVerticalSelectionExtentIsPreserved(optimized, table, cell, actualSelectionRects);
            if (!expectedSelectionRects.SequenceEqual(actualSelectionRects))
            {
                Assert.Fail(
                    $"Cell {cellIndex} selection differs. Expected={FormatRects(expectedSelectionRects)}; " +
                    $"actual={FormatRects(actualSelectionRects)}.");
            }
            Assert.Equal(expectedSelectionRects, actualSelectionRects);

            Assert.Equal(GetCellSemanticTextRects(fallback, fallbackCell), GetCellSemanticTextRects(optimized, cell));
            if (!comparedCaret && cell.Runs.Any(static run => run.TextLength > 0))
            {
                Assert.Equal(GetCellEndpointCaretPoint(fallback, fallbackCell), GetCellEndpointCaretPoint(optimized, cell));
                comparedCaret = true;
            }
        }
        Assert.True(comparedCaret, "The table fixture must exercise a text caret endpoint.");
    }

    /// <summary>
    /// The table's columns match the fallback's. With flexible column space,
    /// the cell bounds and at least one column width also moved.
    /// </summary>
    internal static void AssertColumnsRebalancedLikeFallback(
        Rect[] initialCellBounds,
        float[] initialColumnWidths,
        LayoutSnapshot optimized,
        TableBox table,
        TableBox fallbackTable,
        bool hasFlexibleColumnSpace)
    {
        Rect[] finalCellBounds = CaptureCellBounds(optimized, table);
        float[] finalColumnWidths = CaptureColumnWidths(table);
        Assert.Equal(CaptureColumnWidths(fallbackTable), finalColumnWidths);
        if (hasFlexibleColumnSpace)
        {
            Assert.False(initialCellBounds.SequenceEqual(finalCellBounds));
            Assert.True(AnyColumnWidthChanged(initialColumnWidths, finalColumnWidths),
                "Decoded image aspect ratios must participate in table column rebalancing when flexible width is available.");
        }
    }

    /// <summary>The SVG table's cell bounds and columns match the fallback's, and provider sizes moved a column.</summary>
    internal static void AssertSvgColumnsRebalancedLikeFallback(
        float[] columnWidthsBefore,
        LayoutSnapshot optimized,
        TableBox table,
        LayoutSnapshot fallback,
        TableBox fallbackTable)
    {
        Assert.Equal(CaptureCellBounds(fallback, fallbackTable), CaptureCellBounds(optimized, table));
        float[] finalColumnWidths = CaptureColumnWidths(table);
        Assert.Equal(CaptureColumnWidths(fallbackTable), finalColumnWidths);
        Assert.True(AnyColumnWidthChanged(columnWidthsBefore, finalColumnWidths),
            "Provider intrinsic sizes must feed the measured table columns.");
    }

    /// <summary>The plan's index orders the images by block ordinal and records each image's logical vertical extent.</summary>
    internal static void AssertPlanIndexMatchesImageBounds(
        RasterPlacementImagePlan plan,
        LayoutSnapshot snapshot,
        ImageBox[] images)
    {
        for (int imageIndex = 0; imageIndex < images.Length; imageIndex++)
        {
            Assert.Equal(imageIndex, plan.Index.GetBlockOrdinal(imageIndex));
            Rect logicalBounds = LayoutPlacementTestGeometry.ToLogical(snapshot, images[imageIndex]);
            Assert.Equal((logicalBounds.Top, logicalBounds.Bottom), plan.Index.GetBounds(imageIndex));
        }
    }

    /// <summary>
    /// Every root keeps its owner and matches the fallback; the distant
    /// estimated root also keeps its own horizontal extent and height.
    /// </summary>
    internal static void AssertRootsMatchFallbackKeepingEstimatedOwner(
        BlockBox[] rootsBefore,
        Rect[] rootBoundsBefore,
        BlockBox estimatedOwner,
        LayoutSnapshot optimized,
        LayoutSnapshot fallback)
    {
        AssertRootsMatchFallback(rootsBefore, optimized, fallback);
        int ordinal = Array.FindIndex(rootsBefore, root => ReferenceEquals(root, estimatedOwner));
        Assert.Equal(rootBoundsBefore[ordinal].X, optimized.Blocks[ordinal].Bounds.X);
        Assert.Equal(rootBoundsBefore[ordinal].Width, optimized.Blocks[ordinal].Bounds.Width);
        Assert.Equal(rootBoundsBefore[ordinal].Height, optimized.Blocks[ordinal].Bounds.Height);
    }

    internal static void AssertUnclippedVerticalSelectionExtentIsPreserved(
        LayoutSnapshot snapshot,
        TableBox table,
        InlineContainerBox cell,
        Rect[] publishedRects)
    {
        Assert.True(snapshot.TryBeginPaint());
        try
        {
            using LayoutPlacementTransformContext.Scope placementScope =
                snapshot.EnterPlacementTransformScopeForTopLevelBlockUnderPaint(table);
            (DocumentPosition start, DocumentPosition end) = cell.GetBlockBoundaries();
            Rect[] sourceRects = cell.GetSelectionRects(new DocumentRange(start, end).Normalized()).ToArray();
            Assert.Equal(sourceRects.Length, publishedRects.Length);
            Rect clip = table.HorizontalViewportBounds;
            for (int index = 0; index < sourceRects.Length; index++)
            {
                Rect source = sourceRects[index];
                if (source.Top >= clip.Top && source.Bottom <= clip.Bottom)
                    Assert.Equal(source.Height, publishedRects[index].Height);
            }
        }
        finally
        {
            snapshot.EndPaint();
        }
    }
}
