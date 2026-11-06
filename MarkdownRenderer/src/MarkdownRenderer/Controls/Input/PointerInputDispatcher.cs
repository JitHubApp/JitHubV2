using MarkdownRenderer.Controls.Selection;
using Microsoft.UI.Input;

namespace MarkdownRenderer.Controls.Input;

/// <summary>
/// Hands one admitted pointer input to the interaction that owns its kind:
/// document selection, horizontal overflow, selection handles, or the
/// recognized touch gestures.
/// </summary>
internal sealed class PointerInputDispatcher
{
    private readonly RecognizedGestureAttribution _attribution;
    private readonly TouchTapSuppressionPolicy _touchTapSuppression;
    private readonly DocumentPointerInteraction _document;
    private readonly HorizontalOverflowPointerDrag _horizontalOverflowDrag;
    private readonly SelectionHandlesPresenter _handles;
    private readonly TouchGestureInteraction _touchGestures;

    internal PointerInputDispatcher(
        RecognizedGestureAttribution attribution,
        TouchTapSuppressionPolicy touchTapSuppression,
        DocumentPointerInteraction document,
        HorizontalOverflowPointerDrag horizontalOverflowDrag,
        SelectionHandlesPresenter handles,
        TouchGestureInteraction touchGestures)
    {
        _attribution = attribution;
        _touchTapSuppression = touchTapSuppression;
        _document = document;
        _horizontalOverflowDrag = horizontalOverflowDrag;
        _handles = handles;
        _touchGestures = touchGestures;
    }

    /// <summary>
    /// Runs <paramref name="input"/> under the snapshot lease its caller holds.
    /// </summary>
    internal void Process(ref PointerInput input)
    {
        if (input.IgnoreUnattributableTouchGesture)
            return;

        if (input.DeferredTouchGesturePointerId != 0 &&
            !_attribution.ResolveDeferredOwner(ref input))
        {
            return;
        }

        if (input.TouchTapGestureEpoch == 0 &&
            (input.Kind is PointerInputKind.Move or PointerInputKind.Release or PointerInputKind.Cancel or
                PointerInputKind.Exit or PointerInputKind.HandleMove or PointerInputKind.HandleRelease or
                PointerInputKind.HandleCancel) &&
            input.Pointer.PointerDeviceType == PointerDeviceType.Touch)
        {
            // A press may have been deferred before it could establish the
            // primary gesture epoch. Replay later pointer events against the
            // owner created by that earlier queued press.
            input.TouchTapGestureEpoch = _touchTapSuppression.GetGestureEpochForPointer(
                input.Pointer.PointerId);
        }

        switch (input.Kind)
        {
            case PointerInputKind.Press: _document.ProcessPressed(ref input); break;
            case PointerInputKind.Move: _document.ProcessMoved(ref input); break;
            case PointerInputKind.Release: _document.ProcessReleased(ref input); break;
            case PointerInputKind.Cancel: _document.ProcessCanceledOrCaptureLost(ref input); break;
            case PointerInputKind.Exit: _document.ProcessExited(ref input); break;
            case PointerInputKind.Wheel: _horizontalOverflowDrag.ProcessWheel(ref input); break;
            case PointerInputKind.HandlePress: _handles.ProcessPointerPressed(ref input); break;
            case PointerInputKind.HandleMove: _handles.ProcessPointerMoved(ref input); break;
            case PointerInputKind.HandleRelease:
                if (!_handles.TryProcessPointerReleased(ref input))
                    _document.ProcessReleased(ref input);
                break;
            case PointerInputKind.HandleCancel:
                if (!_handles.TryProcessPointerCanceled(ref input))
                    _document.ProcessCanceledOrCaptureLost(ref input);
                break;
            case PointerInputKind.Tap: _touchGestures.ProcessTapped(ref input); break;
            case PointerInputKind.DoubleTap: _touchGestures.ProcessDoubleTapped(ref input); break;
            case PointerInputKind.Hold: _touchGestures.ProcessHolding(ref input); break;
            case PointerInputKind.RightTap: _touchGestures.ProcessRightTapped(ref input); break;
        }
    }
}
