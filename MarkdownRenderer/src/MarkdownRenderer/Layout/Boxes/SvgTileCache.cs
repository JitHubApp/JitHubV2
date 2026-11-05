using System;
using System.Collections.Generic;
using System.Threading;
using MarkdownRenderer.Images;
using Microsoft.Graphics.Canvas;
using Windows.Foundation;
using SvgTileKey = (int X, int Y, int Width, int Height);
using SvgRasterPlan = (int Width, int Height, bool UsesTiles, MarkdownRenderer.Images.MarkdownSvgPhysicalViewport PhysicalViewport);

namespace MarkdownRenderer.Layout.Boxes;

/// <summary>
/// An image box's bounded SVG tile residency: the published tile leases, the
/// previous good grid kept as a fallback, in-flight tile work, the visible
/// tile range and viewport observation, and the pressure-demand signature.
/// Every field is guarded by the cache's own tile lock. The accepted plan,
/// document and generations stay with the box and are read through
/// <see cref="ISvgTileCacheHost"/>; the box's publication and image-work
/// locks are taken before this lock, never while holding it.
/// </summary>
/// <remarks>
/// This file is longer than 500 lines because the current grid, the fallback
/// grid, in-flight work and viewport observation all change together under
/// the one tile lock. The fallback grid is the next split, once it can be
/// handed over without a second lock.
/// </remarks>
internal sealed class SvgTileCache : ISvgTileGapFill
{
    internal const int SvgTileSizePixels = 1024;

    private readonly ISvgTileCacheHost _host;
    private readonly object _svgTileLock = new();
    private Dictionary<SvgTileKey, SharedCanvasBitmapCache.Lease> _svgTiles = new();
    private readonly SvgTileWorkSet _work = new();
    private object? _svgTileCacheTransitionToken;
    private bool _svgHasRenderedTile;
    private string? _svgTileCacheIdentityPrefix;
    private int _svgTileCacheWidthPixels;
    private int _svgTileCacheHeightPixels;
    private MarkdownSvgPhysicalViewport? _svgTileCachePhysicalViewport;
    private Dictionary<SvgTileKey, SharedCanvasBitmapCache.Lease>? _svgFallbackTiles;
    private string? _svgFallbackIdentityPrefix;
    private int _svgFallbackWidthPixels;
    private int _svgFallbackHeightPixels;
    private MarkdownSvgPhysicalViewport? _svgFallbackPhysicalViewport;
    private readonly SvgTileViewportObservation _viewport = new();

    internal SvgTileCache(ISvgTileCacheHost host)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
    }

    /// <summary>
    /// The tile lock guarding every field here. Callers that also take the
    /// box's publication or image-work locks take those first.
    /// </summary>
    internal object Sync => _svgTileLock;

    /// <summary>Whether a tile from the current grid has been published since the last reset.</summary>
    internal bool HasRenderedTile => _svgHasRenderedTile;

    /// <summary>Whether a pressure-parked image has recorded a visible-demand signature.</summary>
    internal bool PressureDemandSignatureKnown => _viewport.PressureSignatureKnown;

    /// <summary>Whether the latest viewport observation placed the image in the viewport.</summary>
    internal bool IsViewportVisible => _viewport.Visibility == 1;

    /// <summary>Increments whenever the observed visibility or visible tile range changes.</summary>
    internal long ViewportObservationVersion => _viewport.Version;

    /// <summary>Whether a tile-cache transition is staged and closes tile admission.</summary>
    internal bool HasPendingTransition => Volatile.Read(ref _svgTileCacheTransitionToken) is not null;

    /// <summary>Whether <paramref name="expectedToken"/> is the staged transition (or none is staged when null).</summary>
    internal bool IsCurrentTransition(object? expectedToken) =>
        ReferenceEquals(Volatile.Read(ref _svgTileCacheTransitionToken), expectedToken);

    /// <summary>
    /// Records that a tile was published. Like the box's other publication
    /// flags it is written on the UI thread without the tile lock.
    /// </summary>
    internal void MarkTileRendered() => _svgHasRenderedTile = true;

    /// <summary>
    /// Clears the rendered-tile flag and the pressure-demand signature for a
    /// new accepted plan. The caller holds the box's image-work lock, as the
    /// local publication commit always has; the tile lock is not taken.
    /// </summary>
    internal void ResetRenderedTileState()
    {
        _svgHasRenderedTile = false;
        _viewport.ForgetPressureSignature();
    }

    /// <summary>
    /// Paints the current tile grid under the tile lock. Holes fall back to
    /// the previous grid, then to whatever the box paints without tiles.
    /// </summary>
    internal void PaintTiles(CanvasDrawingSession ds, Rect destination, Rect viewport)
    {
        lock (_svgTileLock)
        {
            SvgTilePainter.PaintLayer(
                ds,
                _svgTiles,
                destination,
                viewport,
                _svgTileCacheWidthPixels,
                _svgTileCacheHeightPixels,
                _svgTileCachePhysicalViewport,
                this,
                useFallbackTiles: true);
        }
    }

    /// <summary>Whether tiles that still own bitmap output are resident.</summary>
    internal bool HasRetainedTileOutput(bool existingTileOutput)
    {
        lock (_svgTileLock)
            return (_svgFallbackTiles?.Count ?? 0) > 0 ||
                (!existingTileOutput && _svgTiles.Count > 0);
    }

    /// <summary>Reads the whole cache state at once, under the tile lock.</summary>
    internal SvgTileCacheSnapshot CaptureSnapshot()
    {
        lock (_svgTileLock)
        {
            return new SvgTileCacheSnapshot(
                new Dictionary<SvgTileKey, SharedCanvasBitmapCache.Lease>(_svgTiles),
                _svgFallbackTiles is { } fallback
                    ? new Dictionary<SvgTileKey, SharedCanvasBitmapCache.Lease>(fallback)
                    : null,
                _work.CopyCancellations(),
                _svgTileCacheIdentityPrefix,
                _svgTileCacheWidthPixels,
                _svgTileCacheHeightPixels,
                _svgFallbackIdentityPrefix,
                _svgFallbackWidthPixels,
                _svgFallbackHeightPixels,
                _svgHasRenderedTile,
                _svgTileCacheTransitionToken is not null,
                _viewport.Visibility,
                _viewport.RangeKnown,
                _viewport.FirstTileX,
                _viewport.FirstTileY,
                _viewport.LastTileX,
                _viewport.LastTileY,
                _viewport.Version);
        }
    }

    /// <summary>Whether no tile, fallback, in-flight work, transition, or cache identity is held.</summary>
    internal bool IsClear()
    {
        lock (_svgTileLock)
        {
            return _svgTileCacheTransitionToken is null &&
                _svgTiles.Count == 0 &&
                _work.IsEmpty &&
                _svgFallbackTiles is null &&
                _svgFallbackIdentityPrefix is null &&
                _svgFallbackWidthPixels == 0 &&
                _svgFallbackHeightPixels == 0 &&
                _svgFallbackPhysicalViewport is null &&
                _svgTileCacheIdentityPrefix is null &&
                _svgTileCacheWidthPixels == 0 &&
                _svgTileCacheHeightPixels == 0 &&
                _svgTileCachePhysicalViewport is null &&
                !_svgHasRenderedTile;
        }
    }

    /// <summary>
    /// Installs a rendered tile if <paramref name="workCancellation"/> still
    /// owns <paramref name="key"/> for the current plan. The replaced lease is
    /// returned for retirement outside the lock.
    /// </summary>
    internal bool TryPublishTile(
        SvgTileRenderPlan plan,
        SvgTileKey key,
        CancellationTokenSource workCancellation,
        bool rendererCurrent,
        SharedCanvasBitmapCache.Lease publishedLease,
        ref SvgTilePublicationOwnership publicationOwnership,
        ref SharedCanvasBitmapCache.Lease? previous,
        ref bool transferred)
    {
        bool accepted;
        lock (_svgTileLock)
        {
            accepted = rendererCurrent &&
                IsCurrentSvgTileWorkOwnerNoLock(plan, key, workCancellation);
            if (accepted)
            {
                _svgTiles.Remove(key, out previous);
                _svgTiles.Add(key, publishedLease);
                transferred = publicationOwnership.TryTransfer();
                if (!transferred)
                {
                    // A rejection cleanup must never race an
                    // accepted transfer. If it already won,
                    // remove the just-added mapping while this
                    // lock still protects the dictionary.
                    _svgTiles.Remove(key);
                    accepted = false;
                }
            }
        }
        return accepted;
    }

    /// <summary>Removes queued work whose render task could not be scheduled.</summary>
    internal void AbandonUnscheduledTileWork(SvgTileKey key, SvgTileWorkOwner workOwner)
    {
        lock (_svgTileLock)
            _work.Abandon(key, workOwner);
    }

    /// <summary>
    /// Claims terminal invalidation of a failed tile's work owner (state 3),
    /// which prevents its completion callback from removing or reassigning
    /// the map entry until the failure transition detaches the whole grid.
    /// </summary>
    internal bool TryBeginFailedTileInvalidation(
        SvgTileRenderPlan plan,
        SvgTileKey key,
        SvgTileWorkOwner workOwner)
    {
        lock (_svgTileLock)
            return _work.TryBeginInvalidation(plan, key, workOwner);
    }

    /// <summary>
    /// Detaches every tile, fallback tile and in-flight work entry. The caller
    /// holds the box's publication lock and retires the result with
    /// <see cref="RetireDetached"/> after releasing it.
    /// </summary>
    internal (SharedCanvasBitmapCache.Lease[] Tiles, CancellationTokenSource[] Work) DetachAll()
    {
        SharedCanvasBitmapCache.Lease[] tiles;
        CancellationTokenSource[] work;
        lock (_svgTileLock)
        {
            int tileCount = _svgTiles.Count + (_svgFallbackTiles?.Count ?? 0);
            tiles = new SharedCanvasBitmapCache.Lease[tileCount];
            int offset = 0;
            foreach (SharedCanvasBitmapCache.Lease tile in _svgTiles.Values)
                tiles[offset++] = tile;
            if (_svgFallbackTiles is { } fallback)
            {
                foreach (SharedCanvasBitmapCache.Lease tile in fallback.Values)
                    tiles[offset++] = tile;
            }
            work = _work.ToCancellationArray();
            _svgTiles.Clear();
            _svgFallbackTiles?.Clear();
            _svgFallbackTiles = null;
            _svgFallbackIdentityPrefix = null;
            _svgFallbackWidthPixels = 0;
            _svgFallbackHeightPixels = 0;
            _svgFallbackPhysicalViewport = null;
            _work.Clear();
            _svgTileCacheIdentityPrefix = null;
            _svgTileCacheWidthPixels = 0;
            _svgTileCacheHeightPixels = 0;
            _svgTileCachePhysicalViewport = null;
            _svgHasRenderedTile = false;
            _viewport.SetVisibility(0);
            _viewport.ForgetRangeAndSignature();
            _svgTileCacheTransitionToken = null;
        }
        return (tiles, work);
    }

    /// <summary>Retires leases and requests cancellation of work detached by <see cref="DetachAll"/>.</summary>
    internal void RetireDetached(SharedCanvasBitmapCache.Lease[] tiles, CancellationTokenSource[] work)
    {
        foreach (SharedCanvasBitmapCache.Lease tile in tiles)
            _host.TryImageCleanup(tile.Dispose);
        foreach (CancellationTokenSource cancellation in work)
        {
            try
            {
                // CancelAsync transitions the token immediately but runs
                // provider/linked-source callbacks away from this UI path.
                // CompleteSvgTileWork owns final source retirement and adds
                // both callback completion and source disposal to the image
                // lifetime barrier.
                _ = CancellationTokenSourceRetirement.RequestCancellation(cancellation);
            }
            catch (Exception exception) { _host.RecordImageRetirementFailure(exception); }
        }
    }

    /// <summary>
    /// The tile-lock half of the pressure-park tier probe: 0 when the plan is
    /// offscreen (or an unpainted overscan lookahead), 1 when its visible
    /// range is fully resident with no visible work in flight.
    /// </summary>
    internal bool TryGetPressureParkTier(
        SvgTileRenderPlan plan,
        long demandVersion,
        out int tier)
    {
        tier = int.MaxValue;
        lock (_svgTileLock)
        {
            if (!IsCurrentSvgTilePlan(plan))
                return false;

            int currentViewportVisible = _viewport.Visibility;
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
            if (!_viewport.RangeKnown)
                return false;

            if (!IsVisibleRangeSettledNoLock(plan))
                return false;
        }

        tier = 1;
        return true;
    }

    /// <summary>
    /// The tile-lock half of pressure parking: under one tile-lock section,
    /// rechecks eligibility for the box's parking plan and claims and
    /// detaches that plan's in-flight work. Detached cancellation sources are
    /// written to <paramref name="canceledWork"/> as they are removed, so the
    /// caller can cancel them even if a later step fails.
    /// </summary>
    internal bool TryDetachPressureParkedWork(
        SvgTileRenderPlan plan,
        long demandVersion,
        int maximumTier,
        out long viewportObservationVersion,
        ref CancellationTokenSource[] canceledWork,
        ref int canceledWorkCount)
    {
        bool canPark;
        lock (_svgTileLock)
        {
            viewportObservationVersion = _viewport.Version;
            canPark = IsSvgPressureParkEligibleNoLock(plan, demandVersion, maximumTier) &&
                ReferenceEquals(_host.SvgParkingPlan, plan);

            if (canPark)
            {
                int ownedWorkCount = _work.CountDetachablePlanWork(plan);
                canPark = ownedWorkCount >= 0;

                if (canPark)
                {
                    // This bounded snapshot is allocated before any owner state
                    // or map entry changes. OOM therefore leaves tile ownership
                    // intact and the outer finally still clears the marker.
                    canceledWork = ownedWorkCount == 0
                        ? Array.Empty<CancellationTokenSource>()
                        : new CancellationTokenSource[ownedWorkCount];
                    canPark = _viewport.Version == viewportObservationVersion &&
                        IsSvgPressureParkEligibleNoLock(plan, demandVersion, maximumTier) &&
                        ReferenceEquals(_host.SvgParkingPlan, plan);
                }

                if (canPark)
                    canPark = _work.TryDetachPlanWork(plan, canceledWork, ref canceledWorkCount);
            }
        }
        return canPark;
    }

    internal bool IsCurrentSvgTilePlan(SvgTileRenderPlan plan) =>
        _host.IsAcceptedSvgTilePlan(plan) &&
        Volatile.Read(ref _svgTileCacheTransitionToken) is null &&
        !ReferenceEquals(plan, _host.SvgParkingPlan);

    // The caller holds _svgTileLock. A hole in the new tile grid shows the
    // fallback grid, then the previous whole bitmap, then the placeholder.
    void ISvgTileGapFill.FillSvgTileGap(CanvasDrawingSession ds, Rect destination, Rect gap, bool useFallbackTiles)
    {
        if (useFallbackTiles && _svgFallbackTiles is { Count: > 0 } fallback &&
            _svgFallbackWidthPixels > 0 && _svgFallbackHeightPixels > 0)
        {
            SvgTilePainter.PaintLayer(ds, fallback, destination, gap,
                _svgFallbackWidthPixels, _svgFallbackHeightPixels,
                _svgFallbackPhysicalViewport, this, useFallbackTiles: false);
        }
        else
        {
            _host.FillSvgTileGapWithoutTiles(ds, destination);
        }
    }

    /// <summary>
    /// Records that the accepted plan's destination is outside the viewport.
    /// The caller holds the plan's viewport-mutation gate.
    /// </summary>
    internal void ObserveOffscreen(SvgTileRenderPlan plan)
    {
        lock (_svgTileLock)
        {
            if (IsCurrentSvgTilePlan(plan))
                _viewport.ObserveOffscreen();
        }
    }

    /// <summary>
    /// Under one tile-lock section, detaches tiles, fallback tiles and queued
    /// work outside the overscan range and records the visible tile range.
    /// Detached leases and cancellation sources are added to the caller's
    /// lists, which it retires outside the lock even if this throws. Returns
    /// false when the plan is no longer current.
    /// </summary>
    internal bool TryObserveVisibleTiles(
        SvgTileRenderPlan plan,
        Rect destination,
        Rect visible,
        int firstTileX,
        int firstTileY,
        int lastTileX,
        int lastTileY,
        int firstVisibleTileX,
        int firstVisibleTileY,
        int lastVisibleTileX,
        int lastVisibleTileY,
        ref List<SharedCanvasBitmapCache.Lease>? retired,
        ref List<CancellationTokenSource>? staleWork,
        out int remainingQueueCapacity)
    {
        remainingQueueCapacity = 0;
        lock (_svgTileLock)
        {
            if (!IsCurrentSvgTilePlan(plan))
                return false;

            // Dictionary.Remove does not invalidate its enumerator on the
            // supported .NET runtimes. No additions occur in either loop and
            // the lock excludes concurrent writers. Warm paints need no arrays.
            foreach (KeyValuePair<SvgTileKey, SharedCanvasBitmapCache.Lease> tile in _svgTiles)
            {
                SvgTileKey key = tile.Key;
                int tileX = key.X / SvgTileSizePixels;
                int tileY = key.Y / SvgTileSizePixels;
                if (tileX < firstTileX || tileX > lastTileX ||
                    tileY < firstTileY || tileY > lastTileY)
                {
                    // Reserve all retirement slots before removing a lease:
                    // an allocation failure must leave ownership recoverable.
                    retired ??= new List<SharedCanvasBitmapCache.Lease>(checked(
                        _svgTiles.Count + (_svgFallbackTiles?.Count ?? 0)));
                    if (_svgTiles.Remove(key))
                        retired.Add(tile.Value);
                }
            }

            if (_svgFallbackTiles is not null)
            {
                foreach (KeyValuePair<SvgTileKey, SharedCanvasBitmapCache.Lease> tile in _svgFallbackTiles)
                {
                    if (SvgFallbackTileRetention.ShouldRetain(
                        tile.Key,
                        _svgFallbackWidthPixels,
                        _svgFallbackHeightPixels,
                        plan.FullWidthPixels,
                        plan.FullHeightPixels,
                        destination,
                        visible,
                        _svgTiles,
                        _svgFallbackPhysicalViewport,
                        plan.PhysicalViewport))
                    {
                        continue;
                    }

                    retired ??= new List<SharedCanvasBitmapCache.Lease>(checked(
                        _svgTiles.Count + _svgFallbackTiles.Count));
                    if (_svgFallbackTiles.Remove(tile.Key))
                        retired.Add(tile.Value);
                }
            }

            _work.DetachOutside(firstTileX, firstTileY, lastTileX, lastTileY, ref staleWork);

            _viewport.ObserveVisibleRange(firstVisibleTileX, firstVisibleTileY, lastVisibleTileX, lastVisibleTileY);

            remainingQueueCapacity = Math.Max(0, SvgTileWorkSet.MaximumInFlight - _work.Count);
        }
        return true;
    }

    /// <summary>
    /// Whether a render of <paramref name="key"/> may be queued for the current
    /// plan; <paramref name="existingVisibleWork"/> is set when the plan's
    /// queued render should be promoted to visible priority instead.
    /// </summary>
    internal bool TryProbeTileWork(
        SvgTileRenderPlan plan,
        SvgTileKey key,
        MarkdownSvgRenderPriority priority,
        out SvgTileWorkOwner? existingVisibleWork)
    {
        existingVisibleWork = null;
        lock (_svgTileLock)
        {
            if (!IsCurrentSvgTilePlan(plan) || _svgTiles.ContainsKey(key))
                return false;
            return _work.CanQueue(plan, key, priority, out existingVisibleWork);
        }
    }

    /// <summary>Admits a render of <paramref name="key"/> if it is still absent and capacity remains.</summary>
    internal bool TryAdmitTileWork(
        SvgTileRenderPlan plan,
        SvgTileKey key,
        SvgTileWorkOwner workOwner)
    {
        lock (_svgTileLock)
        {
            return IsCurrentSvgTilePlan(plan) &&
                !_svgTiles.ContainsKey(key) &&
                _work.TryAdd(key, workOwner);
        }
    }

    internal bool TryClaimFailedSvgTile(
        SvgTileRenderPlan plan,
        SvgTileKey key,
        SvgTileWorkOwner workOwner)
    {
        lock (_svgTileLock)
        {
            return ReferenceEquals(workOwner.Plan, plan) &&
                IsCurrentSvgTilePlan(plan) &&
                IsCurrentSvgTileWorkOwnerNoLock(plan, key, workOwner.Cancellation) &&
                Interlocked.CompareExchange(ref workOwner.State, 1, 0) == 0;
        }
    }

    // Caller holds _svgTileLock. Both success and failure are fenced by the
    // exact request owner: eviction/reassignment can supersede work without
    // changing the accepted document or plan identity.
    private bool IsCurrentSvgTileWorkOwnerNoLock(
        SvgTileRenderPlan plan,
        SvgTileKey key,
        CancellationTokenSource workCancellation)
    {
        return IsCurrentSvgTilePlan(plan) &&
            _work.IsCurrentOwner(plan, key, workCancellation);
    }

    internal void CompleteSvgTileWork(
        SvgTileKey key,
        CancellationTokenSource workCancellation)
    {
        void Complete()
        {
            lock (_svgTileLock)
                _work.Complete(key, workCancellation);
        }

        _host.PublishOnUiThread(
            Complete,
            Complete);
    }

    internal SvgTileCacheTransition? PrepareSvgTileCacheTransition(SvgTileRenderPlan? plan)
    {
        object token = new();
        lock (_svgTileLock)
        {
            if (_svgTileCacheTransitionToken is not null)
                return null;

            _svgTileCacheTransitionToken = token;
            try
            {
                return SvgTileCacheTransition.ForPublication(
                    token,
                    _host.AcceptedSvgTilePlan,
                    plan,
                    _svgTiles,
                    _svgFallbackTiles,
                    _svgTileCacheIdentityPrefix,
                    _svgTileCacheWidthPixels,
                    _svgTileCacheHeightPixels,
                    _svgTileCachePhysicalViewport,
                    _work.Cancellations);
            }
            catch
            {
                if (ReferenceEquals(_svgTileCacheTransitionToken, token))
                    _svgTileCacheTransitionToken = null;
                throw;
            }
        }
    }

    internal SvgTileCacheTransition? PrepareSvgTileFailureTransition(SvgTileRenderPlan plan)
    {
        object token = new();
        lock (_svgTileLock)
        {
            if (_svgTileCacheTransitionToken is not null ||
                !ReferenceEquals(_host.AcceptedSvgTilePlan, plan))
            {
                return null;
            }

            _svgTileCacheTransitionToken = token;
            try
            {
                return SvgTileCacheTransition.ForFailure(
                    token,
                    plan,
                    _svgTiles,
                    _svgFallbackTiles,
                    _svgTileCacheIdentityPrefix,
                    _svgTileCacheWidthPixels,
                    _svgTileCacheHeightPixels,
                    _svgTileCachePhysicalViewport,
                    _svgFallbackIdentityPrefix,
                    _svgFallbackWidthPixels,
                    _svgFallbackHeightPixels,
                    _svgFallbackPhysicalViewport,
                    _work.Cancellations);
            }
            catch
            {
                if (ReferenceEquals(_svgTileCacheTransitionToken, token))
                    _svgTileCacheTransitionToken = null;
                throw;
            }
        }
    }

    internal bool ApplySvgTileCacheTransition(SvgTileCacheTransition transition)
    {
        lock (_svgTileLock)
        {
            if (!ReferenceEquals(_svgTileCacheTransitionToken, transition.Token) ||
                !ReferenceEquals(_host.AcceptedSvgTilePlan, transition.NextPlan) ||
                !ReferenceEquals(_svgTiles, transition.ExpectedTiles) ||
                !ReferenceEquals(_svgFallbackTiles, transition.ExpectedFallbackTiles))
            {
                return false;
            }

            SvgTileRenderPlan? plan = transition.NextPlan;
            if (plan is null)
            {
                if (transition.ReplacementTiles is null)
                    return false;

                _svgTiles = transition.ReplacementTiles;
                _svgFallbackTiles = null;
                _svgFallbackIdentityPrefix = null;
                _svgFallbackWidthPixels = 0;
                _svgFallbackHeightPixels = 0;
                _svgFallbackPhysicalViewport = null;
                if (transition.RestoreFallback)
                {
                    _svgTileCacheIdentityPrefix = transition.RestoredFallbackIdentityPrefix;
                    _svgTileCacheWidthPixels = transition.RestoredFallbackWidthPixels;
                    _svgTileCacheHeightPixels = transition.RestoredFallbackHeightPixels;
                    _svgTileCachePhysicalViewport = transition.RestoredFallbackPhysicalViewport;
                    _svgHasRenderedTile = _svgTiles.Count > 0;
                    _viewport.SetVisibility(-1);
                }
                else
                {
                    _svgTileCacheIdentityPrefix = null;
                    _svgTileCacheWidthPixels = 0;
                    _svgTileCacheHeightPixels = 0;
                    _svgTileCachePhysicalViewport = null;
                    _svgHasRenderedTile = false;
                    _viewport.SetVisibility(0);
                }
            }
            else
            {
                if (!transition.SameIdentity)
                {
                    if (_svgTiles.Count > 0)
                    {
                        if (transition.ReplacementTiles is null)
                            return false;
                        _svgFallbackTiles = _svgTiles;
                        _svgFallbackIdentityPrefix = transition.PreviousIdentityPrefix;
                        _svgFallbackWidthPixels = transition.PreviousWidthPixels;
                        _svgFallbackHeightPixels = transition.PreviousHeightPixels;
                        _svgFallbackPhysicalViewport = transition.PreviousPhysicalViewport;
                        _svgTiles = transition.ReplacementTiles;
                    }
                    else if (_svgFallbackTiles is null)
                    {
                        if (transition.ReplacementTiles is null)
                            return false;
                        _svgTiles = transition.ReplacementTiles;
                    }
                }

                _svgTileCacheIdentityPrefix = plan.IdentityPrefix;
                _svgTileCacheWidthPixels = plan.FullWidthPixels;
                _svgTileCacheHeightPixels = plan.FullHeightPixels;
                _svgTileCachePhysicalViewport = plan.PhysicalViewport;
                _svgHasRenderedTile = _svgTiles.Count > 0;
                _viewport.SetVisibility(-1);
            }

            _work.Clear();
            _viewport.ForgetRangeAndSignature();
            _svgTileCacheTransitionToken = null;
            return true;
        }
    }

    internal void AbortSvgTileCacheTransition(SvgTileCacheTransition transition)
    {
        lock (_svgTileLock)
        {
            if (ReferenceEquals(_svgTileCacheTransitionToken, transition.Token))
                _svgTileCacheTransitionToken = null;
        }
    }

    internal void RetireSvgTileCacheTransition(SvgTileCacheTransition transition)
    {
        if (transition.RetiredTiles is { } tiles)
        {
            foreach (SharedCanvasBitmapCache.Lease tile in tiles)
                _host.TryImageCleanup(tile.Dispose);
        }
        if (transition.RetiredFallbackTiles is { } fallbackTiles)
        {
            foreach (SharedCanvasBitmapCache.Lease tile in fallbackTiles)
                _host.TryImageCleanup(tile.Dispose);
        }
        foreach (CancellationTokenSource cancellation in transition.Work)
        {
            try { _ = CancellationTokenSourceRetirement.RequestCancellation(cancellation); }
            catch (Exception exception) { _host.RecordImageRetirementFailure(exception); }
        }
    }

    /// <summary>Whether every tile in the range is resident for a grid of the given size.</summary>
    internal bool HasAllTiles(
        int firstTileX,
        int firstTileY,
        int lastTileX,
        int lastTileY,
        int widthPixels,
        int heightPixels)
    {
        if (firstTileX > lastTileX || firstTileY > lastTileY)
            return false;

        // The host computes a finite, viewport-clamped tile range before this
        // atomic check; keep the resource dictionary observation under one lock.
        lock (_svgTileLock)
        {
            for (int tileY = firstTileY; tileY <= lastTileY; tileY++)
            {
                for (int tileX = firstTileX; tileX <= lastTileX; tileX++)
                {
                    int x = tileX * SvgTileSizePixels;
                    int y = tileY * SvgTileSizePixels;
                    SvgTileKey key = (
                        x,
                        y,
                        Math.Min(SvgTileSizePixels, widthPixels - x),
                        Math.Min(SvgTileSizePixels, heightPixels - y));
                    if (!_svgTiles.ContainsKey(key))
                        return false;
                }
            }
        }

        return true;
    }

    /// <summary>Records that a pressure-parked image's destination is outside the viewport.</summary>
    internal void ObserveParkedOffscreen()
    {
        lock (_svgTileLock)
            _viewport.MarkParkedOffscreen();
    }

    /// <summary>
    /// Records a pressure-parked image's visible tile range for the raster
    /// it would request, and returns true when part of that range has no
    /// resident tile: the demand that should reopen its document. A changed
    /// demand signature advances the host's visual-demand epoch.
    /// </summary>
    internal bool TryObserveParkedVisibleRange(
        int firstX,
        int firstY,
        int lastX,
        int lastY,
        SvgRasterPlan desired,
        out long visualDemandEpoch)
    {
        visualDemandEpoch = 0;
        int width = desired.Width;
        int height = desired.Height;
        lock (_svgTileLock)
        {
            if (!_host.IsSvgPressureParked)
                return false;

            _viewport.MarkParkedVisibleRange(firstX, firstY, lastX, lastY);

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

            if (_viewport.TryRecordPressureSignature(
                    firstX, firstY, lastX, lastY, width, height, desired.PhysicalViewport))
            {
                visualDemandEpoch = _host.AdvanceSvgHostVisualDemandEpoch();
            }
            else
            {
                visualDemandEpoch = _host.SvgHostVisualDemandEpoch;
            }
            return true;
        }
    }

    internal void ClearPressureParkedViewportVisibility()
    {
        lock (_svgTileLock)
        {
            if (!_host.IsSvgPressureParked)
                return;
            _viewport.MarkParkedOffscreen();
        }
    }

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
                if (!_host.IsAcceptedSvgTilePlan(expectedPlan) ||
                    !ReferenceEquals(_host.AcceptedSvgTilePlan, expectedPlan) ||
                    Volatile.Read(ref _svgTileCacheTransitionToken) is not null)
                {
                    return false;
                }

                _viewport.ObserveVisibility(visible);
                return true;
            }
        }
        finally
        {
            expectedPlan.EndViewportMutation();
        }
    }

    private bool IsSvgPressureParkEligibleNoLock(
        SvgTileRenderPlan plan,
        long demandVersion,
        int maximumTier)
    {
        if (plan.Demand.Version != demandVersion ||
            !_host.IsAcceptedSvgTilePlan(plan) ||
            Volatile.Read(ref _svgTileCacheTransitionToken) is not null ||
            !ReferenceEquals(_host.SvgParkingPlan, plan))
            return false;

        int currentViewportVisible = _viewport.Visibility;
        if (currentViewportVisible < 0)
        {
            return demandVersion == 0 &&
                plan.Demand.Priority == MarkdownSvgRenderPriority.Overscan;
        }

        if (currentViewportVisible == 0)
            return true;
        if (currentViewportVisible != 1 || maximumTier < 1 || !_viewport.RangeKnown)
            return false;

        return IsVisibleRangeSettledNoLock(plan);
    }

    // Caller holds _svgTileLock. The plan's whole visible range is resident,
    // and none of the plan's work is in flight inside it.
    private bool IsVisibleRangeSettledNoLock(SvgTileRenderPlan plan)
    {
        for (int y = _viewport.FirstTileY; y <= _viewport.LastTileY; y++)
        {
            for (int x = _viewport.FirstTileX; x <= _viewport.LastTileX; x++)
            {
                int pixelX = x * SvgTileSizePixels;
                int pixelY = y * SvgTileSizePixels;
                var key = (
                    pixelX,
                    pixelY,
                    Math.Min(SvgTileSizePixels, plan.FullWidthPixels - pixelX),
                    Math.Min(SvgTileSizePixels, plan.FullHeightPixels - pixelY));
                if (!_svgTiles.ContainsKey(key) || _work.Contains(key))
                    return false;
            }
        }

        if (_work.HasPlanWorkWithin(
                plan, _viewport.FirstTileX, _viewport.FirstTileY, _viewport.LastTileX, _viewport.LastTileY))
        {
            return false;
        }

        return true;
    }
}
