using System;
using MarkdownRenderer.Controls.Selection;
using MarkdownRenderer.Diagnostics;
using MarkdownRenderer.Layout;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml.Input;
using VirtualKeyModifiers = Windows.System.VirtualKeyModifiers;

namespace MarkdownRenderer.Controls.Input;

/// <summary>
/// The path of a routed pointer event from the canvas, a selection handle or
/// the drag shield: it runs at once when the published snapshot's lease is
/// free, and is otherwise queued on <see cref="DeferredInputReplay"/> with a
/// speculative pointer capture.
/// </summary>
internal sealed class RoutedPointerInputRouting
{
    private readonly IPointerInputRouterHost _host;
    private readonly MarkdownViewSurface _surface;
    private readonly MarkdownViewLifetime _lifetime;
    private readonly PublishedLayout _published;
    private readonly MarkdownTouchSelectionSession _touchSelection;
    private readonly TouchTapSuppressionPolicy _touchTapSuppression;
    private readonly SelectionHandlesPresenter _handles;
    private readonly DocumentPointerInteraction _document;
    private readonly HorizontalOverflowPointerDrag _horizontalOverflowDrag;
    private readonly SelectionDragShield _dragShield;
    private readonly PointerInputDispatcher _dispatcher;
    private readonly PointerPostInteraction _postInteraction;
    private readonly DeferredPointerCaptures _deferredCaptures;
    private readonly DeferredInputReplay _replay;

    internal RoutedPointerInputRouting(
        IPointerInputRouterHost host,
        MarkdownViewSurface surface,
        MarkdownViewLifetime lifetime,
        PublishedLayout published,
        MarkdownTouchSelectionSession touchSelection,
        TouchTapSuppressionPolicy touchTapSuppression,
        SelectionHandlesPresenter handles,
        DocumentPointerInteraction document,
        HorizontalOverflowPointerDrag horizontalOverflowDrag,
        SelectionDragShield dragShield,
        PointerInputDispatcher dispatcher,
        PointerPostInteraction postInteraction,
        DeferredPointerCaptures deferredCaptures,
        DeferredInputReplay replay)
    {
        _host = host;
        _surface = surface;
        _lifetime = lifetime;
        _published = published;
        _touchSelection = touchSelection;
        _touchTapSuppression = touchTapSuppression;
        _handles = handles;
        _document = document;
        _horizontalOverflowDrag = horizontalOverflowDrag;
        _dragShield = dragShield;
        _dispatcher = dispatcher;
        _postInteraction = postInteraction;
        _deferredCaptures = deferredCaptures;
        _replay = replay;
    }

    /// <summary>
    /// Dispatches a routed pointer event from the canvas, a selection handle
    /// or the drag shield.
    /// </summary>
    /// <remarks>
    /// This method stays whole because cancel classification must precede
    /// input creation, the immediate path and deferral, in that order, and
    /// Test-MarkdownCorrectnessUiHarness.ps1 checks that order within it.
    /// </remarks>
    internal void Dispatch(PointerInputKind kind, object sender, PointerRoutedEventArgs args)
    {
        if (_lifetime.IsDisposed || _lifetime.IsUnloaded || _replay.IsResetting || _surface.Canvas is null || _published.Snapshot is not { } snapshot)
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

        if (_replay.IsIdle && snapshot.TryBeginInteraction())
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
            _replay.Queue is { Count: > 0, HasBlockingSnapshotInteractions: false })
        {
            long generationBeforeWaiterDrop = _published.Generation;
            _replay.DropQueuePreservingSelection();
            if (_lifetime.IsDisposed || _lifetime.IsUnloaded || !ReferenceEquals(_published.Snapshot, snapshot) ||
                generationBeforeWaiterDrop != _published.Generation)
                return;

            if (snapshot.TryBeginInteraction())
            {
                ProcessUnderLease(ref input, snapshot, args);
                return;
            }
        }

        Defer(kind, sender, ref input, snapshot, args);
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
            _replay.CompletePostInteraction(
                ref input,
                snapshot,
                snapshotGeneration,
                layoutRevision);
        }
        args.Handled = input.Handled;
    }

    private void Defer(
        PointerInputKind kind,
        object sender,
        ref PointerInput input,
        LayoutSnapshot snapshot,
        PointerRoutedEventArgs args)
    {
        _replay.AdoptSnapshot(snapshot);
        _deferredCaptures.CaptureForDeferral(kind, sender, ref input, _surface.Canvas);
        bool hasSnapshotPointerOwner = input.CapturedBeforeDeferral ||
            _document.IsSessionPointer(input.Pointer.PointerId) ||
            (_horizontalOverflowDrag.PointerId == input.Pointer.PointerId && _horizontalOverflowDrag.PointerId != 0) ||
            _handles.DragPointerId == input.Pointer.PointerId;
        bool blocksSnapshotInteraction = kind is not (
            PointerInputKind.Move or PointerInputKind.Release or PointerInputKind.Cancel or PointerInputKind.Exit) ||
            hasSnapshotPointerOwner;
        DeferredInputQueue<PointerInput> queue = _replay.EnsureQueue();
        if (!queue.TryEnqueue(input, input.Pointer.PointerId,
                kind is PointerInputKind.Move or PointerInputKind.HandleMove,
                blocksSnapshotInteraction: blocksSnapshotInteraction))
        {
            if (!queue.HasBlockingSnapshotInteractions)
                _replay.DropQueuePreservingSelection();
            else
                CancelDeferredGesture();
            return;
        }
        args.Handled |= input.CapturedBeforeDeferral || kind == PointerInputKind.Wheel;
        _replay.StartRetry();
    }

    private void CancelDeferredGesture()
    {
        _replay.Reset();
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
