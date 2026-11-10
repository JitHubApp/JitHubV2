using MarkdownRenderer.Layout.Internal.Snapshots;

namespace MarkdownRenderer.Controls.Selection;

/// <summary>
/// Runs one adorner refresh for a snapshot generation inside
/// <see cref="SelectionAdornerRefreshCoordinator"/>, which holds the
/// snapshot's paint lease around it.
/// </summary>
internal readonly struct SelectionAdornerRefreshOperation : ISelectionAdornerRefreshOperation
{
    private readonly ISelectionAdornerPainter _painter;
    private readonly long _generation;

    internal SelectionAdornerRefreshOperation(ISelectionAdornerPainter painter, long generation)
    {
        _painter = painter;
        _generation = generation;
    }

    public SelectionAdornerRefreshOperationResult RefreshUnderPaintLease(LayoutSnapshot snapshot)
        => _painter.RefreshUnderPaintLease(snapshot, _generation);
}
