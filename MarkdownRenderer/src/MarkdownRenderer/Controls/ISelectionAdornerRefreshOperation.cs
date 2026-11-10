using MarkdownRenderer.Layout.Internal.Snapshots;

namespace MarkdownRenderer.Controls;

internal interface ISelectionAdornerRefreshOperation
{
    SelectionAdornerRefreshOperationResult RefreshUnderPaintLease(LayoutSnapshot snapshot);
}
