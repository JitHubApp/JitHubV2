using MarkdownRenderer.Layout;

namespace MarkdownRenderer.Controls;

internal enum SelectionAdornerRefreshOperationResult
{
    Retry,
    Completed,
    AwaitLifecycle,
}
