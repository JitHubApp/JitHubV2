using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MarkdownRenderer.Diagnostics;
using Windows.Foundation;
using MarkdownRenderer.Images;
using MarkdownRenderer.Performance;

namespace MarkdownRenderer.Layout.Boxes;

internal sealed partial class ImageBox : IMarkdownImageResidencyTarget
{
    private CancellationTokenSource? _imageResidencyCancellation;
    private volatile bool _resourcesSuspended;
    private bool _residencyReleased;
    private bool _hasPresentedImage;
    // Completed result carried from an earlier layout of the same
    // document. Immutable; never owns pixels or native resources.
    private CompletedImageCarry? _carriedCompletion;
    // True while the current failure is permanent (remembered or
    // deterministic), so a scene rebuild may carry it.
    private bool _failureCarryable;

    // Only a wake-up signal. The control reconciles on its dispatcher under
    // the snapshot read lease; this callback must never detach paint resources.
    public event EventHandler? ResidencyWorkDrained;

    internal object? CaptureSvgViewportObservationToken() =>
        Volatile.Read(ref _svgTileRenderPlan);

    internal bool ObserveSvgViewportVisibility(object? expectedPlanToken, bool visible) =>
        _svgTileCache.ObserveSvgViewportVisibility(expectedPlanToken, visible);

    private void ReopenPressureParkedSvgForVisibleDemand(long visualDemandEpoch)
    {
        MarkdownSvgRenderDemand? demand = Volatile.Read(ref _svgLoadDemandTicket);
        if (demand is null)
            return;
        demand.PromoteVisible();

        if (SvgHostDeferral.IsDeferred)
        {
            SvgHostDeferral.TryResumeDeferredSvgLoadForObservedDemandOrCapacity(demand);
            SvgHostDeferral.TryQueueDeferredSvgHostRetry(
                demand,
                Volatile.Read(ref _svgLoadDemandGeneration),
                observedDemandVersion: 0,
                observedCapacityVersion: 0,
                observedOwnerVersion: 0,
                observedVisualDemandEpoch: visualDemandEpoch);
            return;
        }

        if (!_loadStarted)
        {
            StartLoad(
                isVisible: true,
                demandOverride: demand,
                loadDemandGeneration: Volatile.Read(ref _svgLoadDemandGeneration));
        }
    }

    internal bool TryGetSvgPressureParkTier(
        SvgTileRenderPlan plan,
        long demandVersion,
        out int tier)
    {
        tier = int.MaxValue;
        if (plan.Demand.Version != demandVersion)
            return false;

        lock (_svgWorkSync)
        {
            if (!IsAcceptedSvgTilePlan(plan) ||
                Volatile.Read(ref _svgPressureParked) ||
                !ReferenceEquals(Volatile.Read(ref _svgLoadDemandTicket), plan.Demand) ||
                plan.OwnerSlot is null)
            {
                return false;
            }
        }

        return _svgTileCache.TryGetPressureParkTier(plan, demandVersion, out tier);
    }

    internal bool TryParkSvgDocument(SvgTileRenderPlan plan, long demandVersion, int maximumTier)
    {
        if (maximumTier is < 0 or > 1 || plan.Demand.Version != demandVersion)
            return false;

        if (!TryPublishSvgParkingMarker(plan))
            return false;

        CancellationTokenSource[] canceledWork = Array.Empty<CancellationTokenSource>();
        int canceledWorkCount = 0;
        bool canPark = true;
        bool ownsParkingMarker = true;
        bool ownsViewportParkingClaim = false;
        bool parked = false;
        long viewportObservationVersion = 0;
        long parkedPublicationEpoch = 0;
        IMarkdownSvgDocument? document = null;
        SvgHostOwnerSlot? ownerSlot = null;
        ImageWorkLease? cleanup = null;
        try
        {
            canPark = _svgTileCache.TryDetachPressureParkedWork(
                plan,
                demandVersion,
                maximumTier,
                out viewportObservationVersion,
                ref canceledWork,
                ref canceledWorkCount);

            if (canPark)
            {
                // A viewport observer can update the coarse visibility or
                // Paint can publish an exact range only while this per-plan
                // gate is open. Winning the claim makes the final version and
                // owner check below linearizable with respect to both paths.
                ownsViewportParkingClaim = plan.TryClaimViewportParking();
                canPark = ownsViewportParkingClaim;
            }

            lock (_svgWorkSync)
            {
                parked = canPark && TryCommitSvgPressureParkNoLock(
                    plan,
                    demandVersion,
                    viewportObservationVersion,
                    out document,
                    out ownerSlot,
                    out cleanup,
                    out parkedPublicationEpoch);
            }
        }
        finally
        {
            if (ownsParkingMarker)
            {
                lock (_svgWorkSync)
                {
                    if (ReferenceEquals(Volatile.Read(ref _svgParkingPlan), plan))
                        Volatile.Write(ref _svgParkingPlan, null);
                }
            }

            if (ownsViewportParkingClaim && !parked)
                plan.EndViewportParking();

            RequestCanceledSvgTileWork(canceledWork, canceledWorkCount);
        }

        if (!parked || document is null)
            return false;

        try { ownerSlot?.Dispose(); }
        catch (Exception exception) { RecordImageRetirementFailure(exception); }
        if (cleanup is not null)
            ScheduleSvgDocumentDisposal(document, cleanup);
        QueueSvgPressureParkedVisualUpdate(
            plan.Demand,
            plan.RenderGeneration,
            parkedPublicationEpoch);
        return true;
    }

    // Publishes the parking marker for the accepted plan, so a concurrent
    // park of the same box or a tile-cache transition cannot start.
    private bool TryPublishSvgParkingMarker(SvgTileRenderPlan plan)
    {
        lock (_svgWorkSync)
        {
            if (!IsAcceptedSvgTilePlan(plan) ||
                !ReferenceEquals(Volatile.Read(ref _svgLoadDemandTicket), plan.Demand) ||
                plan.OwnerSlot is null ||
                _svgTileCache.HasPendingTransition ||
                Volatile.Read(ref _svgParkingPlan) is not null)
            {
                return false;
            }
            Volatile.Write(ref _svgParkingPlan, plan);
            return true;
        }
    }

    // Caller holds _svgWorkSync. Detaches the parked plan and retires its
    // document if the demand, viewport observation, marker and accepted
    // document are all unchanged since the work was detached.
    private bool TryCommitSvgPressureParkNoLock(
        SvgTileRenderPlan plan,
        long demandVersion,
        long viewportObservationVersion,
        out IMarkdownSvgDocument? document,
        out SvgHostOwnerSlot? ownerSlot,
        out ImageWorkLease? cleanup,
        out long parkedPublicationEpoch)
    {
        document = null;
        ownerSlot = null;
        cleanup = null;
        parkedPublicationEpoch = 0;
        if (plan.Demand.Version != demandVersion ||
            _svgTileCache.ViewportObservationVersion != viewportObservationVersion ||
            !ReferenceEquals(Volatile.Read(ref _svgParkingPlan), plan) ||
            !IsAcceptedSvgTilePlan(plan) ||
            _svgTileCache.HasPendingTransition ||
            !ReferenceEquals(Volatile.Read(ref _svgDocument), plan.Document))
        {
            return false;
        }

        document = plan.Document;
        ownerSlot = plan.OwnerSlot;
        cleanup = RetireSvgDocumentNoLock(document);
        Volatile.Write(ref _svgDocument, null);
        Volatile.Write(ref _svgTileRenderPlan, null);
        Volatile.Write(ref _svgParkingPlan, null);
        _svgPressureParked = true;
        parkedPublicationEpoch = Interlocked.Increment(ref _svgPressureParkPublicationEpoch);
        _loadFailed = false;
        if (ReferenceEquals(Volatile.Read(ref _svgLoadDemandTicket), plan.Demand))
            _loadStarted = false;
        return true;
    }

    private void QueueSvgPressureParkedVisualUpdate(
        MarkdownSvgRenderDemand demand,
        int renderGeneration,
        long parkedPublicationEpoch)
    {
        try
        {
            var dispatcher = Context.Dispatcher;
            if (dispatcher is null)
            {
                MarkdownDiagnostics.WriteLine(
                    "[ImageBox] SVG pressure park completed without a dispatcher; visual update was not queued.");
                return;
            }

            // Force the existing publication owner through the dispatcher even
            // when parking happened on the UI thread. The callback carries no
            // plan/document/source/provider reference, and the event is raised
            // only after exact parked residency checks have completed.
            PublishOnUiThread(
                () =>
                {
                    lock (_svgWorkSync)
                    {
                        if (_disposed || _resourcesSuspended ||
                            renderGeneration != Volatile.Read(ref _svgRenderGeneration) ||
                            !ReferenceEquals(demand, Volatile.Read(ref _svgLoadDemandTicket)) ||
                            !Volatile.Read(ref _svgPressureParked) ||
                            parkedPublicationEpoch != Volatile.Read(ref _svgPressureParkPublicationEpoch))
                        {
                            return;
                        }
                    }

                    RaiseLoadCompleted(layoutInvalidated: false);
                },
                tryEnqueueOverride: callback => dispatcher.TryEnqueue(() => callback()));
        }
        catch (Exception exception)
        {
            RecordImageRetirementFailure(exception);
        }
    }

    private void RequestCanceledSvgTileWork(
        CancellationTokenSource[] canceledWork,
        int canceledWorkCount)
    {
        for (int index = 0; index < canceledWorkCount; index++)
        {
            try { _ = CancellationTokenSourceRetirement.RequestCancellation(canceledWork[index]); }
            catch (Exception exception) { RecordImageRetirementFailure(exception); }
        }
    }

    /// <summary>Closes admission immediately; cancellation and native work drain asynchronously.</summary>
    public void SuspendResources()
    {
        CancellationTokenSource? cancellation;
        ImageWorkLease? cancellationWork = null;
        lock (_svgWorkSync)
        {
            if (_disposed || _resourcesSuspended ||
                Context.PerformanceSession?.Options.ReleaseOffscreenImageResources != true)
                return;

            cancellation = _imageResidencyCancellation;
            if (cancellation is not null)
            {
                cancellationWork = new ImageWorkLease(this, CancellationToken.None);
                if (_svgWorkCount == 0)
                    _imageWorkDrained = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _svgWorkCount++;
            }
            _resourcesSuspended = true;
        }

        AdvanceSvgRenderGeneration();
        SvgHostDeferral.ClearSvgHostDeferral();
        OutputDeferral.ClearBitmapOutputDeferral();
        if (cancellation is not null)
            _ = CancelResidencyAsync(cancellation, cancellationWork!);
    }

    private async Task CancelResidencyAsync(CancellationTokenSource cancellation, ImageWorkLease work)
    {
        using (work)
        {
            try
            {
                await cancellation.CancelAsync().ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
            }
            catch (Exception exception)
            {
                RecordImageRetirementFailure(exception);
            }
        }
    }

    /// <summary>
    /// Called only under the committed snapshot read lease. Detaches resources
    /// after the canceled residency drains; metadata and exact geometry survive.
    /// A detached SVG document is disposed on the pool inside the terminal drain.
    /// </summary>
    public bool TryReleaseSuspendedResources()
    {
        IMarkdownSvgDocument? document;
        SvgHostOwnerSlot? ownerSlot;
        ImageWorkLease? documentCleanup = null;
        lock (_svgWorkSync)
        {
            if (_disposed)
                return true;
            if (!_resourcesSuspended || _svgWorkCount != 0)
                return false;
            if (_residencyReleased)
                return true;

            // Reserve the per-document cleanup lease in the same critical
            // section as detachment, so terminal retirement cannot observe a
            // false zero-work gap. Unrelated SVG documents drain independently.
            document = _svgDocument;
            ownerSlot = Volatile.Read(ref _svgTileRenderPlan)?.OwnerSlot;
            if (document is not null)
            {
                documentCleanup = RetireSvgDocumentNoLock(document);
                _svgDocument = null;
            }
            _residencyReleased = true;
            Volatile.Write(ref _svgTileRenderPlan, null);
            Volatile.Write(ref _svgParkingPlan, null);
            _svgPressureParked = false;
            _imageResidencyCancellation?.Dispose();
            _imageResidencyCancellation = null;
            // Re-entry uses the same source store and authorized resolver; this
            // box retains only a weak source-identity hint, not the byte array.
        }

        SvgHostDeferral.ClearSvgHostDeferral();
        OutputDeferral.ClearBitmapOutputDeferral();
        ownerSlot?.Dispose();
        ReleaseSvgTiles();
        ReleaseBitmap();

        if (document is null)
            return true;

        if (documentCleanup is not null)
            ScheduleSvgDocumentDisposal(document, documentCleanup);
        return false;
    }

    /// <summary>
    /// Re-entry waits for every old operation and publication. Consequently no
    /// callback can mutate a newer residency, even if its provider ignores cancel.
    /// </summary>
    public bool TryResumeResources()
    {
        lock (_svgWorkSync)
        {
            if (_disposed)
                return false;
            if (!_resourcesSuspended)
                return true;
            if (_svgWorkCount != 0)
                return false;

            var replacement = CancellationTokenSource.CreateLinkedTokenSource(_imageLifetimeCancellation.Token);
            _imageResidencyCancellation?.Dispose();
            _imageResidencyCancellation = replacement;
            _residencyReleased = false;
            _loadStarted = _loadFailed;
            _resourcesSuspended = false;
            return true;
        }
    }

    private ImageWorkLease? RetireSvgDocumentNoLock(IMarkdownSvgDocument document)
    {
        SvgDocumentUseState state = GetSvgDocumentUseStateNoLock(document);
        if (state.DisposalScheduled)
            return null;

        if (state.ActiveUses != 0)
        {
            state.Retired = true;
            return null;
        }

        ImageWorkLease disposalWork = state.ReservedDisposalWork ??
            BeginImageWorkNoLock(CancellationToken.None);
        state.ReservedDisposalWork = null;
        state.Retired = true;
        state.DisposalScheduled = true;
        return disposalWork;
    }

    private ImageWorkLease? RetireSvgDocumentNoLock(
        IMarkdownSvgDocument document,
        SvgDocumentUseState preparedState,
        ref ImageWorkLease? preparedDisposalWork)
    {
        if (!ReferenceEquals(preparedState.Document, document))
            throw new InvalidOperationException("Prepared SVG document retirement does not match its document.");
        if (preparedState.DisposalScheduled)
            return null;

        if (preparedState.ActiveUses != 0)
        {
            preparedState.Retired = true;
            // Transfer the already-accounted cleanup lease to the state so the
            // final outstanding document use cannot need a new allocation.
            if (preparedState.ReservedDisposalWork is null && preparedDisposalWork is not null)
            {
                preparedState.ReservedDisposalWork = preparedDisposalWork;
                preparedDisposalWork = null;
            }
            return null;
        }

        ImageWorkLease? disposalWork = preparedState.ReservedDisposalWork;
        if (disposalWork is not null)
        {
            preparedState.ReservedDisposalWork = null;
        }
        else
        {
            disposalWork = preparedDisposalWork ??
                throw new InvalidOperationException("SVG document retirement has no prepared cleanup lease.");
            preparedDisposalWork = null;
        }

        preparedState.Retired = true;
        preparedState.DisposalScheduled = true;
        return disposalWork;
    }

    private void ScheduleSvgDocumentDisposal(
        IMarkdownSvgDocument document,
        ImageWorkLease cleanupWork)
    {
        void ReleaseDocument()
        {
            using (cleanupWork)
                DisposeSvgDocument(document);
        }

        try { _ = Task.Run(ReleaseDocument); }
        catch (Exception exception)
        {
            try { RecordImageRetirementFailure(exception); }
            finally { ReleaseDocument(); }
        }
    }
}
