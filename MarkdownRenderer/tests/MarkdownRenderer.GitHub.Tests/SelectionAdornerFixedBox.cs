using MarkdownRenderer.Document;
using MarkdownRenderer.Layout.Internal.Boxes;
using Microsoft.Graphics.Canvas;
using Windows.Foundation;

namespace MarkdownRenderer.GitHub.Tests;

/// <summary>
/// A root with fixed height whose whole bounds are its selection rectangle.
/// </summary>
internal sealed class SelectionAdornerFixedBox(Rect bounds) : BlockBox
{
    public override float Measure(float availableWidth)
    {
        Bounds = new Rect(Bounds.X, Bounds.Y, availableWidth, bounds.Height);
        return (float)Bounds.Height;
    }

    public override void Arrange(float x, float y, float width)
    {
        Bounds = new Rect(x, y, width, bounds.Height);
        IsDirty = false;
    }

    public override IEnumerable<Rect> GetSelectionRects(DocumentRange range)
    {
        DocumentRange normalized = range.Normalized();
        if (BlockIndex >= normalized.Start.BlockIndex && BlockIndex <= normalized.End.BlockIndex &&
            !normalized.IsEmpty)
        {
            yield return Bounds;
        }
    }

    public override void Paint(CanvasDrawingSession ds, Rect viewport) { }
}
