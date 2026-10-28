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

    private bool TryObservePressureParkedVisibleGap(
        Rect destination,
        Rect viewport,
        out long visualDemandEpoch)
    {
        visualDemandEpoch = 0;
        if (!Volatile.Read(ref _svgPressureParked))
        {
            return false;
        }

        if (destination.Width <= 0 || destination.Height <= 0)
        {
            ClearPressureParkedViewportVisibility();
            return false;
        }

        Rect visible = IntersectRects(destination, viewport);
        if (visible.Width <= 0 || visible.Height <= 0)
        {
            lock (_svgTileLock)
            {
                Volatile.Write(ref _svgCurrentViewportVisible, 0);
                _svgVisibleTileRangeKnown = false;
            }
            return false;
        }

        var desired = PickRasterDimensions(_svgIntrinsicSize);
        int width = desired.Width;
        int height = desired.Height;
        if (width <= 0 || height <= 0)
        {
            ClearPressureParkedViewportVisibility();
            return false;
        }

        int visibleLeft = (int)Math.Clamp(
            Math.Floor((visible.Left - destination.Left) * desired.PhysicalViewport.Width / destination.Width),
            0,
            Math.Max(0, width - 1));
        int visibleTop = (int)Math.Clamp(
            Math.Floor((visible.Top - destination.Top) * desired.PhysicalViewport.Height / destination.Height),
            0,
            Math.Max(0, height - 1));
        int visibleRight = (int)Math.Clamp(
            Math.Ceiling((visible.Right - destination.Left) * desired.PhysicalViewport.Width / destination.Width),
            1,
            width);
        int visibleBottom = (int)Math.Clamp(
            Math.Ceiling((visible.Bottom - destination.Top) * desired.PhysicalViewport.Height / destination.Height),
            1,
            height);
        int firstX = visibleLeft / SvgTileSizePixels;
        int firstY = visibleTop / SvgTileSizePixels;
        int lastX = Math.Max(firstX, (visibleRight - 1) / SvgTileSizePixels);
        int lastY = Math.Max(firstY, (visibleBottom - 1) / SvgTileSizePixels);

        lock (_svgTileLock)
        {
            if (!Volatile.Read(ref _svgPressureParked))
                return false;

            Volatile.Write(ref _svgCurrentViewportVisible, 1);
            _svgVisibleFirstTileX = firstX;
            _svgVisibleFirstTileY = firstY;
            _svgVisibleLastTileX = lastX;
            _svgVisibleLastTileY = lastY;
            _svgVisibleTileRangeKnown = true;

            bool missing = !desired.UsesTiles ||
                width != _svgTileCacheWidthPixels ||
                height != _svgTileCacheHeightPixels ||
                !Nullable.Equals(_svgTileCachePhysicalViewport, desired.PhysicalViewport);
            for (int y = firstY; !missing && y <= lastY; y++)
            {
                for (int x = firstX; x <= lastX; x++)
                {
                    int pixelX = x * SvgTileSizePixels;
                    int pixelY = y * SvgTileSizePixels;
                    var key = (
                        pixelX,
                        pixelY,
                        Math.Min(SvgTileSizePixels, width - pixelX),
                        Math.Min(SvgTileSizePixels, height - pixelY));
                    if (!_svgTiles.ContainsKey(key))
                    {
                        missing = true;
                        break;
                    }
                }
            }

            if (!missing)
                return false;

            bool changed = !_svgPressureDemandSignatureKnown ||
                _svgPressureDemandFirstX != firstX || _svgPressureDemandFirstY != firstY ||
                _svgPressureDemandLastX != lastX || _svgPressureDemandLastY != lastY ||
                _svgPressureDemandWidth != width || _svgPressureDemandHeight != height ||
                !Nullable.Equals(_svgPressureDemandPhysicalViewport, desired.PhysicalViewport);
            if (changed)
            {
                _svgPressureDemandSignatureKnown = true;
                _svgPressureDemandFirstX = firstX;
                _svgPressureDemandFirstY = firstY;
                _svgPressureDemandLastX = lastX;
                _svgPressureDemandLastY = lastY;
                _svgPressureDemandWidth = width;
                _svgPressureDemandHeight = height;
                _svgPressureDemandPhysicalViewport = desired.PhysicalViewport;
                long current = Volatile.Read(ref _svgHostVisualDemandEpoch);
                if (current < long.MaxValue)
                    visualDemandEpoch = Interlocked.Increment(ref _svgHostVisualDemandEpoch);
                else
                    visualDemandEpoch = current;
            }
            else
            {
                visualDemandEpoch = Volatile.Read(ref _svgHostVisualDemandEpoch);
            }
            return true;
        }
    }

    private void ClearPressureParkedViewportVisibility()
    {
        lock (_svgTileLock)
        {
            if (!Volatile.Read(ref _svgPressureParked))
                return;
            Volatile.Write(ref _svgCurrentViewportVisible, 0);
            _svgVisibleTileRangeKnown = false;
        }
    }

    internal object? CaptureSvgViewportObservationToken() =>
        Volatile.Read(ref _svgTileRenderPlan);

    internal bool ObserveSvgViewportVisibility(object? expectedPlanToken, bool visible)
    {
        if (expectedPlanToken is not SvgTileRenderPlan expectedPlan)
            return false;
        if (!expectedPlan.TryBeginViewportMutation())
            return false;

        try
        {
            lock (_svgTileLock)
            {
                if (!IsAcceptedSvgTilePlan(expectedPlan) ||
                    !ReferenceEquals(Volatile.Read(ref _svgTileRenderPlan), expectedPlan) ||
                    Volatile.Read(ref _svgTileCacheTransitionToken) is not null)
                {
                    return false;
                }

                Volatile.Write(ref _svgCurrentViewportVisible, visible ? 1 : 0);
                // Scroll realization knows visibility, not the exact tile range.
                // Re-entering the viewport therefore stays pinned until Paint
                // computes a fresh range for the current viewport.
                _svgVisibleTileRangeKnown = false;
                _svgVisibleFirstTileX = 0;
                _svgVisibleFirstTileY = 0;
                _svgVisibleLastTileX = 0;
                _svgVisibleLastTileY = 0;
                Interlocked.Increment(ref _svgViewportObservationVersion);
                return true;
            }
        }
        finally
        {
            expectedPlan.EndViewportMutation();
        }
    }

    private void ReopenPressureParkedSvgForVisibleDemand(long visualDemandEpoch)
    {
        MarkdownSvgRenderDemand? demand = Volatile.Read(ref _svgLoadDemandTicket);
        if (demand is null)
            return;
        demand.PromoteVisible();

        if (Volatile.Read(ref _svgHostDeferred) != 0)
        {
            TryResumeDeferredSvgLoadForObservedDemandOrCapacity(demand);
            TryQueueDeferredSvgHostRetry(
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

    private bool RestoreSvgTileFallbackAfterFailure()
    {
        SharedCanvasBitmapCache.Lease[] failedTiles;
        lock (_svgTileLock)
        {
            if (_svgFallbackTiles is not { Count: > 0 })
                return false;

            failedTiles = new SharedCanvasBitmapCache.Lease[_svgTiles.Count];
            int index = 0;
            foreach (SharedCanvasBitmapCache.Lease tile in _svgTiles.Values)
                failedTiles[index++] = tile;

            _svgTiles = _svgFallbackTiles!;
            _svgFallbackTiles = null;
            _svgTileCacheIdentityPrefix = _svgFallbackIdentityPrefix;
            _svgTileCacheWidthPixels = _svgFallbackWidthPixels;
            _svgTileCacheHeightPixels = _svgFallbackHeightPixels;
            _svgTileCachePhysicalViewport = _svgFallbackPhysicalViewport;
            _svgFallbackIdentityPrefix = null;
            _svgFallbackWidthPixels = 0;
            _svgFallbackHeightPixels = 0;
            _svgFallbackPhysicalViewport = null;
            _svgHasRenderedTile = _svgTiles.Count > 0;
        }

        foreach (SharedCanvasBitmapCache.Lease tile in failedTiles)
            TryImageCleanup(tile.Dispose);
        return true;
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

        lock (_svgTileLock)
        {
            if (!IsCurrentSvgTilePlan(plan))
                return false;

            int currentViewportVisible = Volatile.Read(ref _svgCurrentViewportVisible);
            if (currentViewportVisible < 0)
            {
                // A never-painted lookahead plan is still reclaimable while
                // it remains the original overscan demand. Unknown geometry
                // must not make promoted visible work look evictable.
                if (demandVersion != 0 || plan.Demand.Priority != MarkdownSvgRenderPriority.Overscan)
                    return false;
                tier = 0;
                return true;
            }
            if (currentViewportVisible == 0)
            {
                tier = 0;
                return true;
            }
            if (!_svgVisibleTileRangeKnown)
                return false;

            for (int y = _svgVisibleFirstTileY; y <= _svgVisibleLastTileY; y++)
            {
                for (int x = _svgVisibleFirstTileX; x <= _svgVisibleLastTileX; x++)
                {
                    int pixelX = x * SvgTileSizePixels;
                    int pixelY = y * SvgTileSizePixels;
                    var key = (
                        pixelX,
                        pixelY,
                        Math.Min(SvgTileSizePixels, plan.FullWidthPixels - pixelX),
                        Math.Min(SvgTileSizePixels, plan.FullHeightPixels - pixelY));
                    if (!_svgTiles.ContainsKey(key) || _svgTilesInFlight.ContainsKey(key))
                        return false;
                }
            }

            foreach (KeyValuePair<(int X, int Y, int Width, int Height), SvgTileWorkOwner> active in _svgTileWorkOwners)
            {
                if (!ReferenceEquals(active.Value.Plan, plan))
                    continue;
                int tileX = active.Key.X / SvgTileSizePixels;
                int tileY = active.Key.Y / SvgTileSizePixels;
                if (tileX >= _svgVisibleFirstTileX && tileX <= _svgVisibleLastTileX &&
                    tileY >= _svgVisibleFirstTileY && tileY <= _svgVisibleLastTileY)
                    return false;
            }
        }

        tier = 1;
        return true;
    }

    internal bool TryParkSvgDocument(SvgTileRenderPlan plan, long demandVersion, int maximumTier)
    {
        if (maximumTier is < 0 or > 1 || plan.Demand.Version != demandVersion)
            return false;

        lock (_svgWorkSync)
        {
            if (!IsAcceptedSvgTilePlan(plan) ||
                !ReferenceEquals(Volatile.Read(ref _svgLoadDemandTicket), plan.Demand) ||
                plan.OwnerSlot is null ||
                Volatile.Read(ref _svgTileCacheTransitionToken) is not null ||
                Volatile.Read(ref _svgParkingPlan) is not null)
            {
                return false;
            }
            Volatile.Write(ref _svgParkingPlan, plan);
        }

        CancellationTokenSource[] canceledWork = Array.Empty<CancellationTokenSource>();
        int canceledWorkCount = 0;
        bool canPark = true;
        bool ownsParkingMarker = true;
        bool ownsViewportParkingClaim = false;
        bool parked = false;
        long viewportObservationVersion = 0;
        long parkedPublicationEpoch = 0;
        IMarkdownSvgDocument? document = null;
        SvgHostAdmissionCoordinator.OwnerSlot? ownerSlot = null;
        ImageWorkLease? cleanup = null;
        try
        {
            lock (_svgTileLock)
            {
                viewportObservationVersion = Volatile.Read(ref _svgViewportObservationVersion);
                canPark = IsSvgPressureParkEligibleNoLock(plan, demandVersion, maximumTier) &&
                    ReferenceEquals(Volatile.Read(ref _svgParkingPlan), plan);

                if (canPark)
                {
                    int ownedWorkCount = 0;
                    foreach (KeyValuePair<(int X, int Y, int Width, int Height), CancellationTokenSource> work in _svgTilesInFlight)
                    {
                        if (!_svgTileWorkOwners.TryGetValue(work.Key, out SvgTileWorkOwner? owner) ||
                            !ReferenceEquals(owner.Plan, plan))
                        {
                            continue;
                        }

                        int state = Volatile.Read(ref owner.State);
                        if (state is 1 or 3)
                        {
                            canPark = false;
                            break;
                        }
                        ownedWorkCount++;
                    }

                    if (canPark)
                    {
                        // This bounded snapshot is allocated before any owner state
                        // or map entry changes. OOM therefore leaves tile ownership
                        // intact and the outer finally still clears the marker.
                        canceledWork = ownedWorkCount == 0
                            ? Array.Empty<CancellationTokenSource>()
                            : new CancellationTokenSource[ownedWorkCount];
                        canPark = Volatile.Read(ref _svgViewportObservationVersion) == viewportObservationVersion &&
                            IsSvgPressureParkEligibleNoLock(plan, demandVersion, maximumTier) &&
                            ReferenceEquals(Volatile.Read(ref _svgParkingPlan), plan);
                    }

                    if (canPark)
                    {
                        foreach (KeyValuePair<(int X, int Y, int Width, int Height), CancellationTokenSource> work in _svgTilesInFlight)
                        {
                            if (!_svgTileWorkOwners.TryGetValue(work.Key, out SvgTileWorkOwner? owner) ||
                                !ReferenceEquals(owner.Plan, plan))
                            {
                                continue;
                            }

                            int state = Volatile.Read(ref owner.State);
                            if (state == 0 && Interlocked.CompareExchange(ref owner.State, 2, 0) != 0)
                            {
                                canPark = false;
                                break;
                            }
                            if (state is not (0 or 2))
                            {
                                canPark = false;
                                break;
                            }

                            _svgTilesInFlight.Remove(work.Key);
                            _svgTileWorkOwners.Remove(work.Key);
                            canceledWork[canceledWorkCount++] = work.Value;
                        }
                    }
                }
            }

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
                if (canPark &&
                    plan.Demand.Version == demandVersion &&
                    Volatile.Read(ref _svgViewportObservationVersion) == viewportObservationVersion &&
                    ReferenceEquals(Volatile.Read(ref _svgParkingPlan), plan) &&
                    IsAcceptedSvgTilePlan(plan) &&
                    Volatile.Read(ref _svgTileCacheTransitionToken) is null &&
                    ReferenceEquals(Volatile.Read(ref _svgDocument), plan.Document))
                {
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
                    parked = true;
                }
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

    private bool IsSvgPressureParkEligibleNoLock(
        SvgTileRenderPlan plan,
        long demandVersion,
        int maximumTier)
    {
        if (plan.Demand.Version != demandVersion ||
            !IsAcceptedSvgTilePlan(plan) ||
            Volatile.Read(ref _svgTileCacheTransitionToken) is not null ||
            !ReferenceEquals(Volatile.Read(ref _svgParkingPlan), plan))
            return false;

        int currentViewportVisible = Volatile.Read(ref _svgCurrentViewportVisible);
        if (currentViewportVisible < 0)
        {
            return demandVersion == 0 &&
                plan.Demand.Priority == MarkdownSvgRenderPriority.Overscan;
        }

        if (currentViewportVisible == 0)
            return true;
        if (currentViewportVisible != 1 || maximumTier < 1 || !_svgVisibleTileRangeKnown)
            return false;

        for (int y = _svgVisibleFirstTileY; y <= _svgVisibleLastTileY; y++)
        {
            for (int x = _svgVisibleFirstTileX; x <= _svgVisibleLastTileX; x++)
            {
                int pixelX = x * SvgTileSizePixels;
                int pixelY = y * SvgTileSizePixels;
                var key = (
                    pixelX,
                    pixelY,
                    Math.Min(SvgTileSizePixels, plan.FullWidthPixels - pixelX),
                    Math.Min(SvgTileSizePixels, plan.FullHeightPixels - pixelY));
                if (!_svgTiles.ContainsKey(key) || _svgTilesInFlight.ContainsKey(key))
                    return false;
            }
        }

        foreach (KeyValuePair<(int X, int Y, int Width, int Height), SvgTileWorkOwner> active in _svgTileWorkOwners)
        {
            if (!ReferenceEquals(active.Value.Plan, plan))
                continue;
            int tileX = active.Key.X / SvgTileSizePixels;
            int tileY = active.Key.Y / SvgTileSizePixels;
            if (tileX >= _svgVisibleFirstTileX && tileX <= _svgVisibleLastTileX &&
                tileY >= _svgVisibleFirstTileY && tileY <= _svgVisibleLastTileY)
            {
                return false;
            }
        }

        return true;
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
        ClearSvgHostDeferral();
        ClearBitmapOutputDeferral();
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
        SvgHostAdmissionCoordinator.OwnerSlot? ownerSlot;
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

        ClearSvgHostDeferral();
        ClearBitmapOutputDeferral();
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
