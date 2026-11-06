using System;
using MarkdownRenderer.Controls.Selection;
using MarkdownRenderer.Diagnostics;
using MarkdownRenderer.Layout;
using MarkdownRenderer.Selection;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using VirtualKeyModifiers = Windows.System.VirtualKeyModifiers;

namespace MarkdownRenderer.Controls.Input;

/// <summary>
/// The single entry point for pointer input and recognized gestures on the
/// document. Input runs at once when the published snapshot's interaction
/// lease is free; otherwise it is queued against that exact snapshot, with
/// speculative pointer captures, and replayed in FIFO order by a retry timer.
/// </summary>
/// <remarks>
/// <para>
/// Post-interaction work, such as releasing a pointer capture, opening the
/// context menu or activating a link, can reenter XAML. It always runs after
/// the snapshot lease ends, and reentrant input joins the queue instead of
/// nesting inside it.
/// </para>
/// <para>
/// This file is longer than 500 lines because the queue, its retry timer and
/// the immediate path are one ordered state machine. Every exit path must end
/// the lease, the draining flag and the queue in the same order, so the drain
/// pass, its outcome handling and the immediate paths share this class's
/// queue state rather than being split across files.
/// Everything outside that state machine already has its own owner:
/// <see cref="DeferredGestureAdmission"/>, <see cref="RecognizedGestureAttribution"/>,
/// <see cref="PointerInputDispatcher"/>, <see cref="DroppedTouchTerminalRetirement"/>,
/// <see cref="PointerPostInteraction"/> and <see cref="DeferredPointerCaptures"/>.
/// </para>
/// </remarks>
internal sealed class PointerInputRouter
{
    private readonly IPointerInputRouterHost _host;
    private readonly IDeferredGestureLayoutHost _layout;
    private readonly DispatcherQueue _dispatcherQueue;
    private readonly MarkdownViewSurface _surface;
    private readonly MarkdownViewLifetime _lifetime;
    private readonly PublishedLayout _published;
    private readonly MarkdownViewport _viewport;
    private readonly MarkdownTouchSelectionSession _touchSelection;
    private readonly TouchTapSuppressionPolicy _touchTapSuppression;
    private readonly SelectionHandlesPresenter _handles;
    private readonly DocumentPointerInteraction _document;
    private readonly HorizontalOverflowPointerDrag _horizontalOverflowDrag;
    private readonly SelectionContextMenuPresenter _contextMenu;
    private readonly SelectionDragShield _dragShield;
    private readonly DeferredGestureAdmission _admission;
    private readonly RecognizedGestureAttribution _attribution;
    private readonly PointerInputDispatcher _dispatcher;
    private readonly DroppedTouchTerminalRetirement _droppedTerminals;
    private readonly PointerPostInteraction _postInteraction;
    private readonly DeferredPointerCaptures _deferredCaptures;

    private DeferredInputQueue<PointerInput>? _deferred;
    private LayoutSnapshot? _deferredSnapshot;
    private DispatcherQueueTimer? _retryTimer;
    private bool _draining;
    private bool _resetting;

    internal PointerInputRouter(
        IPointerInputRouterHost host,
        IDeferredGestureLayoutHost layout,
        DispatcherQueue dispatcherQueue,
        MarkdownViewSurface surface,
        MarkdownViewLifetime lifetime,
        PublishedLayout published,
        MarkdownViewport viewport,
        SelectionController selection,
        MarkdownTouchSelectionSession touchSelection,
        TouchTapSuppressionPolicy touchTapSuppression,
        SelectionHandlesPresenter handles,
        DocumentPointerInteraction document,
        HorizontalOverflowPointerDrag horizontalOverflowDrag,
        TouchGestureInteraction touchGestures,
        SelectionContextMenuPresenter contextMenu,
        SelectionAdornerPresenter adorner,
        SelectionDragShield dragShield)
    {
        _host = host;
        _layout = layout;
        _dispatcherQueue = dispatcherQueue;
        _surface = surface;
        _lifetime = lifetime;
        _published = published;
        _viewport = viewport;
        _touchSelection = touchSelection;
        _touchTapSuppression = touchTapSuppression;
        _handles = handles;
        _document = document;
        _horizontalOverflowDrag = horizontalOverflowDrag;
        _contextMenu = contextMenu;
        _dragShield = dragShield;
        _admission = new DeferredGestureAdmission(layout, surface, lifetime, published, viewport, touchTapSuppression, document);
        _attribution = new RecognizedGestureAttribution(viewport, touchSelection, touchTapSuppression);
        _dispatcher = new PointerInputDispatcher(_attribution, touchTapSuppression, document, horizontalOverflowDrag, handles, touchGestures);
        _droppedTerminals = new DroppedTouchTerminalRetirement(host, selection, touchSelection, touchTapSuppression, handles, contextMenu);
        _postInteraction = new PointerPostInteraction(host, surface, lifetime, published, adorner);
        _deferredCaptures = new DeferredPointerCaptures(document, horizontalOverflowDrag, handles);
    }

    /// <summary>
    /// True while any pointer owner holds the published snapshot's identity.
    /// </summary>
    /// <remarks>
    /// A pending horizontal touch contact is not captured until it becomes a
    /// drag, but it already owns a table/snapshot identity. Selection handles
    /// and queued input likewise must finish against their original geometry.
    /// Read only on the UI dispatcher, including the final publication fence.
    /// </remarks>
    internal bool HasSnapshotInteraction =>
        _document.HasActiveSession || _horizontalOverflowDrag.PointerId != 0 ||
        _handles.DragPointerId != 0 || _draining ||
        _deferredCaptures.Count != 0 ||
        (_deferred?.HasBlockingSnapshotInteractions ?? false);

    /// <summary>The snapshot the queued input was hit-tested against, if any.</summary>
    internal LayoutSnapshot? DeferredSnapshot => _deferredSnapshot;

    /// <summary>
    /// True when <paramref name="pointerId"/> holds a speculative capture or
    /// still has queued input.
    /// </summary>
    internal bool HasDeferredInputFor(uint pointerId)
    {
        if (_deferredCaptures.Contains(pointerId))
            return true;
        if (_deferred is not { Count: > 0 } queue)
            return false;
        for (int index = 0; index < queue.Count; index++)
        {
            if (queue.TryGetAt(index, out PointerInput queued) && queued.Pointer?.PointerId == pointerId)
                return true;
        }
        return false;
    }

    /// <summary>
    /// Dispatches a gesture WinUI recognized on the canvas. Returns whether
    /// the gesture was handled or queued.
    /// </summary>
    internal bool DispatchRecognizedGesture(PointerInputKind kind, Windows.Foundation.Point point, PointerDeviceType device)
    {
        if (_lifetime.IsDisposed || _lifetime.IsUnloaded || _surface.Canvas is null || _published.Snapshot is not { } snapshot)
            return false;
        if (_host.RequestGeometryRecoveryIfRequired(snapshot))
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
            _attribution.AttributeTouchGesture(ref input, point, _deferred);
        input.HasViewportGesturePosition = _viewport.TryMapCanvasToViewport(point, out input.ViewportGesturePosition);
        if (input.HasGestureContact &&
            _viewport.TryMapCanvasContactToViewport(input.GestureContact, out TouchContactGeometry viewportContact))
        {
            input.ViewportGestureContact = viewportContact;
            input.HasViewportGestureContact = true;
        }
        input.WaitForMeasuredHitTest = _admission.IsMeasuredHitTestGesture(input);
        if (!_draining && (_deferred?.Count ?? 0) == 0 && snapshot.TryBeginInteraction())
            return DispatchRecognizedGestureUnderLease(ref input, snapshot);
        if (_host.RequestGeometryRecoveryIfRequired(snapshot))
            return false;
        return EnqueueRecognizedGesture(snapshot, input);
    }

    /// <summary>
    /// The immediate path of <see cref="DispatchRecognizedGesture"/>, entered
    /// holding the snapshot lease. A gesture whose hit-test band is not
    /// measured yet is queued; any other is dispatched at once.
    /// </summary>
    /// <remarks>
    /// The lease ends before the context menu opens or post-interaction work
    /// runs, and both are checked against the snapshot, generation and layout
    /// revision the gesture hit-tested.
    /// </remarks>
    private bool DispatchRecognizedGestureUnderLease(ref PointerInput input, LayoutSnapshot snapshot)
    {
        long snapshotGeneration = _published.Generation;
        long layoutRevision = snapshot.LayoutRevision;
        input.GestureLayoutRevision = layoutRevision;
        bool waitForGeometry = false;
        LazyLayoutBand waitingBand = default;
        try
        {
            input.InteractionSnapshot = snapshot;
            if (_admission.TryGetAdmissionBand(ref input, snapshot, out waitingBand, out bool bandReady) &&
                !bandReady)
            {
                PrepareContextGestureForDeferral(ref input);
                waitForGeometry = true;
            }
            else
            {
                input.WaitForMeasuredHitTest = false;
                _dispatcher.Process(ref input);
            }
        }
        finally
        {
            input.InteractionSnapshot = null;
            snapshot.EndInteraction();
        }
        if (waitForGeometry)
            return DeferRecognizedGestureForGeometry(ref input, snapshot, snapshotGeneration, layoutRevision, waitingBand);
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

    /// <summary>
    /// Queues a recognized gesture whose hit-test band is not measured yet,
    /// runs its post-interaction work, then waits for layout publication or
    /// requests the band. Returns whether the gesture stays queued.
    /// </summary>
    private bool DeferRecognizedGestureForGeometry(
        ref PointerInput input,
        LayoutSnapshot snapshot,
        long snapshotGeneration,
        long layoutRevision,
        LazyLayoutBand waitingBand)
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
                Reset();
                return false;
            }
        }
        if (input.WaitForLayoutPublication)
        {
            if (_admission.IsLayoutPublished(snapshot))
                _retryTimer?.Start();
            else
                _retryTimer?.Stop();
            return true;
        }
        RequestDeferredGestureBandRealization(snapshot, waitingBand);
        return true;
    }

    /// <summary>
    /// Dispatches a routed pointer event from the canvas, a selection handle
    /// or the drag shield.
    /// </summary>
    /// <remarks>
    /// Kept whole: cancel classification must precede input creation, the
    /// immediate path and deferral, and the source-contract harness checks
    /// that order within this method.
    /// </remarks>
    internal void DispatchPointerInput(PointerInputKind kind, object sender, PointerRoutedEventArgs args)
    {
        if (_lifetime.IsDisposed || _lifetime.IsUnloaded || _resetting || _surface.Canvas is null || _published.Snapshot is not { } snapshot)
            return;
        PointerPoint point = args.GetCurrentPoint(_surface.Canvas);
        kind = PointerInputClassification.NormalizeCanceledPointerInputKind(kind, point.Properties.IsCanceled);
        if ((kind is PointerInputKind.Cancel or PointerInputKind.Exit) &&
            _postInteraction.IsCaptureReleaseInFlight(args.Pointer.PointerId))
        {
            return;
        }

        if (_host.RequestGeometryRecoveryIfRequired(snapshot))
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

        if (!_draining && (_deferred?.Count ?? 0) == 0 && snapshot.TryBeginInteraction())
        {
            ProcessUnderLease(ref input, snapshot, args);
            return;
        }

        if (_host.RequestGeometryRecoveryIfRequired(snapshot))
            return;

        // A new captured physical owner cannot be queued behind a geometry
        // waiter: the capture itself freezes the tree the waiter needs. Cancel
        // only the logical waiter, preserving the existing selection, then
        // retry this fresh input against the still-current snapshot.
        if (startsNewCapturedOwnership &&
            _deferred is { Count: > 0, HasBlockingSnapshotInteractions: false })
        {
            long generationBeforeWaiterDrop = _published.Generation;
            DropQueuePreservingSelection();
            if (_lifetime.IsDisposed || _lifetime.IsUnloaded || !ReferenceEquals(_published.Snapshot, snapshot) ||
                generationBeforeWaiterDrop != _published.Generation)
                return;

            if (snapshot.TryBeginInteraction())
            {
                ProcessUnderLease(ref input, snapshot, args);
                return;
            }
        }

        DeferPointerInput(kind, sender, ref input, snapshot, args);
    }

    /// <summary>
    /// Reports the end of a lazy layout request so a gesture waiting on
    /// <paramref name="snapshot"/> can retry or be dropped.
    /// </summary>
    internal void OnLazyLayoutCompletion(LayoutSnapshot snapshot, bool succeeded)
    {
        if (_deferred is not { Count: > 0 } ||
            !ReferenceEquals(_deferredSnapshot, snapshot))
        {
            return;
        }

        if (!ReferenceEquals(_published.Snapshot, snapshot))
        {
            DropQueuePreservingSelection();
            return;
        }

        if (succeeded)
        {
            if (!_admission.IsLayoutPublished(snapshot))
            {
                // The worker may publish a newer measured mask before its UI
                // realization plans and restored anchor are applied. Wait for
                // LayoutCommitPublisher.Apply to signal that publication boundary.
                _retryTimer?.Stop();
                return;
            }
            _retryTimer ??= CreateRetryTimer();
            _retryTimer.Start();
            return;
        }

        // A superseding request can own the next retry. Otherwise a terminal
        // failure/cancellation would leave a stopped logical waiter forever.
        if (_layout.HasSupersedingLazyLayoutRequest(snapshot))
        {
            _retryTimer?.Stop();
            return;
        }

        DropQueuePreservingSelection();
    }

    /// <summary>
    /// Drops all queued input, its retry timer and its speculative captures.
    /// Retires a touch owner whose terminal event was still queued.
    /// </summary>
    internal void Reset()
    {
        _resetting = true;
        try
        {
            _droppedTerminals.Retire(_deferred);
            _deferred?.Clear();
            _deferredSnapshot = null;
            if (_retryTimer is { } timer)
            {
                _retryTimer = null;
                try { timer.Stop(); } catch (Exception) { }
                try { timer.Tick -= OnRetryTimer; } catch (Exception) { }
            }
            _deferredCaptures.ReleaseAll();
        }
        finally { _resetting = false; }
    }

    private void ProcessUnderLease(ref PointerInput input, LayoutSnapshot snapshot, PointerRoutedEventArgs args)
    {
        long snapshotGeneration = _published.Generation;
        long layoutRevision = snapshot.LayoutRevision;
        try
        {
            input.InteractionSnapshot = snapshot;
            _dispatcher.Process(ref input);
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
    }

    private void DeferPointerInput(
        PointerInputKind kind,
        object sender,
        ref PointerInput input,
        LayoutSnapshot snapshot,
        PointerRoutedEventArgs args)
    {
        if (_deferredSnapshot is not null && !ReferenceEquals(snapshot, _deferredSnapshot))
            Reset();
        _deferredSnapshot = snapshot;
        // Capture in the original press event so a release outside the canvas is
        // delivered even if layout is still busy. Never capture a pending touch
        // pan on the canvas; explicit selection handles own their touch gesture.
        bool primaryPress = input.Point.IsInContact &&
            (kind == PointerInputKind.HandlePress ||
             (kind == PointerInputKind.Press && PointerInputClassification.IsPrecisePointer(input) &&
              input.Point.Properties.IsLeftButtonPressed && !input.Point.Properties.IsBarrelButtonPressed));
        if (primaryPress)
        {
            UIElement target = kind == PointerInputKind.HandlePress && sender is UIElement handle ? handle : _surface.Canvas!;
            if (PointerInputClassification.CapturePointerForInput(target, input))
            {
                input.CapturedBeforeDeferral = true;
                _deferredCaptures.Record(input.Pointer.PointerId, target, input.Pointer);
            }
        }
        input.CapturedBeforeDeferral |= _deferredCaptures.Contains(input.Pointer.PointerId);
        bool hasSnapshotPointerOwner = input.CapturedBeforeDeferral ||
            _document.IsSessionPointer(input.Pointer.PointerId) ||
            (_horizontalOverflowDrag.PointerId == input.Pointer.PointerId && _horizontalOverflowDrag.PointerId != 0) ||
            _handles.DragPointerId == input.Pointer.PointerId;
        bool blocksSnapshotInteraction = kind is not (
            PointerInputKind.Move or PointerInputKind.Release or PointerInputKind.Cancel or PointerInputKind.Exit) ||
            hasSnapshotPointerOwner;
        _deferred ??= new DeferredInputQueue<PointerInput>();
        if (!_deferred.TryEnqueue(input, input.Pointer.PointerId,
                kind is PointerInputKind.Move or PointerInputKind.HandleMove,
                blocksSnapshotInteraction: blocksSnapshotInteraction))
        {
            if (!_deferred.HasBlockingSnapshotInteractions)
                DropQueuePreservingSelection();
            else
                CancelDeferredGesture();
            return;
        }
        args.Handled |= input.CapturedBeforeDeferral || kind == PointerInputKind.Wheel;
        _retryTimer ??= CreateRetryTimer();
        _retryTimer.Start();
    }

    private bool EnqueueRecognizedGesture(LayoutSnapshot snapshot, PointerInput input)
    {
        input.IsDeferredGeometryWait = true;
        if (_deferredSnapshot is not null && !ReferenceEquals(snapshot, _deferredSnapshot))
            Reset();
        _deferredSnapshot = snapshot;
        _deferred ??= new DeferredInputQueue<PointerInput>();
        if (!_deferred.TryEnqueue(input, 0, false, blocksSnapshotInteraction: false))
        {
            DropQueuePreservingSelection();
            return false;
        }
        _retryTimer ??= CreateRetryTimer();
        _retryTimer.Start();
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

    private void RequestDeferredGestureBandRealization(LayoutSnapshot snapshot, LazyLayoutBand hitBand)
    {
        if (_lifetime.IsDisposed || _lifetime.IsUnloaded || !ReferenceEquals(snapshot, _published.Snapshot) ||
            !ReferenceEquals(snapshot, _deferredSnapshot))
        {
            DropQueuePreservingSelection();
            return;
        }

        if (snapshot.GeometryRecoveryRequired || _host.RequestGeometryRecoveryIfRequired(snapshot))
        {
            DropQueuePreservingSelection();
            return;
        }
        if (_admission.TryRequestBandRealization(snapshot, hitBand))
        {
            _retryTimer?.Stop();
            return;
        }

        // Do not retain a waiter that can no longer be made ready; reset only
        // the queued inputs/captures, never the user's current selection.
        DropQueuePreservingSelection();
    }

    private void DropQueuePreservingSelection()
    {
        Reset();
        _host.ResumePendingRebuildAfterPointerInteraction();
    }

    private DispatcherQueueTimer CreateRetryTimer()
    {
        var timer = _dispatcherQueue.CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(16);
        timer.Tick += OnRetryTimer;
        return timer;
    }

    /// <summary>
    /// Replays queued input against the snapshot it was hit-tested on, one
    /// snapshot lease per pass, until the queue drains, waits on layout or
    /// goes stale.
    /// </summary>
    private void OnRetryTimer(DispatcherQueueTimer sender, object args)
    {
        if (!ReferenceEquals(sender, _retryTimer))
            return;

        if (_lifetime.IsDisposed || _lifetime.IsUnloaded || !ReferenceEquals(_published.Snapshot, _deferredSnapshot))
        {
            Reset();
            return;
        }
        while (_published.Snapshot is { } snapshot && ReferenceEquals(snapshot, _deferredSnapshot))
        {
            if (_lifetime.IsDisposed || _lifetime.IsUnloaded)
            {
                Reset();
                return;
            }
            if (_host.RequestGeometryRecoveryIfRequired(snapshot))
            {
                DropQueuePreservingSelection();
                return;
            }
            if (_deferred?.TryPeek(out _, out _) != true)
            {
                FinishDrain(sender);
                return;
            }
            if (!snapshot.TryBeginInteraction())
                return;

            long snapshotGeneration = _published.Generation;
            long layoutRevision = snapshot.LayoutRevision;
            DeferredInputDrainOutcome outcome = DrainUnderLease(sender, snapshot, snapshotGeneration, layoutRevision);
            if (!ContinueAfterDrain(sender, snapshot, snapshotGeneration, layoutRevision, outcome))
                return;
        }
    }

    /// <summary>
    /// Replays queued input under the snapshot lease the caller just took.
    /// The lease and the draining flag end on every exit path, before any
    /// post-interaction work the outcome carries.
    /// </summary>
    private DeferredInputDrainOutcome DrainUnderLease(
        DispatcherQueueTimer sender,
        LayoutSnapshot snapshot,
        long snapshotGeneration,
        long layoutRevision)
    {
        var outcome = default(DeferredInputDrainOutcome);
        try
        {
            try
            {
                _draining = true;
                while (_deferred?.TryPeek(out var input, out _) == true)
                {
                    if (_lifetime.IsDisposed || _lifetime.IsUnloaded ||
                        !ReferenceEquals(_published.Snapshot, snapshot) ||
                        !ReferenceEquals(_deferredSnapshot, snapshot) ||
                        (input.GestureSnapshotGeneration != 0 &&
                         input.GestureSnapshotGeneration != snapshotGeneration))
                    {
                        outcome.StaleQueue = true;
                        break;
                    }

                    if (input.DeferredTouchGesturePointerId != 0 &&
                        !_attribution.ResolveDeferredOwner(ref input))
                    {
                        _deferred.TryDequeue(out _);
                        continue;
                    }

                    if (input.WaitForMeasuredHitTest && !input.ContextCaptureEnded)
                        PrepareContextGestureForDeferral(ref input);

                    if (input.IsDeferredGeometryWait)
                    {
                        DeferredGeometryWaitStep step = AdmitGeometryWait(_deferred, ref input, snapshot, ref outcome);
                        if (step == DeferredGeometryWaitStep.Skip)
                            continue;
                        if (step == DeferredGeometryWaitStep.Stop)
                            break;
                    }

                    _deferred.TryDequeue(out _);

                    input.GestureLayoutRevision = layoutRevision;
                    input.InteractionSnapshot = snapshot;
                    try
                    {
                        _dispatcher.Process(ref input);
                    }
                    finally
                    {
                        input.InteractionSnapshot = null;
                    }
                    if (input.ShowContextMenuAfterInteraction || input.HasPostInteractionWork)
                    {
                        outcome.PostInteractionInput = input;
                        break;
                    }
                }
            }
            finally
            {
                _draining = false;
                snapshot.EndInteraction();
            }
        }
        catch
        {
            FinishDrain(sender);
            throw;
        }

        return outcome;
    }

    /// <summary>
    /// Decides whether the queue head, a gesture that waited for geometry, can
    /// be dispatched now. Records in <paramref name="outcome"/> why the drain
    /// stops when it must wait for layout, or when a dropped gesture still has
    /// post-interaction work.
    /// </summary>
    private DeferredGeometryWaitStep AdmitGeometryWait(
        DeferredInputQueue<PointerInput> queue,
        ref PointerInput input,
        LayoutSnapshot snapshot,
        ref DeferredInputDrainOutcome outcome)
    {
        bool hasBand = _admission.TryGetAdmissionBand(
            ref input,
            snapshot,
            out outcome.WaitingBand,
            out bool bandReady);
        if (input.InvalidDeferredProjection)
            return DropQueueHead(queue, input, ref outcome);
        if (input.WaitForLayoutPublication)
        {
            queue.TryReplaceHead(
                input,
                blocksSnapshotInteraction: false);
            outcome.WaitingForLayoutPublication = true;
            if (input.HasPostInteractionWork)
                outcome.PostInteractionInput = input;
            return DeferredGeometryWaitStep.Stop;
        }
        if (hasBand && !bandReady)
        {
            queue.TryReplaceHead(
                input,
                blocksSnapshotInteraction: false);
            outcome.WaitingForGeometry = true;
            if (input.HasPostInteractionWork)
                outcome.PostInteractionInput = input;
            return DeferredGeometryWaitStep.Stop;
        }
        if (!input.WaitForMeasuredHitTest &&
            !_admission.TryReproject(ref input))
        {
            return DropQueueHead(queue, input, ref outcome);
        }
        input.WaitForMeasuredHitTest = false;
        return DeferredGeometryWaitStep.Dispatch;
    }

    /// <summary>
    /// Dequeues a gesture that can no longer be dispatched. The drain stops
    /// only when the gesture still has post-interaction work to run.
    /// </summary>
    private static DeferredGeometryWaitStep DropQueueHead(
        DeferredInputQueue<PointerInput> queue,
        PointerInput input,
        ref DeferredInputDrainOutcome outcome)
    {
        queue.TryDequeue(out _);
        if (!input.HasPostInteractionWork)
            return DeferredGeometryWaitStep.Skip;
        outcome.PostInteractionInput = input;
        return DeferredGeometryWaitStep.Stop;
    }

    /// <summary>
    /// After the lease of one drain pass has ended, runs the outcome's
    /// post-interaction work or waits, drops or finishes the queue. Returns
    /// true when the drain continues with the next queued input.
    /// </summary>
    private bool ContinueAfterDrain(
        DispatcherQueueTimer sender,
        LayoutSnapshot snapshot,
        long snapshotGeneration,
        long layoutRevision,
        DeferredInputDrainOutcome outcome)
    {
        if (!outcome.WaitingForGeometry && !outcome.WaitingForLayoutPublication &&
            outcome.PostInteractionInput is { } menuInput)
        {
            return CompleteDrainedInput(sender, menuInput, snapshot, snapshotGeneration, layoutRevision);
        }

        if (outcome.StaleQueue)
        {
            DropQueuePreservingSelection();
            return false;
        }

        if (outcome.WaitingForLayoutPublication || outcome.WaitingForGeometry)
        {
            sender.Stop();
            if (outcome.PostInteractionInput is { } deferredPost &&
                deferredPost.HasPostInteractionWork &&
                !CompletePointerPostInteraction(
                    ref deferredPost,
                    snapshot,
                    snapshotGeneration,
                    layoutRevision))
            {
                DropQueuePreservingSelection();
                return false;
            }
            if (outcome.WaitingForLayoutPublication)
            {
                if (_admission.IsLayoutPublished(snapshot))
                    sender.Start();
            }
            else if (!_lifetime.IsDisposed && !_lifetime.IsUnloaded &&
                ReferenceEquals(_published.Snapshot, snapshot) &&
                snapshotGeneration == _published.Generation)
            {
                RequestDeferredGestureBandRealization(snapshot, outcome.WaitingBand);
            }
            else
            {
                DropQueuePreservingSelection();
            }
            return false;
        }

        if (_lifetime.IsDisposed || _lifetime.IsUnloaded ||
            !ReferenceEquals(_published.Snapshot, snapshot) ||
            !ReferenceEquals(_deferredSnapshot, snapshot))
        {
            Reset();
            return false;
        }

        if ((_deferred?.Count ?? 0) == 0)
        {
            FinishDrain(sender);
            return false;
        }
        return true;
    }

    /// <summary>
    /// Opens the context menu or runs the post-interaction work of the input
    /// that ended a drain pass, outside the snapshot lease. Returns true when
    /// later queued input is still current and the drain continues.
    /// </summary>
    private bool CompleteDrainedInput(
        DispatcherQueueTimer sender,
        PointerInput menuInput,
        LayoutSnapshot snapshot,
        long snapshotGeneration,
        long layoutRevision)
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
            !ReferenceEquals(_deferredSnapshot, snapshot) ||
            snapshotGeneration != _published.Generation ||
            snapshot.LayoutRevision != layoutRevision)
        {
            Reset();
            return false;
        }
        if ((_deferred?.Count ?? 0) == 0)
        {
            FinishDrain(sender);
            return false;
        }
        // Process later queued input only after the context menu has been
        // opened without the snapshot lease. This preserves the FIFO ordering
        // of gestures even when layout had to defer them.
        return true;
    }

    private void FinishDrain(DispatcherQueueTimer sender)
    {
        sender.Stop();
        _deferredSnapshot = null;
        _deferred?.Clear();
        _deferredCaptures.ReleaseUnowned();
        _host.ResumePendingRebuildAfterPointerInteraction();
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
        bool wasDraining = _draining;
        _draining = true;
        try
        {
            bool stillCurrent = _postInteraction.Run(ref input, snapshot, snapshotGeneration, layoutRevision);
            if (!stillCurrent)
                Reset();
            return stillCurrent;
        }
        finally
        {
            _draining = wasDraining;
        }
    }

    private void CancelDeferredGesture()
    {
        Reset();
        _document.EndSession();
        _horizontalOverflowDrag.Clear();
        _handles.ResetDrag();
        _touchTapSuppression.Reset();
        _touchSelection.Reset();
        _dragShield.SetActive(false);
        MarkdownDiagnostics.WriteLine("[MarkdownRendererControl] Cancelled an over-budget deferred pointer gesture.");
        _host.ResumePendingRebuildAfterPointerInteraction();
    }
}
