using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using MarkdownRenderer.Controls.Input;
using MarkdownRenderer.Diagnostics;
using RebuildReason = MarkdownRenderer.Controls.MarkdownRebuildCoordinatorReason;

namespace MarkdownRenderer.Controls.Pipeline;

/// <summary>
/// Schedules rebuild pipelines: merges requests, dispatches them at their
/// priority, fences superseded generations at request time, and owns the
/// active pipeline's cancellation scope and lifetime task. The control runs
/// the pipeline itself through <see cref="IRebuildSchedulerHost"/>.
/// </summary>
internal sealed class RebuildScheduler
{
    private readonly IRebuildSchedulerHost _host;
    private readonly MarkdownViewLifetime _lifetime;
    private readonly MarkdownViewSurface _surface;
    private readonly PointerInputRouter _pointerInput;
    private readonly PipelineCancellationRetirement _cancellationRetirement;
    private readonly MarkdownRebuildCoordinator _coordinator = new();
    private MarkdownRebuildDispatchState _dispatchState;
    private long _lastDispatchTicket;
    private readonly RejectedRebuildRequest _rejected = new();
    private long _pendingRequestTimestamp;
    private volatile CancellationTokenSource? _cts;
    private Task? _task;
    private long _generation;
    private long _activeGeneration;

    internal RebuildScheduler(
        IRebuildSchedulerHost host,
        MarkdownViewLifetime lifetime,
        MarkdownViewSurface surface,
        PointerInputRouter pointerInput,
        PipelineCancellationRetirement cancellationRetirement)
    {
        _host = host;
        _lifetime = lifetime;
        _surface = surface;
        _pointerInput = pointerInput;
        _cancellationRetirement = cancellationRetirement;
    }

    /// <summary>The newest requested pipeline generation; publications of older generations are stale.</summary>
    internal long Generation => _generation;

    /// <summary>The token of the pipeline that owns the published snapshot, or none.</summary>
    internal CancellationToken Token => _cts?.Token ?? CancellationToken.None;

    /// <summary>The lifetime task of the active pipeline, published before it starts.</summary>
    internal Task? ActiveTask => _task;

    internal bool HasPending => _coordinator.HasPending;

    internal bool HasPendingOrBuilding => _coordinator.HasPendingOrBuilding;

    /// <summary>
    /// The active pipeline's token when <paramref name="generation"/> is still
    /// that pipeline, otherwise an already-cancelled token.
    /// </summary>
    internal CancellationToken GetActiveToken(long generation)
    {
        CancellationTokenSource? cancellation = _cts;
        return generation > 0 &&
            generation == _activeGeneration &&
            cancellation is not null
                ? cancellation.Token
                : new CancellationToken(canceled: true);
    }

    /// <summary>
    /// True while a rebuild is pending or building, so image relayout should
    /// yield to it. Settles a completed pipeline whose UI completion was
    /// withheld first, so the building marker cannot stay wedged.
    /// </summary>
    internal bool DefersToRebuild()
    {
        // A replacement build's prepublished task is the actual drain barrier;
        // a withheld UI completion must not leave the coordinator's building
        // marker wedged.
        if (_task is { IsCompleted: true })
        {
            OnPipelineCompleted(_activeGeneration);
            // The worker may already have cleared that marker after enqueue
            // rejection. Retry its pending drain without creating a request.
            QueuePending();
        }
        return _coordinator.HasPendingOrBuilding;
    }

    /// <summary>Queues the pending request unless a dispatch is already queued.</summary>
    internal void QueuePendingIfIdle()
    {
        if (!_dispatchState.IsQueued && !_lifetime.IsUnloaded)
            QueuePending();
    }

    /// <summary>
    /// Retires the active pipeline's cancellation scope and forgets every
    /// pending, queued or rejected request, for unload.
    /// </summary>
    internal void Retire()
    {
        var oldCts = _cts;
        var oldTask = _task;
        _cts = null;
        _task = null;
        _activeGeneration = 0;
        _cancellationRetirement.Retire(oldCts, oldTask);
        _dispatchState = default;
        _coordinator.Reset();
        _rejected.Reset();
    }

    /// <summary>
    /// Requests a rebuild for <paramref name="reason"/>. Off the UI thread the
    /// request is marshalled; a rejected dispatch is remembered and retried.
    /// Source, configuration, width and theme requests fence the active
    /// generation at once; scene-only requests wait in the coalescing queue.
    /// </summary>
    internal void Request(RebuildReason reason)
    {
        if (_lifetime.IsDisposed)
            return;

        var dispatcher = _host.DispatcherQueue;
        if (dispatcher is not null && !dispatcher.HasThreadAccess)
        {
            bool enqueued;
            try
            {
                enqueued = dispatcher.TryEnqueue(() => Request(reason));
            }
            catch (Exception exception)
            {
                enqueued = false;
                MarkdownDiagnostics.WriteLine(
                    $"[MarkdownRendererControl] Dispatcher rejected cross-thread rebuild request: {exception.Message}");
            }

            if (!enqueued)
            {
                if (!_lifetime.IsDisposed && !_lifetime.IsUnloaded)
                {
                    RecordRejectedCrossThread(reason);
                    MarkdownDiagnostics.WriteLine("[MarkdownRendererControl] Dispatcher rejected cross-thread rebuild request.");
                }
            }
            return;
        }

        if (_lifetime.IsUnloaded)
            return;

        DrainRejectedCrossThread();

        MarkdownRebuildRequestDisposition disposition = _coordinator.Request(reason);

        var performance = MarkdownPerformanceEventSource.Log;
        if (_pendingRequestTimestamp == 0 && performance.IsMeasurementEnabled())
            _pendingRequestTimestamp = Stopwatch.GetTimestamp();

        // A materialized scene is already immutable and has passed its plan
        // generation fence. While another pipeline is active, remember one
        // follow-up only; do not revoke its probes, image/lazy work, generation,
        // or cancellation scope.
        if (disposition == MarkdownRebuildRequestDisposition.Deferred)
        {
            // Usually the active build owns this follow-up until completion.
            // If a genuine Full/Restyle request is already pending (for
            // example after a rejected dispatch), let it retry without turning
            // this scene event into a supersession.
            QueuePending();
            return;
        }

        // When no pipeline is active, a scene-only request can wait in the
        // ordinary coalescing queue too. Do not supersede the currently
        // published snapshot until the replacement pipeline is actually
        // allowed to start (for example, after an active pointer gesture).
        if (reason == RebuildReason.SceneMaterialized)
        {
            QueuePending();
            return;
        }

        // Source, configuration, width, and theme requests invalidate the
        // progressive scene-plan generation. Scene-only work returned above.
        if (MarkdownRebuildDispatchPolicy.AdvancesDeferredSceneGeneration(
                reason == RebuildReason.SceneMaterialized))
        {
            _host.AdvanceDeferredSceneGeneration();
        }

        // Fence the currently active generation at request time, before the
        // queued rebuild callback can be delayed by UI work. This makes a
        // superseded pipeline unable to publish even while its parse worker is
        // still unwinding, and signals cancellation with request-time latency.
        ++_generation;
        var supersededCts = _cts;
        var supersededTask = _task;
        long supersededGeneration = _activeGeneration;
        _cts = null;
        _task = null;
        _activeGeneration = 0;

        long supersessionTimestamp = 0;
        if (supersededCts is not null &&
            supersededTask is { IsCompleted: false } &&
            supersededGeneration > 0 &&
            performance.IsMeasurementEnabled())
        {
            supersessionTimestamp = Stopwatch.GetTimestamp();
            performance.PipelineSuperseded(supersededGeneration);
        }

        _cancellationRetirement.Retire(
            supersededCts,
            supersededTask,
            supersededGeneration,
            supersessionTimestamp);
        QueuePending();
    }

    /// <summary>Remembers the strongest request whose cross-thread dispatch was rejected.</summary>
    internal void RecordRejectedCrossThread(RebuildReason reason) => _rejected.Record(reason);

    private void DrainRejectedCrossThread()
    {
        var dispatcher = _host.DispatcherQueue;
        if (_lifetime.IsDisposed || _lifetime.IsUnloaded || dispatcher is not null && !dispatcher.HasThreadAccess)
            return;

        if (_rejected.TryTake(out RebuildReason reason))
            Request(reason);
    }

    /// <summary>Schedules the pending request, if any, at its dispatch priority.</summary>
    internal void QueuePending()
    {
        DrainRejectedCrossThread();
        if (_lifetime.IsDisposed || _lifetime.IsUnloaded ||
            !_coordinator.TryGetDispatchReason(out MarkdownRebuildCoordinatorReason pendingReason) ||
            !_host.IsLoaded || _surface.Canvas is null || _surface.Overlay is null || _surface.Root is null)
        {
            return;
        }

        MarkdownRebuildDispatchPriority dispatchPriority =
            MarkdownRebuildDispatchPolicy.SelectPriority(
                pendingReason == MarkdownRebuildCoordinatorReason.Full);
        if (!_dispatchState.ShouldSchedule(dispatchPriority))
            return;

        var dispatcher = _host.DispatcherQueue;
        if (dispatcher is null)
        {
            _dispatchState = default;
            ProcessQueuedCore();
            return;
        }

        long dispatchTicket = NextDispatchTicket();
        bool enqueued = false;
        try
        {
            enqueued = dispatcher.TryEnqueue(
                dispatchPriority.ToDispatcherQueuePriority(),
                () => ProcessQueued(dispatchTicket));
        }
        catch (Exception exception)
        {
            MarkdownDiagnostics.WriteLine(
                $"[MarkdownRendererControl] Dispatcher rejected rebuild callback: {exception.Message}");
        }

        _dispatchState = _dispatchState.AfterEnqueueAttempt(
            dispatchTicket,
            dispatchPriority,
            enqueued);
    }

    private long NextDispatchTicket()
    {
        unchecked
        {
            ++_lastDispatchTicket;
            if (_lastDispatchTicket == 0)
                ++_lastDispatchTicket;
        }

        return _lastDispatchTicket;
    }

    private void ProcessQueued(long dispatchTicket)
    {
        if (!_dispatchState.IsCurrent(dispatchTicket))
            return;

        _dispatchState = default;
        ProcessQueuedCore();
    }

    private void ProcessQueuedCore()
    {
        if (_lifetime.IsDisposed || _lifetime.IsUnloaded || !_host.IsLoaded)
            return;

        // Image completion, theme changes, and width changes may request a
        // relayout while the user is actively selecting text. Releasing pointer
        // capture here terminates the gesture and visibly drops the selection.
        // Keep the latest merged rebuild request pending until pointer release.
        if (_pointerInput.HasSnapshotInteraction)
            return;

        if (!_coordinator.TryGetDispatchReason(
                out MarkdownRebuildCoordinatorReason pendingReason))
        {
            return;
        }

        if (pendingReason == MarkdownRebuildCoordinatorReason.SceneMaterialized)
        {
            // Scene-only requests do not supersede an in-flight/published
            // generation until dispatch reaches this safe start boundary.
            ++_generation;
        }

        long generation = _generation;
        if (!_coordinator.TryBeginPipeline(
                generation,
                out MarkdownRebuildCoordinatorReason coordinatorReason))
        {
            return;
        }

        RebuildReason reason = coordinatorReason;
        _host.CancelSnapshotWork();

        string effectiveSource = _host.EffectiveSource;
        // Keep the committed selection attached to the still-published snapshot
        // while its replacement is prepared. The commit compares immutable
        // document identities and clears selection only if replacement succeeds.
        _host.ResetInteractionForRebuild();
        StartPipeline(generation, reason, sourceUtf16Bytes: (long)effectiveSource.Length * sizeof(char));
    }

    /// <summary>
    /// Opens generation <paramref name="generation"/>'s cancellation scope and
    /// lifetime task, retires the previous scope, and runs the pipeline.
    /// </summary>
    private void StartPipeline(long generation, RebuildReason reason, long sourceUtf16Bytes)
    {
        // A deferred scene may be the first request after the preceding
        // pipeline completed. Keep that published token alive until this new
        // snapshot actually begins replacing it, then retire it with its task.
        var previousCts = _cts;
        var previousTask = _task;
        var cts = new CancellationTokenSource();
        _cts = cts;
        _activeGeneration = generation;
        var lifetime = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        // Publish a lifetime task before entering RebuildAsync: a synchronous
        // RenderCompleted callback can reenter RequestRebuild before the async
        // method returns its Task. Supersession must still wait for this run.
        _task = lifetime.Task;
        if (previousCts is not null && !ReferenceEquals(previousCts, cts))
            _cancellationRetirement.Retire(previousCts, previousTask);

        var performance = MarkdownPerformanceEventSource.Log;
        long pipelineStartTimestamp = performance.IsMeasurementEnabled()
            ? Interlocked.Exchange(ref _pendingRequestTimestamp, 0)
            : 0;
        if (pipelineStartTimestamp == 0 && performance.IsMeasurementEnabled())
            pipelineStartTimestamp = Stopwatch.GetTimestamp();
        if (pipelineStartTimestamp != 0)
            performance.PipelineStarted(generation, sourceUtf16Bytes);

        // Capture the borrowed queue while this start boundary is on the UI
        // thread. Completion can run on the pool after unload/disposal and must
        // not query the control's native dependency object again there.
        Microsoft.UI.Dispatching.DispatcherQueue? completionDispatcher = _host.DispatcherQueue;
        Task task;
        try
        {
            task = _host.RunRebuildAsync(
                cts.Token,
                reason,
                generation,
                pipelineStartTimestamp,
                sourceUtf16Bytes);
        }
        catch (Exception exception)
        {
            MarkdownDiagnostics.WriteLine($"[MarkdownRendererControl] Rebuild startup failed: {exception}");
            task = Task.FromException(exception);
        }

        _ = task.ContinueWith(
            completed => CompleteTask(completed, lifetime, generation, completionDispatcher),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private void CompleteTask(
        Task completed,
        TaskCompletionSource lifetime,
        long generation,
        Microsoft.UI.Dispatching.DispatcherQueue? dispatcher)
    {
        if (completed.IsFaulted)
        {
            AggregateException? pipelineFailure = completed.Exception;
            Exception? diagnosticFailure = pipelineFailure is { InnerExceptions.Count: > 0 }
                ? pipelineFailure.InnerExceptions[0]
                : pipelineFailure;
            if (diagnosticFailure is not null)
            {
                MarkdownDiagnostics.WriteLine(
                    $"[MarkdownRendererControl] Rebuild faulted: {diagnosticFailure.GetType().Name} " +
                    $"(0x{diagnosticFailure.HResult:X8}).");
            }
        }

        // `_task` is the barrier published before RebuildAsync starts;
        // preserve the worker's exact terminal state for teardown observers.
        MarkdownRebuildCoordinator.CompletePipelineLifetime(completed, lifetime);

        // The task is the retirement barrier for the CTS. Do not clear
        // _cts/_activeGeneration here: they identify the
        // published snapshot's image and lazy-layout lifetime after rendering.

        if (dispatcher is null)
        {
            // A loaded WinUI control normally has a dispatcher. If it has been
            // torn down, lifecycle cleanup has already reset the coordinator.
            _coordinator.CompletePipeline(generation);
            return;
        }

        if (dispatcher.HasThreadAccess)
        {
            OnPipelineCompleted(generation);
            return;
        }

        bool enqueued = false;
        try
        {
            enqueued = dispatcher.TryEnqueue(() => OnPipelineCompleted(generation));
        }
        catch (Exception exception)
        {
            MarkdownDiagnostics.WriteLine(
                $"[MarkdownRendererControl] Dispatcher rejected rebuild completion callback: {exception.Message}");
        }

        if (!enqueued)
        {
            // Keep pending state intact. A later pointer release or rebuild
            // request can retry scheduling; unloading resets it outright.
            _coordinator.CompletePipeline(generation);
            MarkdownDiagnostics.WriteLine("[MarkdownRendererControl] Dispatcher did not accept rebuild completion callback.");
        }
    }

    private void OnPipelineCompleted(long generation)
    {
        if (_lifetime.IsDisposed || _lifetime.IsUnloaded || !_coordinator.CompletePipeline(generation))
            return;

        DrainRejectedCrossThread();
        if (_coordinator.HasPending)
        {
            // ProcessQueuedCore allocates the scene-only generation at
            // the moment the replacement pipeline can actually start.
            QueuePending();
        }
        else
        {
            // A failed/canceled current-generation rebuild did not publish the
            // snapshot needed to restore handles. Do not carry this intent into
            // an unrelated future rebuild.
            _host.DiscardTouchSelectionRestore();
        }
    }
}
