using System;
using MarkdownRenderer.Layout.Internal.Snapshots;

namespace MarkdownRenderer.Controls;

/// <summary>
/// Owns the nonblocking snapshot admission boundary for a selection-adorner
/// refresh. The operation is a value so the hot UI path needs neither a closure
/// nor a boxed delegate, and the coordinator retains no snapshot or operation.
/// </summary>
internal static class SelectionAdornerRefreshCoordinator
{
    internal static SelectionAdornerRefreshResult TryRefresh<TOperation>(
        LayoutSnapshot snapshot,
        ref TOperation operation)
        where TOperation : struct, ISelectionAdornerRefreshOperation
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        if (!snapshot.TryBeginPaint())
            return SelectionAdornerRefreshResult.Busy;

        try
        {
            return operation.RefreshUnderPaintLease(snapshot) switch
            {
                SelectionAdornerRefreshOperationResult.Retry => SelectionAdornerRefreshResult.Retry,
                SelectionAdornerRefreshOperationResult.Completed => SelectionAdornerRefreshResult.Completed,
                SelectionAdornerRefreshOperationResult.AwaitLifecycle => SelectionAdornerRefreshResult.AwaitLifecycle,
                _ => throw new ArgumentOutOfRangeException(nameof(operation)),
            };
        }
        finally
        {
            snapshot.EndPaint();
        }
    }
}
