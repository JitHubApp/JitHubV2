using MarkdownRenderer.Layout;

namespace MarkdownRenderer.Controls.Selection;

/// <summary>
/// Runs one adorner refresh for a snapshot generation inside
/// <see cref="SelectionAdornerRefreshCoordinator"/>, which holds the
/// snapshot's paint lease around it.
/// </summary>
internal readonly struct SelectionAdornerRefreshOperation : ISelectionAdornerRefreshOperation
{
    private readonly SelectionAdornerPresenter _presenter;
    private readonly long _generation;

    internal SelectionAdornerRefreshOperation(SelectionAdornerPresenter presenter, long generation)
    {
        _presenter = presenter;
        _generation = generation;
    }

    public SelectionAdornerRefreshOperationResult RefreshUnderPaintLease(LayoutSnapshot snapshot)
        => _presenter.RefreshUnderPaintLease(snapshot, _generation);
}
