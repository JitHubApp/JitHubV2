using System;
using System.Collections.Generic;
using MarkdownRenderer.Diagnostics;
using MarkdownRenderer.Layout;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using VirtualKeyModifiers = Windows.System.VirtualKeyModifiers;
using MarkdownRenderer.Controls.Input;

namespace MarkdownRenderer.Controls;

public partial class MarkdownRendererControl
{
    // A pending horizontal touch contact is not captured until it becomes a
    // drag, but it already owns a table/snapshot identity. Selection handles
    // and queued input likewise must finish against their original geometry.
    // Read only on the UI dispatcher, including the final publication fence.
    private bool HasSnapshotPointerInteraction =>
        _document.HasActiveSession || _horizontalOverflowDrag.PointerId != 0 ||
        _handles.DragPointerId != 0 || _drainingPointerInput ||
        _deferredPointerCaptures.Count != 0 ||
        (_deferredPointerInput?.HasBlockingSnapshotInteractions ?? false);

    private DeferredInputQueue<PointerInput>? _deferredPointerInput;
    private LayoutSnapshot? _deferredPointerSnapshot;
    private DispatcherQueueTimer? _pointerRetryTimer;
    private bool _drainingPointerInput;
    private bool _resettingPointerInput;
    private uint _contextCaptureReleaseInFlightId;
    private readonly Dictionary<uint, (UIElement Target, Pointer Pointer)> _deferredPointerCaptures = new();

    private void OnPointerPressed(object sender, PointerRoutedEventArgs e) => DispatchPointerInput(PointerInputKind.Press, sender, e);
    private void OnPointerMoved(object sender, PointerRoutedEventArgs e) => DispatchPointerInput(PointerInputKind.Move, sender, e);
    private void OnPointerReleased(object sender, PointerRoutedEventArgs e) => DispatchPointerInput(PointerInputKind.Release, sender, e);
    private void OnPointerCanceledOrCaptureLost(object sender, PointerRoutedEventArgs e) => DispatchPointerInput(PointerInputKind.Cancel, sender, e);
    private void OnPointerExited(object sender, PointerRoutedEventArgs e) => DispatchPointerInput(PointerInputKind.Exit, sender, e);
    private void OnPointerWheelChanged(object sender, PointerRoutedEventArgs e) => DispatchPointerInput(PointerInputKind.Wheel, sender, e);

    private bool HasQueuedPointerInput(uint pointerId)
    {
        if (_deferredPointerInput is not { Count: > 0 } queue)
            return false;
        for (int index = 0; index < queue.Count; index++)
        {
            if (queue.TryGetAt(index, out PointerInput queued) && queued.Pointer?.PointerId == pointerId)
                return true;
        }
        return false;
    }

    private bool DispatchRecognizedGesture(PointerInputKind kind, Windows.Foundation.Point point, PointerDeviceType device)
    {
        if (_lifetime.IsDisposed || _lifetime.IsUnloaded || _surface.Canvas is null || _published.Snapshot is not { } snapshot)
            return false;
        if (RequestGeometryRecoveryIfRequired(snapshot))
            return false;
        snapshot.NotifyPointerInput();
        var input = new PointerInput
        {
            Kind = kind,
            GesturePosition = point,
            GestureDevice = device,
            LinkModifiers = InputModifierState.GetCurrentInputModifiers(),
            GestureSnapshotGeneration = _published.Generation,
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
        input.HasViewportGesturePosition = _viewport.TryMapCanvasToViewport(point, out input.ViewportGesturePosition);
        if (input.HasGestureContact &&
            _viewport.TryMapCanvasContactToViewport(input.GestureContact, out TouchContactGeometry viewportContact))
        {
            input.ViewportGestureContact = viewportContact;
            input.HasViewportGestureContact = true;
        }
        input.WaitForMeasuredHitTest = IsMeasuredHitTestGesture(input);
        if (!_drainingPointerInput && (_deferredPointerInput?.Count ?? 0) == 0 && snapshot.TryBeginInteraction())
        {
            long snapshotGeneration = _published.Generation;
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
            _touchTapSuppression.ShouldSuppressTap(input.TouchTapGestureEpoch, Environment.TickCount64, input.GesturePosition))
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
        return GestureHitTestBands.TryCreateGestureHitTestBand(
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
        if (!GestureHitTestBands.IncludesContactProbes(input) ||
            _document.IsSelectableTouchPoint(input.GesturePosition, snapshot, out _))
        {
            return true;
        }

        double scale = XamlRoot?.RasterizationScale ?? 1;
        if (!GestureHitTestBands.TryCreateGestureHitTestBand(
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
        !_lifetime.IsDisposed && !_lifetime.IsUnloaded &&
        ReferenceEquals(snapshot, _published.Snapshot) &&
        !_publishingLayoutCommit &&
        _pendingLayoutCommit is null &&
        _appliedLazyLayoutRevision >= snapshot.LayoutRevision;

    private bool TryReprojectDeferredGesture(ref PointerInput input)
    {
        if (!input.HasViewportGesturePosition ||
            !_viewport.TryMapViewportToCanvas(input.ViewportGesturePosition, out var currentPosition))
        {
            input.InvalidDeferredProjection = true;
            return false;
        }
        input.GesturePosition = currentPosition;
        if (input.HasViewportGestureContact &&
            _viewport.TryMapViewportContactToCanvas(input.ViewportGestureContact, out TouchContactGeometry contact))
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

    private void PrepareContextGestureForDeferral(ref PointerInput input)
    {
        if (input.ContextCaptureEnded || input.Kind is not (PointerInputKind.DoubleTap or PointerInputKind.Hold))
            return;

        _horizontalOverflowDrag.Clear();
        input.ResumeRebuildAfterInteraction = true;
        _document.EndCapturedSessionForContextGesture(ref input);
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
        if (_lifetime.IsDisposed || _lifetime.IsUnloaded || !ReferenceEquals(snapshot, _published.Snapshot) ||
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
        if (_viewport.TryGet(out double viewportTop, out double viewportHeight, out _) &&
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
            preserveScrollAnchor: _surface.Scroll is not null);

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

        if (!ReferenceEquals(_published.Snapshot, snapshot))
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
        if (_lifetime.IsDisposed || _lifetime.IsUnloaded || _resettingPointerInput || _surface.Canvas is null || _published.Snapshot is not { } snapshot)
            return;
        PointerPoint point = args.GetCurrentPoint(_surface.Canvas);
        kind = PointerInputClassification.NormalizeCanceledPointerInputKind(kind, point.Properties.IsCanceled);
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
            LinkModifiers = InputModifierState.MapInputModifiers(args.KeyModifiers),
            InteractionCanvas = _surface.Canvas,
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
            _handles.DragPointerId != 0 && snapshot.TryBeginInteraction())
        {
            try { _handles.RetireSilentlyLostCapture(); }
            finally { snapshot.EndInteraction(); }
        }

        bool startsNewCapturedOwnership = kind == PointerInputKind.HandlePress ||
            (kind == PointerInputKind.Press && input.Point.IsInContact &&
             (input.Pointer.PointerDeviceType is PointerDeviceType.Touch or PointerDeviceType.Pen ||
              PointerInputClassification.IsPrecisePointer(input) && input.Point.Properties.IsLeftButtonPressed &&
              !input.Point.Properties.IsBarrelButtonPressed));

        if (!_drainingPointerInput && (_deferredPointerInput?.Count ?? 0) == 0 && snapshot.TryBeginInteraction())
        {
            long snapshotGeneration = _published.Generation;
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
            long generationBeforeWaiterDrop = _published.Generation;
            DropDeferredInputQueuePreservingSelection();
            if (_lifetime.IsDisposed || _lifetime.IsUnloaded || !ReferenceEquals(_published.Snapshot, snapshot) ||
                generationBeforeWaiterDrop != _published.Generation)
                return;

            if (snapshot.TryBeginInteraction())
            {
                long snapshotGeneration = _published.Generation;
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
             (kind == PointerInputKind.Press && PointerInputClassification.IsPrecisePointer(input) &&
              input.Point.Properties.IsLeftButtonPressed && !input.Point.Properties.IsBarrelButtonPressed));
        if (primaryPress)
        {
            UIElement target = kind == PointerInputKind.HandlePress && sender is UIElement handle ? handle : _surface.Canvas;
            if (PointerInputClassification.CapturePointerForInput(target, input))
            {
                input.CapturedBeforeDeferral = true;
                _deferredPointerCaptures[input.Pointer.PointerId] = (target, input.Pointer);
            }
        }
        input.CapturedBeforeDeferral |= _deferredPointerCaptures.ContainsKey(input.Pointer.PointerId);
        bool hasSnapshotPointerOwner = input.CapturedBeforeDeferral ||
            _document.IsSessionPointer(input.Pointer.PointerId) ||
            (_horizontalOverflowDrag.PointerId == input.Pointer.PointerId && _horizontalOverflowDrag.PointerId != 0) ||
            _handles.DragPointerId == input.Pointer.PointerId;
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

        if (_lifetime.IsDisposed || _lifetime.IsUnloaded || !ReferenceEquals(_published.Snapshot, _deferredPointerSnapshot))
        {
            ResetDeferredPointerInput();
            return;
        }
        while (_published.Snapshot is { } snapshot && ReferenceEquals(snapshot, _deferredPointerSnapshot))
        {
            if (_lifetime.IsDisposed || _lifetime.IsUnloaded)
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

            long snapshotGeneration = _published.Generation;
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
                        if (_lifetime.IsDisposed || _lifetime.IsUnloaded ||
                            !ReferenceEquals(_published.Snapshot, snapshot) ||
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
                if (_lifetime.IsDisposed || _lifetime.IsUnloaded ||
                    !ReferenceEquals(_published.Snapshot, snapshot) ||
                    !ReferenceEquals(_deferredPointerSnapshot, snapshot) ||
                    snapshotGeneration != _published.Generation ||
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
                if (!_lifetime.IsDisposed && !_lifetime.IsUnloaded &&
                    ReferenceEquals(_published.Snapshot, snapshot) &&
                    snapshotGeneration == _published.Generation)
                {
                    RequestDeferredGestureBandRealization(snapshot, waitingBand);
                }
                else
                {
                    DropDeferredInputQueuePreservingSelection();
                }
                return;
            }

            if (_lifetime.IsDisposed || _lifetime.IsUnloaded ||
                !ReferenceEquals(_published.Snapshot, snapshot) ||
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
            if (_document.IsSessionPointer(id) ||
                (_horizontalOverflowDrag.IsCaptured && _horizontalOverflowDrag.PointerId == id) ||
                _handles.DragPointerId == id)
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

        if (_lifetime.IsDisposed || _lifetime.IsUnloaded ||
            !ReferenceEquals(_published.Snapshot, snapshot) ||
            snapshotGeneration != _published.Generation ||
            input.GestureLayoutRevision != layoutRevision ||
            snapshot.LayoutRevision != layoutRevision)
        {
            return false;
        }

        return _contextMenu.Show(
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
                  !ReferenceEquals(_surface.Canvas, capturedCanvas))))
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
                    !ReferenceEquals(_surface.Canvas, input.InteractionCanvas))
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
                _adorner.InvalidateGeometry();
                _adorner.Invalidate();
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
        !_lifetime.IsDisposed && !_lifetime.IsUnloaded &&
        ReferenceEquals(_published.Snapshot, snapshot) &&
        snapshotGeneration == _published.Generation &&
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
            if (_viewport.TryMapCanvasContactToViewport(input.GestureContact, out TouchContactGeometry viewportContact))
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
        _document.EndSession();
        _horizontalOverflowDrag.Clear();
        _handles.ResetDrag();
        _touchTapSuppression.Reset();
        _touchSelection.Reset();
        _dragShield.SetActive(false);
        MarkdownDiagnostics.WriteLine("[MarkdownRendererControl] Cancelled an over-budget deferred pointer gesture.");
        ResumePendingRebuildAfterPointerInteraction();
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
