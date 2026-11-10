using System;
using Microsoft.UI.Dispatching;
using MarkdownRenderer.Controls.Internal.Input.Gestures;
using MarkdownRenderer.Controls.Internal.Input.HorizontalOverflow;
using MarkdownRenderer.Controls.Internal.Pipeline;
using MarkdownRenderer.Controls.Internal.View;
using MarkdownRenderer.Layout.Internal.LazyLayout;
using MarkdownRenderer.Layout.Internal.Snapshots;

namespace MarkdownRenderer.Controls.Internal.Input.Deferred;

/// <summary>
/// The queue of pointer input and recognized gestures that could not run at
/// once, the snapshot they were hit-tested against, and the retry timer that
/// replays them in FIFO order, one snapshot lease per drain pass.
/// </summary>
/// <remarks>
/// <para>
/// Every exit path of a drain pass ends the lease, the draining flag and the
/// queue in the same order, and post-interaction work, which can reenter
/// XAML, always runs after the lease ends while this queue reports itself as
/// draining, so reentrant input joins the queue instead of nesting.
/// </para>
/// <para>
/// <see cref="RecognizedGestureRouting"/> and <see cref="RoutedPointerInputRouting"/>
/// own the immediate paths and queue input here;
/// <see cref="DeferredGestureAdmission"/> decides when a queued gesture's
/// geometry is ready.
/// </para>
/// </remarks>
internal sealed class DeferredInputReplay
{
    private readonly IPointerInputRouterHost _host;
    private readonly DispatcherQueue _dispatcherQueue;
    private readonly MarkdownViewLifetime _lifetime;
    private readonly PublishedLayout _published;
    private readonly DocumentPointerInteraction _document;
    private readonly HorizontalOverflowPointerDrag _horizontalOverflowDrag;
    private readonly DeferredGestureAdmission _admission;
    private readonly RecognizedGestureAttribution _attribution;
    private readonly PointerInputDispatcher _dispatcher;
    private readonly DroppedTouchTerminalRetirement _droppedTerminals;
    private readonly PointerPostInteraction _postInteraction;
    private readonly GestureContextMenuCompletion _contextMenuCompletion;
    private readonly DeferredPointerCaptures _deferredCaptures;

    private DeferredInputQueue<PointerInput>? _deferred;
    private LayoutSnapshot? _deferredSnapshot;
    private DispatcherQueueTimer? _retryTimer;
    private bool _draining;
    private bool _resetting;

    internal DeferredInputReplay(
        IPointerInputRouterHost host,
        DispatcherQueue dispatcherQueue,
        MarkdownViewLifetime lifetime,
        PublishedLayout published,
        DocumentPointerInteraction document,
        HorizontalOverflowPointerDrag horizontalOverflowDrag,
        DeferredGestureAdmission admission,
        RecognizedGestureAttribution attribution,
        PointerInputDispatcher dispatcher,
        DroppedTouchTerminalRetirement droppedTerminals,
        PointerPostInteraction postInteraction,
        GestureContextMenuCompletion contextMenuCompletion,
        DeferredPointerCaptures deferredCaptures)
    {
        _host = host;
        _dispatcherQueue = dispatcherQueue;
        _lifetime = lifetime;
        _published = published;
        _document = document;
        _horizontalOverflowDrag = horizontalOverflowDrag;
        _admission = admission;
        _attribution = attribution;
        _dispatcher = dispatcher;
        _droppedTerminals = droppedTerminals;
        _postInteraction = postInteraction;
        _contextMenuCompletion = contextMenuCompletion;
        _deferredCaptures = deferredCaptures;
    }

    /// <summary>The queued input, once anything has been queued.</summary>
    internal DeferredInputQueue<PointerInput>? Queue => _deferred;

    /// <summary>The snapshot the queued input was hit-tested against, if any.</summary>
    internal LayoutSnapshot? Snapshot => _deferredSnapshot;

    /// <summary>True during a drain pass and while post-interaction work runs.</summary>
    internal bool IsDraining => _draining;

    /// <summary>True while <see cref="Reset"/> is dropping the queue.</summary>
    internal bool IsResetting => _resetting;

    /// <summary>True when new input may run at once instead of queueing behind earlier input.</summary>
    internal bool IsIdle => !_draining && (_deferred?.Count ?? 0) == 0;

    /// <summary>
    /// Ties the queue to <paramref name="snapshot"/>, first dropping input
    /// queued against a different snapshot.
    /// </summary>
    internal void AdoptSnapshot(LayoutSnapshot snapshot)
    {
        if (_deferredSnapshot is not null && !ReferenceEquals(snapshot, _deferredSnapshot))
            Reset();
        _deferredSnapshot = snapshot;
    }

    /// <summary>The queue, created on first use.</summary>
    internal DeferredInputQueue<PointerInput> EnsureQueue() => _deferred ??= new DeferredInputQueue<PointerInput>();

    /// <summary>Starts the retry timer, creating it on first use.</summary>
    internal void StartRetry()
    {
        _retryTimer ??= CreateRetryTimer();
        _retryTimer.Start();
    }

    /// <summary>Restarts the retry timer if it exists.</summary>
    internal void ResumeRetry() => _retryTimer?.Start();

    /// <summary>Stops the retry timer if it exists.</summary>
    internal void StopRetry() => _retryTimer?.Stop();

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

    /// <summary>Drops the queue, keeping the user's selection, and resumes a held rebuild.</summary>
    internal void DropQueuePreservingSelection()
    {
        Reset();
        _host.ResumePendingRebuildAfterPointerInteraction();
    }

    /// <summary>
    /// Ends the captured session of a double-tap or hold that must wait for
    /// geometry, so the wait does not freeze the tree it needs.
    /// </summary>
    internal void PrepareContextGestureForDeferral(ref PointerInput input)
    {
        if (input.ContextCaptureEnded || input.Kind is not (PointerInputKind.DoubleTap or PointerInputKind.Hold))
            return;

        _horizontalOverflowDrag.Clear();
        input.ResumeRebuildAfterInteraction = true;
        _document.EndCapturedSessionForContextGesture(ref input);
    }

    /// <summary>
    /// Requests the band a queued gesture needs measured, or drops the queue
    /// when it is stale or the band can no longer be made ready.
    /// </summary>
    internal void RequestBandRealization(LayoutSnapshot snapshot, LazyLayoutBand hitBand)
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

    /// <summary>
    /// Runs the post-interaction work <paramref name="input"/> recorded under
    /// its lease, reporting itself as draining so reentrant input queues.
    /// Drops the queue and returns false when the snapshot is no longer current.
    /// </summary>
    internal bool CompletePostInteraction(
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
                        DeferredGeometryWaitStep step = _admission.AdmitQueuedGesture(_deferred, ref input, snapshot, ref outcome);
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
                !CompletePostInteraction(
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
                RequestBandRealization(snapshot, outcome.WaitingBand);
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
            _ = _contextMenuCompletion.Complete(
                menuInput,
                snapshot,
                snapshotGeneration,
                layoutRevision);
        }
        else
        {
            CompletePostInteraction(
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
}
