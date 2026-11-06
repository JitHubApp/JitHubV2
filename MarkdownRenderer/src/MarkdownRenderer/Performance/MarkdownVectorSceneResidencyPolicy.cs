using System;
using System.Collections.Generic;
using System.Threading;
using Windows.Foundation;
using MarkdownRenderer.Extensions;
using MarkdownRenderer.Layout;
using MarkdownRenderer.Layout.Boxes;

namespace MarkdownRenderer.Performance;

/// <summary>
/// Owns residency pins for native vector resources in the realized layout band.
/// Eligible completed block graphs can be detached and refilled through their
/// plan. Participating drawings share weighted native admission; denied scenes
/// keep readable fallback while awaiting a capacity-generation wake. Neither
/// the graph estimate nor that complexity weight is an RSS guarantee.
/// </summary>
/// <remarks>
/// This file is longer than 500 lines because band replacement (collect,
/// weigh, prepare and commit), graph rebind adoption, unpinned release and
/// disposal each move pins between the committed active scenes, the pending
/// transient scenes and the pending rebinds. Those sets stay in one owner so
/// that superseding a pending band retires its transient pins without
/// touching the committed band. Band walking lives in
/// <see cref="VectorSceneBandCollector"/> and size estimates in
/// <see cref="MarkdownVectorSceneEstimateCache"/>.
/// </remarks>
internal sealed class MarkdownVectorSceneResidencyPolicy : IMarkdownVectorSceneResidencyPolicy
{
    private readonly VectorSceneBandCollector _bandCollector;
    private MarkdownProgressiveScenePlan? _graphPlan;
    private readonly List<VectorSceneBox> _pendingGraphRebinds = [];
    private readonly List<VectorSceneBox> _graphStatusChanges = [];
    private readonly VectorNativeResourceAdmission? _nativeAdmission;
    private readonly MarkdownVectorSceneEstimateCache _estimates;
    private LazyLayoutBand? _pendingGraphBand;
    private Dictionary<object, VectorSceneResidencyEntry> _activeScenes =
        new(ReferenceEqualityComparer.Instance);
    // Pending fallback/rebound native resources are owned separately from the
    // last committed band. Superseding a pending band retires these pins without
    // touching the still-good committed band.
    private readonly Dictionary<object, VectorSceneResidencyEntry> _transientScenes =
        new(ReferenceEqualityComparer.Instance);
    // LayoutSnapshot serializes policy calls with its paint/read lock. Reuse
    // this buffer for readiness probes so they do not retain a second document
    // graph or allocate a fresh per-scene array on every viewport check.
    private readonly List<VectorSceneResidencyEntry> _readinessScratch = [];
    private readonly HashSet<object> _readinessSeen = new(ReferenceEqualityComparer.Instance);
    private readonly List<string> _visibleDeferredSceneMarkerScratch = [];
    private LazyLayoutBand? _activeBand;
    private long _activeEstimatedBytes;
    private int _commandGraphEstimateCount;
    private bool _disposed;
    private bool _hasCapacityDeferredScenes;
    private long _activeCapacityGeneration;

    internal MarkdownVectorSceneResidencyPolicy(
        IReadOnlyList<BlockBox> blocks,
        MarkdownProgressiveScenePlan? graphPlan = null,
        VectorNativeResourceAdmission? nativeAdmission = null,
        MarkdownVectorSceneEstimateCache? estimates = null)
    {
        _bandCollector = new VectorSceneBandCollector(blocks ?? throw new ArgumentNullException(nameof(blocks)));
        _graphPlan = graphPlan;
        _nativeAdmission = nativeAdmission;
        // A policy without a scope memoizes only its own walks.
        _estimates = estimates ?? new MarkdownVectorSceneEstimateCache();
    }

    public bool HasPendingGraphRebinds => _pendingGraphRebinds.Count != 0;
    public bool HasCapacityDeferredScenes => Volatile.Read(ref _hasCapacityDeferredScenes);
    public long ActiveCapacityGeneration => Volatile.Read(ref _activeCapacityGeneration);
    public IReadOnlyList<VectorSceneBox> GraphStatusChanges => _graphStatusChanges;
    public void ClearGraphStatusChanges() => _graphStatusChanges.Clear();
    internal void RecordGraphStatusChange(VectorSceneBox box)
    {
        if (!_graphStatusChanges.Contains(box))
            _graphStatusChanges.Add(box);
    }

    internal IReadOnlyList<VectorSceneBox> PendingGraphRebinds => _pendingGraphRebinds;

    internal bool CanCommitGraphRebind(VectorSceneBox box, LazyLayoutBand band) =>
        !_disposed && _pendingGraphBand is { } pending &&
        pending.Top == band.Top && pending.Bottom == band.Bottom && _pendingGraphRebinds.Contains(box);

    internal bool AdoptGraphRebind(
        VectorSceneBox box, LazyLayoutBand band, VectorSceneDrawing replacement, long estimate)
    {
        if (!CanCommitGraphRebind(box, band) || !box.IsGraphDetached ||
            (_nativeAdmission is null && _activeScenes.ContainsKey(box)))
            return false;
        // Reserve the pin before adoption: even an allocation failure cannot
        // leave a prepared replacement outside policy retirement ownership.
        bool wasTracked = _transientScenes.TryGetValue(box, out VectorSceneResidencyEntry previous);
        _transientScenes[box] = new VectorSceneResidencyEntry(box, estimate);
        try
        {
            box.AdoptPreparedReplacement(replacement);
        }
        catch
        {
            if (wasTracked)
                _transientScenes[box] = previous;
            else
                _transientScenes.Remove(box);
            throw;
        }
        if (_activeScenes.TryGetValue(box, out VectorSceneResidencyEntry priorActive))
        {
            _activeScenes[box] = new VectorSceneResidencyEntry(box, estimate);
            _transientScenes.Remove(box);
            _activeEstimatedBytes = SaturatingAdd(
                Math.Max(0, _activeEstimatedBytes - priorActive.EstimatedBytes), estimate);
        }
        if (box.NativePreparationStatus == VectorNativePreparationStatus.DeferredCapacity)
        {
            Volatile.Write(ref _hasCapacityDeferredScenes, true);
            // The failed attempt's generation must survive later refunds. The
            // control registers then rechecks, closing a stationary missed wake.
            Volatile.Write(ref _activeCapacityGeneration,
                Math.Min(ActiveCapacityGeneration, replacement.PreparationOutcome.CapacityGeneration));
        }
        return true;
    }

    public int ActiveSceneCount => _activeScenes.Count + _transientScenes.Count;

    /// <summary>Weighted diagnostic estimate, not an allocation-size guarantee.</summary>
    public long ActiveEstimatedBytes
    {
        get
        {
            long estimate = _activeEstimatedBytes;
            foreach (VectorSceneResidencyEntry entry in _transientScenes.Values)
                estimate = SaturatingAdd(estimate, entry.EstimatedBytes);
            return estimate;
        }
    }

    public int InlineGeometryIndexBuildCount => _bandCollector.InlineGeometryIndexBuildCount;

    public int CommandGraphEstimateCount => Volatile.Read(ref _commandGraphEstimateCount);

    public int ActiveInlineSceneGeometryIndexCount => _bandCollector.ActiveInlineIndexCount;

    /// <summary>
    /// Proves that a small, known subset of a newly built tree cannot contain
    /// deferred vector-scene residency state. This runs before a snapshot or
    /// its layout gate exists, so it reads only immutable construction shape.
    /// Unknown or over-bound trees retain the normal policy.
    /// </summary>
    internal static bool CanOmitForPrivatePlacement(IReadOnlyList<BlockBox> blocks) =>
        PrivatePlacementResidencyProof.CanOmitResidencyPolicy(blocks);

    public List<string> VisibleDeferredSceneMarkerScratch => _visibleDeferredSceneMarkerScratch;

    public bool AreNativeResourcesPrepared(
        IReadOnlyList<int> topLevelOrdinals,
        LazyLayoutBand band,
        LayoutPlacementGeneration placements,
        IReadOnlyDictionary<int, int> topLevelOrdinalByBlockIndex)
        => IsBandPrepared(
            topLevelOrdinals,
            band,
            placements,
            topLevelOrdinalByBlockIndex,
            acceptTerminalFailure: false);

    public bool IsBandWorkSettled(
        IReadOnlyList<int> topLevelOrdinals,
        LazyLayoutBand band,
        LayoutPlacementGeneration placements,
        IReadOnlyDictionary<int, int> topLevelOrdinalByBlockIndex)
        => IsBandPrepared(
            topLevelOrdinals,
            band,
            placements,
            topLevelOrdinalByBlockIndex,
            acceptTerminalFailure: true);

    public bool HasFailedScenes(
        LazyLayoutBand band,
        LayoutPlacementGeneration placements,
        IReadOnlyDictionary<int, int> topLevelOrdinalByBlockIndex)
    {
        if (_disposed || placements is null || topLevelOrdinalByBlockIndex is null ||
            placements.Count != _bandCollector.BlockCount || !VectorSceneBandGeometry.IsValidBand(band))
            return false;

        foreach (object owner in _activeScenes.Keys)
        {
            if (owner is VectorSceneBox { GraphRebindFailed: true } box &&
                _bandCollector.IsFailedSceneVisible(box, band, placements, topLevelOrdinalByBlockIndex))
                return true;
        }
        foreach (object owner in _transientScenes.Keys)
        {
            if (owner is VectorSceneBox { GraphRebindFailed: true } box &&
                _bandCollector.IsFailedSceneVisible(box, band, placements, topLevelOrdinalByBlockIndex))
                return true;
        }
        return false;
    }

    private bool IsBandPrepared(
        IReadOnlyList<int> topLevelOrdinals,
        LazyLayoutBand band,
        LayoutPlacementGeneration placements,
        IReadOnlyDictionary<int, int> topLevelOrdinalByBlockIndex,
        bool acceptTerminalFailure)
    {
        if (_disposed || topLevelOrdinals is null || placements is null ||
            topLevelOrdinalByBlockIndex is null || placements.Count != _bandCollector.BlockCount || !VectorSceneBandGeometry.IsValidBand(band))
            return false;

        _readinessScratch.Clear();
        _readinessSeen.Clear();
        _visibleDeferredSceneMarkerScratch.Clear();
        try
        {
            foreach (int ordinal in topLevelOrdinals)
            {
                if (!_bandCollector.TryGetRootLocalBand(
                        ordinal,
                        band,
                        placements,
                        topLevelOrdinalByBlockIndex,
                        out LazyLayoutBand rootLocalBand))
                    return false;

                // The prepared inline index is pixel-snapped in its root's
                // published placement. Query with that same placement, or a
                // fractional translation shifts the local band outside the
                // index even when all of its scenes are already prepared.
                LayoutRootPlacement placement = placements.GetPlacement(ordinal);
                using LayoutPlacementTransformContext.Scope placementScope =
                    LayoutPlacementTransformContext.Enter(placement.TranslationX, placement.TranslationY);
                if (!_bandCollector.TryCollectVisibleBlockScenes(
                        _bandCollector.GetRoot(ordinal),
                        rootLocalBand,
                        _readinessScratch,
                        _readinessSeen,
                        _bandCollector.ActiveInlineIndexes,
                        allowIndexBuild: false,
                        CancellationToken.None))
                {
                    return false;
                }
            }

            // The realized pin band may intentionally include overscan. A
            // viewport sub-band is ready when covered by that lease, while a
            // scroll beyond its edge (including a text-only band) must request
            // a new exchange so old scenes are retired.
            if (_activeBand is not { } activeBand ||
                band.Top < activeBand.Top || band.Bottom > activeBand.Bottom)
            {
                return false;
            }

            if (_readinessScratch.Count > _activeScenes.Count)
                return false;

            foreach (VectorSceneResidencyEntry entry in _readinessScratch)
            {
                if (!_activeScenes.ContainsKey(entry.Owner) ||
                    (!entry.IsPrepared && !(acceptTerminalFailure &&
                        (entry.IsCapacityFallback ||
                         entry.Owner is VectorSceneBox { IsGraphDetached: true, GraphRebindFailed: true }))))
                    return false;
            }

            return true;
        }
        finally
        {
            _readinessScratch.Clear();
            _readinessSeen.Clear();
        }
    }

    public bool ReplaceActiveBand(
        IReadOnlyList<int> topLevelOrdinals,
        LazyLayoutBand band,
        LayoutPlacementGeneration placements,
        IReadOnlyDictionary<int, int> topLevelOrdinalByBlockIndex,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(topLevelOrdinals);
        ArgumentNullException.ThrowIfNull(placements);
        ArgumentNullException.ThrowIfNull(topLevelOrdinalByBlockIndex);
        if (placements.Count != _bandCollector.BlockCount || !VectorSceneBandGeometry.IsValidBand(band))
            throw new ArgumentOutOfRangeException(nameof(band));

        _pendingGraphRebinds.Clear();
        _pendingGraphBand = null;

        List<VectorSceneResidencyEntry> requested = [];
        var requestedByOwner = new Dictionary<object, VectorSceneResidencyEntry>(ReferenceEqualityComparer.Instance);
        var requestedInlineIndexes = new Dictionary<InlineContainerBox, InlineSceneGeometryIndex>(
            ReferenceEqualityComparer.Instance);
        var seen = new HashSet<object>(ReferenceEqualityComparer.Instance);
        CollectRequestedScenes(
            topLevelOrdinals,
            band,
            placements,
            topLevelOrdinalByBlockIndex,
            requested,
            seen,
            requestedInlineIndexes,
            cancellationToken);
        RetireObsoleteTransientScenes(seen);

        bool fallbackChanged = PrepareDetachedFallbacks(requested, cancellationToken);
        if (_pendingGraphRebinds.Count != 0)
        {
            _pendingGraphBand = band;
            // Legacy policies keep their old pin transaction. Participating
            // policies can publish the complete readable fallback set first,
            // freeing outgoing native credits before asynchronous graph refill.
            if (_nativeAdmission is null)
                return fallbackChanged;
        }

        WeighRequestedScenes(requested, requestedByOwner, cancellationToken);

        long attemptedCapacityGeneration = _nativeAdmission?.CapacityGeneration ?? 0;
        bool resourcesChanged = PrepareIncomingScenes(requested, cancellationToken) | fallbackChanged;
        return CommitActiveBand(
            band,
            requested,
            requestedByOwner,
            requestedInlineIndexes,
            resourcesChanged,
            attemptedCapacityGeneration);
    }

    private void CollectRequestedScenes(
        IReadOnlyList<int> topLevelOrdinals,
        LazyLayoutBand band,
        LayoutPlacementGeneration placements,
        IReadOnlyDictionary<int, int> topLevelOrdinalByBlockIndex,
        List<VectorSceneResidencyEntry> requested,
        HashSet<object> seen,
        Dictionary<InlineContainerBox, InlineSceneGeometryIndex> requestedInlineIndexes,
        CancellationToken cancellationToken)
    {
        foreach (int ordinal in topLevelOrdinals)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LayoutRootPlacement placement = placements.GetPlacement(ordinal);
            using LayoutPlacementTransformContext.Scope placementScope =
                LayoutPlacementTransformContext.Enter(placement.TranslationX, placement.TranslationY);
            if (!_bandCollector.TryGetRootLocalBand(
                    ordinal,
                    band,
                    placements,
                    topLevelOrdinalByBlockIndex,
                    out LazyLayoutBand rootLocalBand))
                throw new InvalidOperationException("The vector-scene residency request contains an invalid top-level ordinal.");

            if (!_bandCollector.TryCollectVisibleBlockScenes(
                    _bandCollector.GetRoot(ordinal),
                    rootLocalBand,
                    requested,
                    seen,
                    requestedInlineIndexes,
                    allowIndexBuild: true,
                    cancellationToken))
                throw new InvalidOperationException(
                    "Visible vector-scene layout geometry is unavailable; native resources were not published.");
        }
    }

    private void RetireObsoleteTransientScenes(HashSet<object> seen)
    {
        List<VectorSceneResidencyEntry>? obsoleteTransient = null;
        foreach ((object owner, VectorSceneResidencyEntry entry) in _transientScenes)
        {
            if (!seen.Contains(owner))
                (obsoleteTransient ??= []).Add(entry);
        }
        if (obsoleteTransient is not null)
        {
            foreach (VectorSceneResidencyEntry entry in obsoleteTransient)
                _transientScenes.Remove(entry.Owner);
            ReleaseEntriesBestEffort(obsoleteTransient, _activeScenes, detachGraphs: true);
        }
    }

    private bool PrepareDetachedFallbacks(
        List<VectorSceneResidencyEntry> requested,
        CancellationToken cancellationToken)
    {
        // A graph miss is not a layout/provider exception. Leave the prior pin
        // band intact, prepare readable bounded fallback off the UI thread,
        // then let the active lazy request await refill outside this lock/queue
        // slot. No detached owner may be dereferenced through Scene below.
        bool fallbackChanged = false;
        foreach (VectorSceneResidencyEntry entry in requested)
        {
            if (entry.Owner is VectorSceneBox { IsGraphDetached: true } box)
            {
                if (!_activeScenes.ContainsKey(box))
                    _transientScenes.TryAdd(box, new VectorSceneResidencyEntry(box, 8_192));
                fallbackChanged |= box.PrepareDetachedFallback(cancellationToken, MarkdownDetachedSceneFallback.Create);
                if (!box.GraphRebindFailed)
                    _pendingGraphRebinds.Add(box);
            }
        }
        return fallbackChanged;
    }

    private void WeighRequestedScenes(
        List<VectorSceneResidencyEntry> requested,
        Dictionary<object, VectorSceneResidencyEntry> requestedByOwner,
        CancellationToken cancellationToken)
    {
        // Readiness collection records owners only. Walk immutable commands for
        // diagnostic weights here, on the background band preparation path,
        // never in a warm UI probe.
        for (int index = 0; index < requested.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            object owner = requested[index].Owner;
            if (_activeScenes.TryGetValue(owner, out VectorSceneResidencyEntry retained) ||
                _transientScenes.TryGetValue(owner, out retained))
            {
                // The command graph is immutable. Moving the band boundary
                // does not change the weight of a scene already pinned here;
                // do not walk a large diagram again just to move its viewport.
                requested[index] = retained;
                requestedByOwner.Add(owner, retained);
                continue;
            }
            if (owner is VectorSceneBox { IsGraphDetached: true })
            {
                // A typed failed-refill source fallback remains unready, but
                // must still join the pin band so leaving it retires its small
                // native text layout. This is a diagnostic weight, not bytes.
                var fallbackEntry = new VectorSceneResidencyEntry(owner, 8_192);
                requested[index] = fallbackEntry;
                requestedByOwner.Add(owner, fallbackEntry);
                continue;
            }
            MarkdownVectorScene scene = owner switch
            {
                VectorSceneBox box => box.Scene,
                InlineVectorSceneRun run => run.Scene,
                _ => throw new InvalidOperationException("The residency band contains an unsupported scene owner."),
            };
            var entry = new VectorSceneResidencyEntry(owner, GetPreparedResourceEstimateForRebind(scene, cancellationToken));
            requested[index] = entry;
            requestedByOwner.Add(owner, entry);
        }
    }

    /// <summary>
    /// Prepares the incoming set while the old band stays pinned. Returns
    /// whether any incoming scene needed native preparation; on failure the
    /// incoming, not-yet-active scenes are released and the error propagates.
    /// </summary>
    private bool PrepareIncomingScenes(
        List<VectorSceneResidencyEntry> requested,
        CancellationToken cancellationToken)
    {
        bool resourcesChanged = false;
        try
        {
            // Prepare the incoming set first. The old band remains pinned until
            // every visible item in the new band is ready, preventing blank
            // frames at a viewport transition.
            foreach (VectorSceneResidencyEntry entry in requested)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (entry.Owner is VectorSceneBox { IsGraphDetached: true })
                    continue;
                if (!entry.IsPrepared)
                    resourcesChanged = true;
                entry.Prepare(cancellationToken);
            }
            cancellationToken.ThrowIfCancellationRequested();
        }
        catch
        {
            ReleaseEntriesBestEffort(requested, _activeScenes);
            throw;
        }
        return resourcesChanged;
    }

    private bool CommitActiveBand(
        LazyLayoutBand band,
        List<VectorSceneResidencyEntry> requested,
        Dictionary<object, VectorSceneResidencyEntry> requestedByOwner,
        Dictionary<InlineContainerBox, InlineSceneGeometryIndex> requestedInlineIndexes,
        bool resourcesChanged,
        long attemptedCapacityGeneration)
    {
        bool changed = resourcesChanged || requestedByOwner.Count != _activeScenes.Count;
        List<VectorSceneResidencyEntry>? retired = null;
        foreach ((object owner, VectorSceneResidencyEntry active) in _activeScenes)
        {
            if (!requestedByOwner.ContainsKey(owner))
            {
                changed = true;
                (retired ??= []).Add(active);
            }
        }
        foreach ((object owner, VectorSceneResidencyEntry transient) in _transientScenes)
        {
            if (!requestedByOwner.ContainsKey(owner) && !_activeScenes.ContainsKey(owner))
                (retired ??= []).Add(transient);
        }

        foreach (object owner in requestedByOwner.Keys)
            changed |= !_activeScenes.ContainsKey(owner);

        // Publish the complete incoming pin set before releasing the outgoing
        // set. LayoutSnapshot holds the lease lock for this entire exchange.
        long estimatedBytes = 0;
        foreach (VectorSceneResidencyEntry entry in requested)
            estimatedBytes = SaturatingAdd(estimatedBytes, entry.EstimatedBytes);
        _activeScenes = requestedByOwner;
        _transientScenes.Clear();
        _bandCollector.PublishActiveInlineIndexes(requestedInlineIndexes);
        _activeEstimatedBytes = estimatedBytes;
        _activeBand = band;

        // Pending rectangles are a short-lived handoff from layout measurement
        // to the active band index. Do not retain a second copy on the boxes
        // after this transaction commits. On cancellation they remain available
        // for a retry, and the old active indexes and pins stay authoritative.
        foreach ((InlineContainerBox inline, InlineSceneGeometryIndex index) in requestedInlineIndexes)
            inline.ReleasePendingSceneGeometry(index.Generation);

        // Native disposal failures must not unwind a completed band exchange:
        // the newly visible set is already prepared and published. Dispose on
        // the next snapshot retirement remains the final cleanup opportunity.
        ReleaseEntriesBestEffort(retired, keep: null, detachGraphs: true);

        // The outgoing set may have owned the only freeable credits. Preserve
        // the attempt's generation so register-then-recheck schedules one fresh
        // stationary retry after this gate is released. Do not construct a
        // second native prefix or introduce a cancellation point after commit.
        // Failed disposal never makes those credits available.
        bool hasDeferred = false;
        foreach (VectorSceneResidencyEntry entry in requested)
            hasDeferred |= entry.IsCapacityDeferred;
        Volatile.Write(ref _activeCapacityGeneration, attemptedCapacityGeneration);
        Volatile.Write(ref _hasCapacityDeferredScenes, hasDeferred);
        return changed;
    }

    public void ReleaseUnpinnedResources(
        IReadOnlyList<int> topLevelOrdinals,
        LazyLayoutBand band,
        LayoutPlacementGeneration placements,
        IReadOnlyDictionary<int, int> topLevelOrdinalByBlockIndex)
    {
        ArgumentNullException.ThrowIfNull(topLevelOrdinals);
        ArgumentNullException.ThrowIfNull(placements);
        ArgumentNullException.ThrowIfNull(topLevelOrdinalByBlockIndex);
        if (placements.Count != _bandCollector.BlockCount || !VectorSceneBandGeometry.IsValidBand(band))
            return;
        List<VectorSceneResidencyEntry> candidates = [];
        var seen = new HashSet<object>(ReferenceEqualityComparer.Instance);
        foreach (int ordinal in topLevelOrdinals)
        {
            if (!_bandCollector.TryGetRootLocalBand(
                    ordinal,
                    band,
                    placements,
                    topLevelOrdinalByBlockIndex,
                    out LazyLayoutBand rootLocalBand))
                return;

            LayoutRootPlacement placement = placements.GetPlacement(ordinal);
            using LayoutPlacementTransformContext.Scope placementScope =
                LayoutPlacementTransformContext.Enter(placement.TranslationX, placement.TranslationY);
            if (!_bandCollector.TryCollectVisibleBlockScenes(
                    _bandCollector.GetRoot(ordinal),
                    rootLocalBand,
                    candidates,
                    seen,
                    _bandCollector.ActiveInlineIndexes,
                    allowIndexBuild: false,
                    CancellationToken.None))
            {
                // Incomplete/stale geometry cannot authorize disposal.
                return;
            }
        }

        // A same-band cancellation cleanup cannot steal a pending refill pin.
        // New-band pruning above owns retirement of superseded transient pins.
        candidates.RemoveAll(entry => _transientScenes.ContainsKey(entry.Owner));
        ReleaseEntriesBestEffort(candidates, _activeScenes);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        List<VectorSceneResidencyEntry> active = [.. _activeScenes.Values, .. _transientScenes.Values];
        _activeScenes.Clear();
        _transientScenes.Clear();
        _bandCollector.ClearActiveInlineIndexes();
        _activeEstimatedBytes = 0;
        _activeBand = null;
        Volatile.Write(ref _hasCapacityDeferredScenes, false);
        try
        {
            ReleaseScenes(active);
        }
        finally
        {
            // A retained policy must not keep its retired snapshot's boxes (and
            // their immutable command graphs) alive after the snapshot drains.
            _bandCollector.ReleaseBlocks();
            _graphPlan = null;
            _pendingGraphRebinds.Clear();
            _graphStatusChanges.Clear();
            _pendingGraphBand = null;
            _readinessScratch.Clear();
            _readinessSeen.Clear();
            _visibleDeferredSceneMarkerScratch.Clear();
        }
    }

    public bool TryAppendVisibleInlineSceneMarkers(
        InlineContainerBox inline,
        double viewportTop,
        double viewportBottom,
        Rect? viewport,
        List<string> markerKeys,
        ref bool visibleSceneResourcesReady)
    {
        ArgumentNullException.ThrowIfNull(inline);
        ArgumentNullException.ThrowIfNull(markerKeys);
        if (_disposed)
            return false;
        return _bandCollector.TryAppendVisibleInlineSceneMarkers(
            inline,
            viewportTop,
            viewportBottom,
            viewport,
            markerKeys,
            ref visibleSceneResourcesReady);
    }

    internal long GetPreparedResourceEstimate(MarkdownVectorScene scene) =>
        GetPreparedResourceEstimateForRebind(scene, CancellationToken.None);

    internal long GetPreparedResourceEstimateForRebind(MarkdownVectorScene scene, CancellationToken cancellationToken) =>
        _estimates.GetOrEstimate(scene, ref _commandGraphEstimateCount, cancellationToken);

    private void ReleaseEntriesBestEffort(
        IEnumerable<VectorSceneResidencyEntry>? candidates,
        IReadOnlyDictionary<object, VectorSceneResidencyEntry>? keep,
        bool detachGraphs = false)
    {
        if (candidates is null)
            return;
        foreach (VectorSceneResidencyEntry entry in candidates)
        {
            if (keep?.ContainsKey(entry.Owner) == true)
                continue;
            try
            {
                if (detachGraphs && entry.Owner is VectorSceneBox box &&
                    _graphPlan?.CanDetachGraph(box) == true)
                    box.ReleaseSceneGraph();
                else
                    entry.Release();
            }
            catch
            {
                // Preserve the already-published active band. Snapshot disposal
                // retries remaining active resources; an off-band disposal error
                // must not roll back visible geometry.
            }
        }
    }

    private static void ReleaseScenes(IEnumerable<VectorSceneResidencyEntry> scenes)
    {
        List<Exception>? failures = null;
        foreach (VectorSceneResidencyEntry entry in scenes)
        {
            try
            {
                entry.Release();
            }
            catch (Exception exception)
            {
                (failures ??= []).Add(exception);
            }
        }

        if (failures is { Count: 1 })
            throw failures[0];
        if (failures is { Count: > 1 })
            throw new AggregateException("One or more native scene resources failed to release.", failures);
    }

    private static long SaturatingAdd(long left, long right)
        => left > long.MaxValue - right ? long.MaxValue : left + right;

    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(MarkdownVectorSceneResidencyPolicy));
    }
}
