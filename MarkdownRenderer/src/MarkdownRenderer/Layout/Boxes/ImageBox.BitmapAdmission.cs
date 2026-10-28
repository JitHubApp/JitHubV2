using System;
using System.Threading;
using System.Threading.Tasks;
using MarkdownRenderer.Images;
using MarkdownRenderer.Performance;
using Microsoft.UI.Dispatching;
using Microsoft.Graphics.Canvas;

namespace MarkdownRenderer.Layout.Boxes;

internal sealed partial class ImageBox
{
    private readonly object _bitmapOutputSync = new();
    private BitmapOutputAdmission.WaiterRegistration? _bitmapOutputWaiter;
    private BitmapOutputWaiter? _bitmapOutputWaiterOwner;
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

    private readonly struct BitmapOutputWorkContext(
        MarkdownSvgRenderDemand? Demand,
        int LoadGeneration,
        int SvgGeneration,
        CancellationToken CancellationToken,
        bool IsExistingSvgTileOutput,
        bool IsValid)
    {
        public MarkdownSvgRenderDemand? Demand { get; init; } = Demand;
        public int LoadGeneration { get; init; } = LoadGeneration;
        public int SvgGeneration { get; init; } = SvgGeneration;
        public CancellationToken CancellationToken { get; init; } = CancellationToken;
        public bool IsExistingSvgTileOutput { get; init; } = IsExistingSvgTileOutput;
        public bool IsValid { get; init; } = IsValid;
    }

    private BitmapOutputWorkContext CaptureBitmapOutputWorkContext(
        CancellationToken token,
        bool isExistingSvgTileOutput = false,
        MarkdownSvgRenderDemand? expectedDemand = null,
        int? expectedSvgGeneration = null)
    {
        MarkdownSvgRenderDemand? demand = Volatile.Read(ref _svgLoadDemandTicket);
        int loadGeneration = Volatile.Read(ref _svgLoadDemandGeneration);
        int svgGeneration = Volatile.Read(ref _svgRenderGeneration);
        bool valid = !token.IsCancellationRequested &&
            (expectedDemand is null || ReferenceEquals(expectedDemand, demand)) &&
            (!expectedSvgGeneration.HasValue || expectedSvgGeneration.Value == svgGeneration);
        return new BitmapOutputWorkContext(
            demand,
            loadGeneration,
            svgGeneration,
            token,
            isExistingSvgTileOutput,
            valid);
    }

    private bool IsBitmapOutputWorkCurrent(in BitmapOutputWorkContext work) =>
        work.IsValid &&
        !work.CancellationToken.IsCancellationRequested &&
        ReferenceEquals(Volatile.Read(ref _svgLoadDemandTicket), work.Demand) &&
        Volatile.Read(ref _svgLoadDemandGeneration) == work.LoadGeneration &&
        Volatile.Read(ref _svgRenderGeneration) == work.SvgGeneration;

    private bool IsBitmapOutputParticipationEnabled =>
        Context.PerformanceSession?.Options.ReleaseOffscreenImageResources == true;

    private bool IsBitmapOutputDeferred => Volatile.Read(ref _bitmapOutputDeferred) != 0;

    private void RequestOversizedCachedBitmapTrimIfNeeded(SharedCanvasBitmapCache.Lease lease)
    {
        if (IsBitmapOutputParticipationEnabled &&
            lease.PixelWeightBytes > BitmapOutputAdmission.Shared.MaximumSingleOutputBytes)
        {
            SharedCanvasBitmapCache.RequestBitmapOutputPressureTrim();
        }
    }

    private bool TryReserveBitmapOutput(
        long bytes,
        out BitmapOutputAdmission.Reservation? reservation,
        bool isExistingSvgTileOutput = false,
        BitmapOutputWorkContext? expectedWork = null)
    {
        reservation = null;
        if (!IsBitmapOutputParticipationEnabled)
            return true;

        BitmapOutputAdmission admission = BitmapOutputAdmission.Shared;
        if (bytes <= 0 || bytes > admission.MaximumSingleOutputBytes)
            return false;

        if (expectedWork is { } initialWork &&
            (initialWork.IsExistingSvgTileOutput != isExistingSvgTileOutput ||
             !IsBitmapOutputWorkCurrent(initialWork)))
        {
            return false;
        }

        GetBitmapOutputDemand(
            isExistingSvgTileOutput,
            expectedWork,
            out bool isVisible,
            out bool isReplacement,
            out MarkdownSvgRenderDemand? demand,
            out int demandGeneration,
            out int svgGeneration);

        if (admission.TryReserve(bytes, isVisible, isReplacement, out reservation, out long version))
        {
            if (expectedWork is { } work)
            {
                if (!IsBitmapOutputWorkCurrent(work) ||
                    !CompleteBitmapOutputClaim(work))
                {
                    reservation!.Dispose();
                    reservation = null;
                    return false;
                }
            }
            return true;
        }

        if (expectedWork is { } deniedWork && IsBitmapOutputWorkCurrent(deniedWork))
        {
            RecordBitmapOutputDeferral(
                bytes,
                version,
                deniedWork,
                isVisible,
                isReplacement,
                needsCacheTrim: true);
        }
        return false;
    }

    private bool TryAdmitCachedBitmapOutput(
        SharedCanvasBitmapCache.Lease lease,
        BitmapOutputWorkContext? expectedWork = null)
        => TryAdmitCachedBitmapOutput(
            lease,
            isExistingSvgTileOutput: false,
            expectedWork: expectedWork);

    private bool TryAcquireAdmittedSvgBitmap(
        CanvasDevice device,
        string identity,
        out SharedCanvasBitmapCache.Lease? lease,
        bool isExistingSvgTileOutput = false,
        BitmapOutputWorkContext? expectedWork = null)
    {
        if (!SharedCanvasBitmapCache.TryAcquire(device, identity, out lease) || lease is null)
            return false;

        if (TryAdmitCachedBitmapOutput(lease, isExistingSvgTileOutput, expectedWork))
            return true;

        TryImageCleanup(lease.Dispose);
        lease = null;
        return false;
    }

    private bool TryAdmitCachedBitmapOutput(
        SharedCanvasBitmapCache.Lease lease,
        bool isExistingSvgTileOutput,
        BitmapOutputWorkContext? expectedWork = null)
    {
        ArgumentNullException.ThrowIfNull(lease);
        if (!IsBitmapOutputParticipationEnabled)
            return true;

        if (expectedWork is { } initialWork &&
            (initialWork.IsExistingSvgTileOutput != isExistingSvgTileOutput ||
             !IsBitmapOutputWorkCurrent(initialWork)))
        {
            return false;
        }

        BitmapOutputAdmission admission = BitmapOutputAdmission.Shared;
        GetBitmapOutputDemand(
            isExistingSvgTileOutput,
            expectedWork,
            out bool isVisible,
            out bool isReplacement,
            out _,
            out _,
            out _);

        if (lease.IsBitmapOutputAdmitted)
        {
            if (expectedWork is { } admittedWork)
                return IsBitmapOutputWorkCurrent(admittedWork) && CompleteBitmapOutputClaim(admittedWork);
            return true;
        }

        if (lease.TryAdmitBitmapOutput(isVisible, isReplacement, out long requiredBytes, out long version))
        {
            if (expectedWork is { } admittedWork)
            {
                if (!IsBitmapOutputWorkCurrent(admittedWork) ||
                    !CompleteBitmapOutputClaim(admittedWork))
                {
                    return false;
                }
            }
            return true;
        }

        if (requiredBytes > admission.MaximumSingleOutputBytes)
            SharedCanvasBitmapCache.RequestBitmapOutputPressureTrim();

        if (expectedWork is { } deniedWork && IsBitmapOutputWorkCurrent(deniedWork) &&
            deniedWork.Demand is not null && requiredBytes > 0 &&
            requiredBytes <= admission.MaximumSingleOutputBytes)
        {
            RecordBitmapOutputDeferral(
                requiredBytes,
                version,
                deniedWork,
                isVisible,
                isReplacement,
                needsCacheTrim: true);
        }

        return false;
    }

    private void GetBitmapOutputDemand(
        bool isExistingSvgTileOutput,
        BitmapOutputWorkContext? expectedWork,
        out bool isVisible,
        out bool isReplacement,
        out MarkdownSvgRenderDemand? demand,
        out int demandGeneration,
        out int svgGeneration)
    {
        demand = expectedWork.HasValue
            ? expectedWork.Value.Demand
            : Volatile.Read(ref _svgLoadDemandTicket);
        demandGeneration = expectedWork.HasValue
            ? expectedWork.Value.LoadGeneration
            : Volatile.Read(ref _svgLoadDemandGeneration);
        svgGeneration = expectedWork.HasValue
            ? expectedWork.Value.SvgGeneration
            : Volatile.Read(ref _svgRenderGeneration);
        isVisible = demand?.Priority == MarkdownSvgRenderPriority.Visible ||
            Volatile.Read(ref _svgCurrentViewportVisible) == 1;
        isReplacement = Volatile.Read(ref _bitmap) is not null ||
            Volatile.Read(ref _bitmapLease) is not null;

        if (!isReplacement)
        {
            lock (_svgTileLock)
            {
                isReplacement = (_svgFallbackTiles?.Count ?? 0) > 0 ||
                    (!isExistingSvgTileOutput && _svgTiles.Count > 0);
            }
        }
    }

    private bool RecordBitmapOutputDeferral(
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
            requiredBytes > BitmapOutputAdmission.Shared.MaximumSingleOutputBytes)
        {
            return false;
        }

        CancellationTokenRegistration priorImageRegistration = default;
        CancellationTokenRegistration priorSessionRegistration = default;
        BitmapOutputAdmission.WaiterRegistration? priorWaiter = null;
        BitmapOutputWaiter? waiterOwner = null;
        long waiterEpoch = 0;
        bool returnPriorOffer = false;
        bool registerCancellation = false;

        lock (_bitmapOutputSync)
        {
            if (_disposed || _resourcesSuspended || _imageLifetimeCancellation.IsCancellationRequested ||
                (Context.PerformanceSession?.IsDisposed ?? false) ||
                !IsBitmapOutputWorkCurrent(work) ||
                !ReferenceEquals(Volatile.Read(ref _svgLoadDemandTicket), demand) ||
                demandGeneration != Volatile.Read(ref _svgLoadDemandGeneration) ||
                (isExistingSvgTileOutput && svgGeneration != Volatile.Read(ref _svgRenderGeneration)))
            {
                return false;
            }

            bool sameDemand = _bitmapOutputWaiterOwner is not null &&
                ReferenceEquals(_bitmapOutputDemand, demand) &&
                _bitmapOutputDemandGeneration == demandGeneration &&
                _bitmapOutputSvgGeneration == svgGeneration &&
                (_bitmapOutputExistingTile != 0) == isExistingSvgTileOutput;
            if (!sameDemand)
            {
                long nextEpoch = _bitmapOutputWaiterEpoch == long.MaxValue
                    ? 1
                    : _bitmapOutputWaiterEpoch + 1;
                // Allocate before detaching the prior registration: failure
                // leaves its ownership intact and available for cleanup.
                var nextOwner = new BitmapOutputWaiter(this, nextEpoch);
                priorWaiter = _bitmapOutputWaiter;
                returnPriorOffer = _bitmapOutputRetryOfferPending != 0;
                priorImageRegistration = _bitmapOutputImageCancellation;
                priorSessionRegistration = _bitmapOutputSessionCancellation;
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
            }

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

        if (priorWaiter is not null)
        {
            if (returnPriorOffer)
                priorWaiter.ReturnUnusedNotification();
            else
                priorWaiter.Dispose();
        }
        priorImageRegistration.Unregister();
        priorSessionRegistration.Unregister();

        // Cancellation must be able to retire even a scalar-only waiter that
        // could not enter the governor's bounded queue. The callback target is
        // weak with respect to ImageBox and contains no source-bearing state.
        if (registerCancellation && waiterOwner is not null)
        {
            try
            {
                RegisterBitmapOutputCancellation(
                    waiterOwner,
                    waiterEpoch,
                    _imageLifetimeCancellation.Token,
                    session: false);
                CancellationToken sessionToken = Context.PerformanceSession?.LifetimeToken ?? default;
                if (sessionToken.CanBeCanceled)
                {
                    RegisterBitmapOutputCancellation(
                        waiterOwner,
                        waiterEpoch,
                        sessionToken,
                        session: true);
                }
            }
            catch (Exception exception)
            {
                RecordImageRetirementFailure(exception);
                CancelBitmapOutputDeferral(waiterEpoch, resetLoad: false);
                return false;
            }
        }
        return true;
    }

    private void RegisterBitmapOutputCancellation(
        BitmapOutputWaiter owner,
        long epoch,
        CancellationToken token,
        bool session)
    {
        CancellationTokenRegistration registration = token.Register(
            static state => ((BitmapOutputWaiter)state!).Cancel(), owner);
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

    private bool CompleteBitmapOutputClaim(in BitmapOutputWorkContext work)
    {
        if (!IsBitmapOutputWorkCurrent(work))
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
                !ReferenceEquals(_bitmapOutputDemand, Volatile.Read(ref _svgLoadDemandTicket)) ||
                _bitmapOutputDemandGeneration != Volatile.Read(ref _svgLoadDemandGeneration) ||
                _bitmapOutputSvgGeneration != Volatile.Read(ref _svgRenderGeneration);

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

    private bool ClearBitmapOutputDeferral(
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

    private bool ObserveDeferredBitmapOutput(bool isVisible)
    {
        if (!IsBitmapOutputParticipationEnabled || !IsBitmapOutputDeferred)
            return false;

        MarkdownSvgRenderDemand? observedDemand = Volatile.Read(ref _svgLoadDemandTicket);
        if (isVisible)
            observedDemand?.PromoteVisible();

        BitmapOutputAdmission.Snapshot admissionSnapshot = BitmapOutputAdmission.Shared.GetSnapshot();
        long currentCapacityVersion = admissionSnapshot.CapacityVersion;
        long currentWaiterSlotVersion = admissionSnapshot.WaiterSlotVersion;
        bool replacementSnapshot = HasRetainedBitmapOutput(_bitmapOutputExistingTile != 0);

        MarkdownSvgRenderDemand? demand;
        BitmapOutputAdmission.WaiterRegistration? waiter;
        long bytes;
        long version;
        bool replacement;
        bool visibleNow;
        bool valid;
        bool staleStoredRequest;
        bool retryRegistration;
        long epoch;
        lock (_bitmapOutputSync)
        {
            if (_bitmapOutputDeferred == 0)
                return false;
            epoch = _bitmapOutputWaiterEpoch;
            demand = _bitmapOutputDemand;
            if (demand is null ||
                !ReferenceEquals(demand, Volatile.Read(ref _svgLoadDemandTicket)) ||
                _bitmapOutputDemandGeneration != Volatile.Read(ref _svgLoadDemandGeneration) ||
                _bitmapOutputSvgGeneration != Volatile.Read(ref _svgRenderGeneration))
            {
                staleStoredRequest = true;
                waiter = null;
                bytes = 0;
                version = 0;
                replacement = false;
                visibleNow = false;
                valid = false;
                retryRegistration = false;
            }
            else
            {
                staleStoredRequest = false;
                bool previousVisible = _bitmapOutputVisible != 0;
                visibleNow = isVisible || demand.Priority == MarkdownSvgRenderPriority.Visible ||
                    Volatile.Read(ref _svgCurrentViewportVisible) == 1;
                replacement = replacementSnapshot;
                Volatile.Write(ref _bitmapOutputVisible, visibleNow ? 1 : 0);
                Volatile.Write(ref _bitmapOutputReplacement, replacement ? 1 : 0);
                bytes = _bitmapOutputRequiredBytes;
                version = currentCapacityVersion;
                waiter = _bitmapOutputWaiter;
                valid = true;
                retryRegistration = BitmapOutputAdmission.ShouldRetryWaiterRegistration(
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
            }
        }

        if (!valid || demand is null || bytes == 0)
        {
            if (staleStoredRequest || bytes == 0)
            {
                ClearBitmapOutputDeferral(expectedEpoch: epoch);
                return false;
            }

            // This call observed an older request, but the waiter still
            // belongs to the current request and must continue suppressing
            // duplicate work.
            return true;
        }

        if (waiter is not null)
        {
            try
            {
                if (!waiter.UpdateDemand(bytes, visibleNow, replacement, version))
                    CancelBitmapOutputDeferral(epoch, resetLoad: false);
            }
            catch (Exception exception)
            {
                RecordImageRetirementFailure(exception);
                CancelBitmapOutputDeferral(epoch, resetLoad: false);
            }
        }

        if (retryRegistration)
            ArmDeferredBitmapOutputRetryAfterWorkDrained();
        return true;
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
                _imageLifetimeCancellation.IsCancellationRequested ||
                (Context.PerformanceSession?.IsDisposed ?? false))
            {
                return false;
            }

            lock (_bitmapOutputSync)
            {
                if (_disposed || _resourcesSuspended || _bitmapOutputDeferred == 0 ||
                    _bitmapOutputWaiterOwner is null || _bitmapOutputWaiter is not null)
                {
                    return false;
                }

                MarkdownSvgRenderDemand? demand = _bitmapOutputDemand;
                return demand is not null &&
                    ReferenceEquals(demand, Volatile.Read(ref _svgLoadDemandTicket)) &&
                    _bitmapOutputDemandGeneration == Volatile.Read(ref _svgLoadDemandGeneration) &&
                    _bitmapOutputSvgGeneration == Volatile.Read(ref _svgRenderGeneration);
            }
        }
    }

    /// <summary>
    /// Performs a source-free, generation/version-gated observation of an
    /// existing deferral. It never calls EnsureLoading; a successful observation
    /// merely queues the already-defined deferred retry after active work drains.
    /// </summary>
    internal bool ObserveBitmapOutputRecovery(bool isVisible) =>
        ObserveDeferredBitmapOutput(isVisible);

    private bool HasRetainedBitmapOutput(bool existingTileOutput)
    {
        if (Volatile.Read(ref _bitmap) is not null ||
            Volatile.Read(ref _bitmapLease) is not null)
        {
            return true;
        }

        lock (_svgTileLock)
            return (_svgFallbackTiles?.Count ?? 0) > 0 ||
                (!existingTileOutput && _svgTiles.Count > 0);
    }

    private void ArmDeferredBitmapOutputRetryAfterWorkDrained()
    {
        try { ArmDeferredBitmapOutputRetryAfterWorkDrainedCore(); }
        catch (Exception exception) { RecordImageRetirementFailure(exception); }
    }

    private void ArmDeferredBitmapOutputRetryAfterWorkDrainedCore()
    {
        lock (_svgWorkSync)
        {
            if (_svgWorkCount != 0 || _disposed || _resourcesSuspended)
                return;
        }

        BitmapOutputWaiter? owner;
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

    private void OnBitmapOutputCapacityAvailable(long epoch, long capacityVersion)
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
        bool workDrained;
        lock (_svgWorkSync)
            workDrained = _svgWorkCount == 0 && !_disposed && !_resourcesSuspended;
        if (!workDrained)
        {
            lock (_bitmapOutputSync)
                if (_bitmapOutputWaiterEpoch == epoch)
                    _bitmapOutputRetryQueued = 0;
            return;
        }

        BitmapOutputWaiter? owner;
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
            !ReferenceEquals(Volatile.Read(ref _svgLoadDemandTicket), demand) ||
            generation != Volatile.Read(ref _svgLoadDemandGeneration) ||
            svgGeneration != Volatile.Read(ref _svgRenderGeneration) ||
            _imageLifetimeCancellation.IsCancellationRequested ||
            (Context.PerformanceSession?.IsDisposed ?? false))
        {
            CancelBitmapOutputDeferral(epoch, resetLoad: false);
            return;
        }

        if (needsTrim)
        {
            try { SharedCanvasBitmapCache.TrimForBitmapOutputPressure(); }
            catch (Exception exception) { RecordImageRetirementFailure(exception); }
        }

        bool registrationReady = false;
        Exception? registrationFailure = null;
        BitmapOutputAdmission.WaiterRegistration? staleWaiter = null;
        long capacityVersionAtAttempt = observedVersion;
        long waiterSlotVersionAtAttempt = observedSlotVersion;
        try
        {
            if (waiter is not null)
            {
                registrationReady = waiter.UpdateDemand(
                    bytes,
                    visible,
                    replacement,
                    observedVersion,
                    out capacityVersionAtAttempt,
                    out waiterSlotVersionAtAttempt);
                if (!registrationReady)
                {
                    // The captured registration may have been removed while
                    // this source-free retry was queued. Rejoin immediately;
                    // the fresh attempt returns an atomic slot generation on
                    // failure so a later removal cannot be lost.
                    staleWaiter = waiter;
                    registrationReady = BitmapOutputAdmission.Shared.TryRegisterWaiter(
                        owner,
                        bytes,
                        visible,
                        replacement,
                        observedVersion,
                        out BitmapOutputAdmission.WaiterRegistration? replacementWaiter,
                        out capacityVersionAtAttempt,
                        out waiterSlotVersionAtAttempt);
                    waiter = registrationReady ? replacementWaiter : null;
                }
            }
            else
            {
                registrationReady = BitmapOutputAdmission.Shared.TryRegisterWaiter(
                    owner,
                    bytes,
                    visible,
                    replacement,
                    observedVersion,
                    out waiter,
                    out capacityVersionAtAttempt,
                    out waiterSlotVersionAtAttempt);
            }
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
                RecordImageRetirementFailure(registrationFailure);
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
            DispatchBitmapOutputRetry(epoch);
    }

    private void SignalBitmapOutputRecoveryIfNeeded(long epoch, BitmapOutputWaiter? owner)
    {
        if (owner is null)
            return;

        bool signal;
        lock (_bitmapOutputSync)
        {
            signal = !_disposed && !_resourcesSuspended &&
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
            LoadCompleted?.Invoke(this, LoadCompletedEventArgs.ForBitmapOutputRecoverySignal());
        }
        catch (Exception exception)
        {
            RecordImageRetirementFailure(exception);
        }
    }

    private void DispatchBitmapOutputRetry(long epoch)
    {
        try
        {
            if (Context.Dispatcher is { } dispatcher)
            {
                if (!dispatcher.TryEnqueue(() => RunBitmapOutputRetrySafely(epoch)))
                    CancelBitmapOutputDeferral(epoch, resetLoad: true);
                return;
            }

            RunBitmapOutputRetrySafely(epoch);
        }
        catch (Exception exception)
        {
            RecordImageRetirementFailure(exception);
            CancelBitmapOutputDeferral(epoch, resetLoad: true);
        }
    }

    private void RunBitmapOutputRetrySafely(long epoch)
    {
        try
        {
            RunBitmapOutputRetry(epoch);
        }
        catch (Exception exception)
        {
            RecordImageRetirementFailure(exception);
            CancelBitmapOutputDeferral(epoch, resetLoad: true);
        }
    }

    private void RunBitmapOutputRetry(long epoch)
    {
        MarkdownSvgRenderDemand? demand;
        int demandGeneration;
        int svgGeneration;
        bool visible;
        bool tileOutput;
        lock (_bitmapOutputSync)
        {
            if (_bitmapOutputWaiterEpoch != epoch || _bitmapOutputDeferred == 0 ||
                _bitmapOutputCapacityReady == 0 || _bitmapOutputRetryQueued == 0)
            {
                return;
            }

            demand = _bitmapOutputDemand;
            demandGeneration = _bitmapOutputDemandGeneration;
            svgGeneration = _bitmapOutputSvgGeneration;
            visible = _bitmapOutputVisible != 0;
            tileOutput = _bitmapOutputExistingTile != 0;
        }

        bool workDrained;
        lock (_svgWorkSync)
            workDrained = _svgWorkCount == 0 && !_disposed && !_resourcesSuspended;
        if (!workDrained || demand is null ||
            !ReferenceEquals(Volatile.Read(ref _svgLoadDemandTicket), demand) ||
            demandGeneration != Volatile.Read(ref _svgLoadDemandGeneration) ||
            svgGeneration != Volatile.Read(ref _svgRenderGeneration) ||
            _imageLifetimeCancellation.IsCancellationRequested ||
            (Context.PerformanceSession?.IsDisposed ?? false))
        {
            if (!workDrained)
            {
                lock (_bitmapOutputSync)
                    _bitmapOutputRetryQueued = 0;
                return;
            }

            CancelBitmapOutputDeferral(epoch, resetLoad: true);
            return;
        }

        lock (_bitmapOutputSync)
        {
            if (_bitmapOutputWaiterEpoch != epoch || _bitmapOutputDeferred == 0)
                return;
            _bitmapOutputRetryInProgress = 1;
            _bitmapOutputRetryQueued = 0;
        }

        if (tileOutput)
        {
            // The accepted document/tree remains alive. Repaint queues only
            // the current visible tile work; the source-free waiter never
            // owns or reopens that document.
            ClearBitmapOutputDeferral(expectedEpoch: epoch);
            try { RaiseLoadCompleted(layoutInvalidated: false); }
            catch (Exception exception) { RecordImageRetirementFailure(exception); }
            return;
        }

        try
        {
            StartLoad(
                isVisible: visible,
                demandOverride: demand,
                loadDemandGeneration: demandGeneration);
        }
        catch (Exception exception)
        {
            RecordImageRetirementFailure(exception);
            CancelBitmapOutputDeferral(epoch, resetLoad: true);
            return;
        }

        bool noWork;
        lock (_svgWorkSync)
            noWork = _svgWorkCount == 0;
        if (noWork)
            SettleBitmapOutputRetry(capacityClaimed: false);
    }

    private void SettleBitmapOutputRetry(bool capacityClaimed)
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

    private void CancelBitmapOutputDeferral(long epoch, bool resetLoad)
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
            bool reset = false;
            lock (_svgHostDeferredSync)
            {
                if (!_disposed && !_resourcesSuspended &&
                    ReferenceEquals(Volatile.Read(ref _svgLoadDemandTicket), demand) &&
                    generation == Volatile.Read(ref _svgLoadDemandGeneration) &&
                    svgGeneration == Volatile.Read(ref _svgRenderGeneration))
                {
                    _loadStarted = false;
                    reset = true;
                }
            }

            if (reset)
                RequestBitmapOutputRepaint();
        }
    }

    private void RequestBitmapOutputRepaint()
    {
        MarkdownSvgRenderDemand? demand = Volatile.Read(ref _svgLoadDemandTicket);
        int generation = Volatile.Read(ref _svgLoadDemandGeneration);
        try
        {
            if (Context.Dispatcher is { } dispatcher)
            {
                _ = dispatcher.TryEnqueue(() => DeliverBitmapOutputRepaint(demand, generation));
            }
            else
            {
                DeliverBitmapOutputRepaint(demand, generation);
            }
        }
        catch (Exception exception)
        {
            RecordImageRetirementFailure(exception);
        }
    }

    private void DeliverBitmapOutputRepaint(MarkdownSvgRenderDemand? demand, int generation)
    {
        if (_disposed || _resourcesSuspended ||
            (Context.PerformanceSession?.IsDisposed ?? false) ||
            !ReferenceEquals(Volatile.Read(ref _svgLoadDemandTicket), demand) ||
            Volatile.Read(ref _svgLoadDemandGeneration) != generation)
        {
            return;
        }

        try { RaiseLoadCompleted(layoutInvalidated: false); }
        catch (Exception exception) { RecordImageRetirementFailure(exception); }
    }

    private void ReleaseAdmittedBitmap(
        ref CanvasBitmap? bitmap,
        ref BitmapOutputAdmission.Reservation? reservation)
    {
        CanvasBitmap? owned = bitmap;
        BitmapOutputAdmission.Reservation? charge = reservation;
        bitmap = null;
        reservation = null;

        bool cleanupSucceeded = true;
        if (owned is not null)
        {
            try { owned.Dispose(); }
            catch (Exception exception)
            {
                cleanupSucceeded = false;
                RecordImageRetirementFailure(exception);
            }
        }

        if (charge is null)
            return;
        if (!cleanupSucceeded || (owned is null && charge.IsCommitted))
        {
            charge.MarkUncertain();
            return;
        }

        if (owned is not null && charge.IsCommitted)
            charge.ReleaseAfterSuccessfulCleanup();
        else if (owned is null && !charge.IsCommitted)
            charge.Dispose();
        else
            charge.MarkUncertain();
    }

    private sealed class BitmapOutputWaiter : IBitmapOutputWaiter
    {
        private readonly WeakReference<ImageBox> _owner;
        private readonly long _epoch;

        internal BitmapOutputWaiter(ImageBox owner, long epoch)
        {
            _owner = new WeakReference<ImageBox>(owner);
            _epoch = epoch;
        }

        public void OnBitmapOutputCapacityAvailable(long capacityVersion)
        {
            if (_owner.TryGetTarget(out ImageBox? owner))
                owner.OnBitmapOutputCapacityAvailable(_epoch, capacityVersion);
        }

        internal void Cancel()
        {
            if (_owner.TryGetTarget(out ImageBox? owner))
                owner.CancelBitmapOutputDeferral(_epoch, resetLoad: false);
        }

        internal void DispatchRetry(long epoch)
        {
            if (epoch == _epoch && _owner.TryGetTarget(out ImageBox? owner))
                owner.DispatchBitmapOutputRetry(epoch);
        }

        internal void PrepareRetry(long epoch)
        {
            if (epoch == _epoch && _owner.TryGetTarget(out ImageBox? owner))
            {
                try { owner.PrepareDeferredBitmapOutputWaiter(epoch); }
                catch (Exception exception)
                {
                    owner.RecordImageRetirementFailure(exception);
                    owner.CancelBitmapOutputDeferral(epoch, resetLoad: false);
                }
            }
        }
    }

    private readonly struct BitmapOutputRetryWork(BitmapOutputWaiter Owner, long Epoch)
    {
        public BitmapOutputWaiter Owner { get; init; } = Owner;
        public long Epoch { get; init; } = Epoch;
    }
}
