using MarkdownRenderer.Layout;

namespace MarkdownRenderer.Controls;

internal interface ISelectionAdornerRefreshOperation
{
    SelectionAdornerRefreshOperationResult RefreshUnderPaintLease(LayoutSnapshot snapshot);
}
