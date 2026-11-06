using MarkdownRenderer.Controls;
using MarkdownRenderer.Layout;
using Microsoft.Graphics.Canvas;
using Windows.UI;

namespace MarkdownRenderer.GitHub.Tests;

/// <summary>
/// A selection-adornment refresh that clears the whole target, so a rejected refresh is visible as unchanged pixels.
/// </summary>
internal struct SelectionAdornerPaintWholeTargetOperation(CanvasRenderTarget target, Color color)
    : ISelectionAdornerRefreshOperation
{
    private readonly CanvasRenderTarget _target = target;
    private readonly Color _color = color;

    internal int CallCount { get; private set; }

    public SelectionAdornerRefreshOperationResult RefreshUnderPaintLease(LayoutSnapshot snapshot)
    {
        CallCount++;
        using CanvasDrawingSession drawing = _target.CreateDrawingSession();
        drawing.Clear(_color);
        return SelectionAdornerRefreshOperationResult.Completed;
    }
}
