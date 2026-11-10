using MarkdownRenderer.Accessibility.Internal.Semantics;
using MarkdownRenderer.Document;
using MarkdownRenderer.Images;
using MarkdownRenderer.Layout;
using MarkdownRenderer.Layout.Boxes;
using MarkdownRenderer.Layout.Internal.Images;
using MarkdownRenderer.Layout.Internal.Images.BitmapCache;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Text;
using Windows.Foundation;
using Xunit;
using static MarkdownRenderer.GitHub.Tests.RootLocalTableQueries;
using static MarkdownRenderer.GitHub.Tests.RootLocalTableRelayoutAssertions;

namespace MarkdownRenderer.GitHub.Tests;

/// <summary>
/// The owners a committed root-local table relayout must keep, captured before
/// the relayout: the snapshot's roots, source map, semantic document, size and
/// revision, and the table's cells, runs, text layouts, images, bitmaps and
/// leases.
/// </summary>
internal sealed record RootLocalTableOwners(
    BlockBox[] Roots,
    InlineContainerBox[] Cells,
    InlineRun[][] Runs,
    CanvasTextLayout[] Layouts,
    ImageBox[] Images,
    CanvasBitmap[] Bitmaps,
    SharedCanvasBitmapCache.Lease[] Leases,
    MarkdownSourceMap SourceMap,
    MarkdownSemanticDocument Semantic,
    Size Size,
    long Revision)
{
    /// <summary>
    /// Captures the owners now. Pass <paramref name="cells"/> when the test
    /// captured the table's cells earlier, for example before images decoded.
    /// </summary>
    internal static RootLocalTableOwners Capture(
        LayoutSnapshot snapshot,
        TableBox table,
        ImageBox[] images,
        InlineContainerBox[]? cells = null)
    {
        cells ??= table.GetCellBoxes().ToArray();
        return new RootLocalTableOwners(
            snapshot.Blocks.ToArray(),
            cells,
            cells.Select(static cell => cell.Runs.ToArray()).ToArray(),
            cells.Select(GetInlineLayout).ToArray(),
            images,
            images.Select(GetBitmap).ToArray(),
            images.Select(GetBitmapLease).ToArray(),
            snapshot.SourceMap,
            snapshot.SemanticDocument,
            snapshot.Size,
            snapshot.LayoutRevision);
    }

    /// <summary>
    /// After a committed relayout: the source map and semantic document are the
    /// same objects, the snapshot has the fallback's size, and every root, cell
    /// and image keeps its owner while matching the fallback's geometry.
    /// </summary>
    internal void AssertCommittedMatchesFallback(
        LayoutSnapshot optimized,
        TableBox table,
        LayoutSnapshot fallback,
        TableBox fallbackTable,
        ImageBox[] fallbackImages)
    {
        Assert.Same(SourceMap, optimized.SourceMap);
        Assert.Same(Semantic, optimized.SemanticDocument);
        Assert.Equal(fallback.Size, optimized.Size);
        AssertRootsMatchFallback(Roots, optimized, fallback);
        AssertCellsMatchFallback(Cells, Runs, Layouts, table.GetCellBoxes().ToArray(),
            fallbackTable.GetCellBoxes().ToArray(), optimized, fallback);
        AssertImagesMatchFallback(Images, EnumerateTableImages(table).ToArray(), Bitmaps, Leases,
            fallbackImages, optimized, fallback);
    }
}
