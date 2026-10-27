using System;
using System.Collections.Generic;
using System.Threading;
using MarkdownRenderer.Diagnostics;
using MarkdownRenderer.Layout;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Windows.Foundation;
using VirtualKeyModifiers = Windows.System.VirtualKeyModifiers;

namespace MarkdownRenderer.Controls;

public partial class MarkdownRendererControl
{
    // A pending horizontal touch contact is not captured until it becomes a
    // drag, but it already owns a table/snapshot identity. Selection handles
    // and queued input likewise must finish against their original geometry.
    // Read only on the UI dispatcher, including the final publication fence.
    private bool HasSnapshotPointerInteraction =>
        _pointerSession.IsActive || _horizontalOverflowPointerId != 0 ||
        _selectionHandlePointerId != 0 || _drainingPointerInput ||
        _deferredPointerCaptures.Count != 0 ||
        (_deferredPointerInput?.HasBlockingSnapshotInteractions ?? false);

    private DeferredInputQueue<PointerInput>? _deferredPointerInput;
    private LayoutSnapshot? _deferredPointerSnapshot;
    private DispatcherQueueTimer? _pointerRetryTimer;
    private bool _drainingPointerInput;
    private bool _resettingPointerInput;
    private uint _contextCaptureReleaseInFlightId;
    private readonly Dictionary<uint, (UIElement Target, Pointer Pointer)> _deferredPointerCaptures = new();

    private enum PointerInputKind { Press, Move, Release, Cancel, Exit, Wheel, HandlePress, HandleMove, HandleRelease, HandleCancel, Tap, DoubleTap, Hold, RightTap }

    // Capture point/modifiers/time at event delivery. Never retain routed event
    // arguments or re-query a moved pointer when deferred work is replayed.
    private struct PointerInput
    {
        internal PointerInputKind Kind;
        internal object Sender;
        internal Pointer Pointer;
        internal PointerPoint Point;
        internal VirtualKeyModifiers Modifiers;
        internal long TickCount;
        internal long TouchTapGestureEpoch;
        internal uint DeferredTouchGesturePointerId;
        internal bool IgnoreUnattributableTouchGesture;
        internal bool CapturedBeforeDeferral;
        internal bool Handled;
        internal Windows.Foundation.Point GesturePosition;
        internal PointerDeviceType GestureDevice;
        internal long GestureLayoutRevision;
        internal bool ShowContextMenuAfterInteraction;
        internal bool TouchSelectionContextMenu;
        internal LayoutSnapshot? InteractionSnapshot;
        internal UIElement? InteractionCanvas;
        internal MarkdownInputModifiers LinkModifiers;
        internal bool ClearSelectionAfterInteraction;
        internal bool ReleaseCanvasCaptureAfterInteraction;
        internal bool ResumeRebuildAfterInteraction;
        internal bool RefreshSelectionAdornerAfterInteraction;
        internal bool WaitForMeasuredHitTest;
        internal bool WaitForLayoutPublication;
        internal bool IsDeferredGeometryWait;
        internal bool InvalidDeferredProjection;
        internal bool ContextCaptureEnded;
        internal Point ViewportGesturePosition;
        internal bool HasViewportGesturePosition;
        internal TouchContactGeometry ViewportGestureContact;
        internal bool HasViewportGestureContact;
        internal TouchContactGeometry GestureContact;
        internal bool HasGestureContact;
        internal long GestureSnapshotGeneration;
        internal uint ReleasePointerIdAfterInteraction;
        private LinkActivationIntent? _linkActivation;

        internal bool HasPostInteractionWork =>
            ClearSelectionAfterInteraction ||
            ReleaseCanvasCaptureAfterInteraction ||
            ResumeRebuildAfterInteraction ||
            RefreshSelectionAdornerAfterInteraction ||
            _linkActivation is not null;

        internal void SetLinkActivation(LinkActivationIntent intent) => _linkActivation = intent;

        internal bool TryGetLinkActivation(out LinkActivationIntent intent)
        {
            if (_linkActivation is { } activation)
            {
                intent = activation;
                return true;
            }

            intent = default;
            return false;
        }
    }

    private void OnPointerPressed(object sender, PointerRoutedEventArgs e) => DispatchPointerInput(PointerInputKind.Press, sender, e);
    private void OnPointerMoved(object sender, PointerRoutedEventArgs e) => DispatchPointerInput(PointerInputKind.Move, sender, e);
    private void OnPointerReleased(object sender, PointerRoutedEventArgs e) => DispatchPointerInput(PointerInputKind.Release, sender, e);
    private void OnPointerCanceledOrCaptureLost(object sender, PointerRoutedEventArgs e) => DispatchPointerInput(PointerInputKind.Cancel, sender, e);
    private void OnPointerExited(object sender, PointerRoutedEventArgs e) => DispatchPointerInput(PointerInputKind.Exit, sender, e);
    private void OnPointerWheelChanged(object sender, PointerRoutedEventArgs e) => DispatchPointerInput(PointerInputKind.Wheel, sender, e);
    private void OnSelectionHandlePointerPressed(object sender, PointerRoutedEventArgs e) => DispatchPointerInput(PointerInputKind.HandlePress, sender, e);
    private void OnSelectionHandlePointerMoved(object sender, PointerRoutedEventArgs e) => DispatchPointerInput(PointerInputKind.HandleMove, sender, e);
    private void OnSelectionHandlePointerReleased(object sender, PointerRoutedEventArgs e) => DispatchPointerInput(PointerInputKind.HandleRelease, sender, e);
    private void OnSelectionHandlePointerCanceled(object sender, PointerRoutedEventArgs e) => DispatchPointerInput(PointerInputKind.HandleCancel, sender, e);

    private static PointerInputKind NormalizeCanceledPointerInputKind(PointerInputKind kind, bool isCanceled)
    {
        if (!isCanceled)
            return kind;

        // Device cancellation can accompany a routed move or release packet.
        // Classify it once before either live dispatch or deferred queuing so
        // it cannot commit a tap, link activation, or final handle movement.
        return kind switch
        {
            PointerInputKind.Press or PointerInputKind.Move or PointerInputKind.Release or
                PointerInputKind.Exit or PointerInputKind.Wheel => PointerInputKind.Cancel,
            PointerInputKind.HandlePress or PointerInputKind.HandleMove or
                PointerInputKind.HandleRelease => PointerInputKind.HandleCancel,
            _ => kind,
        };
    }

    private bool DispatchRecognizedGesture(PointerInputKind kind, Windows.Foundation.Point point, PointerDeviceType device)
    {
        if (_isDisposed || _isUnloaded || _canvas is null || _snapshot is not { } snapshot)
            return false;
        if (RequestGeometryRecoveryIfRequired(snapshot))
            return false;
        snapshot.NotifyPointerInput();
        var input = new PointerInput
        {
            Kind = kind,
            GesturePosition = point,
            GestureDevice = device,
            LinkModifiers = GetCurrentInputModifiers(),
            GestureSnapshotGeneration = Volatile.Read(ref _snapshotGeneration),
        };

        if (device == PointerDeviceType.Touch)
        {
            var deferredOwner = FindDeferredTouchGestureOwner(point);
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
        input.HasViewportGesturePosition = TryMapCanvasToViewport(point, out input.ViewportGesturePosition);
        if (input.HasGestureContact &&
            TryMapCanvasContactToViewport(input.GestureContact, out TouchContactGeometry viewportContact))
        {
            input.ViewportGestureContact = viewportContact;
            input.HasViewportGestureContact = true;
        }
        input.WaitForMeasuredHitTest = IsMeasuredHitTestGesture(input);
        if (!_drainingPointerInput && (_deferredPointerInput?.Count ?? 0) == 0 && snapshot.TryBeginInteraction())
        {
            long snapshotGeneration = Volatile.Read(ref _snapshotGeneration);
            long layoutRevision = snapshot.LayoutRevision;
            input.GestureLayoutRevision = layoutRevision;
            bool waitForGeometry = false;
            LazyLayoutBand waitingBand = default;
            try
            {
                input.InteractionSnapshot = snapshot;
                if (TryGetGestureAdmissionBand(ref input, snapshot, out waitingBand, out bool bandReady) &&
                    !bandReady)
                {
                    PrepareContextGestureForDeferral(ref input);
                    waitForGeometry = true;
                }
                else
                {
                    input.WaitForMeasuredHitTest = false;
                    ProcessPointerInput(ref input);
                }
            }
            finally
            {
                input.InteractionSnapshot = null;
                snapshot.EndInteraction();
            }
            if (waitForGeometry)
            {
                if (!EnqueueRecognizedGesture(snapshot, input))
                    return false;
                if (input.HasPostInteractionWork)
                {
                    if (!CompletePointerPostInteraction(
                            ref input,
                            snapshot,
                            snapshotGeneration,
                            layoutRevision))
                    {
                        ResetDeferredPointerInput();
                        return false;
                    }
                }
                if (input.WaitForLayoutPublication)
                {
                    if (IsDeferredGestureLayoutPublished(snapshot))
                        _pointerRetryTimer?.Start();
                    else
                        _pointerRetryTimer?.Stop();
                    return true;
                }
                RequestDeferredGestureBandRealization(snapshot, waitingBand);
                return true;
            }
            if (input.ShowContextMenuAfterInteraction)
            {
                input.Handled = CompleteGestureAfterSnapshotInteraction(
                    input,
                    snapshot,
                    snapshotGeneration,
                    layoutRevision);
            }
            else if (input.HasPostInteractionWork)
            {
                CompletePointerPostInteraction(
                    ref input,
                    snapshot,
                    snapshotGeneration,
                    layoutRevision);
            }
            return input.Handled;
        }
        if (RequestGeometryRecoveryIfRequired(snapshot))
            return false;
        return EnqueueRecognizedGesture(snapshot, input);
    }

    private bool EnqueueRecognizedGesture(LayoutSnapshot snapshot, PointerInput input)
    {
        input.IsDeferredGeometryWait = true;
        if (_deferredPointerSnapshot is not null && !ReferenceEquals(snapshot, _deferredPointerSnapshot))
            ResetDeferredPointerInput();
        _deferredPointerSnapshot = snapshot;
        _deferredPointerInput ??= new DeferredInputQueue<PointerInput>();
        if (!_deferredPointerInput.TryEnqueue(input, 0, false, blocksSnapshotInteraction: false))
        {
            DropDeferredInputQueuePreservingSelection();
            return false;
        }
        _pointerRetryTimer ??= CreatePointerRetryTimer();
        _pointerRetryTimer.Start();
        return true;
    }

    private DeferredTouchGestureOwnerResolver FindDeferredTouchGestureOwner(
        Windows.Foundation.Point gesturePosition)
    {
        var resolver = new DeferredTouchGestureOwnerResolver(gesturePosition);
        if (_deferredPointerInput is not { Count: > 0 } queue)
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

    internal static bool TryCreateGestureHitTestBand(
        Windows.Foundation.Point gestureCanvas,
        double rasterizationScale,
        bool includeContact,
        TouchContactGeometry capturedContact,
        out LazyLayoutBand band)
    {
        band = default;
        if (!double.IsFinite(gestureCanvas.X) || !double.IsFinite(gestureCanvas.Y))
            return false;

        if (!double.IsFinite(rasterizationScale) || rasterizationScale <= 0)
            rasterizationScale = 1;
        // The scale term covers one physical pixel, but a malformed caller
        // must not turn that term into a document-scale realization request.
        double radius = Math.Min(256, Math.Max(4, 1 / rasterizationScale));
        double top = gestureCanvas.Y - radius;
        double bottom = gestureCanvas.Y + radius;
        if (!double.IsFinite(top) || !double.IsFinite(bottom))
            return false;

        if (includeContact && IsBoundedGestureContact(gestureCanvas, capturedContact))
        {
            double contactTop = capturedContact.Y;
            double contactBottom = capturedContact.Y + capturedContact.Height;
            double centerY = capturedContact.CenterY;
            if (!double.IsFinite(contactBottom) || !double.IsFinite(centerY))
                return false;
            top = Math.Min(top, Math.Min(contactTop - 4, centerY - 4));
            bottom = Math.Max(bottom, Math.Max(contactBottom + 4, centerY + 4));
        }

        // HitTest samples outside the document are guaranteed misses. Clamp
        // the only in-document edge, and represent a wholly pre-document band
        // as empty so it cannot wait forever for the first block to measure.
        if (bottom < 0)
        {
            band = new LazyLayoutBand(0, 0);
            return true;
        }

        band = new LazyLayoutBand(Math.Max(0, top), Math.Max(0, bottom));
        return double.IsFinite(band.Top) && double.IsFinite(band.Bottom) &&
            band.Bottom >= band.Top;
    }

    private static bool IsBoundedGestureContact(
        Windows.Foundation.Point gestureCanvas,
        TouchContactGeometry contact)
    {
        if (!double.IsFinite(contact.X) || !double.IsFinite(contact.Y) ||
            !double.IsFinite(contact.Width) || !double.IsFinite(contact.Height) ||
            contact.Width <= 0 || contact.Height <= 0 ||
            contact.Width > 256 || contact.Height > 256)
        {
            return false;
        }

        double right = contact.X + contact.Width;
        double bottom = contact.Y + contact.Height;
        double centerX = contact.CenterX;
        double centerY = contact.CenterY;
        if (!double.IsFinite(right) || !double.IsFinite(bottom) ||
            !double.IsFinite(centerX) || !double.IsFinite(centerY))
        {
            return false;
        }

        double dx = centerX - gestureCanvas.X;
        double dy = centerY - gestureCanvas.Y;
        return double.IsFinite(dx) && double.IsFinite(dy) &&
            dx * dx + dy * dy <= 256 * 256;
    }

    private static bool IncludesContactProbes(PointerInput input) =>
        input.HasGestureContact &&
        (input.GestureDevice is PointerDeviceType.Touch or PointerDeviceType.Pen) &&
        (input.Kind is PointerInputKind.DoubleTap or PointerInputKind.Hold or PointerInputKind.RightTap);

    private bool IsMeasuredHitTestGesture(PointerInput input)
    {
        bool candidate = input.Kind switch
        {
            PointerInputKind.Tap => input.GestureDevice == PointerDeviceType.Touch,
            PointerInputKind.DoubleTap => input.GestureDevice == PointerDeviceType.Touch,
            PointerInputKind.Hold => input.GestureDevice is PointerDeviceType.Touch or PointerDeviceType.Pen,
            // Right-click menus also target layout-owned text and must not be
            // placed against an estimated/unpublished root. Contact geometry
            // is consulted only for touch/pen by IncludesContactProbes.
            PointerInputKind.RightTap => true,
            _ => false,
        };
        if (!candidate)
            return false;
        if (input.Kind == PointerInputKind.Tap &&
            IsSuppressedTouchGesture(input.TouchTapGestureEpoch, input.GesturePosition))
            return false;
        return !IsPointOverEmbed(input.GesturePosition) &&
            !IsPointOverCodeBlockAction(input.GesturePosition);
    }

    private bool TryGetGestureHitTestBand(ref PointerInput input, out LazyLayoutBand band)
    {
        band = default;
        if (input.IsDeferredGeometryWait && !TryReprojectDeferredGesture(ref input))
            return false;
        if (!input.WaitForMeasuredHitTest)
            return false;

        if (!IsMeasuredHitTestGesture(input))
        {
            input.WaitForMeasuredHitTest = false;
            return false;
        }

        double scale = XamlRoot?.RasterizationScale ?? 1;
        return TryCreateGestureHitTestBand(
            input.GesturePosition,
            scale,
            includeContact: false,
            capturedContact: input.GestureContact,
            band: out band);
    }

    private bool TryGetGestureAdmissionBand(
        ref PointerInput input,
        LayoutSnapshot snapshot,
        out LazyLayoutBand band,
        out bool ready)
    {
        ready = true;
        band = default;
        input.WaitForLayoutPublication = false;
        if (input.WaitForMeasuredHitTest &&
            (_publishingLayoutCommit ||
             _pendingLayoutCommit is not null || _appliedLazyLayoutRevision < snapshot.LayoutRevision))
        {
            // A worker's measured mask is not enough: scroll anchoring and
            // realization-plan publication still need to commit on the UI
            // thread before a queued canvas-local hit can be replayed.
            input.WaitForLayoutPublication = true;
            ready = false;
            return true;
        }

        if (!TryGetGestureHitTestBand(ref input, out LazyLayoutBand gestureBand))
            return false;

        band = gestureBand;
        if (!snapshot.IsInputHitTestBandMeasuredUnderInteraction(gestureBand))
        {
            ready = false;
            return true;
        }

        // The production hit-test is ordered: a selectable direct gesture hit
        // returns before the wider touch-contact fallback probes. Do not delay
        // that common case on neighboring, unmeasured contact roots.
        if (!IncludesContactProbes(input) ||
            IsSelectableTouchPoint(input.GesturePosition, snapshot, out _))
        {
            return true;
        }

        double scale = XamlRoot?.RasterizationScale ?? 1;
        if (!TryCreateGestureHitTestBand(
                input.GesturePosition,
                scale,
                includeContact: true,
                capturedContact: input.GestureContact,
                band: out LazyLayoutBand contactBand))
        {
            return true;
        }

        band = contactBand;
        ready = snapshot.IsInputHitTestBandMeasuredUnderInteraction(contactBand);
        return true;
    }

    private bool IsDeferredGestureLayoutPublished(LayoutSnapshot snapshot) =>
        !_isDisposed && !_isUnloaded &&
        ReferenceEquals(snapshot, _snapshot) &&
        !_publishingLayoutCommit &&
        _pendingLayoutCommit is null &&
        _appliedLazyLayoutRevision >= snapshot.LayoutRevision;

    private bool TryReprojectDeferredGesture(ref PointerInput input)
    {
        if (!input.HasViewportGesturePosition ||
            !TryMapViewportToCanvas(input.ViewportGesturePosition, out var currentPosition))
        {
            input.InvalidDeferredProjection = true;
            return false;
        }
        input.GesturePosition = currentPosition;
        if (input.HasViewportGestureContact &&
            TryMapViewportContactToCanvas(input.ViewportGestureContact, out TouchContactGeometry contact))
        {
            input.GestureContact = contact;
            input.HasGestureContact = true;
        }
        else
        {
            input.GestureContact = default;
            input.HasGestureContact = false;
        }
        return true;
    }

    private bool TryMapCanvasToViewport(
        Windows.Foundation.Point canvasPoint,
        out Windows.Foundation.Point viewportPoint)
    {
        viewportPoint = default;
        if (_canvas is null)
            return false;
        try
        {
            if (_scroll is { } scroll)
            {
                viewportPoint = _canvas.TransformToVisual(scroll).TransformPoint(canvasPoint);
            }
            else if (_hasExternalViewport)
            {
                viewportPoint = new Windows.Foundation.Point(
                    canvasPoint.X - _externalViewport.X,
                    canvasPoint.Y - _externalViewport.Y);
            }
            else
            {
                viewportPoint = _canvas.TransformToVisual(this).TransformPoint(canvasPoint);
            }
            return double.IsFinite(viewportPoint.X) && double.IsFinite(viewportPoint.Y);
        }
        catch (Exception)
        {
            viewportPoint = default;
            return false;
        }
    }

    private bool TryMapViewportToCanvas(
        Windows.Foundation.Point viewportPoint,
        out Windows.Foundation.Point canvasPoint)
    {
        canvasPoint = default;
        if (_canvas is null || !double.IsFinite(viewportPoint.X) || !double.IsFinite(viewportPoint.Y))
            return false;
        try
        {
            if (_scroll is { } scroll)
            {
                canvasPoint = scroll.TransformToVisual(_canvas).TransformPoint(viewportPoint);
            }
            else if (_hasExternalViewport)
            {
                canvasPoint = new Windows.Foundation.Point(
                    viewportPoint.X + _externalViewport.X,
                    viewportPoint.Y + _externalViewport.Y);
            }
            else
            {
                canvasPoint = this.TransformToVisual(_canvas).TransformPoint(viewportPoint);
            }
            return double.IsFinite(canvasPoint.X) && double.IsFinite(canvasPoint.Y);
        }
        catch (Exception)
        {
            canvasPoint = default;
            return false;
        }
    }

    private bool TryMapCanvasContactToViewport(
        TouchContactGeometry contact,
        out TouchContactGeometry viewportContact)
        => TryMapContact(contact, canvasToViewport: true, out viewportContact);

    private bool TryMapViewportContactToCanvas(
        TouchContactGeometry contact,
        out TouchContactGeometry canvasContact)
        => TryMapContact(contact, canvasToViewport: false, out canvasContact);

    private bool TryMapContact(
        TouchContactGeometry contact,
        bool canvasToViewport,
        out TouchContactGeometry mapped)
    {
        mapped = default;
        if (_canvas is null || !double.IsFinite(contact.X) || !double.IsFinite(contact.Y) ||
            !double.IsFinite(contact.Width) || !double.IsFinite(contact.Height) ||
            contact.Width < 0 || contact.Height < 0)
        {
            return false;
        }

        Windows.Foundation.Point p1 = new(contact.X, contact.Y);
        Windows.Foundation.Point p2 = new(contact.X + contact.Width, contact.Y);
        Windows.Foundation.Point p3 = new(contact.X, contact.Y + contact.Height);
        Windows.Foundation.Point p4 = new(contact.X + contact.Width, contact.Y + contact.Height);
        if (!TryMapContactPoint(p1, canvasToViewport, out p1) ||
            !TryMapContactPoint(p2, canvasToViewport, out p2) ||
            !TryMapContactPoint(p3, canvasToViewport, out p3) ||
            !TryMapContactPoint(p4, canvasToViewport, out p4))
        {
            return false;
        }

        double left = Math.Min(Math.Min(p1.X, p2.X), Math.Min(p3.X, p4.X));
        double top = Math.Min(Math.Min(p1.Y, p2.Y), Math.Min(p3.Y, p4.Y));
        double right = Math.Max(Math.Max(p1.X, p2.X), Math.Max(p3.X, p4.X));
        double bottom = Math.Max(Math.Max(p1.Y, p2.Y), Math.Max(p3.Y, p4.Y));
        if (!double.IsFinite(left) || !double.IsFinite(top) ||
            !double.IsFinite(right) || !double.IsFinite(bottom))
        {
            return false;
        }
        mapped = new TouchContactGeometry(left, top, right - left, bottom - top);
        return double.IsFinite(mapped.Width) && double.IsFinite(mapped.Height);
    }

    private bool TryMapContactPoint(
        Windows.Foundation.Point point,
        bool canvasToViewport,
        out Windows.Foundation.Point mapped)
    {
        mapped = default;
        try
        {
            if (_scroll is { } scroll)
            {
                mapped = canvasToViewport
                    ? _canvas!.TransformToVisual(scroll).TransformPoint(point)
                    : scroll.TransformToVisual(_canvas!).TransformPoint(point);
            }
            else if (_hasExternalViewport)
            {
                mapped = canvasToViewport
                    ? new Windows.Foundation.Point(point.X - _externalViewport.X, point.Y - _externalViewport.Y)
                    : new Windows.Foundation.Point(point.X + _externalViewport.X, point.Y + _externalViewport.Y);
            }
            else
            {
                mapped = canvasToViewport
                    ? _canvas!.TransformToVisual(this).TransformPoint(point)
                    : this.TransformToVisual(_canvas!).TransformPoint(point);
            }
            return double.IsFinite(mapped.X) && double.IsFinite(mapped.Y);
        }
        catch (Exception)
        {
            mapped = default;
            return false;
        }
    }

    private void PrepareContextGestureForDeferral(ref PointerInput input)
    {
        if (input.ContextCaptureEnded || input.Kind is not (PointerInputKind.DoubleTap or PointerInputKind.Hold))
            return;

        ClearHorizontalOverflowPointerState();
        input.ResumeRebuildAfterInteraction = true;
        EndCapturedSelectionForContextGesture(ref input);
    }

    private bool IsDeferredGestureBandCovered(LayoutSnapshot snapshot, LazyLayoutBand band)
    {
        bool active = _lazyLayoutCts is { IsCancellationRequested: false } &&
            ReferenceEquals(snapshot, _activeLazyLayoutSnapshot) &&
            _activeLazyLayoutRevision == snapshot.LayoutRevision &&
            band.Top >= _activeLazyLayoutBand.Top && band.Bottom <= _activeLazyLayoutBand.Bottom;
        bool pending = _hasPendingLazyLayoutBand &&
            ReferenceEquals(snapshot, _pendingLazyLayoutSnapshot) &&
            band.Top >= _pendingLazyLayoutBand.Top && band.Bottom <= _pendingLazyLayoutBand.Bottom;
        return active || pending;
    }

    private void RequestDeferredGestureBandRealization(LayoutSnapshot snapshot, LazyLayoutBand hitBand)
    {
        if (_isDisposed || _isUnloaded || !ReferenceEquals(snapshot, _snapshot) ||
            !ReferenceEquals(snapshot, _deferredPointerSnapshot))
        {
            DropDeferredInputQueuePreservingSelection();
            return;
        }

        if (snapshot.GeometryRecoveryRequired || RequestGeometryRecoveryIfRequired(snapshot))
        {
            DropDeferredInputQueuePreservingSelection();
            return;
        }
        if (IsDeferredGestureBandCovered(snapshot, hitBand))
        {
            _pointerRetryTimer?.Stop();
            return;
        }

        LazyLayoutBand viewportBand = hitBand;
        if (TryGetViewport(out double viewportTop, out double viewportHeight, out _) &&
            double.IsFinite(viewportTop) && double.IsFinite(viewportHeight) &&
            viewportHeight > 0 && viewportHeight <= 6000)
        {
            viewportBand = LazyLayoutBand.FromVisibleViewport(viewportTop, viewportHeight);
        }
        var requestedBand = new LazyLayoutBand(
            Math.Min(hitBand.Top, viewportBand.Top),
            Math.Max(hitBand.Bottom, viewportBand.Bottom));
        QueueLazyLayoutForBand(
            snapshot,
            requestedBand,
            viewportBand,
            preserveScrollAnchor: _scroll is not null);

        if (IsDeferredGestureBandCovered(snapshot, hitBand))
        {
            _pointerRetryTimer?.Stop();
            return;
        }

        // QueueLazyLayoutForBand may decline the request when its recovery
        // clock/dispatcher handoff is unavailable. Do not retain a waiter that
        // can no longer be made ready; reset only the queued inputs/captures,
        // never the user's current selection.
        DropDeferredInputQueuePreservingSelection();
    }

    private void DropDeferredInputQueuePreservingSelection()
    {
        ResetDeferredPointerInput();
        ResumePendingRebuildAfterPointerInteraction();
    }

    private void OnDeferredGestureLayoutCompletion(LayoutSnapshot snapshot, bool succeeded)
    {
        if (_deferredPointerInput is not { Count: > 0 } ||
            !ReferenceEquals(_deferredPointerSnapshot, snapshot))
        {
            return;
        }

        if (!ReferenceEquals(_snapshot, snapshot))
        {
            DropDeferredInputQueuePreservingSelection();
            return;
        }

        if (succeeded)
        {
            if (!IsDeferredGestureLayoutPublished(snapshot))
            {
                // The worker may publish a newer measured mask before its UI
                // realization plans and restored anchor are applied. Wait for
                // ApplyLazyLayoutCommit to signal that publication boundary.
                _pointerRetryTimer?.Stop();
                return;
            }
            _pointerRetryTimer ??= CreatePointerRetryTimer();
            _pointerRetryTimer.Start();
            return;
        }

        // A superseding request can own the next retry. Otherwise a terminal
        // failure/cancellation would leave a stopped logical waiter forever.
        if ((_lazyLayoutCts is { IsCancellationRequested: false } &&
             ReferenceEquals(snapshot, _activeLazyLayoutSnapshot)) ||
            (_hasPendingLazyLayoutBand && ReferenceEquals(snapshot, _pendingLazyLayoutSnapshot)) ||
            (_lazyLayoutDispatchQueued && ReferenceEquals(snapshot, _pendingLazyLayoutSnapshot)))
        {
            _pointerRetryTimer?.Stop();
            return;
        }

        DropDeferredInputQueuePreservingSelection();
    }

    private void DropDeferredInputHead(DispatcherQueueTimer sender)
    {
        if (_deferredPointerInput?.TryDequeue(out _) != true)
        {
            FinishDeferredPointerInputDrain(sender);
            return;
        }

        if (_deferredPointerInput.Count == 0)
        {
            FinishDeferredPointerInputDrain(sender);
            return;
        }

        sender.Start();
    }

    private void DispatchPointerInput(PointerInputKind kind, object sender, PointerRoutedEventArgs args)
    {
        if (_isDisposed || _isUnloaded || _resettingPointerInput || _canvas is null || _snapshot is not { } snapshot)
            return;
        PointerPoint point = args.GetCurrentPoint(_canvas);
        kind = NormalizeCanceledPointerInputKind(kind, point.Properties.IsCanceled);
        if ((kind is PointerInputKind.Cancel or PointerInputKind.Exit) &&
            args.Pointer.PointerId == _contextCaptureReleaseInFlightId &&
            _contextCaptureReleaseInFlightId != 0)
        {
            return;
        }

        if (RequestGeometryRecoveryIfRequired(snapshot))
            return;

        // Hover moves do not own document geometry. Contact/handle starts and
        // scrolling invalidate a root measure captured before UI dispatch;
        // deferred presses count too, before they can capture or hit-test.
        if (kind is PointerInputKind.Press or PointerInputKind.HandlePress or PointerInputKind.Wheel)
            snapshot.NotifyPointerInput();

        var input = new PointerInput
        {
            Kind = kind,
            Sender = sender,
            Pointer = args.Pointer,
            Point = point,
            TouchTapGestureEpoch = args.Pointer.PointerDeviceType is PointerDeviceType.Touch or PointerDeviceType.Pen
                ? _touchTapSuppression.GetGestureEpochForPointer(args.Pointer.PointerId)
                : 0,
            Modifiers = args.KeyModifiers,
            LinkModifiers = MapInputModifiers(args.KeyModifiers),
            InteractionCanvas = _canvas,
            TickCount = Environment.TickCount64,
            Handled = args.Handled,
        };
        // Vertical wheel/pan ownership remains with the page ScrollViewer.
        if (kind == PointerInputKind.Wheel && !input.Point.Properties.IsHorizontalMouseWheel &&
            (input.Modifiers & VirtualKeyModifiers.Shift) == 0)
            return;

        // A new contact must never be rejected by a handle drag whose
        // capture was silently lost; retire it before the press is processed.
        // Retirement refreshes handles from layout, so it needs the snapshot
        // lease; when the worker holds it the per-frame check retries.
        if (kind is PointerInputKind.Press or PointerInputKind.HandlePress &&
            _selectionHandlePointerId != 0 && snapshot.TryBeginInteraction())
        {
            try { RetireSilentlyLostSelectionHandleCapture(); }
            finally { snapshot.EndInteraction(); }
        }

        bool startsNewCapturedOwnership = kind == PointerInputKind.HandlePress ||
            (kind == PointerInputKind.Press && input.Point.IsInContact &&
             (input.Pointer.PointerDeviceType is PointerDeviceType.Touch or PointerDeviceType.Pen ||
              IsPrecisePointer(input) && input.Point.Properties.IsLeftButtonPressed &&
              !input.Point.Properties.IsBarrelButtonPressed));

        if (!_drainingPointerInput && (_deferredPointerInput?.Count ?? 0) == 0 && snapshot.TryBeginInteraction())
        {
            long snapshotGeneration = Volatile.Read(ref _snapshotGeneration);
            long layoutRevision = snapshot.LayoutRevision;
            try
            {
                input.InteractionSnapshot = snapshot;
                ProcessPointerInput(ref input);
            }
            finally
            {
                input.InteractionSnapshot = null;
                snapshot.EndInteraction();
            }
            if (input.HasPostInteractionWork)
            {
                CompletePointerPostInteraction(
                    ref input,
                    snapshot,
                    snapshotGeneration,
                    layoutRevision);
            }
            args.Handled = input.Handled;
            return;
        }

        if (RequestGeometryRecoveryIfRequired(snapshot))
            return;

        // A new captured physical owner cannot be queued behind a geometry
        // waiter: the capture itself freezes the tree the waiter needs. Cancel
        // only the logical waiter, preserving the existing selection, then
        // retry this fresh input against the still-current snapshot.
        if (startsNewCapturedOwnership &&
            _deferredPointerInput is { Count: > 0, HasBlockingSnapshotInteractions: false })
        {
            long generationBeforeWaiterDrop = Volatile.Read(ref _snapshotGeneration);
            DropDeferredInputQueuePreservingSelection();
            if (_isDisposed || _isUnloaded || !ReferenceEquals(_snapshot, snapshot) ||
                generationBeforeWaiterDrop != Volatile.Read(ref _snapshotGeneration))
                return;

            if (snapshot.TryBeginInteraction())
            {
                long snapshotGeneration = Volatile.Read(ref _snapshotGeneration);
                long layoutRevision = snapshot.LayoutRevision;
                try
                {
                    input.InteractionSnapshot = snapshot;
                    ProcessPointerInput(ref input);
                }
                finally
                {
                    input.InteractionSnapshot = null;
                    snapshot.EndInteraction();
                }
                if (input.HasPostInteractionWork)
                {
                    CompletePointerPostInteraction(
                        ref input,
                        snapshot,
                        snapshotGeneration,
                        layoutRevision);
                }
                args.Handled = input.Handled;
                return;
            }
        }

        if (_deferredPointerSnapshot is not null && !ReferenceEquals(snapshot, _deferredPointerSnapshot))
            ResetDeferredPointerInput();
        _deferredPointerSnapshot = snapshot;
        // Capture in the original press event so a release outside the canvas is
        // delivered even if layout is still busy. Never capture a pending touch
        // pan on the canvas; explicit selection handles own their touch gesture.
        bool primaryPress = input.Point.IsInContact &&
            (kind == PointerInputKind.HandlePress ||
             (kind == PointerInputKind.Press && IsPrecisePointer(input) &&
              input.Point.Properties.IsLeftButtonPressed && !input.Point.Properties.IsBarrelButtonPressed));
        if (primaryPress)
        {
            UIElement target = kind == PointerInputKind.HandlePress && sender is UIElement handle ? handle : _canvas;
            if (CapturePointerForInput(target, input))
            {
                input.CapturedBeforeDeferral = true;
                _deferredPointerCaptures[input.Pointer.PointerId] = (target, input.Pointer);
            }
        }
        input.CapturedBeforeDeferral |= _deferredPointerCaptures.ContainsKey(input.Pointer.PointerId);
        bool hasSnapshotPointerOwner = input.CapturedBeforeDeferral ||
            (_pointerSession.IsActive && _pointerSession.PointerId == input.Pointer.PointerId) ||
            (_horizontalOverflowPointerId == input.Pointer.PointerId && _horizontalOverflowPointerId != 0) ||
            _selectionHandlePointerId == input.Pointer.PointerId;
        bool blocksSnapshotInteraction = kind is not (
            PointerInputKind.Move or PointerInputKind.Release or PointerInputKind.Cancel or PointerInputKind.Exit) ||
            hasSnapshotPointerOwner;
        _deferredPointerInput ??= new DeferredInputQueue<PointerInput>();
        if (!_deferredPointerInput.TryEnqueue(input, input.Pointer.PointerId,
                kind is PointerInputKind.Move or PointerInputKind.HandleMove,
                blocksSnapshotInteraction: blocksSnapshotInteraction))
        {
            if (!_deferredPointerInput.HasBlockingSnapshotInteractions)
                DropDeferredInputQueuePreservingSelection();
            else
                CancelDeferredGesture();
            return;
        }
        args.Handled |= input.CapturedBeforeDeferral || kind == PointerInputKind.Wheel;
        _pointerRetryTimer ??= CreatePointerRetryTimer();
        _pointerRetryTimer.Start();
    }

    private DispatcherQueueTimer CreatePointerRetryTimer()
    {
        var timer = DispatcherQueue.CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(16);
        timer.Tick += OnPointerRetryTimer;
        return timer;
    }

    private void OnPointerRetryTimer(DispatcherQueueTimer sender, object args)
    {
        if (!ReferenceEquals(sender, _pointerRetryTimer))
            return;

        if (_isDisposed || _isUnloaded || !ReferenceEquals(_snapshot, _deferredPointerSnapshot))
        {
            ResetDeferredPointerInput();
            return;
        }
        while (_snapshot is { } snapshot && ReferenceEquals(snapshot, _deferredPointerSnapshot))
        {
            if (_isDisposed || _isUnloaded)
            {
                ResetDeferredPointerInput();
                return;
            }
            if (RequestGeometryRecoveryIfRequired(snapshot))
            {
                DropDeferredInputQueuePreservingSelection();
                return;
            }
            if (_deferredPointerInput?.TryPeek(out _, out _) != true)
            {
                FinishDeferredPointerInputDrain(sender);
                return;
            }
            if (!snapshot.TryBeginInteraction())
                return;

            long snapshotGeneration = Volatile.Read(ref _snapshotGeneration);
            long layoutRevision = snapshot.LayoutRevision;
            PointerInput? postInteractionInput = null;
            bool waitingForGeometry = false;
            bool waitingForLayoutPublication = false;
            bool staleQueue = false;
            LazyLayoutBand waitingBand = default;
            try
            {
                try
                {
                    _drainingPointerInput = true;
                    while (_deferredPointerInput?.TryPeek(out var input, out _) == true)
                    {
                        if (_isDisposed || _isUnloaded ||
                            !ReferenceEquals(_snapshot, snapshot) ||
                            !ReferenceEquals(_deferredPointerSnapshot, snapshot))
                        {
                            staleQueue = true;
                            break;
                        }

                        if (input.GestureSnapshotGeneration != 0 &&
                            input.GestureSnapshotGeneration != snapshotGeneration)
                        {
                            staleQueue = true;
                            break;
                        }

                        if (input.DeferredTouchGesturePointerId != 0 &&
                            !ResolveDeferredTouchGesture(ref input))
                        {
                            _deferredPointerInput.TryDequeue(out _);
                            continue;
                        }

                        if (input.WaitForMeasuredHitTest && !input.ContextCaptureEnded)
                            PrepareContextGestureForDeferral(ref input);

                        if (input.IsDeferredGeometryWait)
                        {
                            bool hasBand = TryGetGestureAdmissionBand(
                                ref input,
                                snapshot,
                                out waitingBand,
                                out bool bandReady);
                            if (input.InvalidDeferredProjection)
                            {
                                _deferredPointerInput.TryDequeue(out _);
                                if (input.HasPostInteractionWork)
                                {
                                    postInteractionInput = input;
                                    break;
                                }
                                continue;
                            }
                            if (input.WaitForLayoutPublication)
                            {
                                _deferredPointerInput.TryReplaceHead(
                                    input,
                                    blocksSnapshotInteraction: false);
                                waitingForLayoutPublication = true;
                                if (input.HasPostInteractionWork)
                                    postInteractionInput = input;
                                break;
                            }
                            if (hasBand && !bandReady)
                            {
                                _deferredPointerInput.TryReplaceHead(
                                    input,
                                    blocksSnapshotInteraction: false);
                                waitingForGeometry = true;
                                if (input.HasPostInteractionWork)
                                    postInteractionInput = input;
                                break;
                            }
                            if (!input.WaitForMeasuredHitTest &&
                                !TryReprojectDeferredGesture(ref input))
                            {
                                _deferredPointerInput.TryDequeue(out _);
                                if (input.HasPostInteractionWork)
                                {
                                    postInteractionInput = input;
                                    break;
                                }
                                continue;
                            }
                            input.WaitForMeasuredHitTest = false;
                        }

                        _deferredPointerInput.TryDequeue(out _);

                        input.GestureLayoutRevision = layoutRevision;
                        input.InteractionSnapshot = snapshot;
                        try
                        {
                            ProcessPointerInput(ref input);
                        }
                        finally
                        {
                            input.InteractionSnapshot = null;
                        }
                        if (input.ShowContextMenuAfterInteraction || input.HasPostInteractionWork)
                        {
                            postInteractionInput = input;
                            break;
                        }
                    }
                }
                finally
                {
                    _drainingPointerInput = false;
                    snapshot.EndInteraction();
                }
            }
            catch
            {
                FinishDeferredPointerInputDrain(sender);
                throw;
            }

            if (!waitingForGeometry && !waitingForLayoutPublication &&
                postInteractionInput is { } menuInput)
            {
                if (menuInput.ShowContextMenuAfterInteraction)
                {
                    _ = CompleteGestureAfterSnapshotInteraction(
                        menuInput,
                        snapshot,
                        snapshotGeneration,
                        layoutRevision);
                }
                else
                {
                    CompletePointerPostInteraction(
                        ref menuInput,
                        snapshot,
                        snapshotGeneration,
                        layoutRevision);
                }
                if (_isDisposed || _isUnloaded ||
                    !ReferenceEquals(_snapshot, snapshot) ||
                    !ReferenceEquals(_deferredPointerSnapshot, snapshot) ||
                    snapshotGeneration != Volatile.Read(ref _snapshotGeneration) ||
                    snapshot.LayoutRevision != layoutRevision)
                {
                    ResetDeferredPointerInput();
                    return;
                }
                if ((_deferredPointerInput?.Count ?? 0) == 0)
                {
                    FinishDeferredPointerInputDrain(sender);
                    return;
                }
                // Process later queued input only after the context menu has
                // been opened without the snapshot lease. This preserves the
                // FIFO ordering of gestures even when layout had to defer them.
                continue;
            }

            if (staleQueue)
            {
                DropDeferredInputQueuePreservingSelection();
                return;
            }

            if (waitingForLayoutPublication)
            {
                sender.Stop();
                if (postInteractionInput is { } deferredPost &&
                    deferredPost.HasPostInteractionWork &&
                    !CompletePointerPostInteraction(
                        ref deferredPost,
                        snapshot,
                        snapshotGeneration,
                        layoutRevision))
                {
                    DropDeferredInputQueuePreservingSelection();
                    return;
                }
                if (IsDeferredGestureLayoutPublished(snapshot))
                    sender.Start();
                return;
            }

            if (waitingForGeometry)
            {
                sender.Stop();
                if (postInteractionInput is { } deferredPost &&
                    deferredPost.HasPostInteractionWork &&
                    !CompletePointerPostInteraction(
                        ref deferredPost,
                        snapshot,
                        snapshotGeneration,
                        layoutRevision))
                {
                    DropDeferredInputQueuePreservingSelection();
                    return;
                }
                if (!_isDisposed && !_isUnloaded &&
                    ReferenceEquals(_snapshot, snapshot) &&
                    snapshotGeneration == Volatile.Read(ref _snapshotGeneration))
                {
                    RequestDeferredGestureBandRealization(snapshot, waitingBand);
                }
                else
                {
                    DropDeferredInputQueuePreservingSelection();
                }
                return;
            }

            if (_isDisposed || _isUnloaded ||
                !ReferenceEquals(_snapshot, snapshot) ||
                !ReferenceEquals(_deferredPointerSnapshot, snapshot))
            {
                ResetDeferredPointerInput();
                return;
            }

            if ((_deferredPointerInput?.Count ?? 0) == 0)
            {
                FinishDeferredPointerInputDrain(sender);
                return;
            }
        }
    }

    private void FinishDeferredPointerInputDrain(DispatcherQueueTimer sender)
    {
        sender.Stop();
        _deferredPointerSnapshot = null;
        _deferredPointerInput?.Clear();
        // A successfully started live gesture now owns its capture. Release
        // speculative captures only for misses or gestures already ended.
        var captures = new List<(UIElement Target, Pointer Pointer)>(_deferredPointerCaptures.Values);
        _deferredPointerCaptures.Clear();
        foreach (var capture in captures)
        {
            uint id = capture.Pointer.PointerId;
            if ((_pointerSession.IsActive && _pointerSession.PointerId == id) ||
                (_horizontalOverflowCaptured && _horizontalOverflowPointerId == id) ||
                _selectionHandlePointerId == id)
                continue;
            capture.Target.ReleasePointerCapture(capture.Pointer);
        }
        ResumePendingRebuildAfterPointerInteraction();
    }

    private bool CompleteGestureAfterSnapshotInteraction(
        PointerInput input,
        LayoutSnapshot snapshot,
        long snapshotGeneration,
        long layoutRevision)
    {
        if (!input.ShowContextMenuAfterInteraction)
            return input.Handled;

        if (_isDisposed || _isUnloaded ||
            !ReferenceEquals(_snapshot, snapshot) ||
            snapshotGeneration != Volatile.Read(ref _snapshotGeneration) ||
            input.GestureLayoutRevision != layoutRevision ||
            snapshot.LayoutRevision != layoutRevision)
        {
            return false;
        }

        return ShowSelectionContextMenu(
            input.GesturePosition,
            touchSelection: input.TouchSelectionContextMenu,
            requiredSnapshot: snapshot,
            requiredSnapshotGeneration: snapshotGeneration,
            requiredLayoutRevision: layoutRevision);
    }

    private bool CompletePointerPostInteraction(
        ref PointerInput input,
        LayoutSnapshot snapshot,
        long snapshotGeneration,
        long layoutRevision)
    {
        bool wasDraining = _drainingPointerInput;
        _drainingPointerInput = true;
        try
        {
            if (!IsCurrentPointerInteraction(snapshot, snapshotGeneration, layoutRevision) ||
                (input.ReleaseCanvasCaptureAfterInteraction &&
                 (input.InteractionCanvas is not { } capturedCanvas ||
                  !ReferenceEquals(_canvas, capturedCanvas))))
            {
                ResetDeferredPointerInput();
                return false;
            }

            // Pointer capture release can synchronously route CaptureLost. It
            // must run after the snapshot lease, and reentrant pointer input
            // must join the existing FIFO queue instead of nesting here.
            if (input.ReleaseCanvasCaptureAfterInteraction)
            {
                UIElement captureTarget = input.InteractionCanvas!;
                if (input.ReleasePointerIdAfterInteraction == 0)
                {
                    captureTarget.ReleasePointerCapture(input.Pointer);
                }
                else if (captureTarget.PointerCaptures is { } captures)
                {
                    foreach (Pointer capturedPointer in captures)
                    {
                        if (capturedPointer.PointerId == input.ReleasePointerIdAfterInteraction)
                        {
                            _contextCaptureReleaseInFlightId = input.ReleasePointerIdAfterInteraction;
                            try
                            {
                                captureTarget.ReleasePointerCapture(capturedPointer);
                            }
                            finally
                            {
                                _contextCaptureReleaseInFlightId = 0;
                            }
                            break;
                        }
                    }
                }
                if (!IsCurrentPointerInteraction(snapshot, snapshotGeneration, layoutRevision) ||
                    !ReferenceEquals(_canvas, input.InteractionCanvas))
                {
                    ResetDeferredPointerInput();
                    return false;
                }
            }

            if (input.ResumeRebuildAfterInteraction)
            {
                ResumePendingRebuildAfterPointerInteraction();
                if (!IsCurrentPointerInteraction(snapshot, snapshotGeneration, layoutRevision))
                {
                    ResetDeferredPointerInput();
                    return false;
                }
            }

            if (input.ClearSelectionAfterInteraction)
            {
                ClearSelectionForExternalInteraction(resetClickTracking: false);
                if (!IsCurrentPointerInteraction(snapshot, snapshotGeneration, layoutRevision))
                {
                    ResetDeferredPointerInput();
                    return false;
                }
            }

            if (input.RefreshSelectionAdornerAfterInteraction)
            {
                UpdateSelectionAdornerViewport();
                InvalidateSelectionAdorner();
            }

            if (!IsCurrentPointerInteraction(snapshot, snapshotGeneration, layoutRevision))
            {
                ResetDeferredPointerInput();
                return false;
            }

            if (input.TryGetLinkActivation(out LinkActivationIntent activation))
                ActivateLinkIntent(
                    activation,
                    snapshot,
                    snapshotGeneration,
                    layoutRevision);

            bool stillCurrent = IsCurrentPointerInteraction(snapshot, snapshotGeneration, layoutRevision);
            if (!stillCurrent)
                ResetDeferredPointerInput();
            return stillCurrent;
        }
        finally
        {
            _drainingPointerInput = wasDraining;
        }
    }

    private bool IsCurrentPointerInteraction(
        LayoutSnapshot snapshot,
        long snapshotGeneration,
        long layoutRevision) =>
        !_isDisposed && !_isUnloaded &&
        ReferenceEquals(_snapshot, snapshot) &&
        snapshotGeneration == Volatile.Read(ref _snapshotGeneration) &&
        snapshot.LayoutRevision == layoutRevision;

    private void ProcessPointerInput(ref PointerInput input)
    {
        if (input.IgnoreUnattributableTouchGesture)
            return;

        if (input.DeferredTouchGesturePointerId != 0 &&
            !ResolveDeferredTouchGesture(ref input))
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
            case PointerInputKind.Press: ProcessPointerPressed(ref input); break;
            case PointerInputKind.Move: ProcessPointerMoved(ref input); break;
            case PointerInputKind.Release: ProcessPointerReleased(ref input); break;
            case PointerInputKind.Cancel: ProcessPointerCanceledOrCaptureLost(ref input); break;
            case PointerInputKind.Exit: ProcessPointerExited(ref input); break;
            case PointerInputKind.Wheel: ProcessPointerWheelChanged(ref input); break;
            case PointerInputKind.HandlePress: ProcessSelectionHandlePointerPressed(ref input); break;
            case PointerInputKind.HandleMove: ProcessSelectionHandlePointerMoved(ref input); break;
            case PointerInputKind.HandleRelease: ProcessSelectionHandlePointerReleased(ref input); break;
            case PointerInputKind.HandleCancel: ProcessSelectionHandlePointerCanceled(ref input); break;
            case PointerInputKind.Tap: ProcessTapped(ref input); break;
            case PointerInputKind.DoubleTap: ProcessDoubleTapped(ref input); break;
            case PointerInputKind.Hold: ProcessHolding(ref input); break;
            case PointerInputKind.RightTap: ProcessRightTapped(ref input); break;
        }
    }

    private bool ResolveDeferredTouchGesture(ref PointerInput input)
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
            if (TryMapCanvasContactToViewport(input.GestureContact, out TouchContactGeometry viewportContact))
            {
                input.ViewportGestureContact = viewportContact;
                input.HasViewportGestureContact = true;
            }
        }

        return true;
    }

    private void CancelDeferredGesture()
    {
        ResetDeferredPointerInput();
        _pointerSession = default;
        _selectionAnchor = null;
        ClearHorizontalOverflowPointerState();
        ResetSelectionHandleDrag();
        _touchTapSuppression.Reset();
        _touchSelection.Reset();
        SetSelectionDragShieldActive(false);
        MarkdownDiagnostics.WriteLine("[MarkdownRendererControl] Cancelled an over-budget deferred pointer gesture.");
        ResumePendingRebuildAfterPointerInteraction();
    }

    private static bool CapturePointerForInput(UIElement target, PointerInput input)
    {
        // Pointer input can be replayed after a layout/image realization pass.
        // Do not ask WinUI for capture state once that target has left its tree.
        if (target.XamlRoot is null)
            return false;

        if (input.CapturedBeforeDeferral)
            return true;

        // ButtonBase can capture a touch contact in its class handler before our
        // handled-events-too selection-handle handler runs. Re-capturing the same
        // pointer may report false even though the intended target already owns it;
        // treat that existing capture as success so native Button behavior and the
        // renderer's drag state can cooperate.
        // Although the projected API is non-nullable, WinUI can surface a null
        // capture view while an element's input site is being initialized or
        // torn down. Treat it as the normal empty-capture state.
        IReadOnlyList<Pointer>? captures = target.PointerCaptures;
        if (ContainsPointerCapture(captures, input.Pointer.PointerId))
            return true;

        return target.CapturePointer(input.Pointer);
    }

    internal static bool ContainsPointerCapture(IReadOnlyList<Pointer>? captures, uint pointerId)
    {
        if (captures is null)
            return false;

        foreach (Pointer captured in captures)
        {
            if (captured.PointerId == pointerId)
                return true;
        }

        return false;
    }

    private void ResetDeferredPointerInput()
    {
        _resettingPointerInput = true;
        try
        {
            RetireTouchOwnerForDroppedTerminalInput();
            _deferredPointerInput?.Clear();
            _deferredPointerSnapshot = null;
            if (_pointerRetryTimer is { } timer)
            {
                _pointerRetryTimer = null;
                try { timer.Stop(); } catch (Exception) { }
                try { timer.Tick -= OnPointerRetryTimer; } catch (Exception) { }
            }
            foreach (var capture in _deferredPointerCaptures.Values)
            {
                try { capture.Target.ReleasePointerCapture(capture.Pointer); }
                catch (Exception)
                {
                    // A native input site may already be closed. Still release
                    // every other capture and clear the geometry-bound queue.
                }
            }
            _deferredPointerCaptures.Clear();
        }
        finally { _resettingPointerInput = false; }
    }

    private void RetireTouchOwnerForDroppedTerminalInput()
    {
        if (_deferredPointerInput is not { Count: > 0 } queue)
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
                ? CancelTouchOwnerIfCurrent(ownerPointerId, ownerGestureEpoch)
                : _touchTapSuppression.TryEndContact(ownerPointerId, ownerGestureEpoch);
            if (contactRetired)
            {
                if (isHandleTerminal)
                {
                    if (isCanceled)
                    {
                        ResetSelectionHandleDrag();
                        _touchSelection.CancelContact(ownerPointerId);
                    }
                    else
                    {
                        _touchSelection.CompleteHandleDrag(ownerPointerId);
                        ResetSelectionHandleDrag();
                    }
                    UpdateSelectionHandles();
                }
                else if (isCanceled)
                {
                    TouchSelectionCancelResult cancelResult = _touchSelection.CancelContact(ownerPointerId);
                    if (cancelResult != TouchSelectionCancelResult.NotOwner)
                    {
                        CloseSelectionContextMenu();
                        UpdateSelectionHandles();
                        if (cancelResult == TouchSelectionCancelResult.ClearSelection && _selection.IsActive)
                            ClearSelectionForExternalInteraction(resetDeferredPointerInput: false);
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

    private bool CancelTouchOwnerIfCurrent(uint pointerId, long gestureEpoch)
        => _touchTapSuppression.TryCancelContact(pointerId, gestureEpoch);
}
