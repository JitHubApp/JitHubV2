using Microsoft.Graphics.Canvas;
using MarkdownRenderer.Document;
using MarkdownRenderer.GitHub.Tests.TestSupport.Fakes;
using MarkdownRenderer.Layout.Internal.Snapshots;
using MarkdownRenderer.Selection;
using MarkdownRenderer.Selection.Internal;
using Windows.Foundation;
using Windows.UI;
using Xunit;

namespace MarkdownRenderer.GitHub.Tests.TestSupport.Fixtures;

/// <summary>
/// Builds the boxes and selections that the selection adorner viewport
/// tests compare, and reads the legacy viewport rectangles and hit tests
/// they are compared with.
/// </summary>
internal static class SelectionAdornerViewportFixture
{
    internal static readonly double[] RasterizationScales = [1, 1.25, 1.5, 1.75, 2, 2.25, 3, 4];

    internal static SelectionController SelectEveryBlock(int blockCount, int firstBlockIndex = 0)
    {
        var selection = new SelectionController();
        selection.SetAnchor(new DocumentPosition(firstBlockIndex, 0, 0));
        selection.ExtendTo(new DocumentPosition(firstBlockIndex + blockCount - 1, 0, 1));
        return selection;
    }

    internal static bool ContainsPointUnderReadLease(
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

    internal static bool LegacyContainsPoint(
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

    internal static List<Rect> GetLegacyViewportRects(
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

    internal static List<Rect> CollectVisible(
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

    internal static Color[] RasterizeSelectionRects(
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

    internal static void ResetCounts(IEnumerable<CountingSelectionBox> boxes)
    {
        foreach (CountingSelectionBox box in boxes)
            box.SelectionQueryCount = 0;
    }

    internal static bool Intersects(Rect left, Rect right)
        => left.Right >= right.Left && left.Left <= right.Right &&
           left.Bottom >= right.Top && left.Top <= right.Bottom;

    internal static Rect SnapToPhysicalPixels(Rect rect, double scale)
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

    internal static Rect IntersectForTest(Rect left, Rect right)
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
}
