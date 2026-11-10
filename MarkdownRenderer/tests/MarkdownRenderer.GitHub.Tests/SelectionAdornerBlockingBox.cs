using MarkdownRenderer.Document;
using MarkdownRenderer.Layout.Internal.Boxes;
using Microsoft.Graphics.Canvas;
using Windows.Foundation;

namespace MarkdownRenderer.GitHub.Tests;

/// <summary>
/// A root whose Measure blocks until released, so a test can hold the snapshot's layout gate open.
/// </summary>
internal sealed class SelectionAdornerBlockingBox : BlockBox
{
    internal SelectionAdornerBlockingBox() { }

    internal SelectionAdornerBlockingBox(Rect initialBounds)
    {
        Bounds = initialBounds;
    }

    internal ManualResetEventSlim MeasureEntered { get; } = new(false);
    internal ManualResetEventSlim ReleaseMeasure { get; } = new(false);

    public override float Measure(float availableWidth)
    {
        MeasureEntered.Set();
        if (!ReleaseMeasure.Wait(TimeSpan.FromSeconds(5)))
            throw new TimeoutException("Selection-adornment test did not release snapshot measurement.");
        Bounds = new Rect(Bounds.X, Bounds.Y, availableWidth, 24);
        return 24;
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
