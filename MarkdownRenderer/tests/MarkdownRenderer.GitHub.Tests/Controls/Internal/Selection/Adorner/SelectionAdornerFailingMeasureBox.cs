using MarkdownRenderer.Document;
using MarkdownRenderer.Layout.Internal.Boxes;
using Microsoft.Graphics.Canvas;
using Windows.Foundation;

namespace MarkdownRenderer.GitHub.Tests.Controls.Internal.Selection.Adorner;

/// <summary>
/// A root whose Measure always throws, which taints the snapshot's committed geometry.
/// </summary>
internal sealed class SelectionAdornerFailingMeasureBox : BlockBox
{
    public override float Measure(float availableWidth)
    {
        Bounds = new Rect(0, 0, availableWidth, 20);
        throw new InvalidOperationException("Injected geometry failure.");
    }

    public override void Arrange(float x, float y, float width)
    {
        Bounds = new Rect(x, y, width, 20);
        IsDirty = false;
    }

    public override IEnumerable<Rect> GetSelectionRects(DocumentRange range)
        => Array.Empty<Rect>();

    public override void Paint(CanvasDrawingSession ds, Rect viewport) { }
}
