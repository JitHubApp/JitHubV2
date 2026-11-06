using System;
using System.Threading;
using System.Threading.Tasks;
using MarkdownRenderer.Controls.Focus;
using MarkdownRenderer.Controls.Input;
using MarkdownRenderer.Controls.Pipeline;
using MarkdownRenderer.Controls.Threading;
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
/// completion all read and write the same pending/active band state. The
/// viewport band policy lives in <see cref="LazyLayoutViewportRequests"/>,
/// applying a current result in <see cref="LazyLayoutCompletionPublisher"/>
/// and the worker side in <see cref="LazyLayoutWork"/>.
/// </remarks>
internal sealed class LazyLayoutRealization
{
    private readonly ILazyLayoutHost _host;
    private readonly MarkdownViewLifetime _lifetime;
    private readonly PublishedLayout _published;
    private readonly MarkdownViewSurface _surface;
    private readonly RecoveryClock _recoveryClock;
    private readonly VectorCapacityRetry _vectorCapacity;
    private readonly PointerInputRouter _pointerInput;
    private readonly KeyboardFocusModel _focus;
    private readonly WeakSnapshotPublicationDrainFence<LayoutSnapshot> _publicationHandoff = new();
    private readonly LazyLayoutViewportRequests _viewportRequests;
    private readonly LazyLayoutCompletionPublisher _completion;

    private RetirableCancellationTokenSource? _cts;
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

    internal LazyLayoutRealization(
        ILazyLayoutHost host,
        MarkdownViewLifetime lifetime,
        PublishedLayout published,
        LayoutCommitState layoutCommit,
        LayoutCommitPublisher layoutPublisher,
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
        _surface = surface;
        _recoveryClock = recoveryClock;
        _vectorCapacity = vectorCapacity;
        _pointerInput = pointerInput;
        _focus = focus;
        _viewportRequests = new LazyLayoutViewportRequests(
            this, host, published, surface, viewport, vectorCapacity);
        _completion = new LazyLayoutCompletionPublisher(
            host, layoutCommit, layoutPublisher, surface, vectorCapacity, pointerInput, focusCommit);
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
    internal double ScrollVelocity => _viewportRequests.ScrollVelocity;

    /// <summary>Forgets the scroll velocity, so the next viewport sample starts fresh.</summary>
    internal void ResetScrollVelocity() => _viewportRequests.ResetScrollVelocity();

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
    internal void QueueForViewport(bool forceCapacityRetry = false) =>
        _viewportRequests.QueueForViewport(forceCapacityRetry);

    /// <summary>Queues measurement of the band a paint request reached.</summary>
    internal void QueueForPaintRegion(Rect region) => _viewportRequests.QueueForPaintRegion(region);

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

        if (IsCoveredByActiveWork(snapshot, band, visibleVectorBand, preserveScrollAnchor))
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

        DispatchPendingBand();
    }

    /// <summary>
    /// True when the active, uncanceled work for <paramref name="snapshot"/>
    /// already measures <paramref name="band"/> at the current layout
    /// revision, capacity generation, visible vector band and scroll anchor.
    /// </summary>
    private bool IsCoveredByActiveWork(
        LayoutSnapshot snapshot,
        LazyLayoutBand band,
        LazyLayoutBand visibleVectorBand,
        bool preserveScrollAnchor) =>
        _cts is { IsCancellationRequested: false } &&
            ReferenceEquals(snapshot, _activeSnapshot) &&
            // Same-band work from an older image-reflow revision must not
            // absorb the retry needed to refresh scene indexes/graph bindings.
            _activeRevision == snapshot.LayoutRevision &&
            (_vectorCapacity.Admission is not { } admission ||
             admission.CapacityGeneration <= _activeCapacityGeneration) &&
            band.Top >= _activeBand.Top &&
            band.Bottom <= _activeBand.Bottom &&
            (!_vectorCapacity.IsAdmissionEnabled ||
             visibleVectorBand.Top == _activeVisibleVectorBand.Top &&
             visibleVectorBand.Bottom == _activeVisibleVectorBand.Bottom) &&
            (!preserveScrollAnchor ||
             (_surface.Scroll is not null &&
              _activeAnchorOffset is { } activeOffset &&
              Math.Abs(_surface.Scroll.VerticalOffset - activeOffset) < 0.5));

    /// <summary>
    /// Queues the low-priority dispatch that starts the pending band, after
    /// making sure the recovery clock can retry a rejected or withheld one.
    /// </summary>
    private void DispatchPendingBand()
    {
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
        var dispatcher = _host.UiScheduler;
        var receiver = new WeakReference<LazyLayoutRealization>(this);
        try
        {
            if (dispatcher is null || !dispatcher.TryEnqueue(
                    MarkdownUiPriority.Low,
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
        var cts = RetirableCancellationTokenSource.CreateLinked(_host.PipelineToken);
        long realizationGeneration = ++_generation;
        _publicationHandoff.Reset(realizationGeneration);
        long pipelineGeneration = _host.PipelineGeneration;
        CancellationToken operationToken = cts.Token;
        double? anchorOffset = preserveScrollAnchor && _surface.Scroll is not null
            ? _surface.Scroll.VerticalOffset
            : null;
        var dispatcher = _host.UiScheduler;

        _cts = cts;
        _activeSnapshot = snapshot;
        _activeRevision = snapshot.LayoutRevision;
        _activeBand = band;
        _activeVisibleVectorBand = visibleVectorBand;
        // The active ticket is current through every refund already visible
        // when dispatch starts. A later refund invalidates this work and makes
        // the capacity wake eligible to schedule a fresh attempt.
        _activeCapacityGeneration = _vectorCapacity.Admission is { } admission
            ? admission.CapacityGeneration
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

        _completion.Publish(
            snapshot,
            result,
            failure,
            anchorOffset,
            pipelineGeneration,
            attemptedVisibleBand,
            attemptedCapacityGeneration);
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
