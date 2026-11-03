using System.Runtime.InteropServices;
using MarkdownRenderer.Images;

namespace MarkdownRenderer.Svg.Resvg.Internal;

/// <summary>
/// Owns the pool's worker slots and their single state lock: bounded
/// admission and priority dispatch, detached font-wait accounting, worker
/// startup and retirement, and secondary-worker policy. Its lock-free
/// collaborators (the queue, font-wait ledger, startup breaker, idle
/// retirement and canceled-work preemption) are called only under
/// <c>_stateLock</c>, and timed steps re-enter through the host interfaces.
/// Nothing here calls out while holding the lock except the synchronous lease
/// completion it has always run. The file stays near 1,000 lines because
/// admission, dispatch and startup all read and write the same locked slot
/// state; splitting them further would share the lock across classes.
/// </summary>
internal sealed class WorkerScheduler :
    ISecondaryWorkerWarmupHost,
    ISecondaryIdleRetirementHost,
    ICanceledWorkPreemptionHost,
    IAsyncDisposable,
    IDisposable
{
    private readonly ResvgMarkdownSvgRendererOptions _options;
    private readonly WorkerRequestFactory _requests;
    private readonly WorkerProcessFleet _fleet;
    private readonly SecondaryWorkerWarmup _warmup;
    private readonly SecondaryIdleRetirement _idleRetirement;
    private readonly CanceledWorkPreemption _preemption;
    private readonly Action<WorkerFontWaitReservation> _promoteDetachedFontWait;
    private readonly Action<WorkerFontWaitReservation> _releaseDetachedFontWait;
    private readonly WorkerSchedulingQueue<PendingWork> _queue = new();
    private readonly WorkerStartupCircuitBreaker _startupFailures = new();
    private readonly object _stateLock = new();
    private readonly WorkerSlot _primary = new(isSecondary: false);
    private readonly DetachedFontWaitLedger _detachedFontWaits;
    private readonly TimeProvider _timeProvider;
    private readonly WorkerEnergySaver _energySaver;
    private readonly bool _supportsSecondaryHardware;
    private readonly WorkerPolicyRecorder _policyRecorder;
    private readonly IWorkerAdmissionObserver? _admissionObserver;
    private readonly CancellationTokenSource _disposeCancellation = new();
    private TaskCompletionSource _queueCapacityChanged =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private WorkerSlot? _secondary;
    private int _disposed;

    internal WorkerScheduler(
        ResvgMarkdownSvgRendererOptions options,
        WorkerRequestFactory requests,
        IWorkerProcessLauncher launcher,
        TimeProvider timeProvider,
        Func<bool> isEnergySaverOn,
        bool supportsSecondaryHardware,
        SvgWorkerPolicyTrace workerPolicyTrace,
        DetachedFontWaitLedger? detachedFontWaits = null,
        IWorkerAdmissionObserver? admissionObserver = null)
    {
        _admissionObserver = admissionObserver;
        _options = options;
        _requests = requests;
        _fleet = new WorkerProcessFleet(launcher, options, supportsSecondaryHardware);
        _detachedFontWaits = detachedFontWaits ?? new DetachedFontWaitLedger();
        _timeProvider = timeProvider;
        _energySaver = new WorkerEnergySaver(isEnergySaverOn);
        _supportsSecondaryHardware = supportsSecondaryHardware;
        _policyRecorder = new WorkerPolicyRecorder(workerPolicyTrace, requests, _primary);
        _warmup = new SecondaryWorkerWarmup(this, requests, options);
        _idleRetirement = new SecondaryIdleRetirement(this, _fleet, timeProvider);
        _preemption = new CanceledWorkPreemption(this, _fleet, timeProvider);
        _promoteDetachedFontWait = PromoteDetachedFontWait;
        _releaseDetachedFontWait = ReleaseDetachedFontWait;
    }

    internal bool IsDisposed => Volatile.Read(ref _disposed) != 0;

    /// <summary>Canceled when the pool shuts down.</summary>
    internal CancellationToken DisposeToken => _disposeCancellation.Token;

    /// <summary>Serializes font-catalog readiness probes for the primary worker.</summary>
    internal SemaphoreSlim PrimaryFontCatalogGate => _primary.FontCatalogGate;

    /// <summary>The distinct live workers in the primary and secondary slots.</summary>
    /// <returns>A snapshot taken under the state lock.</returns>
    internal WindowsWorkerProcess[] GetLiveWorkers()
    {
        WindowsWorkerProcess[] workers;
        lock (_stateLock)
        {
            workers = new[] { _primary.Worker, _secondary?.Worker }
                .Where(worker => worker is not null && !worker.HasExited)
                .OfType<WindowsWorkerProcess>()
                .Distinct()
                .ToArray();
        }
        return workers;
    }

    internal async Task WarmUpAsync(CancellationToken cancellationToken)
    {
        using WorkerLease lease = await AcquireAsync(
            WorkerOperation.Hello,
            MarkdownSvgRenderPriority.Overscan,
            cancellationToken).ConfigureAwait(false);
        _ = await EnsureWorkerAsync(lease, 0, 0, 0, 0, 0).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        PendingWork[] queued;
        WindowsWorkerProcess? primary;
        WindowsWorkerProcess? secondary;
        lock (_stateLock)
        {
            queued = _queue.Drain();
            PulseQueueCapacityChangedLocked();
            primary = _primary.Worker;
            secondary = _secondary?.Worker;
            _primary.Worker = null;
            if (_secondary is not null)
            {
                SecondaryIdleRetirement.CancelLocked(_secondary);
                _secondary.Worker = null;
            }
        }

        List<Exception>? failures = null;
        // Wake readiness callers parked outside the bounded work queue (font
        // gate, detached admission, or retry delay) before tearing down workers.
        try { _disposeCancellation.Cancel(); }
        catch (Exception exception) { (failures ??= []).Add(exception); }

        foreach (PendingWork pending in queued)
        {
            try { pending.DisposePromotionRegistration(); }
            catch (Exception exception) { (failures ??= []).Add(exception); }
            pending.Completion.TrySetException(new ObjectDisposedException(nameof(WorkerPool)));
        }
        _fleet.ShutDown(primary, secondary, ref failures);
        if (failures is not null)
            throw new AggregateException(failures);
    }

    public async ValueTask DisposeAsync()
    {
        try { Dispose(); }
        catch (Exception) { /* The shared barrier reports cleanup after owners drain. */ }
        await _fleet.Shutdown.ConfigureAwait(false);
    }

    internal async Task<WorkerLease> AcquireAsync(
        WorkerOperation operation,
        MarkdownSvgRenderPriority priority,
        CancellationToken cancellationToken,
        bool requiresFontCatalog = false,
        long preferredWorkerInstanceId = 0,
        bool isSecondaryFontProbe = false,
        WorkerFontWaitReservation? fontWaitReservation = null,
        MarkdownSvgRenderDemand? demandTicket = null)
    {
        _admissionObserver?.AdmissionRequested(operation, cancellationToken);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, typeof(WorkerPool));
        cancellationToken.ThrowIfCancellationRequested();
        var pending = new PendingWork(
            operation,
            priority,
            cancellationToken,
            requiresFontCatalog,
            preferredWorkerInstanceId,
            isSecondaryFontProbe,
            demandTicket);
        WorkerLease? admitted = null;
        try
        {
            // Registration is installed before queue publication. An immediate
            // callback may find no membership and return, so PumpLocked also
            // synchronizes the monotonic ticket immediately before dispatch.
            if (demandTicket is not null)
            {
                pending.SetPromotionRegistration(demandTicket.RegisterPromotion(
                    () => PromotePendingVisible(pending)));
            }

            while (true)
            {
                Task? capacityWait;
                lock (_stateLock)
                    capacityWait = EnqueueOrWaitLocked(pending, fontWaitReservation, cancellationToken);

                if (capacityWait is not null)
                {
                    await capacityWait.WaitAsync(cancellationToken).ConfigureAwait(false);
                    continue;
                }

                admitted = await WorkerAdmissionTasks.AwaitAdmissionAsync(
                    pending.Completion.Task,
                    pending.CancellationRegistration).ConfigureAwait(false);
                return admitted;
            }
        }
        finally
        {
            pending.DisposePromotionRegistration();
            if (admitted is null)
                pending.CancellationRegistration.Dispose();
        }
    }

    /// <summary>
    /// Enqueues <paramref name="pending"/> and pumps when admission has room,
    /// consuming its font-wait reservation if it re-enters; otherwise returns
    /// the capacity signal to wait on. A failure after enqueue removes it.
    /// </summary>
    /// <param name="pending">The work requesting admission.</param>
    /// <param name="fontWaitReservation">The detached font-wait reservation it re-enters with, if any.</param>
    /// <param name="cancellationToken">The admission's cancellation.</param>
    /// <returns>The capacity wait, or <see langword="null"/> once enqueued.</returns>
    private Task? EnqueueOrWaitLocked(
        PendingWork pending,
        WorkerFontWaitReservation? fontWaitReservation,
        CancellationToken cancellationToken)
    {
        ThrowIfUnavailableLocked();
        bool consumesReservation = fontWaitReservation?.IsActive == true;
        bool canEnter = consumesReservation
            ? WorkerFontWaitAdmissionPolicy.CanReenter(_queue.Count, _options.QueueCapacity)
            : WorkerFontWaitAdmissionPolicy.CanAdmitNew(
                _queue.Count,
                _detachedFontWaits.Count,
                _options.QueueCapacity);
        if (!canEnter)
        {
            // A full queue is backpressure, not a property of the SVG: a
            // README with more images than the queue holds (flags, sponsor
            // avatars) must render them all. Wait for a dispatch,
            // cancellation or retirement to free a slot; the queue stays bounded.
            return _queueCapacityChanged.Task;
        }

        pending.SynchronizePriorityFromTicket();
        _queue.Enqueue(
            pending,
            pending.Priority,
            pending.Operation is WorkerOperation.Render or WorkerOperation.RasterDecode,
            pending.RequiresFontCatalog,
            isOpen: pending.Operation == WorkerOperation.Open,
            preferredWorkerInstanceId: pending.PreferredWorkerInstanceId,
            isSecondaryFontProbe: pending.IsSecondaryFontProbe);
        try
        {
            if (consumesReservation)
            {
                if (!fontWaitReservation!.TryConsume())
                    throw new InvalidOperationException("The SVG font-wait admission ticket was already consumed.");
                _detachedFontWaits.Consume(fontWaitReservation);
            }

            if (cancellationToken.CanBeCanceled)
                pending.RegisterCancellation(this, cancellationToken);
        }
        catch
        {
            _queue.Remove(pending);
            if (consumesReservation)
                PulseQueueCapacityChangedLocked();
            throw;
        }
        PumpLocked();
        return null;
    }

    internal WorkerFontWaitReservation ReserveDetachedFontWait(WorkerLease activeLease)
    {
        ArgumentNullException.ThrowIfNull(activeLease);
        lock (_stateLock)
        {
            ThrowIfUnavailableLocked();
            bool ownsActiveLease = ReferenceEquals(activeLease.Slot.Active, activeLease.Pending) && activeLease.Slot.Busy;
            bool alreadyReserved = activeLease.Pending.HasDetachedFontWaitReservation;
            // Admission was originally bounded for the pool's maximum active
            // slots. Preserve those already-admitted active requests as they
            // detach, even if another slot has just become idle.
            int maximumActiveSlots = _supportsSecondaryHardware ? 2 : 1;
            if (!WorkerFontWaitAdmissionPolicy.CanReserveFromActive(
                    _queue.Count,
                    _detachedFontWaits.Count,
                    _options.QueueCapacity,
                    maximumActiveSlots,
                    leaseIsActive: ownsActiveLease,
                    alreadyReserved: alreadyReserved))
            {
                if (!ownsActiveLease)
                    throw new InvalidOperationException("A detached SVG font wait must transfer an active worker lease.");
                if (alreadyReserved)
                    throw new InvalidOperationException("An active SVG worker lease can reserve only one detached font wait.");
                throw new MarkdownSvgException(
                    MarkdownSvgFailureReason.ResourceLimitExceeded,
                    "The SVG worker font-wait admission limit is full.");
            }
            return _detachedFontWaits.Reserve(activeLease.Pending, _releaseDetachedFontWait, _promoteDetachedFontWait);
        }
    }

    private void PromoteDetachedFontWait(WorkerFontWaitReservation reservation)
    {
        lock (_stateLock)
        {
            if (!_detachedFontWaits.TryPromote(reservation))
                return;
            PumpLocked();
        }
    }

    private void ReleaseDetachedFontWait(WorkerFontWaitReservation reservation)
    {
        lock (_stateLock)
        {
            if (!_detachedFontWaits.TryRelease(reservation))
                return;
            PulseQueueCapacityChangedLocked();
            if (Volatile.Read(ref _disposed) == 0)
                PumpLocked();
        }
    }

    private void PromotePendingVisible(PendingWork pending)
    {
        lock (_stateLock)
        {
            if (!_queue.Contains(pending) || !pending.PromoteVisibleFromTicket())
                return;
            _queue.PromoteVisible(pending);
            PumpLocked();
        }
    }

    private void PulseQueueCapacityChangedLocked()
    {
        TaskCompletionSource prior = _queueCapacityChanged;
        _queueCapacityChanged = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        prior.TrySetResult();
    }

    internal void Cancel(PendingWork pending)
    {
        lock (_stateLock)
        {
            if (_queue.Remove(pending))
            {
                pending.DisposePromotionRegistration();
                pending.IsCanceled = true;
                pending.Completion.TrySetCanceled(pending.CancellationToken);
                PulseQueueCapacityChangedLocked();
                PumpLocked();
                return;
            }
            pending.IsCanceled = true;
            EvaluateCanceledBlockingLocked();
        }
    }

    internal void RebindLeaseCancellation(
        WorkerLease lease,
        CancellationToken cancellationToken,
        CancellationToken admissionCancellationToken) =>
        lease.Pending.RebindCancellation(this, cancellationToken, admissionCancellationToken);

    private void PumpLocked()
    {
        if (Volatile.Read(ref _disposed) != 0)
            return;

        // Promotion callbacks are deliberately asynchronous. Reconcile the
        // monotonic ticket before queue counts, secondary-worker decisions,
        // and dequeue so a delayed callback can never dispatch stale overscan.
        _queue.PromoteVisibleWhere(static pending => pending.PromoteVisibleFromTicket());
        _detachedFontWaits.PromoteFromTickets();

        DropUnavailableSecondaryLocked();

        bool progressed;
        do
        {
            progressed = false;

            // Sample power policy once for this scheduling pass, then use the
            // same decision for both dispatch and its diagnostics snapshot.
            bool energySaverOn = _energySaver.IsOn(out bool energySaverQueryFailed);
            bool canUseSecondary = WorkerSchedulingPolicy.CanUseSecondary(
                RuntimeInformation.ProcessArchitecture,
                Environment.ProcessorCount,
                energySaverOn,
                energySaverQueryFailed);

            if (!_primary.Busy && TryAssignOldestSecondaryAffinityLocked(canUseSecondary))
                progressed = true;

            long primaryInstanceId = _primary.Worker is { HasExited: false } primaryWorker
                ? primaryWorker.InstanceId
                : 0;
            if (!_primary.Busy &&
                _queue.TryDequeuePrimary(primaryInstanceId, out PendingWork? primaryWork))
            {
                AssignLocked(_primary, primaryWork!);
                progressed = true;
            }

            DispatchSecondaryLocked(canUseSecondary, ref progressed);
            StartSecondaryIfNeededLocked(energySaverOn, energySaverQueryFailed, canUseSecondary, ref progressed);
        }
        while (progressed);

        EvaluateCanceledBlockingLocked();
    }

    // A worker that exited while idle is no longer an eligible secondary.
    // Remove it before evaluating the normal two-visible-render gate so a
    // replacement can never be started merely because a slot was retained.
    private void DropUnavailableSecondaryLocked()
    {
        if (_secondary is { Busy: false } staleSecondary &&
            staleSecondary.Readiness!.HasFailed &&
            _queue.Count == 0)
        {
            SecondaryIdleRetirement.CancelLocked(staleSecondary);
            _secondary = null;
        }
        else if (_secondary is { Busy: false } unavailableSecondary &&
                 (unavailableSecondary.Worker is null || unavailableSecondary.Worker.HasExited) &&
                 !unavailableSecondary.Readiness!.IsWarming &&
                 !unavailableSecondary.Readiness.HasFailed)
        {
            SecondaryIdleRetirement.CancelLocked(unavailableSecondary);
            unavailableSecondary.Readiness.FailWarmup();
            _secondary = null;
        }
    }

    // Gives a live, dispatchable secondary its font probe, then text work when
    // its catalog is current, or non-text work, or else warms its catalog when
    // visible text demand calls for it. Warming sets progress to whether the
    // slot became busy, exactly as an assigned dispatch would.
    private void DispatchSecondaryLocked(bool canUseSecondary, ref bool progressed)
    {
        if (_secondary is not { } secondary ||
            secondary.Worker is not { HasExited: false } secondaryWorker ||
            !WorkerSchedulingPolicy.CanDispatchSecondary(
                canUseSecondary,
                secondary.Readiness!.CanDispatch(requiresFontCatalog: false),
                secondary.Busy))
        {
            return;
        }

        if (_queue.TryDequeueSecondaryFontProbe(secondaryWorker.InstanceId, out PendingWork? fontProbe))
        {
            AssignLocked(secondary, fontProbe!);
            progressed = true;
        }
        // The secondary can help with text only after its own catalog
        // is ready. Until then, it may still consume visible non-text
        // renders without making text wait behind cold initialization.
        bool secondaryFontsReady = WorkerFontCatalogGeneration.IsReadyFor(
                secondaryWorker.ReadyFontGeneration,
                _requests.FontGeneration) &&
            WorkerFontCatalogGeneration.IsReadyFor(
                secondary.Readiness.FontCatalogReadyGeneration,
                _requests.FontGeneration);
        if (secondary.Busy)
        {
            // The font probe just assigned the slot. It is one short
            // control exchange; leave the renderer queue untouched.
        }
        else if (secondaryFontsReady)
        {
            if (_queue.TryDequeueSecondary(
                    secondaryWorker.InstanceId,
                    fontCatalogReady: true,
                    out PendingWork? secondaryWork))
            {
                AssignLocked(secondary, secondaryWork!);
                progressed = true;
            }
        }
        else if (_queue.TryDequeueSecondary(
                     secondaryWorker.InstanceId,
                     fontCatalogReady: false,
                     out PendingWork? nonTextWork))
        {
            AssignLocked(secondary, nonTextWork!);
            progressed = true;
        }
        else if (WorkerSchedulingPolicy.ShouldWarmSecondaryFontCatalog(
                     _primary.Worker?.ReadyFontGeneration ?? -1,
                     secondaryWorker.ReadyFontGeneration,
                     _requests.FontGeneration,
                     WorkerFontWaitAdmissionPolicy.CountVisibleDemand(
                         _queue.VisibleTextRenderCount,
                         _detachedFontWaits.VisibleTextRenderCount)))
        {
            BeginSecondaryWarmupLocked(secondary);
            progressed = secondary.Busy;
        }
    }

    // Records this pass's policy decision and, when visible demand warrants a
    // secondary that does not exist yet, creates and warms it. Starting sets
    // progress to whether the new slot became busy.
    private void StartSecondaryIfNeededLocked(
        bool energySaverOn,
        bool energySaverQueryFailed,
        bool canUseSecondary,
        ref bool progressed)
    {
        bool shouldStartSecondary = WorkerSchedulingPolicy.ShouldStartSecondary(
                canUseSecondary,
                _primary.Busy,
                _secondary is not null,
                WorkerFontWaitAdmissionPolicy.CountVisibleDemand(
                    _queue.VisibleRenderCount,
                    _detachedFontWaits.VisibleRenderCount));
        long decisionSequence = _policyRecorder.RecordEvaluated(
            _secondary,
            _queue.VisibleOpenCount,
            _queue.VisibleRenderCount,
            energySaverOn,
            energySaverQueryFailed,
            canUseSecondary,
            shouldStartSecondary,
            out SvgWorkerPolicySnapshot policySnapshot);
        if (shouldStartSecondary)
        {
            var newSecondary = new WorkerSlot(isSecondary: true);
            newSecondary.PolicyDecisionSequence = decisionSequence;
            newSecondary.PolicySnapshot = policySnapshot with
            {
                DecisionSequence = decisionSequence,
            };
            _secondary = newSecondary;
            RecordWorkerPolicyPhaseLocked(newSecondary, SvgWorkerPolicyPhase.StartRequested);
            BeginSecondaryWarmupLocked(_secondary);
            progressed = _secondary.Busy;
        }
    }

    private bool TryAssignOldestSecondaryAffinityLocked(bool machineEligible)
    {
        if (_secondary is not { } secondary ||
            secondary.Worker is not { HasExited: false } worker ||
            secondary.Readiness is not { } readiness)
        {
            return false;
        }

        bool canDispatch = WorkerSchedulingPolicy.CanDispatchSecondary(
            machineEligible,
            readiness.CanDispatch(requiresFontCatalog: false),
            secondary.Busy);
        long requestedGeneration = _requests.FontGeneration;
        bool fontCatalogReady =
            WorkerFontCatalogGeneration.IsReadyFor(worker.ReadyFontGeneration, requestedGeneration) &&
            WorkerFontCatalogGeneration.IsReadyFor(readiness.FontCatalogReadyGeneration, requestedGeneration);
        if (!_queue.TryDequeueOldestVisibleForSecondaryAffinity(
                worker.InstanceId,
                fontCatalogReady,
                canDispatch,
                out PendingWork? work))
        {
            return false;
        }

        AssignLocked(secondary, work!);
        return true;
    }

    private void BeginSecondaryWarmupLocked(WorkerSlot slot)
    {
        if (!slot.IsSecondary || slot.Busy || slot.Readiness is null || slot.Readiness.HasFailed)
            return;

        WindowsWorkerProcess? worker = slot.Worker is { HasExited: false } existing
            ? existing
            : null;
        bool workerReady = worker is not null;
        long fontCatalogReadyGeneration = workerReady ? worker!.ReadyFontGeneration : -1;
        if (!slot.Readiness.TryBeginWarmup(workerReady, fontCatalogReadyGeneration))
            return;

        var maintenance = new PendingWork(
            WorkerOperation.Hello,
            MarkdownSvgRenderPriority.Overscan,
            CancellationToken.None);
        AssignLocked(slot, maintenance);
        _ = Task.Run(async () =>
        {
            WorkerLease lease = await maintenance.Completion.Task.ConfigureAwait(false);
            await _warmup.WarmSecondaryAsync(slot, lease).ConfigureAwait(false);
        });
    }

    bool ISecondaryWorkerWarmupHost.ShouldWarmSecondaryFontCatalog(
        WorkerSlot slot,
        WindowsWorkerProcess? worker)
    {
        if (!_energySaver.AllowsSecondary())
            return false;

        lock (_stateLock)
        {
            if (Volatile.Read(ref _disposed) != 0 ||
                !ReferenceEquals(_secondary, slot) ||
                worker is null ||
                !ReferenceEquals(slot.Worker, worker) ||
                worker.HasExited)
            {
                return false;
            }

            long primaryGeneration = _primary.Worker is { HasExited: false } primaryWorker
                ? primaryWorker.ReadyFontGeneration
                : -1;
            if (slot.Readiness!.FontCatalogFailedGeneration >= _requests.FontGeneration)
                return false;
            return WorkerSchedulingPolicy.ShouldWarmSecondaryFontCatalog(
                primaryGeneration,
                worker.ReadyFontGeneration,
                _requests.FontGeneration,
                WorkerFontWaitAdmissionPolicy.CountVisibleDemand(
                    _queue.VisibleTextRenderCount,
                    _detachedFontWaits.VisibleTextRenderCount));
        }
    }

    private void AssignLocked(WorkerSlot slot, PendingWork pending)
    {
        pending.DisposePromotionRegistration();
        SecondaryIdleRetirement.CancelLocked(slot);
        slot.Busy = true;
        slot.Active = pending;
        pending.ActiveSlot = slot;
        if (!pending.Completion.TrySetResult(new WorkerLease(this, slot, pending)))
        {
            slot.Active = null;
            slot.Busy = false;
            pending.ActiveSlot = null;
        }
        PulseQueueCapacityChangedLocked();
    }

    internal async Task<WindowsWorkerProcess> EnsureWorkerAsync(
        WorkerLease lease,
        long diagnosticWorkId,
        ulong diagnosticRequestId,
        ulong diagnosticDocumentId,
        long diagnosticSourceBytes,
        long diagnosticTargetPixels)
    {
        WindowsWorkerProcess? stale = null;
        WorkerLifetimeRegistry<WindowsWorkerProcess>.Reservation startupOwnership;
        lock (_stateLock)
        {
            ThrowIfUnavailableLocked();
            if (lease.Slot.Worker is { HasExited: false } existing)
                return existing;
            startupOwnership = _fleet.ReserveStartup();
            stale = lease.Slot.Worker;
            lease.Slot.Worker = null;
        }
        using var startupReservation = startupOwnership;
        _fleet.RetireForRestart(stale);

        WindowsWorkerProcess created;
        if (lease.Slot.IsSecondary)
        {
            lock (_stateLock)
            {
                if (ReferenceEquals(_secondary, lease.Slot))
                {
                    RecordWorkerPolicyPhaseLocked(
                        lease.Slot,
                        SvgWorkerPolicyPhase.WorkerLaunchStarted);
                }
            }
        }
        long startupStartedAt = SvgWorkerStageTrace.Start(diagnosticWorkId);
        try
        {
            created = await _fleet.StartAsync(_disposeCancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_disposeCancellation.IsCancellationRequested)
        {
            // Pool shutdown is cancellation, not a failed worker startup and
            // must not advance the startup-failure circuit breaker.
            throw;
        }
        catch (WorkerDeadlineException)
        {
            RecordStartupFailure();
            throw;
        }
        catch (Exception exception)
        {
            RecordStartupFailure();
            throw new MarkdownSvgException(
                MarkdownSvgFailureReason.WorkerFailure,
                "The isolated SVG worker could not start.",
                exception);
        }

        startupOwnership.Attach(created);
        bool rejectCreated;
        lock (_stateLock)
        {
            rejectCreated = Volatile.Read(ref _disposed) != 0 ||
                !ReferenceEquals(lease.Slot.Active, lease.Pending) ||
                (lease.Slot.KillCanceledWhenStarted &&
                 lease.Pending.IsCanceled &&
                 _queue.VisibleCount > 0);
            if (!rejectCreated)
            {
                lease.Slot.Worker = created;
                if (lease.Slot.IsSecondary)
                    RecordWorkerPolicyPhaseLocked(lease.Slot, SvgWorkerPolicyPhase.WorkerProcessStarted);
            }
        }
        if (rejectCreated)
        {
            _fleet.Retire(created);
            if (lease.Pending.IsCanceled)
                throw new OperationCanceledException(lease.Pending.CancellationToken);
            throw new ObjectDisposedException(nameof(WorkerPool));
        }
        SvgWorkerStageTrace.Record(
            diagnosticWorkId,
            SvgWorkerStageId.WorkerStartupAndPipeHandshake,
            created.InstanceId,
            diagnosticRequestId,
            diagnosticDocumentId,
            startupStartedAt,
            sourceBytes: diagnosticSourceBytes,
            targetPixels: diagnosticTargetPixels);
        // Start preparation off-thread, but do not delay non-text requests.
        // The worker observes this cached task even if no text is ever opened.
        _ = created.EnsureFontCatalogSuppliedAsync(
            _requests.CreateControlRequest(WorkerOperation.FontCatalog, created, fontGeneration: _requests.FontGeneration),
            CancellationToken.None);
        return created;
    }

    bool ISecondaryWorkerWarmupHost.CanContinueSecondaryWarmup(WorkerSlot slot, WindowsWorkerProcess worker)
    {
        if (!_energySaver.AllowsSecondary())
            return false;

        lock (_stateLock)
        {
            return Volatile.Read(ref _disposed) == 0 &&
                ReferenceEquals(_secondary, slot) &&
                ReferenceEquals(slot.Worker, worker) &&
                !worker.HasExited;
        }
    }

    internal void Release(WorkerSlot slot, PendingWork pending)
    {
        pending.CancellationRegistration.Dispose();
        lock (_stateLock)
        {
            if (!ReferenceEquals(slot.Active, pending))
                return;
            slot.Active = null;
            slot.Busy = false;
            slot.KillCanceledWhenStarted = false;
            pending.ActiveSlot = null;
            pending.BlockedSince = null;
            if (Volatile.Read(ref _disposed) != 0)
                return;

            PumpLocked();
            if (slot.IsSecondary && !slot.Busy && ReferenceEquals(_secondary, slot))
            {
                bool retainFailedSentinel = WorkerSchedulingPolicy.ShouldRetainFailedSecondaryAfterRelease(
                    slot.Readiness!.HasFailed,
                    _queue.Count);
                if (slot.Worker is null && !retainFailedSentinel)
                {
                    SecondaryIdleRetirement.CancelLocked(slot);
                    _secondary = null;
                }
                else if (slot.Worker is not null)
                {
                    _idleRetirement.ScheduleLocked(slot);
                }
            }
        }
    }

    private void EvaluateCanceledBlockingLocked()
    {
        DateTimeOffset now = _timeProvider.GetUtcNow();
        _preemption.EvaluateLocked(_primary, now, _queue.VisibleCount);
        if (_secondary is not null)
            _preemption.EvaluateLocked(_secondary, now, _queue.VisibleCount);
    }

    internal void InvalidateWorker(long workerInstanceId)
    {
        if (workerInstanceId == 0)
            return;
        WindowsWorkerProcess? worker;
        lock (_stateLock)
        {
            worker = _primary.Worker?.InstanceId == workerInstanceId
                ? _primary.Worker
                : _secondary?.Worker?.InstanceId == workerInstanceId
                    ? _secondary.Worker
                    : null;
        }
        if (worker is not null)
            Invalidate(worker);
    }

    internal void Invalidate(WorkerSlot slot, WindowsWorkerProcess worker)
    {
        lock (_stateLock)
        {
            if (ReferenceEquals(slot.Worker, worker))
                slot.Worker = null;
        }
        _fleet.RetireForRestart(worker);
    }

    internal void Invalidate(WindowsWorkerProcess worker)
    {
        lock (_stateLock)
        {
            if (ReferenceEquals(_primary.Worker, worker))
                _primary.Worker = null;
            if (_secondary is not null && ReferenceEquals(_secondary.Worker, worker))
                _secondary.Worker = null;
        }
        _fleet.RetireForRestart(worker);
    }

    internal void RecordStartupFailure()
    {
        DateTimeOffset now = _timeProvider.GetUtcNow();
        lock (_stateLock)
            _startupFailures.RecordFailure(now);
    }

    private void ThrowIfUnavailableLocked()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, typeof(WorkerPool));
        if (_startupFailures.IsOpen(_timeProvider.GetUtcNow()))
        {
            throw new MarkdownSvgException(
                MarkdownSvgFailureReason.WorkerFailure,
                "The SVG provider is temporarily disabled after repeated startup failures.");
        }
    }

    private void RecordWorkerPolicyPhaseLocked(WorkerSlot slot, SvgWorkerPolicyPhase phase) =>
        _policyRecorder.RecordPhase(slot, phase, _secondary, _queue.VisibleOpenCount, _queue.VisibleRenderCount);

    internal bool IsPrimaryFontCatalogReady(long requestGeneration)
    {
        lock (_stateLock)
        {
            ThrowIfUnavailableLocked();
            return _primary.Worker is { HasExited: false } worker &&
                worker.ReadyFontGeneration == requestGeneration;
        }
    }

    Task<WindowsWorkerProcess> ISecondaryWorkerWarmupHost.EnsureWorkerAsync(
        WorkerLease lease,
        long diagnosticWorkId,
        ulong diagnosticRequestId,
        ulong diagnosticDocumentId,
        long diagnosticSourceBytes,
        long diagnosticTargetPixels) =>
        EnsureWorkerAsync(
            lease, diagnosticWorkId, diagnosticRequestId, diagnosticDocumentId, diagnosticSourceBytes, diagnosticTargetPixels);

    Task<WorkerLease> ISecondaryWorkerWarmupHost.AcquireSecondaryFontProbeAsync(
        long workerInstanceId,
        CancellationToken cancellationToken) =>
        AcquireAsync(
            WorkerOperation.Hello,
            MarkdownSvgRenderPriority.Overscan,
            cancellationToken,
            preferredWorkerInstanceId: workerInstanceId,
            isSecondaryFontProbe: true);

    void ISecondaryWorkerWarmupHost.CompleteSecondaryWarmup(WorkerSlot slot, bool warmupSucceeded)
    {
        WindowsWorkerProcess? discard = null;
        lock (_stateLock)
        {
            if (ReferenceEquals(_secondary, slot))
            {
                RecordWorkerPolicyPhaseLocked(
                    slot,
                    warmupSucceeded ? SvgWorkerPolicyPhase.Ready : SvgWorkerPolicyPhase.Failed);
            }
            if (ReferenceEquals(_secondary, slot) && !warmupSucceeded)
            {
                // Keep a failed-slot sentinel until the current queue batch
                // drains. This prevents an immediate background retry loop
                // while the primary continues serving visible work.
                slot.Readiness!.FailWarmup();
                SecondaryIdleRetirement.CancelLocked(slot);
                discard = slot.Worker;
                slot.Worker = null;
            }
        }
        _fleet.RetireForRestart(discard);
    }

    bool ISecondaryIdleRetirementHost.TryDetachIdleSecondary(
        WorkerSlot slot,
        CancellationTokenSource cancellation,
        out WindowsWorkerProcess? worker)
    {
        worker = null;
        lock (_stateLock)
        {
            if (!ReferenceEquals(_secondary, slot) ||
                slot.Busy ||
                !ReferenceEquals(slot.IdleRetirement, cancellation) ||
                !WorkerSchedulingPolicy.ShouldRetireSecondary(slot.IdleSince, _timeProvider.GetUtcNow()))
            {
                return false;
            }
            slot.IdleRetirement = null;
            worker = slot.Worker;
            slot.Worker = null;
            _secondary = null;
        }
        return true;
    }

    TimeSpan ICanceledWorkPreemptionHost.GetCanceledBlockingGraceDelay(PendingWork pending)
    {
        lock (_stateLock)
            return _preemption.GetGraceDelayLocked(pending);
    }

    WindowsWorkerProcess? ICanceledWorkPreemptionHost.CompleteCanceledBlockingCheck(WorkerSlot slot, PendingWork pending)
    {
        lock (_stateLock)
            return _preemption.CompleteCheckLocked(slot, pending, _queue.VisibleCount);
    }
}
