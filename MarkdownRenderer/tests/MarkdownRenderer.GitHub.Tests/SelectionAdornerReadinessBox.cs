using MarkdownRenderer.Document;
using MarkdownRenderer.Layout.Internal.Boxes;
using Microsoft.Graphics.Canvas;
using Windows.Foundation;

namespace MarkdownRenderer.GitHub.Tests;

/// <summary>
/// A root that counts Measure calls, so readiness queries can prove they never measure.
/// </summary>
internal sealed class SelectionAdornerReadinessBox : BlockBox
{
    private readonly Rect _estimatedBounds;

    internal SelectionAdornerReadinessBox(Rect estimatedBounds)
    {
        _estimatedBounds = estimatedBounds;
        Bounds = estimatedBounds;
    }

    internal int MeasureCount { get; private set; }

    public override float Measure(float availableWidth)
    {
        MeasureCount++;
        Bounds = new Rect(Bounds.X, Bounds.Y, availableWidth, _estimatedBounds.Height);
        return (float)Bounds.Height;
    }

    public override void Arrange(float x, float y, float width)
    {
        Bounds = new Rect(x, y, width, _estimatedBounds.Height);
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
