using System;
using System.Threading;
using MarkdownRenderer.Images;

namespace MarkdownRenderer.Layout.Boxes;

/// <summary>
/// One image box's deferred bitmap-output request: the source-free waiter it
/// keeps with the scope's bitmap output governor, the cancellation that
/// retires it, and the retry state that <see cref="ImageBitmapOutputRetry"/>
/// delivers once capacity returns. It owns its state and lock; the box is
/// reached through <see cref="IImageBitmapOutputHost"/>.
/// </summary>
/// <remarks>
/// Lock order: a caller may hold the box's image-work lock when it takes this
/// lock. This type never calls the host's locking members while it holds its
/// own lock.
/// </remarks>
internal sealed class ImageBitmapOutputDeferral
{
    private readonly IImageBitmapOutputHost _host;
    private readonly MarkdownLayoutContext _context;
    private readonly ImageResourceScope _resources;
    private readonly CancellationTokenSource _imageLifetime;
    private readonly ImageBitmapOutputRetry _retry;
    private readonly object _bitmapOutputSync = new();
    private BitmapOutputAdmission.WaiterRegistration? _bitmapOutputWaiter;
    private ImageBitmapOutputWaiter? _bitmapOutputWaiterOwner;
    private CancellationTokenRegistration _bitmapOutputImageCancellation;
    private CancellationTokenRegistration _bitmapOutputSessionCancellation;
    private long _bitmapOutputWaiterEpoch;
    private long _bitmapOutputRecoverySignaledEpoch;
    private long _bitmapOutputRequiredBytes;
    private long _bitmapOutputCapacityVersion;
    private long _bitmapOutputWaiterSlotVersion;
    private MarkdownSvgRenderDemand? _bitmapOutputDemand;
    private int _bitmapOutputDemandGeneration;
    private int _bitmapOutputSvgGeneration;
    private int _bitmapOutputDeferred;
    private int _bitmapOutputVisible;
    private int _bitmapOutputReplacement;
    private int _bitmapOutputExistingTile;
    private int _bitmapOutputNeedsTrim;
    private int _bitmapOutputRegistering;
    private int _bitmapOutputRegistrationNeeded;
    private int _bitmapOutputCapacityReady;
    private int _bitmapOutputRetryQueued;
    private int _bitmapOutputRetryInProgress;
    private int _bitmapOutputRetryOfferPending;
    private int _bitmapOutputRegistrationFailed;
    private int _bitmapOutputCancellationRegistered;

    internal ImageBitmapOutputDeferral(
        IImageBitmapOutputHost host,
        MarkdownLayoutContext context,
        ImageResourceScope resources,
        CancellationTokenSource imageLifetime)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(resources);
        ArgumentNullException.ThrowIfNull(imageLifetime);
        _host = host;
        _context = context;
        _resources = resources;
        _imageLifetime = imageLifetime;
        _retry = new ImageBitmapOutputRetry(host, context, imageLifetime, this);
    }

    internal bool IsBitmapOutputParticipationEnabled =>
        _context.PerformanceSession?.Options.ReleaseOffscreenImageResources == true;

    internal bool IsBitmapOutputDeferred => Volatile.Read(ref _bitmapOutputDeferred) != 0;

    internal bool RecordBitmapOutputDeferral(
        long requiredBytes,
        long capacityVersion,
        BitmapOutputWorkContext work,
        bool isVisible,
        bool isReplacement,
        bool needsCacheTrim)
    {
        MarkdownSvgRenderDemand? demand = work.Demand;
        int demandGeneration = work.LoadGeneration;
        int svgGeneration = work.SvgGeneration;
        bool isExistingSvgTileOutput = work.IsExistingSvgTileOutput;
        if (demand is null || requiredBytes <= 0 ||
            requiredBytes > _resources.BitmapOutput.MaximumSingleOutputBytes)
        {
            return false;
        }

        DetachedBitmapOutputWaiter prior = default;
        ImageBitmapOutputWaiter? waiterOwner = null;
        long waiterEpoch = 0;
        bool registerCancellation = false;

        lock (_bitmapOutputSync)
        {
            if (_host.IsRetired || _imageLifetime.IsCancellationRequested ||
                (_context.PerformanceSession?.IsDisposed ?? false) ||
                !work.IsCurrent(_host) ||
                !ReferenceEquals(_host.LoadDemand, demand) ||
                demandGeneration != _host.LoadDemandGeneration ||
                (isExistingSvgTileOutput && svgGeneration != _host.SvgRenderGeneration))
            {
                return false;
            }

            bool sameDemand = _bitmapOutputWaiterOwner is not null &&
                ReferenceEquals(_bitmapOutputDemand, demand) &&
                _bitmapOutputDemandGeneration == demandGeneration &&
                _bitmapOutputSvgGeneration == svgGeneration &&
                (_bitmapOutputExistingTile != 0) == isExistingSvgTileOutput;
            if (!sameDemand)
                prior = BeginBitmapOutputWaiterEpochNoLock(demand, demandGeneration, svgGeneration);

            // The work that still owns source bytes/decoder/document data must
            // drain before cache trimming or waiter publication can happen.
            _bitmapOutputRequiredBytes = requiredBytes;
            _bitmapOutputCapacityVersion = capacityVersion;
            Volatile.Write(ref _bitmapOutputVisible, isVisible ? 1 : 0);
            Volatile.Write(ref _bitmapOutputReplacement, isReplacement ? 1 : 0);
            Volatile.Write(ref _bitmapOutputExistingTile, isExistingSvgTileOutput ? 1 : 0);
            if (needsCacheTrim)
                Volatile.Write(ref _bitmapOutputNeedsTrim, 1);
            _bitmapOutputRegistrationNeeded = 1;
            if (_bitmapOutputCancellationRegistered == 0)
            {
                _bitmapOutputCancellationRegistered = 1;
                registerCancellation = true;
                waiterOwner = _bitmapOutputWaiterOwner;
                waiterEpoch = _bitmapOutputWaiterEpoch;
            }
        }

        prior.Release();

        // Cancellation must be able to retire even a scalar-only waiter that
        // could not enter the governor's bounded queue. The callback target is
        // weak with respect to ImageBox and contains no source-bearing state.
        if (registerCancellation && waiterOwner is not null)
            return TryRegisterBitmapOutputCancellations(waiterOwner, waiterEpoch);
        return true;
    }

    /// <summary>
    /// Starts a new waiter epoch for a different demand: allocates its owner,
    /// detaches the prior waiter and cancellation registrations, and resets the
    /// retry state. The caller holds <c>_bitmapOutputSync</c> and releases the
    /// detached prior state after leaving it.
    /// </summary>
    private DetachedBitmapOutputWaiter BeginBitmapOutputWaiterEpochNoLock(
        MarkdownSvgRenderDemand demand, int demandGeneration, int svgGeneration)
    {
        long nextEpoch = _bitmapOutputWaiterEpoch == long.MaxValue
            ? 1
            : _bitmapOutputWaiterEpoch + 1;
        // Allocate before detaching the prior registration: failure
        // leaves its ownership intact and available for cleanup.
        var nextOwner = new ImageBitmapOutputWaiter(this, nextEpoch);
        var prior = new DetachedBitmapOutputWaiter(
            _bitmapOutputWaiter,
            ReturnUnusedOffer: _bitmapOutputRetryOfferPending != 0,
            _bitmapOutputImageCancellation,
            _bitmapOutputSessionCancellation);
        _bitmapOutputWaiter = null;
        _bitmapOutputImageCancellation = default;
        _bitmapOutputSessionCancellation = default;
        _bitmapOutputCancellationRegistered = 0;
        _bitmapOutputWaiterOwner = null;
        _bitmapOutputWaiterEpoch = nextEpoch;
        _bitmapOutputRecoverySignaledEpoch = 0;
        _bitmapOutputRetryOfferPending = 0;
        _bitmapOutputRetryInProgress = 0;
        _bitmapOutputCapacityReady = 0;
        _bitmapOutputRetryQueued = 0;
        _bitmapOutputRegistering = 0;
        _bitmapOutputRegistrationNeeded = 0;
        _bitmapOutputRegistrationFailed = 0;
        _bitmapOutputWaiterOwner = nextOwner;
        _bitmapOutputDemand = demand;
        _bitmapOutputDemandGeneration = demandGeneration;
        _bitmapOutputSvgGeneration = svgGeneration;
        Volatile.Write(ref _bitmapOutputDeferred, 1);
        return prior;
    }

    /// <returns><see langword="false"/> when registration failed and the deferral was canceled.</returns>
    private bool TryRegisterBitmapOutputCancellations(ImageBitmapOutputWaiter waiterOwner, long waiterEpoch)
    {
        try
        {
            RegisterBitmapOutputCancellation(
                waiterOwner,
                waiterEpoch,
                _imageLifetime.Token,
                session: false);
            CancellationToken sessionToken = _context.PerformanceSession?.LifetimeToken ?? default;
            if (sessionToken.CanBeCanceled)
            {
                RegisterBitmapOutputCancellation(
                    waiterOwner,
                    waiterEpoch,
                    sessionToken,
                    session: true);
            }
            return true;
        }
        catch (Exception exception)
        {
            _host.RecordImageRetirementFailure(exception);
            CancelBitmapOutputDeferral(waiterEpoch, resetLoad: false);
            return false;
        }
    }

    private void RegisterBitmapOutputCancellation(
        ImageBitmapOutputWaiter owner,
        long epoch,
        CancellationToken token,
        bool session)
    {
        CancellationTokenRegistration registration = token.Register(
            static state => ((ImageBitmapOutputWaiter)state!).Cancel(), owner);
        bool keep;
        lock (_bitmapOutputSync)
        {
            keep = _bitmapOutputWaiterEpoch == epoch &&
                ReferenceEquals(_bitmapOutputWaiterOwner, owner) &&
                Volatile.Read(ref _bitmapOutputDeferred) != 0;
            if (keep)
            {
                if (session)
                    _bitmapOutputSessionCancellation = registration;
                else
                    _bitmapOutputImageCancellation = registration;
            }
        }
        if (!keep)
            registration.Unregister();
    }

    internal bool CompleteBitmapOutputClaim(in BitmapOutputWorkContext work)
    {
        if (!work.IsCurrent(_host))
            return false;

        BitmapOutputAdmission.WaiterRegistration? waiter;
        CancellationTokenRegistration imageCancellation;
        CancellationTokenRegistration sessionCancellation;
        bool settleOffer;
        bool returnUnusedOffer;
        lock (_bitmapOutputSync)
        {
            bool trackedDemand = _bitmapOutputWaiterOwner is not null;
            bool exactMatch = trackedDemand && ReferenceEquals(_bitmapOutputDemand, work.Demand) &&
                _bitmapOutputDemandGeneration == work.LoadGeneration &&
                _bitmapOutputSvgGeneration == work.SvgGeneration &&
                (_bitmapOutputExistingTile != 0) == work.IsExistingSvgTileOutput;
            bool storedWorkIsStale =
                !ReferenceEquals(_bitmapOutputDemand, _host.LoadDemand) ||
                _bitmapOutputDemandGeneration != _host.LoadDemandGeneration ||
                _bitmapOutputSvgGeneration != _host.SvgRenderGeneration;

            bool matchingRetryOffer = _bitmapOutputRetryOfferPending != 0 && exactMatch;
            if (_bitmapOutputDeferred == 0 && !matchingRetryOffer &&
                !(_bitmapOutputRetryOfferPending != 0 && storedWorkIsStale))
            {
                return true;
            }

            // A claim can retire its own exact deferral or one whose captured
            // request is already stale. It never clears another current request.
            if (!exactMatch && !storedWorkIsStale)
                return true;

            waiter = _bitmapOutputWaiter;
            imageCancellation = _bitmapOutputImageCancellation;
            sessionCancellation = _bitmapOutputSessionCancellation;
            settleOffer = _bitmapOutputRetryOfferPending != 0;
            returnUnusedOffer = settleOffer && !exactMatch;
            RetireRequestNoLock();
        }

        imageCancellation.Unregister();
        sessionCancellation.Unregister();
        if (waiter is not null)
        {
            if (settleOffer && !returnUnusedOffer)
                waiter.CompleteRetry(capacityClaimed: true);
            else if (returnUnusedOffer)
                waiter.ReturnUnusedNotification();
            else
                waiter.Dispose();
        }

        return true;
    }

    internal bool ClearBitmapOutputDeferral(
        bool preserveRetryOffer = false,
        long? expectedEpoch = null)
    {
        BitmapOutputAdmission.WaiterRegistration? waiter = null;
        CancellationTokenRegistration imageCancellation = default;
        CancellationTokenRegistration sessionCancellation = default;
        bool returnOffer = false;
        lock (_bitmapOutputSync)
        {
            if (expectedEpoch.HasValue && _bitmapOutputWaiterEpoch != expectedEpoch.Value)
                return false;

            if (preserveRetryOffer)
            {
                Volatile.Write(ref _bitmapOutputDeferred, 0);
                Volatile.Write(ref _bitmapOutputCapacityReady, 0);
                Volatile.Write(ref _bitmapOutputRetryQueued, 0);
                _bitmapOutputRegistrationNeeded = 0;
                return true;
            }

            waiter = _bitmapOutputWaiter;
            imageCancellation = _bitmapOutputImageCancellation;
            sessionCancellation = _bitmapOutputSessionCancellation;
            returnOffer = _bitmapOutputRetryOfferPending != 0;
            RetireRequestNoLock();
        }

        imageCancellation.Unregister();
        sessionCancellation.Unregister();
        if (waiter is not null)
        {
            if (returnOffer)
                waiter.ReturnUnusedNotification();
            else
                waiter.Dispose();
        }
        return true;
    }

    // Detaches the waiter and cancellations (the caller has captured them),
    // clears every request field and advances the epoch past the request.
    private void RetireRequestNoLock()
    {
        _bitmapOutputWaiter = null;
        _bitmapOutputWaiterOwner = null;
        _bitmapOutputImageCancellation = default;
        _bitmapOutputSessionCancellation = default;
        _bitmapOutputCancellationRegistered = 0;
        _bitmapOutputRetryOfferPending = 0;
        _bitmapOutputRetryInProgress = 0;
        _bitmapOutputCapacityReady = 0;
        _bitmapOutputRetryQueued = 0;
        _bitmapOutputRegistering = 0;
        _bitmapOutputRegistrationNeeded = 0;
        _bitmapOutputRegistrationFailed = 0;
        _bitmapOutputRequiredBytes = 0;
        _bitmapOutputCapacityVersion = 0;
        _bitmapOutputWaiterSlotVersion = 0;
        _bitmapOutputDemand = null;
        _bitmapOutputDemandGeneration = 0;
        _bitmapOutputSvgGeneration = 0;
        Volatile.Write(ref _bitmapOutputDeferred, 0);
        Volatile.Write(ref _bitmapOutputNeedsTrim, 0);
        _bitmapOutputWaiterEpoch = _bitmapOutputWaiterEpoch == long.MaxValue
            ? 1
            : _bitmapOutputWaiterEpoch + 1;
}

    /// <summary>
    /// Performs a source-free, generation/version-gated observation of an
    /// existing deferral. It never calls EnsureLoading; a successful observation
    /// merely queues the already-defined deferred retry after active work drains.
    /// </summary>
    internal bool ObserveDeferredBitmapOutput(bool isVisible)
    {
        if (!IsBitmapOutputParticipationEnabled || !IsBitmapOutputDeferred)
            return false;

        MarkdownSvgRenderDemand? observedDemand = _host.LoadDemand;
        if (isVisible)
            observedDemand?.PromoteVisible();

        BitmapOutputAdmission.Snapshot admissionSnapshot = _resources.BitmapOutput.GetSnapshot();
        long currentCapacityVersion = admissionSnapshot.CapacityVersion;
        long currentWaiterSlotVersion = admissionSnapshot.WaiterSlotVersion;
        bool replacementSnapshot = _host.HasRetainedBitmapOutput(_bitmapOutputExistingTile != 0);

        ObservedBitmapOutputRequest observed;
        lock (_bitmapOutputSync)
        {
            if (_bitmapOutputDeferred == 0)
                return false;
            observed = ObserveBitmapOutputRequestNoLock(
                isVisible, replacementSnapshot, currentCapacityVersion, currentWaiterSlotVersion);
        }

        if (!observed.Valid || observed.Demand is null || observed.Bytes == 0)
        {
            if (observed.StaleStoredRequest || observed.Bytes == 0)
            {
                ClearBitmapOutputDeferral(expectedEpoch: observed.Epoch);
                return false;
            }

            // This call observed an older request, but the waiter still
            // belongs to the current request and must continue suppressing
            // duplicate work.
            return true;
        }

        if (observed.Waiter is { } waiter)
        {
            try
            {
                if (!waiter.UpdateDemand(observed.Bytes, observed.VisibleNow, observed.Replacement, observed.Version))
                    CancelBitmapOutputDeferral(observed.Epoch, resetLoad: false);
            }
            catch (Exception exception)
            {
                _host.RecordImageRetirementFailure(exception);
                CancelBitmapOutputDeferral(observed.Epoch, resetLoad: false);
            }
        }

        if (observed.RetryRegistration)
            ArmDeferredBitmapOutputRetryAfterWorkDrained();
        return true;
    }

    /// <summary>
    /// Validates the stored request against the box's current load identity,
    /// refreshes its visibility and replacement state, and decides whether a
    /// failed or stale waiter registration should be retried. The caller holds
    /// <c>_bitmapOutputSync</c> and has checked that output is deferred.
    /// </summary>
    private ObservedBitmapOutputRequest ObserveBitmapOutputRequestNoLock(
        bool isVisible, bool replacementSnapshot, long currentCapacityVersion, long currentWaiterSlotVersion)
    {
        long epoch = _bitmapOutputWaiterEpoch;
        MarkdownSvgRenderDemand? demand = _bitmapOutputDemand;
        if (demand is null ||
            !ReferenceEquals(demand, _host.LoadDemand) ||
            _bitmapOutputDemandGeneration != _host.LoadDemandGeneration ||
            _bitmapOutputSvgGeneration != _host.SvgRenderGeneration)
        {
            return new ObservedBitmapOutputRequest(epoch, demand, StaleStoredRequest: true);
        }

        bool previousVisible = _bitmapOutputVisible != 0;
        bool visibleNow = isVisible || demand.Priority == MarkdownSvgRenderPriority.Visible ||
            _host.IsViewportVisible;
        bool replacement = replacementSnapshot;
        Volatile.Write(ref _bitmapOutputVisible, visibleNow ? 1 : 0);
        Volatile.Write(ref _bitmapOutputReplacement, replacement ? 1 : 0);
        long bytes = _bitmapOutputRequiredBytes;
        long version = currentCapacityVersion;
        BitmapOutputAdmission.WaiterRegistration? waiter = _bitmapOutputWaiter;
        bool retryRegistration = BitmapOutputAdmission.ShouldRetryWaiterRegistration(
            registrationFailed: _bitmapOutputRegistrationFailed != 0,
            waiterPresent: waiter is not null,
            observedCapacityVersion: _bitmapOutputCapacityVersion,
            currentCapacityVersion: version,
            observedWaiterSlotVersion: _bitmapOutputWaiterSlotVersion,
            currentWaiterSlotVersion: currentWaiterSlotVersion,
            visibilityPromoted: !previousVisible && visibleNow);
        if (retryRegistration)
        {
            _bitmapOutputRegistrationFailed = 0;
            _bitmapOutputRegistrationNeeded = 1;
            _bitmapOutputCapacityVersion = version;
            _bitmapOutputWaiterSlotVersion = currentWaiterSlotVersion;
        }
        return new ObservedBitmapOutputRequest(epoch, demand, StaleStoredRequest: false)
        {
            Waiter = waiter,
            Bytes = bytes,
            Version = version,
            Replacement = replacement,
            VisibleNow = visibleNow,
            Valid = true,
            RetryRegistration = retryRegistration,
        };
    }

    /// <summary>
    /// True while this live deferred request has no registered governor token.
    /// The control's bounded recovery timer uses this only to select boxes for
    /// version-gated observation; it does not start image work or retain source
    /// state.
    /// </summary>
    internal bool NeedsBitmapOutputRecovery
    {
        get
        {
            if (!IsBitmapOutputParticipationEnabled ||
                _imageLifetime.IsCancellationRequested ||
                (_context.PerformanceSession?.IsDisposed ?? false))
            {
                return false;
            }

            lock (_bitmapOutputSync)
            {
                if (_host.IsRetired || _bitmapOutputDeferred == 0 ||
                    _bitmapOutputWaiterOwner is null || _bitmapOutputWaiter is not null)
                {
                    return false;
                }

                MarkdownSvgRenderDemand? demand = _bitmapOutputDemand;
                return demand is not null &&
                    ReferenceEquals(demand, _host.LoadDemand) &&
                    _bitmapOutputDemandGeneration == _host.LoadDemandGeneration &&
                    _bitmapOutputSvgGeneration == _host.SvgRenderGeneration;
            }
        }
    }

    internal void ArmDeferredBitmapOutputRetryAfterWorkDrained()
    {
        try { ArmDeferredBitmapOutputRetryAfterWorkDrainedCore(); }
        catch (Exception exception) { _host.RecordImageRetirementFailure(exception); }
    }

    private void ArmDeferredBitmapOutputRetryAfterWorkDrainedCore()
    {
        if (!_host.IsImageWorkDrained)
            return;

        ImageBitmapOutputWaiter? owner;
        long epoch;
        bool settleUnusedOffer;
        lock (_bitmapOutputSync)
        {
            settleUnusedOffer = _bitmapOutputRetryOfferPending != 0 &&
                _bitmapOutputRetryInProgress != 0;
        }
        if (settleUnusedOffer)
            SettleBitmapOutputRetry(capacityClaimed: false);

        lock (_bitmapOutputSync)
        {
            if (_bitmapOutputDeferred == 0 || _bitmapOutputRetryQueued != 0 ||
                _bitmapOutputRegistering != 0)
                return;

            if (_bitmapOutputRegistrationFailed != 0)
                return;
            if ((_bitmapOutputRegistrationNeeded == 0 && _bitmapOutputCapacityReady == 0) ||
                _bitmapOutputWaiterOwner is null)
            {
                return;
            }
            else
            {
                _bitmapOutputRetryQueued = 1;
                owner = _bitmapOutputWaiterOwner;
                epoch = _bitmapOutputWaiterEpoch;
            }
        }

        if (owner is null)
        {
            CancelBitmapOutputDeferral(epoch, resetLoad: false);
            return;
        }

        if (!ThreadPool.QueueUserWorkItem(
                static state =>
                {
                    var work = (BitmapOutputRetryWork)state!;
                    work.Owner.PrepareRetry(work.Epoch);
                },
                new BitmapOutputRetryWork(owner, epoch),
                preferLocal: false))
        {
            CancelBitmapOutputDeferral(epoch, resetLoad: true);
        }
    }

    internal void OnBitmapOutputCapacityAvailable(long epoch, long capacityVersion)
    {
        lock (_bitmapOutputSync)
        {
            if (_bitmapOutputWaiterEpoch != epoch || _bitmapOutputDeferred == 0 ||
                (_bitmapOutputWaiter is null && _bitmapOutputRegistering == 0))
            {
                return;
            }

            _bitmapOutputCapacityVersion = capacityVersion;
            _bitmapOutputCapacityReady = 1;
            _bitmapOutputRetryOfferPending = 1;
        }

        ArmDeferredBitmapOutputRetryAfterWorkDrained();
    }

    private void PrepareDeferredBitmapOutputWaiter(long epoch)
    {
        bool workDrained = _host.IsImageWorkDrained;
        if (!workDrained)
        {
            lock (_bitmapOutputSync)
                if (_bitmapOutputWaiterEpoch == epoch)
                    _bitmapOutputRetryQueued = 0;
            return;
        }

        ImageBitmapOutputWaiter? owner;
        BitmapOutputAdmission.WaiterRegistration? waiter;
        long bytes;
        long observedVersion;
        bool visible;
        bool replacement;
        MarkdownSvgRenderDemand? demand;
        int generation;
        int svgGeneration;
        bool needsTrim;
        bool creatingRegistration;
        long observedSlotVersion;
        lock (_bitmapOutputSync)
        {
            if (_bitmapOutputWaiterEpoch != epoch || _bitmapOutputDeferred == 0)
                return;
            owner = _bitmapOutputWaiterOwner;
            waiter = _bitmapOutputWaiter;
            bytes = _bitmapOutputRequiredBytes;
            observedVersion = _bitmapOutputCapacityVersion;
            observedSlotVersion = _bitmapOutputWaiterSlotVersion;
            visible = _bitmapOutputVisible != 0;
            replacement = _bitmapOutputReplacement != 0;
            demand = _bitmapOutputDemand;
            generation = _bitmapOutputDemandGeneration;
            svgGeneration = _bitmapOutputSvgGeneration;
            needsTrim = _bitmapOutputNeedsTrim != 0;
            _bitmapOutputNeedsTrim = 0;
            creatingRegistration = waiter is null;
            _bitmapOutputRegistering = creatingRegistration ? 1 : 0;
        }

        if (owner is null || demand is null ||
            !ReferenceEquals(_host.LoadDemand, demand) ||
            generation != _host.LoadDemandGeneration ||
            svgGeneration != _host.SvgRenderGeneration ||
            _imageLifetime.IsCancellationRequested ||
            (_context.PerformanceSession?.IsDisposed ?? false))
        {
            CancelBitmapOutputDeferral(epoch, resetLoad: false);
            return;
        }

        if (needsTrim)
        {
            try { _resources.BitmapCache.TrimForBitmapOutputPressure(); }
            catch (Exception exception) { _host.RecordImageRetirementFailure(exception); }
        }

        bool registrationReady = false;
        Exception? registrationFailure = null;
        BitmapOutputAdmission.WaiterRegistration? staleWaiter = null;
        long capacityVersionAtAttempt = observedVersion;
        long waiterSlotVersionAtAttempt = observedSlotVersion;
        try
        {
            registrationReady = owner.TryJoin(
                _resources.BitmapOutput,
                ref waiter,
                bytes,
                visible,
                replacement,
                observedVersion,
                ref staleWaiter,
                ref capacityVersionAtAttempt,
                ref waiterSlotVersionAtAttempt);
        }
        catch (Exception exception)
        {
            registrationFailure = exception;
        }

        bool keep;
        bool capacityReady;
        bool refreshRegistration;
        lock (_bitmapOutputSync)
        {
            keep = registrationReady && waiter is not null &&
                _bitmapOutputWaiterEpoch == epoch &&
                ReferenceEquals(_bitmapOutputWaiterOwner, owner) &&
                Volatile.Read(ref _bitmapOutputDeferred) != 0;
            if (_bitmapOutputWaiterEpoch == epoch)
            {
                _bitmapOutputRegistering = 0;
                if (keep)
                {
                    _bitmapOutputWaiter = waiter;
                    _bitmapOutputCapacityVersion = capacityVersionAtAttempt;
                    _bitmapOutputWaiterSlotVersion = waiterSlotVersionAtAttempt;
                    // Another tile can report a different edge/full-tile size
                    // while the governor call is outside this lock. Do not
                    // overwrite that newer request with the captured threshold.
                    _bitmapOutputRegistrationNeeded =
                        _bitmapOutputRequiredBytes != bytes ||
                        (_bitmapOutputVisible != 0) != visible ||
                        (_bitmapOutputReplacement != 0) != replacement ||
                        _bitmapOutputCapacityVersion != observedVersion
                            ? 1 : 0;
                }
                else if (!registrationReady)
                {
                    _bitmapOutputRegistrationFailed = 1;
                    _bitmapOutputRegistrationNeeded = 0;
                    _bitmapOutputCapacityVersion = capacityVersionAtAttempt;
                    _bitmapOutputWaiterSlotVersion = waiterSlotVersionAtAttempt;
                }
            }
            refreshRegistration = keep && _bitmapOutputRegistrationNeeded != 0;
            capacityReady = keep && !refreshRegistration && _bitmapOutputCapacityReady != 0;
            if (!capacityReady && _bitmapOutputWaiterEpoch == epoch)
                _bitmapOutputRetryQueued = 0;
        }

        if (!keep)
        {
            bool disposeRegistration = creatingRegistration || !registrationReady;
            if (!creatingRegistration && !registrationReady)
            {
                lock (_bitmapOutputSync)
                {
                    if (_bitmapOutputWaiterEpoch == epoch &&
                        (ReferenceEquals(_bitmapOutputWaiter, waiter) ||
                         (staleWaiter is not null && ReferenceEquals(_bitmapOutputWaiter, staleWaiter))))
                    {
                        _bitmapOutputWaiter = null;
                        _bitmapOutputRegistrationFailed = 1;
                        _bitmapOutputRegistrationNeeded = 0;
                    }
                }
            }
            // A stale-registration fallback may have created a fresh token
            // while the ImageBox epoch was replaced. That token was never
            // stored in the ImageBox and therefore cannot be detached by the
            // concurrent ClearBitmapOutputDeferral path.
            if (registrationReady && staleWaiter is not null && waiter is not null)
                waiter.Dispose();
            if (staleWaiter is not null)
                staleWaiter.Dispose();
            else if (disposeRegistration && waiter is not null)
                waiter.Dispose();
            if (registrationFailure is not null)
                _host.RecordImageRetirementFailure(registrationFailure);
            if (!registrationReady)
                SignalBitmapOutputRecoveryIfNeeded(epoch, owner);
            return;
        }

        staleWaiter?.Dispose();

        if (refreshRegistration)
        {
            ArmDeferredBitmapOutputRetryAfterWorkDrained();
            return;
        }

        if (capacityReady)
            _retry.DispatchBitmapOutputRetry(epoch);
    }

    private void SignalBitmapOutputRecoveryIfNeeded(long epoch, ImageBitmapOutputWaiter? owner)
    {
        if (owner is null)
            return;

        bool signal;
        lock (_bitmapOutputSync)
        {
            signal = !_host.IsRetired &&
                _bitmapOutputWaiterEpoch == epoch &&
                _bitmapOutputDeferred != 0 &&
                ReferenceEquals(_bitmapOutputWaiterOwner, owner) &&
                _bitmapOutputWaiter is null &&
                _bitmapOutputRegistrationFailed != 0 &&
                _bitmapOutputRecoverySignaledEpoch != epoch;
            if (signal)
                _bitmapOutputRecoverySignaledEpoch = epoch;
        }

        if (!signal)
            return;

        try
        {
            // This is a source-free recovery hint, not a load completion. The
            // host must branch on the flag before ordinary UIA/layout/paint work.
            _host.RaiseBitmapOutputRecoverySignal();
        }
        catch (Exception exception)
        {
            _host.RecordImageRetirementFailure(exception);
        }
    }

    internal void SettleBitmapOutputRetry(bool capacityClaimed)
    {
        BitmapOutputAdmission.WaiterRegistration? waiter;
        long epoch;
        bool remainsDeferred;
        lock (_bitmapOutputSync)
        {
            if (_bitmapOutputRetryOfferPending == 0)
            {
                _bitmapOutputRetryInProgress = 0;
                return;
            }

            _bitmapOutputRetryOfferPending = 0;
            _bitmapOutputRetryInProgress = 0;
            waiter = _bitmapOutputWaiter;
            epoch = _bitmapOutputWaiterEpoch;
            remainsDeferred = _bitmapOutputDeferred != 0;
        }

        waiter?.CompleteRetry(capacityClaimed);
        if (capacityClaimed)
            ClearBitmapOutputDeferral(expectedEpoch: epoch);
        else if (!remainsDeferred)
            ClearBitmapOutputDeferral(expectedEpoch: epoch);
    }

    internal void CancelBitmapOutputDeferral(long epoch, bool resetLoad)
    {
        MarkdownSvgRenderDemand? demand;
        int generation;
        int svgGeneration;
        lock (_bitmapOutputSync)
        {
            if (_bitmapOutputWaiterEpoch != epoch)
                return;
            demand = _bitmapOutputDemand;
            generation = _bitmapOutputDemandGeneration;
            svgGeneration = _bitmapOutputSvgGeneration;
        }

        if (!ClearBitmapOutputDeferral(expectedEpoch: epoch))
            return;
        if (resetLoad && demand is not null)
        {
            if (_host.TryResetLoad(demand, generation, svgGeneration))
                _retry.RequestBitmapOutputRepaint();
        }
    }

    /// <summary>
    /// The retry runner's read of a ready retry: false unless the epoch's
    /// request is still deferred with capacity ready and its retry queued.
    /// </summary>
    internal bool TryReadQueuedRetry(
        long epoch,
        out MarkdownSvgRenderDemand? demand,
        out int demandGeneration,
        out int svgGeneration,
        out bool visible,
        out bool tileOutput)
    {
        lock (_bitmapOutputSync)
        {
            if (_bitmapOutputWaiterEpoch != epoch || _bitmapOutputDeferred == 0 ||
                _bitmapOutputCapacityReady == 0 || _bitmapOutputRetryQueued == 0)
            {
                (demand, demandGeneration, svgGeneration, visible, tileOutput) = (null, 0, 0, false, false);
                return false;
            }

            demand = _bitmapOutputDemand;
            demandGeneration = _bitmapOutputDemandGeneration;
            svgGeneration = _bitmapOutputSvgGeneration;
            visible = _bitmapOutputVisible != 0;
            tileOutput = _bitmapOutputExistingTile != 0;
            return true;
        }
    }

    /// <summary>Lets a retry that found image work still active be queued again.</summary>
    internal void ReleaseQueuedRetry()
    {
        lock (_bitmapOutputSync)
            _bitmapOutputRetryQueued = 0;
    }

    /// <summary>Marks the epoch's retry in progress; false once the request is gone.</summary>
    internal bool TryBeginRetry(long epoch)
    {
        lock (_bitmapOutputSync)
        {
            if (_bitmapOutputWaiterEpoch != epoch || _bitmapOutputDeferred == 0)
                return false;
            _bitmapOutputRetryInProgress = 1;
            _bitmapOutputRetryQueued = 0;
            return true;
        }
    }

    /// <summary>Captures every deferral field under the lock as one consistent request.</summary>
    internal ImageBitmapOutputState GetState()
    {
        lock (_bitmapOutputSync)
        {
            return new ImageBitmapOutputState(
                HasWaiter: _bitmapOutputWaiter is not null,
                HasWaiterOwner: _bitmapOutputWaiterOwner is not null,
                HasImageCancellation: !_bitmapOutputImageCancellation.Equals(default(CancellationTokenRegistration)),
                HasSessionCancellation: !_bitmapOutputSessionCancellation.Equals(default(CancellationTokenRegistration)),
                HasDemand: _bitmapOutputDemand is not null,
                WaiterEpoch: _bitmapOutputWaiterEpoch,
                RecoverySignaledEpoch: _bitmapOutputRecoverySignaledEpoch,
                RequiredBytes: _bitmapOutputRequiredBytes,
                CapacityVersion: _bitmapOutputCapacityVersion,
                WaiterSlotVersion: _bitmapOutputWaiterSlotVersion,
                DemandGeneration: _bitmapOutputDemandGeneration,
                SvgGeneration: _bitmapOutputSvgGeneration,
                Deferred: _bitmapOutputDeferred != 0,
                Visible: _bitmapOutputVisible != 0,
                Replacement: _bitmapOutputReplacement != 0,
                ExistingTile: _bitmapOutputExistingTile != 0,
                NeedsTrim: _bitmapOutputNeedsTrim != 0,
                Registering: _bitmapOutputRegistering != 0,
                RegistrationNeeded: _bitmapOutputRegistrationNeeded != 0,
                CapacityReady: _bitmapOutputCapacityReady != 0,
                RetryQueued: _bitmapOutputRetryQueued != 0,
                RetryInProgress: _bitmapOutputRetryInProgress != 0,
                RetryOfferPending: _bitmapOutputRetryOfferPending != 0,
                RegistrationFailed: _bitmapOutputRegistrationFailed != 0,
                CancellationRegistered: _bitmapOutputCancellationRegistered != 0);
        }
    }

    internal void PrepareDeferredBitmapOutputWaiterSafely(long epoch)
    {
        try { PrepareDeferredBitmapOutputWaiter(epoch); }
        catch (Exception exception)
        {
            _host.RecordImageRetirementFailure(exception);
            CancelBitmapOutputDeferral(epoch, resetLoad: false);
        }
    }
}
