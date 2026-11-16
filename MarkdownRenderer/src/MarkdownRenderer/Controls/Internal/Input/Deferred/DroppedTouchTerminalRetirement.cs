using MarkdownRenderer.Controls.Internal.Input.Gestures;
using MarkdownRenderer.Controls.Internal.Selection.ContextMenu;
using MarkdownRenderer.Controls.Internal.Selection.TouchSelection;
using MarkdownRenderer.Selection;
using MarkdownRenderer.Selection.Internal;
using Microsoft.UI.Input;

namespace MarkdownRenderer.Controls.Internal.Input.Deferred;

/// <summary>
/// Ends the active touch contact when the deferred queue is abandoned with
/// that contact's release or cancel still queued, so the contact never
/// outlives its own terminal event.
/// </summary>
internal sealed class DroppedTouchTerminalRetirement
{
    private readonly IPointerInputRouterHost _host;
    private readonly SelectionController _selection;
    private readonly MarkdownTouchSelectionSession _touchSelection;
    private readonly TouchTapSuppressionPolicy _touchTapSuppression;
    private readonly SelectionHandlesPresenter _handles;
    private readonly SelectionContextMenuPresenter _contextMenu;

    internal DroppedTouchTerminalRetirement(
        IPointerInputRouterHost host,
        SelectionController selection,
        MarkdownTouchSelectionSession touchSelection,
        TouchTapSuppressionPolicy touchTapSuppression,
        SelectionHandlesPresenter handles,
        SelectionContextMenuPresenter contextMenu)
    {
        _host = host;
        _selection = selection;
        _touchSelection = touchSelection;
        _touchTapSuppression = touchTapSuppression;
        _handles = handles;
        _contextMenu = contextMenu;
    }

    /// <summary>
    /// Retires the active touch owner when <paramref name="deferredInput"/>,
    /// which is about to be dropped, holds its terminal event.
    /// </summary>
    internal void Retire(DeferredInputQueue<PointerInput>? deferredInput)
    {
        if (deferredInput is not { Count: > 0 } queue)
            return;

        uint ownerPointerId = _touchSelection.ContactPointerId;
        if (ownerPointerId == 0)
            return;

        long ownerGestureEpoch = _touchTapSuppression.GetGestureEpochForPointer(ownerPointerId);
        if (ownerGestureEpoch == 0)
            return;

        // A geometry/snapshot failure can abandon the queue after its Press
        // already established ownership but before its deferred terminal is
        // replayed. Retire only a terminal from the exact active epoch: XAML
        // pointer IDs can be reused, and an older queued terminal must not end
        // a newer contact. A dropped Release ends the session while preserving
        // a committed selection. A dropped Cancel follows normal cancellation
        // semantics, clearing only a range created by the canceled contact.
        int count = queue.Count;
        for (int index = 0; index < count; index++)
        {
            if (!queue.TryGetAt(index, out PointerInput queued) ||
                queued.Kind is not (PointerInputKind.Release or PointerInputKind.Cancel or
                    PointerInputKind.HandleRelease or PointerInputKind.HandleCancel) ||
                queued.Pointer is null ||
                queued.Pointer.PointerDeviceType != PointerDeviceType.Touch ||
                queued.Pointer.PointerId != ownerPointerId ||
                queued.TouchTapGestureEpoch != ownerGestureEpoch)
            {
                continue;
            }

            bool isHandleTerminal = queued.Kind is PointerInputKind.HandleRelease or PointerInputKind.HandleCancel;
            bool isCanceled = queued.Kind is PointerInputKind.Cancel or PointerInputKind.HandleCancel;
            bool contactRetired = isCanceled
                ? _touchTapSuppression.TryCancelContact(ownerPointerId, ownerGestureEpoch)
                : _touchTapSuppression.TryEndContact(ownerPointerId, ownerGestureEpoch);
            if (contactRetired)
            {
                if (isHandleTerminal)
                {
                    if (isCanceled)
                    {
                        _handles.ResetDrag();
                        _touchSelection.CancelContact(ownerPointerId);
                    }
                    else
                    {
                        _touchSelection.CompleteHandleDrag(ownerPointerId);
                        _handles.ResetDrag();
                    }
                    _handles.Update();
                }
                else if (isCanceled)
                {
                    TouchSelectionCancelResult cancelResult = _touchSelection.CancelContact(ownerPointerId);
                    if (cancelResult != TouchSelectionCancelResult.NotOwner)
                    {
                        _contextMenu.Close();
                        _handles.Update();
                        if (cancelResult == TouchSelectionCancelResult.ClearSelection && _selection.IsActive)
                        {
                            _host.ClearSelectionForExternalInteraction(
                                resetClickTracking: true,
                                resetDeferredPointerInput: false);
                        }
                    }
                }
                else
                {
                    _touchSelection.EndContact(ownerPointerId);
                }
            }
            return;
        }
    }
}
