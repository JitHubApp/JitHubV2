using System;
using System.Threading;
using System.Threading.Tasks;
using MarkdownRenderer.Diagnostics;
using MarkdownRenderer.Images;
using MarkdownRenderer.Performance;

namespace MarkdownRenderer.Layout.Boxes;

/// <summary>
/// Moves an image box's resources out of and back into residency: suspension
/// for offscreen release, the release of suspended resources, resumption,
/// and the memory-pressure park of a tiled SVG's document.
/// </summary>
/// <remarks>
/// Suspension closes admission immediately while cancellation and native
/// work drain asynchronously. Release runs only under the committed snapshot
/// read lease after the canceled residency drained; metadata and exact
/// geometry survive, and re-entry goes back through the same source store
/// and authorized resolver, because the box keeps only a weak source
/// identity. Resumption waits for every old operation and publication, so no
/// callback can mutate a newer residency even if its provider ignores
/// cancellation.
/// </remarks>
internal sealed class ImageResidency
{
    private readonly ImageBox _box;
    private readonly ImageWorkLifetime _lifetime;
    private readonly ImageLoadState _load;
    private readonly SvgImageState _svg;
    private readonly SvgDocumentCustody _documents;

    internal ImageResidency(
        ImageBox box,
        ImageWorkLifetime lifetime,
        ImageLoadState load,
        SvgImageState svg,
        SvgDocumentCustody documents)
    {
        _box = box;
        _lifetime = lifetime;
        _load = load;
        _svg = svg;
        _documents = documents;
    }

    private SvgTileCache Cache => _box.SvgTileCache;

    /// <summary>Closes admission immediately; cancellation and native work drain asynchronously.</summary>
    internal void SuspendResources()
    {
        if (!_lifetime.TrySuspend(out RetirableCancellationTokenSource? cancellation, out ImageWorkLease? cancellationWork))
            return;

        _svg.AdvanceRenderGeneration();
        _box.SvgHostDeferral.ClearSvgHostDeferral();
        _box.OutputDeferral.ClearBitmapOutputDeferral();
        if (cancellation is not null)
            _ = CancelResidencyAsync(cancellation, cancellationWork!);
    }

    private async Task CancelResidencyAsync(RetirableCancellationTokenSource cancellation, ImageWorkLease work)
    {
        using (work)
        {
            try
            {
                await cancellation.CancelAsync().ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
            }
            catch (Exception exception)
            {
                _lifetime.RecordFailure(exception);
            }
        }
    }

    /// <summary>
    /// Detaches resources after the canceled residency drains. False while
    /// work remains or a detached SVG document still has to be disposed on
    /// the pool inside the terminal drain.
    /// </summary>
    internal bool TryReleaseSuspendedResources()
    {
        IMarkdownSvgDocument? document;
        SvgHostOwnerSlot? ownerSlot;
        ImageWorkLease? documentCleanup = null;
        lock (_lifetime.Sync)
        {
            if (_lifetime.IsDisposed)
                return true;
            if (!_lifetime.IsSuspended || _lifetime.WorkCountNoLock != 0)
                return false;
            if (_lifetime.IsResidencyReleasedNoLock)
                return true;

            // Reserve the per-document cleanup lease in the same critical
            // section as detachment, so terminal retirement cannot observe a
            // false zero-work gap. Unrelated SVG documents drain independently.
            document = _documents.Current;
            ownerSlot = _svg.AcceptedPlan?.OwnerSlot;
            if (document is not null)
            {
                documentCleanup = _documents.RetireNoLock(document);
                _documents.SetCurrentNoLock(null);
            }
            _lifetime.MarkResidencyReleasedNoLock();
            _svg.SetAcceptedPlanNoLock(null);
            _svg.SetParkingPlanNoLock(null);
            _svg.SetPressureParkedNoLock(false);
            _lifetime.DisposeResidencyCancellationNoLock();
        }

        _box.SvgHostDeferral.ClearSvgHostDeferral();
        _box.OutputDeferral.ClearBitmapOutputDeferral();
        ownerSlot?.Dispose();
        _box.SvgPublication.ReleaseSvgTiles();
        _box.Presentation.Release();

        if (document is null)
            return true;

        if (documentCleanup is not null)
            _documents.ScheduleDisposal(document, documentCleanup);
        return false;
    }

    /// <summary>
    /// Reopens admission under a new residency token once every old operation
    /// drained; a failed load stays started so it is not retried.
    /// </summary>
    internal bool TryResumeResources()
    {
        lock (_lifetime.Sync)
        {
            if (_lifetime.IsDisposed)
                return false;
            if (!_lifetime.IsSuspended)
                return true;
            if (_lifetime.WorkCountNoLock != 0)
                return false;

            _lifetime.ReplaceResidencyCancellationNoLock();
            _load.IsStarted = _load.IsFailed;
            _lifetime.EndSuspensionNoLock();
            return true;
        }
    }

    /// <summary>
    /// The pressure-park tier of <paramref name="plan"/> while it is the
    /// accepted, unparked plan of the current demand.
    /// </summary>
    internal bool TryGetSvgPressureParkTier(
        SvgTileRenderPlan plan,
        long demandVersion,
        out int tier)
    {
        tier = int.MaxValue;
        if (plan.Demand.Version != demandVersion)
            return false;

        lock (_lifetime.Sync)
        {
            if (!_svg.IsAcceptedTilePlan(plan) ||
                _svg.IsPressureParked ||
                !ReferenceEquals(_load.Demand, plan.Demand) ||
                plan.OwnerSlot is null)
            {
                return false;
            }
        }

        return Cache.TryGetPressureParkTier(plan, demandVersion, out tier);
    }

    /// <summary>
    /// Parks <paramref name="plan"/> under memory pressure: its tile work up
    /// to <paramref name="maximumTier"/> is canceled, its document retires,
    /// and its good pixels stay visible until a visible paint reopens it.
    /// </summary>
    internal bool TryParkSvgDocument(SvgTileRenderPlan plan, long demandVersion, int maximumTier)
    {
        if (maximumTier is < 0 or > 1 || plan.Demand.Version != demandVersion)
            return false;

        if (!TryPublishSvgParkingMarker(plan))
            return false;

        SvgPressurePark park = DetachParkedPlan(plan, demandVersion, maximumTier);
        if (!park.Parked || park.Document is null)
            return false;

        try { park.OwnerSlot?.Dispose(); }
        catch (Exception exception) { _lifetime.RecordFailure(exception); }
        if (park.Cleanup is not null)
            _documents.ScheduleDisposal(park.Document, park.Cleanup);
        QueueSvgPressureParkedVisualUpdate(
            plan.Demand,
            plan.RenderGeneration,
            park.PublicationEpoch);
        return true;
    }

    // Detaches the plan's parked tile work and, when the viewport claim and
    // every fence still hold, the plan itself. The parking marker, the
    // viewport claim of an unparked plan and the canceled tile work are
    // always released.
    private SvgPressurePark DetachParkedPlan(SvgTileRenderPlan plan, long demandVersion, int maximumTier)
    {
        RetirableCancellationTokenSource[] canceledWork = Array.Empty<RetirableCancellationTokenSource>();
        int canceledWorkCount = 0;
        bool ownsViewportParkingClaim = false;
        SvgPressurePark park = default;
        try
        {
            bool canPark = Cache.TryDetachPressureParkedWork(
                plan,
                demandVersion,
                maximumTier,
                out long viewportObservationVersion,
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

            lock (_lifetime.Sync)
            {
                park.Parked = canPark && TryCommitSvgPressureParkNoLock(
                    plan, demandVersion, viewportObservationVersion, ref park);
            }
        }
        finally
        {
            lock (_lifetime.Sync)
            {
                if (ReferenceEquals(_svg.ParkingPlan, plan))
                    _svg.SetParkingPlanNoLock(null);
            }

            if (ownsViewportParkingClaim && !park.Parked)
                plan.EndViewportParking();

            RequestCanceledSvgTileWork(canceledWork, canceledWorkCount);
        }

        return park;
    }

    // Publishes the parking marker for the accepted plan, so a concurrent
    // park of the same box or a tile-cache transition cannot start.
    private bool TryPublishSvgParkingMarker(SvgTileRenderPlan plan)
    {
        lock (_lifetime.Sync)
        {
            if (!_svg.IsAcceptedTilePlan(plan) ||
                !ReferenceEquals(_load.Demand, plan.Demand) ||
                plan.OwnerSlot is null ||
                Cache.HasPendingTransition ||
                _svg.ParkingPlan is not null)
            {
                return false;
            }
            _svg.SetParkingPlanNoLock(plan);
            return true;
        }
    }

    // Caller holds the image-work lock. Detaches the parked plan and retires
    // its document if the demand, viewport observation, marker and accepted
    // document are all unchanged since the work was detached.
    private bool TryCommitSvgPressureParkNoLock(
        SvgTileRenderPlan plan,
        long demandVersion,
        long viewportObservationVersion,
        ref SvgPressurePark park)
    {
        if (plan.Demand.Version != demandVersion ||
            Cache.ViewportObservationVersion != viewportObservationVersion ||
            !ReferenceEquals(_svg.ParkingPlan, plan) ||
            !_svg.IsAcceptedTilePlan(plan) ||
            Cache.HasPendingTransition ||
            !ReferenceEquals(_documents.Current, plan.Document))
        {
            return false;
        }

        park.Document = plan.Document;
        park.OwnerSlot = plan.OwnerSlot;
        park.Cleanup = _documents.RetireNoLock(plan.Document);
        _documents.SetCurrentNoLock(null);
        _svg.SetAcceptedPlanNoLock(null);
        _svg.SetParkingPlanNoLock(null);
        _svg.SetPressureParkedNoLock(true);
        park.PublicationEpoch = _svg.AdvancePressureParkPublicationEpochNoLock();
        _load.IsFailed = false;
        if (ReferenceEquals(_load.Demand, plan.Demand))
            _load.IsStarted = false;
        return true;
    }

    private void QueueSvgPressureParkedVisualUpdate(
        MarkdownSvgRenderDemand demand,
        int renderGeneration,
        long parkedPublicationEpoch)
    {
        try
        {
            var dispatcher = _box.Context.Dispatcher;
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
            _box.PublishOnUiThread(
                () =>
                {
                    lock (_lifetime.Sync)
                    {
                        if (_lifetime.IsDisposed || _lifetime.IsSuspended ||
                            renderGeneration != _svg.RenderGeneration ||
                            !ReferenceEquals(demand, _load.Demand) ||
                            !_svg.IsPressureParked ||
                            parkedPublicationEpoch != _svg.PressureParkPublicationEpoch)
                        {
                            return;
                        }
                    }

                    _box.RaiseLoadCompleted(layoutInvalidated: false);
                },
                tryEnqueueOverride: callback => dispatcher.TryEnqueue(() => callback()));
        }
        catch (Exception exception)
        {
            _lifetime.RecordFailure(exception);
        }
    }

    private void RequestCanceledSvgTileWork(
        RetirableCancellationTokenSource[] canceledWork,
        int canceledWorkCount)
    {
        for (int index = 0; index < canceledWorkCount; index++)
        {
            try { _ = CancellationTokenSourceRetirement.RequestCancellation(canceledWork[index]); }
            catch (Exception exception) { _lifetime.RecordFailure(exception); }
        }
    }
}
