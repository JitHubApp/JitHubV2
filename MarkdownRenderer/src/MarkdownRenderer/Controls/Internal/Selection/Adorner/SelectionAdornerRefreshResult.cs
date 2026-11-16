
namespace MarkdownRenderer.Controls.Internal.Selection.Adorner;

internal enum SelectionAdornerRefreshResult
{
    Busy,
    Retry,
    Completed,
    AwaitLifecycle,
}
