using MarkdownRenderer.GitHub.Tests.TestSupport.Assertions;
using MarkdownRenderer.Images;
using MarkdownRenderer.Layout.Internal.Images;
using MarkdownRenderer.Layout.Internal.Images.BitmapCache;
using MarkdownRenderer.Layout.Internal.Inlines;
using MarkdownRenderer.Layout.Internal.Inlines.Runs;
using MarkdownRenderer.Layout.Internal.Snapshots;
using MarkdownRenderer.Layout.Internal.Tables;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Text;
using Windows.Foundation;
using Xunit;
using static MarkdownRenderer.GitHub.Tests.TestSupport.Assertions.RootLocalTableQueries;

namespace MarkdownRenderer.GitHub.Tests.Layout.Internal.Relayout.Tables;

/// <summary>
/// Everything a rejected root-local table relayout must leave untouched: the
/// snapshot revision and size, the table viewport, every cell's owners and
/// geometry, and every image's owners, geometry and bitmap lease.
/// </summary>
internal sealed record RootLocalTableSourceState(
    long Revision,
    Size Size,
    Rect TableBounds,
    double HorizontalOffset,
    double HorizontalExtent,
    double HorizontalViewport,
    Rect[] CellBounds,
    InlineContainerBox[] Cells,
    InlineRun[][] Runs,
    CanvasTextLayout[] Layouts,
    Rect[][] CellSelectionRects,
    ImageBox[] Images,
    Rect[] ImageBounds,
    float[] ImageWidths,
    float[] ImageHeights,
    CanvasBitmap?[] Bitmaps,
    SharedCanvasBitmapCache.Lease[] Leases)
{
    internal static RootLocalTableSourceState Capture(
        LayoutSnapshot snapshot,
        TableBox table,
        ImageBox? standalone = null)
    {
        InlineContainerBox[] cells = table.GetCellBoxes().ToArray();
        ImageBox[] images = EnumerateTableImages(table).ToArray();
        if (standalone is not null)
            images = images.Append(standalone).ToArray();
        return new RootLocalTableSourceState(
            snapshot.LayoutRevision,
            snapshot.Size,
            table.Bounds,
            table.HorizontalOffset,
            table.HorizontalExtent,
            table.HorizontalViewport,
            table.GetCellInfos().Select(static info => info.Box.Bounds).ToArray(),
            cells,
            cells.Select(static cell => cell.Runs.ToArray()).ToArray(),
            cells.Select(GetInlineLayout).ToArray(),
            cells.Select(cell => GetCellSelectionRects(snapshot, cell)).ToArray(),
            images,
            images.Select(static image => image.Bounds).ToArray(),
            images.Select(static image => image.MeasuredImageWidth).ToArray(),
            images.Select(static image => image.MeasuredImageHeight).ToArray(),
            images.Select(static image => image.Bitmap).ToArray(),
            images.Select(GetBitmapLease).ToArray());
    }

    internal static void AssertEqual(RootLocalTableSourceState expected, RootLocalTableSourceState actual)
    {
        Assert.Equal(expected.Revision, actual.Revision);
        Assert.Equal(expected.Size, actual.Size);
        Assert.Equal(expected.TableBounds, actual.TableBounds);
        Assert.Equal(expected.HorizontalOffset, actual.HorizontalOffset);
        Assert.Equal(expected.HorizontalExtent, actual.HorizontalExtent);
        Assert.Equal(expected.HorizontalViewport, actual.HorizontalViewport);
        Assert.Equal(expected.CellBounds, actual.CellBounds);
        Assert.Equal(expected.Cells.Length, actual.Cells.Length);
        for (int cellIndex = 0; cellIndex < expected.Cells.Length; cellIndex++)
        {
            Assert.Same(expected.Cells[cellIndex], actual.Cells[cellIndex]);
            Assert.Equal(expected.Runs[cellIndex].Length, actual.Runs[cellIndex].Length);
            for (int runIndex = 0; runIndex < expected.Runs[cellIndex].Length; runIndex++)
                Assert.Same(expected.Runs[cellIndex][runIndex], actual.Runs[cellIndex][runIndex]);
            Assert.Same(expected.Layouts[cellIndex], actual.Layouts[cellIndex]);
            Assert.Equal(expected.CellSelectionRects[cellIndex], actual.CellSelectionRects[cellIndex]);
        }
        Assert.Equal(expected.Images.Length, actual.Images.Length);
        for (int imageIndex = 0; imageIndex < expected.Images.Length; imageIndex++)
        {
            Assert.Same(expected.Images[imageIndex], actual.Images[imageIndex]);
            Assert.Equal(expected.ImageBounds[imageIndex], actual.ImageBounds[imageIndex]);
            Assert.Equal(expected.ImageWidths[imageIndex], actual.ImageWidths[imageIndex]);
            Assert.Equal(expected.ImageHeights[imageIndex], actual.ImageHeights[imageIndex]);
            Assert.Same(expected.Bitmaps[imageIndex], actual.Bitmaps[imageIndex]);
            Assert.Same(expected.Leases[imageIndex], actual.Leases[imageIndex]);
        }
    }
}
