using MarkdownRenderer.Controls;
using MarkdownRenderer.Layout.Internal.Snapshots;
using MarkdownRenderer.Selection;
using MarkdownRenderer.Selection.Internal;
using Microsoft.Graphics.Canvas;
using Windows.Foundation;
using Windows.UI;
using Xunit;

namespace MarkdownRenderer.GitHub.Tests;

/// <summary>
/// A selection-adornment refresh that records its calls, reads geometry under the paint lease and paints the staged highlight rectangles.
/// </summary>
internal struct SelectionAdornerRecordingOperation(
    SelectionController selection,
    Rect viewport,
    CanvasRenderTarget target,
    Color color,
    bool retryOnce,
    bool succeeds) : ISelectionAdornerRefreshOperation
{
    private readonly SelectionController _selection = selection;
    private readonly Rect _viewport = viewport;
    private readonly CanvasRenderTarget _target = target;
    private readonly Color _color = color;
    private bool _retryOnce = retryOnce;
    private readonly bool _succeeds = succeeds;

    internal int CallCount { get; private set; }
    internal int GeometryCount { get; private set; }
    internal int CommitCount { get; private set; }
    internal bool ObservedReadLease { get; private set; }

    public SelectionAdornerRefreshOperationResult RefreshUnderPaintLease(LayoutSnapshot snapshot)
    {
        CallCount++;
        ObservedReadLease = snapshot.TryGetMeasuredTopLevelBlocksInBandForSceneProbe(
            _viewport.Top,
            _viewport.Bottom,
            out _);
        Assert.True(ObservedReadLease);

        var staged = new List<Rect>();
        _selection.AppendVisibleHighlightRects(snapshot, _viewport, staged);
        GeometryCount++;
        if (_retryOnce)
        {
            _retryOnce = false;
            return SelectionAdornerRefreshOperationResult.Retry;
        }

        if (!_succeeds)
            return SelectionAdornerRefreshOperationResult.AwaitLifecycle;

        using CanvasDrawingSession drawing = _target.CreateDrawingSession();
        foreach (Rect rectangle in staged)
            drawing.FillRectangle(rectangle, _color);
        CommitCount++;
        return SelectionAdornerRefreshOperationResult.Completed;
    }
}
