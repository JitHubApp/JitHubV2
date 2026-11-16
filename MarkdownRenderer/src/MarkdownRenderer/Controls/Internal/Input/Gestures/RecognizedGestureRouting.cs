using Microsoft.UI.Input;
using Windows.Foundation;
using MarkdownRenderer.Controls.Internal.Input.Deferred;
using MarkdownRenderer.Controls.Internal.Input.Keyboard;
using MarkdownRenderer.Controls.Internal.Pipeline;
using MarkdownRenderer.Controls.Internal.View;
using MarkdownRenderer.Layout.Internal.LazyLayout;
using MarkdownRenderer.Layout.Internal.Snapshots;

namespace MarkdownRenderer.Controls.Internal.Input.Gestures;

/// <summary>
/// The path of a gesture WinUI recognized on the canvas: it runs at once when
/// the published snapshot's lease is free and its hit-test band is measured,
/// and is otherwise queued on <see cref="DeferredInputReplay"/>.
/// </summary>
/// <remarks>
/// The lease ends before the context menu opens or post-interaction work
/// runs, and both are checked against the snapshot, generation and layout
/// revision the gesture hit-tested.
/// </remarks>
internal sealed class RecognizedGestureRouting
{
    private readonly IPointerInputRouterHost _host;
    private readonly IDeferredGestureLayoutHost _layout;
    private readonly MarkdownViewSurface _surface;
    private readonly MarkdownViewLifetime _lifetime;
    private readonly PublishedLayout _published;
    private readonly MarkdownViewport _viewport;
    private readonly DeferredGestureAdmission _admission;
    private readonly RecognizedGestureAttribution _attribution;
    private readonly PointerInputDispatcher _dispatcher;
    private readonly GestureContextMenuCompletion _contextMenuCompletion;
    private readonly DeferredInputReplay _replay;

    internal RecognizedGestureRouting(
        IPointerInputRouterHost host,
        IDeferredGestureLayoutHost layout,
        MarkdownViewSurface surface,
        MarkdownViewLifetime lifetime,
        PublishedLayout published,
        MarkdownViewport viewport,
        DeferredGestureAdmission admission,
        RecognizedGestureAttribution attribution,
        PointerInputDispatcher dispatcher,
        GestureContextMenuCompletion contextMenuCompletion,
        DeferredInputReplay replay)
    {
        _host = host;
        _layout = layout;
        _surface = surface;
        _lifetime = lifetime;
        _published = published;
        _viewport = viewport;
        _admission = admission;
        _attribution = attribution;
        _dispatcher = dispatcher;
        _contextMenuCompletion = contextMenuCompletion;
        _replay = replay;
    }

    /// <summary>
    /// Dispatches a gesture WinUI recognized on the canvas. Returns whether
    /// the gesture was handled or queued.
    /// </summary>
    internal bool Dispatch(PointerInputKind kind, Point point, PointerDeviceType device)
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
            _attribution.AttributeTouchGesture(ref input, point, _replay.Queue);
        input.HasViewportGesturePosition = _viewport.TryMapCanvasToViewport(point, out input.ViewportGesturePosition);
        if (input.HasGestureContact &&
            _viewport.TryMapCanvasContactToViewport(input.GestureContact, out TouchContactGeometry viewportContact))
        {
            input.ViewportGestureContact = viewportContact;
            input.HasViewportGestureContact = true;
        }
        input.WaitForMeasuredHitTest = _admission.IsMeasuredHitTestGesture(input);
        if (_replay.IsIdle && snapshot.TryBeginInteraction())
            return DispatchUnderLease(ref input, snapshot);
        if (_host.RequestGeometryRecoveryIfRequired(snapshot))
            return false;
        return Enqueue(snapshot, input);
    }

    /// <summary>
    /// Reports the end of a lazy layout request so a gesture waiting on
    /// <paramref name="snapshot"/> can retry or be dropped.
    /// </summary>
    internal void OnLazyLayoutCompletion(LayoutSnapshot snapshot, bool succeeded)
    {
        if (_replay.Queue is not { Count: > 0 } ||
            !ReferenceEquals(_replay.Snapshot, snapshot))
        {
            return;
        }

        if (!ReferenceEquals(_published.Snapshot, snapshot))
        {
            _replay.DropQueuePreservingSelection();
            return;
        }

        if (succeeded)
        {
            if (!_admission.IsLayoutPublished(snapshot))
            {
                // The worker may publish a newer measured mask before its UI
                // realization plans and restored anchor are applied. Wait for
                // LayoutCommitPublisher.Apply to signal that publication boundary.
                _replay.StopRetry();
                return;
            }
            _replay.StartRetry();
            return;
        }

        // A superseding request can own the next retry. Otherwise a terminal
        // failure/cancellation would leave a stopped logical waiter forever.
        if (_layout.HasSupersedingLazyLayoutRequest(snapshot))
        {
            _replay.StopRetry();
            return;
        }

        _replay.DropQueuePreservingSelection();
    }

    /// <summary>
    /// The immediate path of <see cref="Dispatch"/>, entered holding the
    /// snapshot lease. A gesture whose hit-test band is not measured yet is
    /// queued; any other is dispatched at once.
    /// </summary>
    private bool DispatchUnderLease(ref PointerInput input, LayoutSnapshot snapshot)
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
                _replay.PrepareContextGestureForDeferral(ref input);
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
            return DeferForGeometry(ref input, snapshot, snapshotGeneration, layoutRevision, waitingBand);
        if (input.ShowContextMenuAfterInteraction)
        {
            input.Handled = _contextMenuCompletion.Complete(
                input,
                snapshot,
                snapshotGeneration,
                layoutRevision);
        }
        else if (input.HasPostInteractionWork)
        {
            _replay.CompletePostInteraction(
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
    private bool DeferForGeometry(
        ref PointerInput input,
        LayoutSnapshot snapshot,
        long snapshotGeneration,
        long layoutRevision,
        LazyLayoutBand waitingBand)
    {
        if (!Enqueue(snapshot, input))
            return false;
        if (input.HasPostInteractionWork)
        {
            if (!_replay.CompletePostInteraction(
                    ref input,
                    snapshot,
                    snapshotGeneration,
                    layoutRevision))
            {
                _replay.Reset();
                return false;
            }
        }
        if (input.WaitForLayoutPublication)
        {
            if (_admission.IsLayoutPublished(snapshot))
                _replay.ResumeRetry();
            else
                _replay.StopRetry();
            return true;
        }
        _replay.RequestBandRealization(snapshot, waitingBand);
        return true;
    }

    private bool Enqueue(LayoutSnapshot snapshot, PointerInput input)
    {
        input.IsDeferredGeometryWait = true;
        _replay.AdoptSnapshot(snapshot);
        if (!_replay.EnsureQueue().TryEnqueue(input, 0, false, blocksSnapshotInteraction: false))
        {
            _replay.DropQueuePreservingSelection();
            return false;
        }
        _replay.StartRetry();
        return true;
    }
}
