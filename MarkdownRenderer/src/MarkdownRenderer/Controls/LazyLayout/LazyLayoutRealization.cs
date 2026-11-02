using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using MarkdownRenderer.Controls.Focus;
using MarkdownRenderer.Controls.Input;
using MarkdownRenderer.Controls.Pipeline;
using MarkdownRenderer.Diagnostics;
using MarkdownRenderer.Layout;
using MarkdownRenderer.Performance;
using Windows.Foundation;

namespace MarkdownRenderer.Controls.LazyLayout;

/// <summary>
/// Viewport-relative measurement of the published snapshot. Viewport, paint
/// and focus requests coalesce into one pending band; one low-priority
/// dispatch starts a worker for it, superseding older work, and its weak
/// completion applies the measured band as a layout commit. The recovery
/// clock retries any dispatch the UI thread rejected or withheld.
/// </summary>
/// <remarks>
/// Kept as one type because request admission, dispatch, cancellation and
/// completion all read and write the same pending/active band state; the
/// worker side lives in <see cref="LazyLayoutWork"/>.
/// </remarks>
internal sealed class LazyLayoutRealization
{
    private readonly ILazyLayoutHost _host;
    private readonly MarkdownViewLifetime _lifetime;
    private readonly PublishedLayout _published;
    private readonly LayoutCommitState _layoutCommit;
    private readonly MarkdownViewSurface _surface;
    private readonly MarkdownViewport _viewport;
    private readonly RecoveryClock _recoveryClock;
    private readonly VectorCapacityRetry _vectorCapacity;
    private readonly PointerInputRouter _pointerInput;
    private readonly KeyboardFocusModel _focus;
    private readonly FocusedItemCommit _focusCommit;
    private readonly WeakSnapshotPublicationDrainFence<LayoutSnapshot> _publicationHandoff = new();

    private CancellationTokenSource? _cts;
    private Task? _task;
    private LayoutSnapshot? _pendingSnapshot;
    private LayoutSnapshot? _activeSnapshot;
    private long _activeRevision = -1;
    private LazyLayoutBand _pendingBand;
    private LazyLayoutBand _activeBand;
    private LazyLayoutBand _pendingVisibleVectorBand;
    private LazyLayoutBand _activeVisibleVectorBand;
    private long _activeCapacityGeneration;
    private double? _activeAnchorOffset;
    private bool _hasPendingBand;
    private bool _dispatchQueued;
    private bool _pendingPreserveScrollAnchor;
    private long _generation;
    private long _dispatchTicket;
    private double _lastViewportTop;
    private long _lastViewportTimestamp;
    private double _scrollVelocity;

    internal LazyLayoutRealization(
        ILazyLayoutHost host,
        MarkdownViewLifetime lifetime,
        PublishedLayout published,
        LayoutCommitState layoutCommit,
        MarkdownViewSurface surface,
        MarkdownViewport viewport,
        RecoveryClock recoveryClock,
        VectorCapacityRetry vectorCapacity,
        PointerInputRouter pointerInput,
        KeyboardFocusModel focus,
        FocusedItemCommit focusCommit)
    {
        _host = host;
        _lifetime = lifetime;
        _published = published;
        _layoutCommit = layoutCommit;
        _surface = surface;
        _viewport = viewport;
        _recoveryClock = recoveryClock;
        _vectorCapacity = vectorCapacity;
        _pointerInput = pointerInput;
        _focus = focus;
        _focusCommit = focusCommit;
    }

    /// <summary>True while a request is pending or dispatched, a completion is staged, or a worker runs.</summary>
    internal bool HasPendingWork =>
        _dispatchQueued ||
        _hasPendingBand ||
        _publicationHandoff.HasPendingPublication ||
        _task is { IsCompleted: false };

    /// <summary>
    /// The latest viewport scroll velocity, in DIPs per second. Measured only
    /// while a performance session is attached.
    /// </summary>
    internal double ScrollVelocity => _scrollVelocity;

    /// <summary>Forgets the scroll velocity, so the next viewport sample starts fresh.</summary>
    internal void ResetScrollVelocity()
    {
        _lastViewportTimestamp = 0;
        _scrollVelocity = 0;
    }

    /// <summary>True when active or pending work for <paramref name="snapshot"/> covers <paramref name="band"/>.</summary>
    internal bool IsBandCovered(LayoutSnapshot snapshot, LazyLayoutBand band)
    {
        bool active = _cts is { IsCancellationRequested: false } &&
            ReferenceEquals(snapshot, _activeSnapshot) &&
            _activeRevision == snapshot.LayoutRevision &&
            band.Top >= _activeBand.Top && band.Bottom <= _activeBand.Bottom;
        bool pending = _hasPendingBand &&
            ReferenceEquals(snapshot, _pendingSnapshot) &&
            band.Top >= _pendingBand.Top && band.Bottom <= _pendingBand.Bottom;
        return active || pending;
    }

    /// <summary>True while any request for <paramref name="snapshot"/> is active, pending or dispatched.</summary>
    internal bool HasSupersedingRequest(LayoutSnapshot snapshot) =>
        (_cts is { IsCancellationRequested: false } &&
         ReferenceEquals(snapshot, _activeSnapshot)) ||
        (_hasPendingBand && ReferenceEquals(snapshot, _pendingSnapshot)) ||
        (_dispatchQueued && ReferenceEquals(snapshot, _pendingSnapshot));

    /// <summary>
    /// Queues measurement of the band around the current viewport, widened
    /// in the scroll direction when a performance session opted in, unless
    /// that band is already settled.
    /// </summary>
    internal void QueueForViewport(bool forceCapacityRetry = false)
    {
        var snapshot = _published.Snapshot;
        if (_host.RequestGeometryRecoveryIfRequired(snapshot) || snapshot is null)
            return;

        _viewport.TryGet(out double viewportTop, out double viewportHeight, out _);
        LazyLayoutBand visibleVectorBand = LazyLayoutBand.FromVisibleViewport(
            viewportTop, viewportHeight);
        LazyLayoutBand band;
        if (_host.PerformanceSession is { } performanceSession)
        {
            long now = Stopwatch.GetTimestamp();
            if (_lastViewportTimestamp > 0)
            {
                double elapsedSeconds = (now - _lastViewportTimestamp) /
                    (double)Stopwatch.Frequency;
                if (elapsedSeconds is > 0.002 and < 1)
                {
                    _scrollVelocity =
                        (viewportTop - _lastViewportTop) / elapsedSeconds;
                }
            }
            _lastViewportTop = viewportTop;
            _lastViewportTimestamp = now;
            band = LazyLayoutBand.FromDirectionalViewport(
                viewportTop,
                viewportHeight,
                performanceSession.Options.LookAheadViewports,
                _scrollVelocity);
        }
        else
        {
            band = LazyLayoutBand.FromViewport(
                viewportTop, viewportHeight, MarkdownLazyLayoutPolicy.OverscanPx);
        }

        // Directional look-ahead and image realization can be broader than the
        // viewport. Keep vector admission tied to the actual viewport rather
        // than letting an unchanged prefetch union absorb a changed viewport.
        band = LazyLayoutBand.Union(band, visibleVectorBand);

        bool capacityAdmissionEnabled = _vectorCapacity.IsAdmissionEnabled;
        bool capacityDeferred = capacityAdmissionEnabled && snapshot.HasCapacityDeferredScenes;
        if (!snapshot.IsLazyLayoutEnabled && !capacityDeferred)
            return;

        if (capacityAdmissionEnabled)
        {
            _vectorCapacity.Synchronize(snapshot);
            forceCapacityRetry |= _vectorCapacity.ConsumePendingWake(snapshot);
        }

        bool capacityRetryDue = capacityDeferred && _vectorCapacity.IsRetryDue(
            snapshot, visibleVectorBand, forceCapacityRetry);
        if (capacityDeferred && !capacityRetryDue &&
            snapshot.IsRealizationBandMeasured(band))
        {
            return;
        }

        if (snapshot.IsBandWorkSettled(band, visibleVectorBand) && !capacityRetryDue)
            return;

        QueueForBand(
            snapshot,
            band,
            visibleVectorBand,
            preserveScrollAnchor: _surface.Scroll is not null);
    }

    /// <summary>Queues measurement of the band a paint request reached.</summary>
    internal void QueueForPaintRegion(Rect region)
    {
        var snapshot = _published.Snapshot;
        if (_host.RequestGeometryRecoveryIfRequired(snapshot) || snapshot is null)
            return;

        _viewport.TryGet(out double viewportTop, out double viewportHeight, out _);
        LazyLayoutBand visibleVectorBand = LazyLayoutBand.FromVisibleViewport(
            viewportTop, viewportHeight);

        var band = _host.PerformanceSession is { } performanceSession
            ? LazyLayoutBand.FromDirectionalViewport(
                region.Top,
                region.Height,
                performanceSession.Options.LookAheadViewports,
                _scrollVelocity)
            : LazyLayoutBand.FromViewport(region.Top, region.Height, MarkdownLazyLayoutPolicy.OverscanPx);
        band = LazyLayoutBand.Union(band, visibleVectorBand);
        bool capacityAdmissionEnabled = _vectorCapacity.IsAdmissionEnabled;
        bool capacityDeferred = capacityAdmissionEnabled && snapshot.HasCapacityDeferredScenes;
        if (!snapshot.IsLazyLayoutEnabled && !capacityDeferred)
            return;
        bool forceCapacityRetry = false;
        if (capacityAdmissionEnabled)
        {
            _vectorCapacity.Synchronize(snapshot);
            bool wake = _vectorCapacity.ConsumePendingWake(snapshot);
            if (capacityDeferred && wake)
                forceCapacityRetry = true;
            if (capacityDeferred && !wake &&
                !_vectorCapacity.IsRetryDue(snapshot, visibleVectorBand, forceCapacityRetry) &&
                snapshot.IsRealizationBandMeasured(band))
            {
                return;
            }
        }
        if (snapshot.IsBandWorkSettled(band, visibleVectorBand) &&
            !(capacityDeferred && _vectorCapacity.IsRetryDue(snapshot, visibleVectorBand, forceCapacityRetry)))
            return;

        QueueForBand(
            snapshot,
            band,
            visibleVectorBand,
            preserveScrollAnchor: _surface.Scroll is not null);
    }

    /// <summary>
    /// Requests measurement of <paramref name="band"/> in
    /// <paramref name="snapshot"/>. Requests coalesce into one pending band
    /// and one low-priority dispatch; work already covering the band is kept.
    /// </summary>
    internal void QueueForBand(
        LayoutSnapshot snapshot,
        LazyLayoutBand band,
        LazyLayoutBand visibleVectorBand,
        bool preserveScrollAnchor)
    {
        if (_lifetime.IsDisposed || _lifetime.IsUnloaded || !ReferenceEquals(snapshot, _published.Snapshot))
            return;

        if (_host.RequestGeometryRecoveryIfRequired(snapshot))
            return;

        if (_cts is { IsCancellationRequested: false } &&
            ReferenceEquals(snapshot, _activeSnapshot) &&
            // Same-band work from an older image-reflow revision must not
            // absorb the retry needed to refresh scene indexes/graph bindings.
            _activeRevision == snapshot.LayoutRevision &&
            (!_vectorCapacity.IsAdmissionEnabled ||
             VectorNativeResourceAdmission.Shared.CapacityGeneration <=
                _activeCapacityGeneration) &&
            band.Top >= _activeBand.Top &&
            band.Bottom <= _activeBand.Bottom &&
            (!_vectorCapacity.IsAdmissionEnabled ||
             visibleVectorBand.Top == _activeVisibleVectorBand.Top &&
             visibleVectorBand.Bottom == _activeVisibleVectorBand.Bottom) &&
            (!preserveScrollAnchor ||
             (_surface.Scroll is not null &&
              _activeAnchorOffset is { } activeOffset &&
              Math.Abs(_surface.Scroll.VerticalOffset - activeOffset) < 0.5)))
        {
            return;
        }

        if (_cts is { } activeCancellation)
            _ = CancellationTokenSourceRetirement.RequestCancellation(activeCancellation);

        if (_hasPendingBand && ReferenceEquals(snapshot, _pendingSnapshot))
        {
            _pendingBand = new LazyLayoutBand(
                Math.Min(_pendingBand.Top, band.Top),
                Math.Max(_pendingBand.Bottom, band.Bottom));
            // Layout/image overscan is unioned; visible vector demand is the
            // latest viewport, never a stale union of previously visible bands.
            _pendingVisibleVectorBand = visibleVectorBand;
            _pendingPreserveScrollAnchor |= preserveScrollAnchor;
        }
        else
        {
            _pendingSnapshot = snapshot;
            _pendingBand = band;
            _pendingVisibleVectorBand = visibleVectorBand;
            _pendingPreserveScrollAnchor = preserveScrollAnchor;
            _hasPendingBand = true;
        }

        if (_dispatchQueued)
            return;

        // Lazy work has two UI handoffs: request admission and result
        // publication. Start the timer before either so rejected or accepted-
        // but-withheld dispatcher callbacks still have a bounded recovery path.
        _recoveryClock.Start();
        if (!_recoveryClock.IsRunning)
        {
            // There is no dispatcher timer left to own a rejected or withheld
            // lazy handoff. Drop this request epoch and rebuild instead of
            // advertising permanently pending work that cannot be recovered.
            Cancel();
            _host.RequestFullRebuild();
            return;
        }

        _dispatchQueued = true;
        long dispatchTicket = ++_dispatchTicket;
        if (dispatchTicket == 0)
            dispatchTicket = ++_dispatchTicket;
        var dispatcher = _host.DispatcherQueue;
        var receiver = new WeakReference<LazyLayoutRealization>(this);
        try
        {
            if (dispatcher is null || !dispatcher.TryEnqueue(
                    Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
                    () =>
                    {
                        if (receiver.TryGetTarget(out var realization))
                            realization.ProcessQueued(dispatchTicket);
                    }))
            {
                _dispatchQueued = false;
            }
        }
        catch (Exception exception)
        {
            _dispatchQueued = false;
            MarkdownDiagnostics.WriteLine(
                $"[MarkdownRendererControl] lazy layout request dispatch failed: {exception.GetType().Name} " +
                $"(0x{exception.HResult:X8}).");
        }
    }

    /// <summary>Recovery clock retry of the latest dispatch.</summary>
    internal void ProcessQueued() => ProcessQueued(_dispatchTicket);

    /// <summary>
    /// Starts the pending band dispatched with <paramref name="dispatchTicket"/>,
    /// superseding any active realization.
    /// </summary>
    internal void ProcessQueued(long dispatchTicket)
    {
        if (dispatchTicket != _dispatchTicket || !_hasPendingBand)
            return;

        _dispatchQueued = false;
        if (_lifetime.IsDisposed || _lifetime.IsUnloaded || !_hasPendingBand)
        {
            _pendingSnapshot = null;
            _hasPendingBand = false;
            _pendingPreserveScrollAnchor = false;
            _host.StopRecoveryClockIfIdle();
            return;
        }

        LayoutSnapshot? snapshot = _pendingSnapshot;
        LazyLayoutBand band = _pendingBand;
        LazyLayoutBand visibleVectorBand = _pendingVisibleVectorBand;
        bool preserveScrollAnchor = _pendingPreserveScrollAnchor;
        _pendingSnapshot = null;
        _hasPendingBand = false;
        _pendingPreserveScrollAnchor = false;

        if (snapshot is null || !ReferenceEquals(snapshot, _published.Snapshot))
            return;

        var oldCts = _cts;
        var oldTask = _task;
        var cts = CancellationTokenSource.CreateLinkedTokenSource(_host.PipelineToken);
        long realizationGeneration = ++_generation;
        _publicationHandoff.Reset(realizationGeneration);
        long pipelineGeneration = _host.PipelineGeneration;
        CancellationToken operationToken = cts.Token;
        double? anchorOffset = preserveScrollAnchor && _surface.Scroll is not null
            ? _surface.Scroll.VerticalOffset
            : null;
        var dispatcher = _host.DispatcherQueue;

        _cts = cts;
        _activeSnapshot = snapshot;
        _activeRevision = snapshot.LayoutRevision;
        _activeBand = band;
        _activeVisibleVectorBand = visibleVectorBand;
        // The active ticket is current through every refund already visible
        // when dispatch starts. A later refund invalidates this work and makes
        // the capacity wake eligible to schedule a fresh attempt.
        _activeCapacityGeneration = _vectorCapacity.IsAdmissionEnabled
            ? VectorNativeResourceAdmission.Shared.CapacityGeneration
            : snapshot.ActiveCapacityGeneration;
        _activeAnchorOffset = anchorOffset;
        var receiver = new WeakReference<LazyLayoutRealization>(this);
        Task operationTask = LazyLayoutWork.RunAsync(
            snapshot,
            band,
            visibleVectorBand,
            anchorOffset,
            pipelineGeneration,
            realizationGeneration,
            operationToken,
            dispatcher,
            _host.PerformanceSession as IMarkdownPerformanceSessionInternal,
            _host.DeferredSceneOwner,
            receiver,
            _publicationHandoff);
        _task = operationTask;
        _ = operationTask.ContinueWith(
            static (_, state) =>
            {
                var (owner, generation) =
                    ((WeakReference<LazyLayoutRealization> Receiver, long Generation))state!;
                if (owner.TryGetTarget(out var realization))
                    realization.MarkWorkDrained(generation);
            },
            (receiver, realizationGeneration),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
        _ = _host.RetireCancellationTokenSource(oldCts, oldTask);
    }

    /// <summary>Recovery clock retry of the current generation's staged completion.</summary>
    internal void TryPublishCompletion() => TryPublishCompletion(_generation);

    internal void TryPublishCompletion(long realizationGeneration)
        => _publicationHandoff.TryPublishAfterWorkDrains(realizationGeneration);

    private void MarkWorkDrained(long realizationGeneration)
        => _publicationHandoff.MarkWorkDrained(realizationGeneration);

    /// <summary>
    /// Applies a finished realization on the UI thread. Only the current
    /// generation publishes, and only for the snapshot and pipeline it ran
    /// against; any outcome releases waiting input.
    /// </summary>
    internal void Complete(
        LayoutSnapshot? snapshot,
        LazyLayoutWorkResult result,
        Exception? failure,
        double? anchorOffset,
        long pipelineGeneration,
        long realizationGeneration,
        bool canceled,
        CancellationToken operationToken)
    {
        // A canceled or superseded worker can still have mutated the currently
        // published tree. Recover that identity before dropping its result.
        if (_host.RequestGeometryRecoveryIfRequired(snapshot))
        {
            if (snapshot is not null)
                _pointerInput.OnLazyLayoutCompletion(snapshot, succeeded: false);
            return;
        }

        if (realizationGeneration != _generation)
        {
            if (snapshot is not null)
                _pointerInput.OnLazyLayoutCompletion(snapshot, succeeded: false);
            return;
        }

        var completedCts = _cts;
        var completedTask = _task;
        LazyLayoutBand attemptedVisibleBand = _activeVisibleVectorBand;
        long attemptedCapacityGeneration = snapshot?.ActiveCapacityGeneration ??
            _activeCapacityGeneration;
        // Retirement requests cancellation as part of disposal. Capture the
        // operation's outcome first so a successful completion isn't mistaken
        // for superseded work merely because this method is now retiring its CTS.
        bool operationWasCanceled = canceled || operationToken.IsCancellationRequested;
        _cts = null;
        _task = null;
        _activeSnapshot = null;
        _activeRevision = -1;
        _activeAnchorOffset = null;
        _ = _host.RetireCancellationTokenSource(completedCts, completedTask);

        // A current canceled or obsolete operation still owns its CTS/task
        // fields. Retire those before dropping its result, or the timer will
        // keep observing a completed but permanently active operation.
        if (_lifetime.IsDisposed ||
            _lifetime.IsUnloaded ||
            operationWasCanceled ||
            pipelineGeneration != _host.PipelineGeneration ||
            snapshot is null ||
            !ReferenceEquals(snapshot, _published.Snapshot))
        {
            _host.StopRecoveryClockIfIdle();
            if (snapshot is not null)
                _pointerInput.OnLazyLayoutCompletion(snapshot, succeeded: false);
            return;
        }

        // Record only a completed attempt.
        _vectorCapacity.RecordAttempt(attemptedVisibleBand, attemptedCapacityGeneration);

        if (failure is not null)
        {
            _host.NotifyVisibleDeferredSceneItemStatusMayHaveChanged();
            if (GraphicsDeviceErrors.IsDeviceLost(failure))
            {
                _host.HandleDeviceLost(failure);
            }
            else
            {
                MarkdownDiagnostics.WriteLine($"[MarkdownRendererControl] lazy layout realization failed: {failure}");
                _host.RequestFullRebuild();
            }
            _host.StopRecoveryClockIfIdle();
            _pointerInput.OnLazyLayoutCompletion(snapshot, succeeded: false);
            return;
        }

        _vectorCapacity.Synchronize(snapshot);

        if (result.Commit.LayoutRevision <= _layoutCommit.AppliedRevision)
        {
            // Moving the active band can finish visible readiness without a
            // geometry revision or resource change (for example, two already
            // measured text-only viewports). Publish that status transition
            // even when there is no new drawing lease to repaint.
            _host.NotifyVisibleDeferredSceneItemStatusMayHaveChanged();
            // This band is now measured and its native resources are
            // settled, but no layout commit follows to re-run the deferred
            // scene probe. A Mermaid or Math fallback that scrolled into this
            // band while it was being prepared would otherwise never be
            // scheduled and keep showing its source as "Loading".
            _host.ScheduleVisibleDeferredScenes();
            if (result.Commit.NativeSceneResourcesChanged)
            {
                // A prepared/retired drawing lease changes only the raster
                // surface. Repaint without rebuilding layout or disturbing
                // selection, focus, source mapping, or scroll position.
                _host.InvalidateAutomationLayout();
                _host.InvalidateCanvas();
            }

            // A focus request can arrive while an already-measured band is
            // publishing its completion. Re-evaluate it even when that worker
            // produced no newer layout revision.
            _ = _focusCommit.ResumePendingLazy(snapshot);
            _host.TryResumePendingScrollToBlock(snapshot, pipelineGeneration);
            _pointerInput.OnLazyLayoutCompletion(snapshot, succeeded: true);
            _host.StopRecoveryClockIfIdle();
            return;
        }

        // A user can jump to an already-measured band while this worker is
        // finishing. That jump need not queue replacement work, so the old
        // completion still arrives. Never restore its captured scroll anchor
        // over a newer viewport position.
        bool restoreAnchor = LazyLayoutScrollAnchorPolicy.ShouldRestore(
            anchorOffset, _surface.Scroll?.VerticalOffset, _host.IsScrollViewIntermediate);
        _host.ApplyLazyLayoutCommit(snapshot, restoreAnchor ? result.ScrollAnchor : null);
        _vectorCapacity.Synchronize(snapshot);
        if (result.Commit.NativeSceneResourcesChanged)
            _host.NotifyVisibleDeferredSceneItemStatusMayHaveChanged();
        _pointerInput.OnLazyLayoutCompletion(snapshot, succeeded: true);
        _host.StopRecoveryClockIfIdle();
    }

    /// <summary>
    /// Drops pending and active realization. Superseding work is not snapshot
    /// replacement, so an already committed publication is kept.
    /// </summary>
    internal void Cancel()
    {
        // Superseding work is not snapshot replacement. Preserve an already
        // committed geometry publication if the replacement subsequently fails.
        long generation = ++_generation;
        _publicationHandoff.Reset(generation);
        ++_dispatchTicket;
        _dispatchQueued = false;
        _pendingSnapshot = null;
        _activeSnapshot = null;
        _activeRevision = -1;
        _pendingVisibleVectorBand = default;
        _activeAnchorOffset = null;
        _hasPendingBand = false;
        _pendingPreserveScrollAnchor = false;
        _focus.ClearLazyFocus();

        var oldCts = _cts;
        var oldTask = _task;
        _cts = null;
        _task = null;
        _ = _host.RetireCancellationTokenSource(oldCts, oldTask);
        _host.StopRecoveryClockIfIdle();
    }
}
