using Microsoft.UI.Input;
using Windows.Foundation;

namespace MarkdownRenderer.Controls.Input;

/// <summary>
/// Ties an ID-less recognized touch gesture (tap, double-tap, hold) to the
/// touch contact that produced it, so the gesture runs against that
/// contact's epoch and geometry and never against a different active pointer.
/// </summary>
internal sealed class RecognizedGestureAttribution
{
    private readonly MarkdownViewport _viewport;
    private readonly MarkdownTouchSelectionSession _touchSelection;
    private readonly TouchTapSuppressionPolicy _touchTapSuppression;

    internal RecognizedGestureAttribution(
        MarkdownViewport viewport,
        MarkdownTouchSelectionSession touchSelection,
        TouchTapSuppressionPolicy touchTapSuppression)
    {
        _viewport = viewport;
        _touchSelection = touchSelection;
        _touchTapSuppression = touchTapSuppression;
    }

    /// <summary>
    /// Attributes a recognized touch gesture at <paramref name="point"/> to a
    /// queued press, to the active contact, or marks it unattributable.
    /// </summary>
    internal void AttributeTouchGesture(
        ref PointerInput input,
        Point point,
        DeferredInputQueue<PointerInput>? deferredInput)
    {
        var deferredOwner = FindDeferredOwner(point, deferredInput);
        if (deferredOwner.IsAmbiguous)
        {
            input.IgnoreUnattributableTouchGesture = true;
        }
        else if (deferredOwner.TryGetOwnerPointerId(out uint deferredPointerId))
        {
            // PointerPressed may still be queued ahead of this recognized
            // gesture. Resolve its epoch only when FIFO replay reaches us.
            // Never substitute today's active owner for an ID-less event.
            input.DeferredTouchGesturePointerId = deferredPointerId;
        }
        else if (_touchTapSuppression.TryGetGestureEpochForRecognizedGesture(
                     point,
                     out long gestureEpoch,
                     out uint ownerPointerId,
                     out bool isAmbiguous))
        {
            input.TouchTapGestureEpoch = gestureEpoch;
            if (_touchSelection.ContactPointerId == ownerPointerId)
            {
                input.GestureContact = _touchSelection.Contact;
                input.HasGestureContact = true;
            }
        }
        else if (isAmbiguous)
        {
            input.IgnoreUnattributableTouchGesture = true;
        }

        // Source/document replacement invalidates the provenance of any
        // unmatched, ID-less XAML gesture. A queued press remains
        // attributable through DeferredTouchGesturePointerId; otherwise
        // require a newly accepted contact before dispatching it.
        if (!_touchTapSuppression.CanDispatchRecognizedGesture(
                input.TouchTapGestureEpoch,
                hasDeferredOwner: input.DeferredTouchGesturePointerId != 0))
        {
            input.IgnoreUnattributableTouchGesture = true;
        }
    }

    /// <summary>
    /// Resolves a gesture attributed to a queued press once FIFO replay has
    /// run that press. Returns false when the press never became a contact.
    /// </summary>
    internal bool ResolveDeferredOwner(ref PointerInput input)
    {
        uint pointerId = input.DeferredTouchGesturePointerId;
        if (pointerId == 0)
            return !input.IgnoreUnattributableTouchGesture;

        long gestureEpoch = _touchTapSuppression.GetGestureEpochForPointerOrCompleted(pointerId);
        input.DeferredTouchGesturePointerId = 0;
        if (gestureEpoch == 0)
        {
            // The queued press was rejected, canceled, or invalidated before
            // replay. An ID-less recognized event must not fall back to a
            // different active pointer or to the synthetic no-contact token.
            input.IgnoreUnattributableTouchGesture = true;
            return false;
        }

        input.TouchTapGestureEpoch = gestureEpoch;
        if (_touchSelection.ContactPointerId == pointerId)
        {
            input.GestureContact = _touchSelection.Contact;
            input.HasGestureContact = true;
            if (_viewport.TryMapCanvasContactToViewport(input.GestureContact, out TouchContactGeometry viewportContact))
            {
                input.ViewportGestureContact = viewportContact;
                input.HasViewportGestureContact = true;
            }
        }

        return true;
    }

    private static DeferredTouchGestureOwnerResolver FindDeferredOwner(
        Point gesturePosition,
        DeferredInputQueue<PointerInput>? deferredInput)
    {
        var resolver = new DeferredTouchGestureOwnerResolver(gesturePosition);
        if (deferredInput is not { Count: > 0 } queue)
            return resolver;

        // This scan is bounded by the queue's fixed capacity. It is used only
        // for ID-less touch recognizer events, never for pointer-move hot paths.
        int count = queue.Count;
        for (int index = 0; index < count; index++)
        {
            if (!queue.TryGetAt(index, out PointerInput queued))
                continue;

            if (queued.Pointer?.PointerDeviceType != PointerDeviceType.Touch)
                continue;

            if (queued.Kind == PointerInputKind.Press)
            {
                resolver.ObserveTouchPress(
                    queued.Pointer.PointerId,
                    queued.Point.Position,
                    queued.Point.IsInContact);
            }
            else if (queued.Kind == PointerInputKind.Cancel)
            {
                resolver.ObserveTouchCancel(queued.Pointer.PointerId);
            }
        }

        return resolver;
    }
}
