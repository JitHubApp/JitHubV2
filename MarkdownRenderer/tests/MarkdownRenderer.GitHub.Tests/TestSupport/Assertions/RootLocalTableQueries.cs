using MarkdownRenderer.Accessibility.Internal.Semantics;
using MarkdownRenderer.Document;
using MarkdownRenderer.GitHub.Tests.Layout.Internal.Images.Raster;
using MarkdownRenderer.GitHub.Tests.TestSupport.Fixtures;
using MarkdownRenderer.Images;
using MarkdownRenderer.Layout.Internal.Boxes;
using MarkdownRenderer.Layout.Internal.Images;
using MarkdownRenderer.Layout.Internal.Images.BitmapCache;
using MarkdownRenderer.Layout.Internal.Inlines;
using MarkdownRenderer.Layout.Internal.Inlines.Runs;
using MarkdownRenderer.Layout.Internal.Snapshots;
using MarkdownRenderer.Layout.Internal.Tables;
using MarkdownRenderer.Selection;
using MarkdownRenderer.Selection.Internal;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Text;
using Windows.Foundation;
using Windows.UI;
using Xunit;

namespace MarkdownRenderer.GitHub.Tests.TestSupport.Assertions;

/// <summary>
/// Read-only lookups and captures shared by the root-local table relayout
/// tests: finding the table and its images, waiting for decode, and reading
/// the cell, lease, selection and pixel state the tests compare.
/// </summary>
internal static class RootLocalTableQueries
{
    internal static TableBox FindTable(LayoutSnapshot snapshot) =>
        Assert.Single(snapshot.Blocks.OfType<TableBox>());

    internal static IEnumerable<ImageBox> EnumerateTableImages(TableBox table)
    {
        foreach (InlineContainerBox cell in table.GetCellBoxes())
        foreach (InlineImageRun run in cell.Runs.OfType<InlineImageRun>())
            yield return run.Image;
    }

    internal static ImageBox FindImage(LayoutSnapshot snapshot, string url) =>
        Assert.Single(snapshot.Blocks.SelectMany(EnumerateImages), image =>
            string.Equals(image.Url, url, StringComparison.Ordinal));

    internal static IEnumerable<ImageBox> EnumerateImages(BlockBox root)
    {
        switch (root)
        {
            case ImageBox image:
                yield return image;
                yield break;
            case TableBox table:
                foreach (ImageBox image in EnumerateTableImages(table))
                    yield return image;
                yield break;
            case InlineContainerBox inline:
                foreach (InlineImageRun run in inline.Runs.OfType<InlineImageRun>())
                    yield return run.Image;
                yield break;
        }
    }

    internal static BlockBox FindTopLevelOwner(LayoutSnapshot snapshot, ImageBox image)
    {
        foreach (BlockBox root in snapshot.Blocks)
            if (EnumerateImages(root).Any(candidate => ReferenceEquals(candidate, image)))
                return root;
        throw new InvalidOperationException("The image is not part of this snapshot.");
    }

    internal static async Task LoadImageAsync(ImageBox image)
    {
        await DisplaySizedRasterDecodeTests.WaitForLoadAsync(image).WaitAsync(TimeSpan.FromSeconds(10));
        await DisplaySizedRasterDecodeTests.WaitUntilAsync(() => image.GetWorkState().ActiveWorkCount == 0);
        Assert.NotNull(image.Bitmap);
    }

    /// <summary>Loads each image in order, as a sequence of <see cref="LoadImageAsync"/> calls.</summary>
    internal static async Task LoadImagesAsync(IEnumerable<ImageBox> images)
    {
        foreach (ImageBox image in images)
            await LoadImageAsync(image);
    }

    internal static Rect[] CaptureCellBounds(LayoutSnapshot snapshot, TableBox table) =>
        table.GetCellBoxes().Select(cell => LayoutPlacementTestGeometry.ToLogical(snapshot, cell)).ToArray();

    internal static float[] CaptureColumnWidths(TableBox table) =>
        table.GetCellInfos().Where(static info => info.IsHeader && info.Row == 0)
            .Select(static info => (float)info.Box.Bounds.Width).ToArray();

    internal static bool AnyColumnWidthChanged(float[] before, float[] after) =>
        before.Where((width, index) => System.Math.Abs(width - after[index]) > 0.5f).Any();

    internal static bool AnyMeasuredImageSizeChanged(float[] widthsBefore, float[] heightsBefore, ImageBox[] images) =>
        images.Where((image, index) =>
            System.Math.Abs(widthsBefore[index] - image.MeasuredImageWidth) > 0.5f ||
            System.Math.Abs(heightsBefore[index] - image.MeasuredImageHeight) > 0.5f).Any();

    internal static Rect[] GetCellSelectionRects(LayoutSnapshot snapshot, InlineContainerBox cell)
    {
        (DocumentPosition start, DocumentPosition end) = cell.GetBlockBoundaries();
        var selection = new SelectionController();
        selection.SetAnchor(start);
        selection.ExtendTo(end);
        return selection.GetHighlightRects(snapshot).ToArray();
    }

    internal static Rect[] GetCellSemanticTextRects(LayoutSnapshot snapshot, InlineContainerBox cell)
    {
        MarkdownSemanticDocument document = snapshot.SemanticDocument;
        MarkdownTextSpan[] spans = document.TextSpans
            .Where(span => ReferenceEquals(span.InlineBox, cell) && span.TextEnd > span.TextStart)
            .ToArray();
        if (spans.Length == 0)
            return Array.Empty<Rect>();

        Assert.True(snapshot.TryBeginPaint());
        try
        {
            var rectangles = new List<Rect>();
            foreach (MarkdownTextSpan span in spans)
            {
                rectangles.AddRange(document.GetDocumentRects(
                    span.TextStart,
                    span.TextEnd,
                    expandDegenerate: false,
                    placementTransform: snapshot.ToLogicalRectForBlockIndexUnderPaint,
                    placementScope: snapshot.EnterPlacementTransformScopeForBlockIndexUnderPaint));
            }

            return rectangles.ToArray();
        }
        finally
        {
            snapshot.EndPaint();
        }
    }

    internal static Point GetCellEndpointCaretPoint(LayoutSnapshot snapshot, InlineContainerBox cell)
    {
        (DocumentPosition start, DocumentPosition end) = cell.GetBlockBoundaries();
        var range = new DocumentRange(start, end).Normalized();
        var selection = new SelectionController();
        selection.SetAnchor(start);
        selection.ExtendTo(end);
        Rect viewport = new(
            -1_000,
            -1_000,
            snapshot.Size.Width + 2_000,
            snapshot.Size.Height + 2_000);

        Assert.True(snapshot.TryBeginPaint());
        try
        {
            Assert.True(selection.TryGetSelectionEndpointGeometry(
                snapshot,
                range.Start,
                range,
                viewport,
                verticalSearchMargin: 0,
                rangeStart: true,
                out Point caretPoint,
                out _,
                out bool hasCaret));
            Assert.True(hasCaret, "The text endpoint must resolve through DirectWrite's caret geometry.");
            return caretPoint;
        }
        finally
        {
            snapshot.EndPaint();
        }
    }

    internal static string FormatRects(IEnumerable<Rect> rects) =>
        "[" + string.Join(";", rects.Select(static rect =>
            $"{rect.X:R},{rect.Y:R},{rect.Width:R},{rect.Height:R}")) + "]";

    internal static bool HasStyleKey(InlineRun run, string key) =>
        string.Equals(run.ElementKey, key, StringComparison.Ordinal) ||
        run.HasStyleModifier(key) ||
        run.StyleAliases.Contains(key, StringComparer.Ordinal);

    internal static CanvasTextLayout GetInlineLayout(InlineContainerBox inline) =>
        Assert.IsType<CanvasTextLayout>(inline.TextLayout);

    internal static CanvasBitmap GetBitmap(ImageBox image) =>
        Assert.IsType<CanvasBitmap>(image.Bitmap);

    internal static SharedCanvasBitmapCache.Lease GetBitmapLease(ImageBox image) =>
        Assert.IsType<SharedCanvasBitmapCache.Lease>(image.Presentation.Lease);

    internal static int GetLeaseReferenceCount(SharedCanvasBitmapCache.Lease lease)
    {
        int? count = lease.EntryReferenceCount;
        Assert.True(count.HasValue, "The lease no longer holds a cache entry.");
        return count.Value;
    }

    internal static Color[] Paint(LayoutSnapshot snapshot, CanvasDevice device, float rasterizationScale)
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
}
