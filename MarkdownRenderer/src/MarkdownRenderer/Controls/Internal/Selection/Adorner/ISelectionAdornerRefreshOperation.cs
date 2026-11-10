using MarkdownRenderer.Layout.Internal.Snapshots;

namespace MarkdownRenderer.Controls.Internal.Selection.Adorner;

internal interface ISelectionAdornerRefreshOperation
{
    SelectionAdornerRefreshOperationResult RefreshUnderPaintLease(LayoutSnapshot snapshot);
}
