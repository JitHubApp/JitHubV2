using System;
using MarkdownRenderer.Layout;

namespace MarkdownRenderer.Controls;

internal enum SelectionAdornerRefreshResult
{
    Busy,
    Retry,
    Completed,
    AwaitLifecycle,
}

internal enum SelectionAdornerRefreshOperationResult
{
    Retry,
    Completed,
    AwaitLifecycle,
}

internal interface ISelectionAdornerRefreshOperation
{
    SelectionAdornerRefreshOperationResult RefreshUnderPaintLease(LayoutSnapshot snapshot);
}

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

/// <summary>
/// Determines whether a viewport change can affect selection-adornment state.
/// Kept value-only so the scroll callback can skip redundant Rendering
/// subscriptions without inspecting layout or allocating.
/// </summary>
internal static class SelectionAdornerScrollRefreshPolicy
{
    internal static bool ShouldRefreshForScroll(
        bool hasActiveSelection,
        bool hasHoveredLink,
        bool hasFocusedLink,
        bool hasVisibleAdorner,
        bool hasRetainedRectangles,
        bool hasTouchHandles,
        bool hasPendingRefresh)
        => hasActiveSelection ||
           hasHoveredLink ||
           hasFocusedLink ||
           hasVisibleAdorner ||
           hasRetainedRectangles ||
           hasTouchHandles ||
           hasPendingRefresh;
}

/// <summary>
/// Value-only coalescing state for a frame-paced retry. A rendering callback
/// consumes one pending attempt; Busy calls Request again for the next frame.
/// It deliberately owns no control, snapshot, box, or native resource.
/// </summary>
internal sealed class SelectionAdornerRetryState
{
    private bool _pending;

    internal bool IsPending => _pending;

    internal void Request() => _pending = true;

    internal bool TryBeginFrame()
    {
        if (!_pending)
            return false;

        _pending = false;
        return true;
    }

    internal void Retry() => _pending = true;

    internal void Complete() => _pending = false;

    internal void Cancel() => _pending = false;
}
