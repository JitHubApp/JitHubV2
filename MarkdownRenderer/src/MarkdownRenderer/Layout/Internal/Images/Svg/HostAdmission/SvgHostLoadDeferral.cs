using System;
using System.Threading;
using System.Threading.Tasks;
using MarkdownRenderer.Diagnostics.Internal;
using MarkdownRenderer.Images;
using MarkdownRenderer.Layout.Internal.Images.Svg.Rendering;

namespace MarkdownRenderer.Layout.Internal.Images.Svg.HostAdmission;

/// <summary>
/// An image box's SVG host-capacity deferral: the denied load it will resume,
/// the bounded waiter registered with the scope's admission coordinator, and
/// the queued retry. Its lock is also the box's load-activation gate, taken
/// after the image-work lock and never before it.
/// </summary>
/// <remarks>
/// This file is longer than 500 lines because the denial record, its bounded
/// waiter and the queued retry are one state machine under one lock: denial,
/// registration, readiness, retry queueing and its fallback, and the retry's
/// start each read and write that state in single lock transactions.
/// </remarks>
internal sealed class SvgHostLoadDeferral
{
    private const int SvgHostDeferredNone = 0;
    private const int SvgHostDeferredWaiting = 1;
    private const int SvgHostDeferredRetryQueued = 2;

    private readonly ISvgHostDeferralHost _host;
    private readonly ImageResourceScope _resources;
    private readonly object _svgHostDeferredSync = new();
    private SvgHostPendingWaiter? _svgHostDeferredWaiter;
    private object? _svgHostDeferredWaiterIdentity;
    private object? _svgHostDeferredRegistrationAttempt;
    private object? _svgHostDeferredDenialIdentity;
    private WeakReference<IMarkdownSvgAdmission>? _svgHostDeferredAdmission;
    private int _svgHostDeferred;
    private long _svgHostDeferredDemandVersion;
    private long _svgHostDeferredCapacityVersion;
    private long _svgHostDeferredOwnerVersion;
    private long _svgHostDeferredVisualDemandEpoch;
    private long _svgHostDeferredRequiredSourceBytes;
    private bool _svgHostDeferredRequiresOwnerSlot;
    private bool _svgHostDeferredCapacityReady;
    // Null for image boxes: retries use the two built-in non-inline queues
    // without allocating a queue callback.
    private readonly Func<Action, bool>? _svgDeferredRetryEnqueueOverride;

    /// <summary>Creates the deferral of the box behind <paramref name="host"/>.</summary>
    /// <param name="host">The box whose load this deferral resumes.</param>
    /// <param name="resources">The scope whose admission coordinator holds the waiter.</param>
    /// <param name="retryQueue">
    /// An injected scheduler for queued retries, used in place of both built-in
    /// non-inline queues (<see cref="Task.Run(Action)"/>, then the thread pool).
    /// It receives the retry and returns whether it accepted it; a rejection or
    /// exception takes the same fallback and restore path as the built-in
    /// queues. Null, the default, uses the built-in queues.
    /// </param>
    internal SvgHostLoadDeferral(
        ISvgHostDeferralHost host,
        ImageResourceScope resources,
        Func<Action, bool>? retryQueue = null)
    {
        _host = host;
        _resources = resources;
        _svgDeferredRetryEnqueueOverride = retryQueue;
    }

    /// <summary>The deferral lock, which is also the box's load-activation gate.</summary>
    internal object Sync => _svgHostDeferredSync;

    /// <summary>Whether a denied load is waiting or has a retry queued.</summary>
    internal bool IsDeferred => Volatile.Read(ref _svgHostDeferred) != 0;

    /// <summary>Whether no denial, waiter, or capacity signal is held.</summary>
    internal bool IsClear()
    {
        lock (_svgHostDeferredSync)
        {
            return _svgHostDeferred == SvgHostDeferredNone &&
                   _svgHostDeferredWaiter is null &&
                   _svgHostDeferredWaiterIdentity is null &&
                   _svgHostDeferredRegistrationAttempt is null &&
                   _svgHostDeferredDenialIdentity is null &&
                   _svgHostDeferredAdmission is null &&
                   !_svgHostDeferredCapacityReady;
        }
    }

    /// <summary>
    /// One consistent view of the denial record, its waiter identities and its
    /// readiness, taken under the deferral lock.
    /// </summary>
    internal SvgHostLoadDeferralState GetState()
    {
        lock (_svgHostDeferredSync)
        {
            return new SvgHostLoadDeferralState(
                Phase: (SvgHostDeferralPhase)_svgHostDeferred,
                HasWaiter: _svgHostDeferredWaiter is not null,
                WaiterIdentity: _svgHostDeferredWaiterIdentity,
                RegistrationAttempt: _svgHostDeferredRegistrationAttempt,
                DenialIdentity: _svgHostDeferredDenialIdentity,
                HasAdmission: _svgHostDeferredAdmission is not null,
                DemandVersion: _svgHostDeferredDemandVersion,
                CapacityVersion: _svgHostDeferredCapacityVersion,
                OwnerVersion: _svgHostDeferredOwnerVersion,
                VisualDemandEpoch: _svgHostDeferredVisualDemandEpoch,
                RequiredSourceBytes: _svgHostDeferredRequiredSourceBytes,
                RequiresOwnerSlot: _svgHostDeferredRequiresOwnerSlot,
                CapacityReady: _svgHostDeferredCapacityReady);
        }
    }

    internal void DeferSvgHostLoad(
        SvgHostCapacityDeferral deferral, object? expectedDenialIdentity = null)
    {
        object registrationAttempt = new();
        if (!_host.TryCaptureSvgHostDeferralCancellation(deferral, out CancellationToken cancellationToken))
            return;

        if (!TryRecordDenial(deferral, expectedDenialIdentity, registrationAttempt, out SvgHostPendingWaiter? previousWaiter))
            return;
        previousWaiter?.Dispose();

        SvgHostPendingWaiter? waiter = TryRegisterWaiter(deferral, registrationAttempt, cancellationToken);
        if (!TryRetainRegisteredWaiter(deferral, registrationAttempt, waiter))
            waiter?.Dispose();

        // Close the denial-to-registration lost-wakeup window. This also
        // handles a waiter that completed synchronously before its handle was
        // stored on the box.
        TryResumeDeferredSvgLoadForObservedDemandOrCapacity(deferral.Demand);
    }

    // Records a fresh denial, replacing only this box's earlier source-free
    // waiter, which the caller disposes after the lock.
    private bool TryRecordDenial(
        SvgHostCapacityDeferral deferral,
        object? expectedDenialIdentity,
        object registrationAttempt,
        out SvgHostPendingWaiter? previousWaiter)
    {
        lock (_svgHostDeferredSync)
        {
            if (!ReferenceEquals(_host.LoadDemand, deferral.Demand) ||
                deferral.LoadGeneration != _host.LoadDemandGeneration ||
                (expectedDenialIdentity is not null &&
                    (!ReferenceEquals(_svgHostDeferredDenialIdentity, expectedDenialIdentity) ||
                        _svgHostDeferred != SvgHostDeferredWaiting ||
                        _svgHostDeferredCapacityReady ||
                        _svgHostDeferredWaiter is { IsFinished: false })))
            {
                previousWaiter = null;
                return false;
            }

            previousWaiter = _svgHostDeferredWaiter;
            _svgHostDeferredWaiter = null;
            _svgHostDeferredWaiterIdentity = null;
            _svgHostDeferredRegistrationAttempt = registrationAttempt;
            _svgHostDeferredDenialIdentity = registrationAttempt;
            _svgHostDeferredAdmission = new WeakReference<IMarkdownSvgAdmission>(deferral.Admission);
            _svgHostDeferred = SvgHostDeferredWaiting;
            _svgHostDeferredDemandVersion = deferral.DemandVersionAtDenial;
            _svgHostDeferredVisualDemandEpoch = deferral.VisualDemandEpochAtDenial;
            _svgHostDeferredCapacityVersion = deferral.CapacityVersion;
            _svgHostDeferredOwnerVersion = deferral.OwnerVersionAtDenial;
            _svgHostDeferredRequiredSourceBytes = deferral.RequiredSourceReservationBytes;
            _svgHostDeferredRequiresOwnerSlot = deferral.RequiresOwnerSlot;
            _svgHostDeferredCapacityReady = false;
            _host.MarkLoadNotStarted();
            return true;
        }
    }

    private SvgHostPendingWaiter? TryRegisterWaiter(
        SvgHostCapacityDeferral deferral,
        object registrationAttempt,
        CancellationToken cancellationToken)
    {
        try
        {
            return _resources.SvgHostAdmission.Defer(
                this,
                deferral.Admission,
                deferral.Demand,
                deferral.LoadGeneration,
                deferral.CapacityVersion,
                deferral.RequiredSourceReservationBytes,
                deferral.RequiresOwnerSlot,
                deferral.OwnerVersionAtDenial,
                deferral.DemandVersionAtDenial,
                deferral.VisualDemandEpochAtDenial,
                registrationAttempt,
                cancellationToken);
        }
        catch (Exception exception)
        {
            // The bounded waiter is an optimization for prompt recovery. A
            // saturated waiter table leaves only weak scalar state on this box;
            // a later demand/capacity observation can still restart it.
            MarkdownDiagnostics.WriteLine(
                $"[ImageBox] SVG capacity wait could not be registered: {exception.GetType().Name}");
            return null;
        }
    }

    // Stores the registered waiter when it still belongs to the current
    // denial. Returns false when the caller must dispose it.
    private bool TryRetainRegisteredWaiter(
        SvgHostCapacityDeferral deferral,
        object registrationAttempt,
        SvgHostPendingWaiter? waiter)
    {
        lock (_svgHostDeferredSync)
        {
            if (_svgHostDeferred == SvgHostDeferredWaiting &&
                ReferenceEquals(_svgHostDeferredRegistrationAttempt, registrationAttempt) &&
                ReferenceEquals(_host.LoadDemand, deferral.Demand) &&
                deferral.LoadGeneration == _host.LoadDemandGeneration &&
                waiter is not null &&
                ReferenceEquals(_svgHostDeferredWaiterIdentity, waiter.Identity) &&
                !waiter.IsFinished)
            {
                _svgHostDeferredWaiter = waiter;
                return true;
            }
            if (ReferenceEquals(_svgHostDeferredRegistrationAttempt, registrationAttempt))
            {
                // A failed/finished registration has no live wait handle. Keep
                // the scalar denial state for a future demand observation, but
                // do not leave an identity that a later waiter could confuse.
                _svgHostDeferredRegistrationAttempt = null;
                if (_svgHostDeferred != SvgHostDeferredRetryQueued && !_svgHostDeferredCapacityReady)
                    _svgHostDeferredWaiterIdentity = null;
            }
            return false;
        }
    }

    internal void TryResumeDeferredSvgLoadForObservedDemandOrCapacity(
        MarkdownSvgRenderDemand? demand)
    {
        if (demand is null)
            return;

        IMarkdownSvgAdmission? admission = null;
        int loadGeneration;
        long demandVersionAtDenial, capacityVersionAtDenial, ownerVersionAtDenial;
        long visualDemandEpochAtDenial, requiredSourceBytes;
        bool requiresOwnerSlot, capacityReady, hasLiveWaiter;
        object? denialIdentity;
        lock (_svgHostDeferredSync)
        {
            if (_svgHostDeferred != SvgHostDeferredWaiting ||
                !ReferenceEquals(_host.LoadDemand, demand) ||
                _svgHostDeferredAdmission is null ||
                !_svgHostDeferredAdmission.TryGetTarget(out admission))
            {
                return;
            }

            loadGeneration = _host.LoadDemandGeneration;
            demandVersionAtDenial = _svgHostDeferredDemandVersion;
            capacityVersionAtDenial = _svgHostDeferredCapacityVersion;
            ownerVersionAtDenial = _svgHostDeferredOwnerVersion;
            visualDemandEpochAtDenial = _svgHostDeferredVisualDemandEpoch;
            requiresOwnerSlot = _svgHostDeferredRequiresOwnerSlot;
            requiredSourceBytes = _svgHostDeferredRequiredSourceBytes;
            capacityReady = _svgHostDeferredCapacityReady;
            hasLiveWaiter = _svgHostDeferredWaiter is { IsFinished: false };
            denialIdentity = _svgHostDeferredDenialIdentity;
        }

        long demandVersion = demand.Version;
        long capacityVersion;
        long ownerVersion;
        try
        {
            capacityVersion = requiresOwnerSlot ? 0 : admission.CapacityVersion;
            ownerVersion = requiresOwnerSlot
                ? _resources.SvgHostAdmission.GetOwnerVersion(admission.AdmissionDomain)
                : 0;
        }
        catch (Exception exception)
        {
            MarkdownDiagnostics.WriteLine(
                $"[ImageBox] SVG capacity state could not be observed: {exception.GetType().Name}");
            return;
        }

        if (!requiresOwnerSlot && !capacityReady && !hasLiveWaiter &&
            denialIdentity is not null && capacityVersion > capacityVersionAtDenial)
        {
            // A release/version pulse may be smaller than the required peak,
            // including the failed attempt's own clone release. If a bounded
            // waiter could not be retained, re-enter only scalar registration
            // once for the real observed version, never source loading.
            DeferSvgHostLoad(new SvgHostCapacityDeferral(
                admission, demand, loadGeneration, capacityVersion,
                requiredSourceBytes, RequiresOwnerSlot: false,
                OwnerVersionAtDenial: 0,
                DemandVersionAtDenial: demandVersionAtDenial,
                VisualDemandEpochAtDenial: visualDemandEpochAtDenial), denialIdentity);
            return;
        }

        TryQueueDeferredSvgHostRetry(
            demand,
            loadGeneration,
            demandVersion > demandVersionAtDenial ? demandVersion : 0,
            capacityVersion > capacityVersionAtDenial ? capacityVersion : 0,
            ownerVersion > ownerVersionAtDenial ? ownerVersion : 0,
            _host.SvgHostVisualDemandEpoch > visualDemandEpochAtDenial
                ? _host.SvgHostVisualDemandEpoch : 0);
    }

    internal void TryQueueDeferredSvgHostRetry(
        MarkdownSvgRenderDemand demand,
        int loadGeneration,
        long observedDemandVersion,
        long observedCapacityVersion,
        long observedOwnerVersion,
        long observedVisualDemandEpoch = 0,
        bool capacityThresholdSatisfied = false,
        object? waiterIdentity = null)
    {
        SvgHostPendingWaiter? waiter;
        SvgDeferredLoadWork retry;
        lock (_svgHostDeferredSync)
        {
            if (_svgHostDeferred != SvgHostDeferredWaiting ||
                !ReferenceEquals(_host.LoadDemand, demand) ||
                loadGeneration != _host.LoadDemandGeneration)
            {
                return;
            }

            if (waiterIdentity is not null &&
                !ReferenceEquals(_svgHostDeferredWaiterIdentity, waiterIdentity))
            {
                return;
            }
            if (capacityThresholdSatisfied)
            {
                // Threshold readiness is independent of a numeric release
                // version. It belongs only to this accepted source waiter.
                if (_svgHostDeferredRequiresOwnerSlot || waiterIdentity is null)
                    return;
                _svgHostDeferredCapacityReady = true;
            }

            bool eligible = observedDemandVersion > _svgHostDeferredDemandVersion ||
                observedVisualDemandEpoch > _svgHostDeferredVisualDemandEpoch ||
                (_svgHostDeferredRequiresOwnerSlot
                    ? observedOwnerVersion > _svgHostDeferredOwnerVersion
                    : _svgHostDeferredCapacityReady);
            if (!eligible)
                return;

            retry = new SvgDeferredLoadWork(this, demand, loadGeneration,
                _svgHostDeferredDenialIdentity);
            _svgHostDeferred = SvgHostDeferredRetryQueued;
            waiter = _svgHostDeferredWaiter;
            _svgHostDeferredWaiter = null;
        }
        waiter?.Dispose();

        try
        {
            if (!QueueDeferredSvgRetry(retry, fallback: false))
                throw new InvalidOperationException("The primary SVG retry queue rejected the work.");
        }
        catch (Exception taskQueueFailure)
        {
            QueueFallbackRetryOrRestoreWaiting(retry, taskQueueFailure);
        }
    }

    // Task.Run can fail under transient scheduler pressure. Make one second
    // non-inline queue attempt; if both fail, leave the exact denial eligible
    // for the next demand/capacity observation.
    private void QueueFallbackRetryOrRestoreWaiting(SvgDeferredLoadWork retry, Exception taskQueueFailure)
    {
        bool queued = false;
        Exception? fallbackQueueFailure = null;
        try
        {
            queued = QueueDeferredSvgRetry(retry, fallback: true);
        }
        catch (Exception exception)
        {
            fallbackQueueFailure = exception;
        }

        if (queued)
        {
            MarkdownDiagnostics.WriteLine(
                $"[ImageBox] SVG deferred retry used the fallback queue after {taskQueueFailure.GetType().Name}.");
            return;
        }

        lock (_svgHostDeferredSync)
        {
            if (_svgHostDeferred == SvgHostDeferredRetryQueued &&
                ReferenceEquals(_host.LoadDemand, retry.Demand) &&
                retry.LoadGeneration == _host.LoadDemandGeneration &&
                ReferenceEquals(_svgHostDeferredDenialIdentity, retry.DenialIdentity))
            {
                // Retain the real threshold reason and accepted identity
                // for one retry on a later demand/paint observation. Do
                // not recurse into an immediately-ready allocation loop.
                _svgHostDeferred = SvgHostDeferredWaiting;
            }
        }
        MarkdownDiagnostics.WriteLine(
            $"[ImageBox] SVG deferred retry could not be queued: {taskQueueFailure.GetType().Name}" +
            (fallbackQueueFailure is null ? string.Empty : $"/{fallbackQueueFailure.GetType().Name}"));
    }

    private bool QueueDeferredSvgRetry(SvgDeferredLoadWork retry, bool fallback)
    {
        if (_svgDeferredRetryEnqueueOverride is { } testQueue)
            return testQueue(() => RunDeferredSvgLoad(retry));
        if (fallback)
        {
            return ThreadPool.UnsafeQueueUserWorkItem(
                static work => RunDeferredSvgLoad(work), retry, preferLocal: false);
        }
        _ = Task.Run(() => RunDeferredSvgLoad(retry));
        return true;
    }

    private static void RunDeferredSvgLoad(SvgDeferredLoadWork work)
    {
        try
        {
            work.Owner.StartDeferredSvgLoad(work.Demand, work.LoadGeneration, work.DenialIdentity);
        }
        catch (Exception exception)
        {
            // StartLoad owns retryable errors; this boundary prevents an
            // unexpected synchronous setup failure from escaping the pool.
            MarkdownDiagnostics.WriteLine(
                $"[ImageBox] SVG deferred retry setup failed: {exception.GetType().Name}");
        }
    }

    private void StartDeferredSvgLoad(
        MarkdownSvgRenderDemand demand, int loadGeneration, object? denialIdentity)
    {
        lock (_svgHostDeferredSync)
        {
            if (_svgHostDeferred != SvgHostDeferredRetryQueued ||
                !ReferenceEquals(_host.LoadDemand, demand) ||
                loadGeneration != _host.LoadDemandGeneration ||
                !ReferenceEquals(_svgHostDeferredDenialIdentity, denialIdentity))
            {
                return;
            }
            ClearSvgHostDeferralNoLock();
            _host.MarkLoadNotStarted();
        }

        _host.StartLoad(
            isVisible: demand.Priority == MarkdownSvgRenderPriority.Visible,
            demandOverride: demand,
            loadDemandGeneration: loadGeneration);
    }

    internal bool OnSvgHostAdmissionWaiterRegistered(
        MarkdownSvgRenderDemand demand,
        int loadGeneration,
        object registrationAttempt,
        SvgHostPendingWaiter waiter)
    {
        lock (_svgHostDeferredSync)
        {
            if (_svgHostDeferred == SvgHostDeferredWaiting &&
                ReferenceEquals(_svgHostDeferredRegistrationAttempt, registrationAttempt) &&
                ReferenceEquals(_host.LoadDemand, demand) &&
                loadGeneration == _host.LoadDemandGeneration &&
                !waiter.IsFinished)
            {
                _svgHostDeferredWaiterIdentity = waiter.Identity;
                _svgHostDeferredDenialIdentity ??= registrationAttempt;
                return true;
            }
        }
        return false;
    }

    internal void OnSvgHostAdmissionWaiterFinished(
        MarkdownSvgRenderDemand demand,
        int loadGeneration,
        object waiterIdentity)
    {
        SvgHostPendingWaiter? waiter = null;
        lock (_svgHostDeferredSync)
        {
            if (ReferenceEquals(_host.LoadDemand, demand) &&
                loadGeneration == _host.LoadDemandGeneration &&
                ReferenceEquals(_svgHostDeferredWaiterIdentity, waiterIdentity))
            {
                waiter = _svgHostDeferredWaiter;
                _svgHostDeferredWaiter = null;
                if (_svgHostDeferred != SvgHostDeferredRetryQueued && !_svgHostDeferredCapacityReady)
                {
                    _svgHostDeferredWaiterIdentity = null;
                    _svgHostDeferredRegistrationAttempt = null;
                }
            }
        }
        waiter?.Dispose();
    }

    internal void ClearSvgHostDeferral()
    {
        IDisposable? waiter;
        lock (_svgHostDeferredSync)
            waiter = ClearSvgHostDeferralNoLock();
        waiter?.Dispose();
    }

    internal IDisposable? ClearSvgHostDeferralNoLock()
    {
        IDisposable? waiter = _svgHostDeferredWaiter;
        _svgHostDeferredWaiter = null;
        _svgHostDeferredWaiterIdentity = null;
        _svgHostDeferredRegistrationAttempt = null;
        _svgHostDeferredDenialIdentity = null;
        _svgHostDeferredAdmission = null;
        _svgHostDeferred = SvgHostDeferredNone;
        _svgHostDeferredDemandVersion = 0;
        _svgHostDeferredCapacityVersion = 0;
        _svgHostDeferredOwnerVersion = 0;
        _svgHostDeferredVisualDemandEpoch = 0;
        _svgHostDeferredRequiredSourceBytes = 0;
        _svgHostDeferredRequiresOwnerSlot = false;
        _svgHostDeferredCapacityReady = false;
        return waiter;
    }
}
