using MarkdownRenderer.Document;
using MarkdownRenderer.Layout;
using Microsoft.Graphics.Canvas;
using Windows.Foundation;

namespace MarkdownRenderer.GitHub.Tests;

internal sealed class CountingSelectionBox : BlockBox
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
