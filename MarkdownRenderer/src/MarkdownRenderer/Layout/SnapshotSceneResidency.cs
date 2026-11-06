using System;
using System.Collections.Generic;
using System.Threading;
using MarkdownRenderer.Performance;

namespace MarkdownRenderer.Layout;

/// <summary>
/// A snapshot's vector-scene residency against viewport bands: owns the
/// residency policy and its band scratch lists, replaces the active scene
/// band after measurement, answers scene readiness and failure probes, and
/// detaches the policy for disposal. Every member that reads mutable geometry
/// runs under the snapshot's layout gate, as before this owner was split out.
/// </summary>
internal sealed class SnapshotSceneResidency
{
    private readonly LayoutSnapshotGeometry _geometry;
    private readonly GeometryRecoveryLatch _recovery;
    private readonly List<int> _sceneBandOrdinalScratch = [];
    private readonly List<BlockBox> _sceneProbeBlockScratch = [];
    private IMarkdownVectorSceneResidencyPolicy? _vectorSceneResidencyPolicy;

    internal SnapshotSceneResidency(
        LayoutSnapshotGeometry geometry,
        GeometryRecoveryLatch recovery,
        IMarkdownVectorSceneResidencyPolicy? vectorSceneResidencyPolicy)
    {
        _geometry = geometry;
        _recovery = recovery;
        _vectorSceneResidencyPolicy = vectorSceneResidencyPolicy;
    }

    /// <summary>
    /// Approximate weight of the block-scene device resources pinned for the
    /// current lazy-layout band. This is telemetry and relative cost, not an
    /// exact native-memory measurement or a hard budget.
    /// </summary>
    internal long ActivePreparedVectorSceneEstimatedBytes
    {
        get { lock (_geometry.Gate) return _vectorSceneResidencyPolicy?.ActiveEstimatedBytes ?? 0; }
    }

    internal int ActivePreparedVectorSceneCount
    {
        get { lock (_geometry.Gate) return _vectorSceneResidencyPolicy?.ActiveSceneCount ?? 0; }
    }

    /// <summary>
    /// Scalar wake state only. These volatile policy properties are safe for
    /// UI/ledger coordination without inspecting mutable scene or box state.
    /// </summary>
    internal bool HasCapacityDeferredScenes =>
        !_recovery.IsRequired &&
        Volatile.Read(ref _vectorSceneResidencyPolicy)?.HasCapacityDeferredScenes == true;

    internal long ActiveCapacityGeneration =>
        Volatile.Read(ref _vectorSceneResidencyPolicy)?.ActiveCapacityGeneration ?? 0;

    // Callers must hold a paint/read lease before using this borrowed policy.
    // Snapshot retirement clears and disposes it under the same layout lock.
    internal IMarkdownVectorSceneResidencyPolicy? VectorSceneResidencyPolicy
        => _vectorSceneResidencyPolicy;

    internal int InlineSceneGeometryIndexBuildCount
    {
        get { lock (_geometry.Gate) return _vectorSceneResidencyPolicy?.InlineGeometryIndexBuildCount ?? 0; }
    }

    internal int SceneCommandGraphEstimateCount
    {
        get { lock (_geometry.Gate) return _vectorSceneResidencyPolicy?.CommandGraphEstimateCount ?? 0; }
    }

    internal int ActiveInlineSceneGeometryIndexCount
    {
        get { lock (_geometry.Gate) return _vectorSceneResidencyPolicy?.ActiveInlineSceneGeometryIndexCount ?? 0; }
    }

    /// <summary>
    /// Returns whether every measured root in the visible band has prepared
    /// (or, when <paramref name="acceptTerminalFailure"/> is set, settled)
    /// scenes. The caller holds the layout gate and has already checked that
    /// the realization band is measured.
    /// </summary>
    internal bool AreVisibleBandScenesReady(
        LazyLayoutBand visibleVectorBand,
        bool acceptTerminalFailure)
    {
        _sceneBandOrdinalScratch.Clear();
        try
        {
            LayoutPlacementGeneration placements = _geometry.CurrentPlacementGenerationNoLock();
            var range = _geometry.FindViewportRangeNoLock(visibleVectorBand.Top, visibleVectorBand.Bottom);
            for (int i = range.Start; i < range.End; i++)
            {
                int ordinal = placements.GetBlockOrdinal(i);
                LayoutRootPlacement placement = placements.GetPlacement(ordinal);
                if (!visibleVectorBand.Intersects(placement.Top, placement.Bottom))
                    continue;
                if (_geometry.LazyLayoutEnabled && _geometry.MeasuredTopLevelBlocks is not null &&
                    !_geometry.MeasuredTopLevelBlocks[ordinal])
                    return false;
                _sceneBandOrdinalScratch.Add(ordinal);
            }

            return _vectorSceneResidencyPolicy is not { } policy || (acceptTerminalFailure
                ? policy.IsBandWorkSettled(
                    _sceneBandOrdinalScratch,
                    visibleVectorBand,
                    placements,
                    _geometry.TopLevelOrdinalByBlockIndex)
                : policy.AreNativeResourcesPrepared(
                    _sceneBandOrdinalScratch,
                    visibleVectorBand,
                    placements,
                    _geometry.TopLevelOrdinalByBlockIndex));
        }
        finally
        {
            _sceneBandOrdinalScratch.Clear();
        }
    }

    /// <summary>
    /// Replaces the policy's active band with the measured roots of the
    /// visible band. The caller holds the layout gate inside a realization pass.
    /// </summary>
    internal void Refresh(
        LazyBandRealizationPass pass,
        IMarkdownVectorSceneResidencyPolicy residencyPolicy,
        LazyLayoutBand visibleVectorBand)
    {
        LayoutPlacementGeneration scenePlacements = _geometry.CurrentPlacementGenerationNoLock();
        var range = _geometry.FindViewportRangeNoLock(visibleVectorBand.Top, visibleVectorBand.Bottom);
        var requestedSceneBandOrdinals = new List<int>(range.End - range.Start);
        pass.RequestedSceneBandOrdinals = requestedSceneBandOrdinals;
        for (int i = range.Start; i < range.End; i++)
        {
            int ordinal = scenePlacements.GetBlockOrdinal(i);
            LayoutRootPlacement placement = scenePlacements.GetPlacement(ordinal);
            if (!visibleVectorBand.Intersects(placement.Top, placement.Bottom))
                continue;
            if (_geometry.LazyLayoutEnabled && _geometry.MeasuredTopLevelBlocks is not null &&
                !_geometry.MeasuredTopLevelBlocks[ordinal])
            {
                // The realization band must include the actual
                // viewport; never attach a native scene to estimated
                // geometry and pretend that scene is visible-ready.
                throw new InvalidOperationException(
                    "The requested visible vector band contains an unrealized top-level block.");
            }
            requestedSceneBandOrdinals.Add(ordinal);
        }

        pass.NativeSceneResourcesChanged = residencyPolicy.ReplaceActiveBand(
            requestedSceneBandOrdinals,
            visibleVectorBand,
            scenePlacements,
            _geometry.TopLevelOrdinalByBlockIndex,
            pass.CancellationToken);
    }

    /// <summary>
    /// Releases the unpinned scenes a failed realization may have prepared for
    /// its requested band, keeping the original failure if release also fails.
    /// </summary>
    internal void ReleaseUnpinnedAfterFailedRealization(
        LazyBandRealizationPass pass,
        LazyLayoutBand visibleVectorBand)
    {
        if (pass.RequestedSceneBandOrdinals is { } requestedSceneBandOrdinals &&
            _vectorSceneResidencyPolicy is { } failedBandPolicy)
        {
            try
            {
                failedBandPolicy.ReleaseUnpinnedResources(
                    requestedSceneBandOrdinals,
                    visibleVectorBand,
                    _geometry.CurrentPlacementGenerationNoLock(),
                    _geometry.TopLevelOrdinalByBlockIndex);
            }
            catch
            {
                // Keep the original measurement/preparation failure.
            }
        }
    }

    /// <summary>
    /// Allocation-stable counterpart for scene readiness/scheduling probes.
    /// The caller must already hold TryBeginPaint/TryBeginInteraction so this
    /// snapshot-owned scratch list cannot be overwritten before consumption.
    /// </summary>
    internal bool TryGetMeasuredTopLevelBlocksInBandForSceneProbe(
        double top,
        double bottom,
        out IReadOnlyList<BlockBox> blocks)
        => TryGetMeasuredTopLevelBlocksInBandForSceneProbe(
            top,
            bottom,
            out blocks,
            out _,
            out _,
            out _);

    internal bool TryGetMeasuredTopLevelBlocksInBandForSceneProbe(
        double top,
        double bottom,
        out IReadOnlyList<BlockBox> blocks,
        out IReadOnlyList<int> topLevelOrdinals,
        out LayoutPlacementGeneration placements,
        out IReadOnlyDictionary<int, int> topLevelOrdinalByBlockIndex)
    {
        blocks = Array.Empty<BlockBox>();
        topLevelOrdinals = Array.Empty<int>();
        placements = null!;
        topLevelOrdinalByBlockIndex = null!;
        if (!Monitor.IsEntered(_geometry.Gate) || _recovery.IsRequired || !double.IsFinite(top) ||
            !double.IsFinite(bottom) || bottom < top)
        {
            return false;
        }

        _sceneProbeBlockScratch.Clear();
        _sceneBandOrdinalScratch.Clear();
        var band = new LazyLayoutBand(top, bottom);
        placements = _geometry.CurrentPlacementGenerationNoLock();
        ViewportRange range = placements.Find(top, bottom);
        for (int index = range.Start; index < range.End; index++)
        {
            int ordinal = placements.GetBlockOrdinal(index);
            BlockBox block = _geometry.Blocks[ordinal];
            LayoutRootPlacement placement = placements.GetPlacement(ordinal);
            if (placement.Bottom < top || placement.Top > bottom)
                continue;
            if (_geometry.LazyLayoutEnabled && _geometry.MeasuredTopLevelBlocks is not null &&
                !_geometry.MeasuredTopLevelBlocks[ordinal])
            {
                _sceneProbeBlockScratch.Clear();
                _sceneBandOrdinalScratch.Clear();
                placements = null!;
                return false;
            }

            _sceneProbeBlockScratch.Add(block);
            _sceneBandOrdinalScratch.Add(ordinal);
        }

        if (_vectorSceneResidencyPolicy?.AreNativeResourcesPrepared(
                _sceneBandOrdinalScratch,
                band,
                placements,
                _geometry.TopLevelOrdinalByBlockIndex) == false)
        {
            _sceneProbeBlockScratch.Clear();
            _sceneBandOrdinalScratch.Clear();
            placements = null!;
            return false;
        }

        blocks = _sceneProbeBlockScratch;
        topLevelOrdinals = _sceneBandOrdinalScratch;
        topLevelOrdinalByBlockIndex = _geometry.TopLevelOrdinalByBlockIndex;
        return true;
    }

    /// <summary>
    /// Checks terminal vector failures for the current logical viewport while
    /// holding the snapshot read lease. Scene owners remain physically arranged
    /// relative to their roots, so the residency policy maps each failed owner
    /// through this published placement generation before testing visibility.
    /// </summary>
    internal bool HasFailedScenesInBandUnderPaint(LazyLayoutBand band)
    {
        if (!Monitor.IsEntered(_geometry.Gate))
            throw new InvalidOperationException("A snapshot read lease is required.");
        if (_recovery.IsRequired || !double.IsFinite(band.Top) ||
            !double.IsFinite(band.Bottom) || band.Bottom < band.Top ||
            _vectorSceneResidencyPolicy is not { } policy)
        {
            return false;
        }

        return policy.HasFailedScenes(
            band,
            _geometry.CurrentPlacementGenerationNoLock(),
            _geometry.TopLevelOrdinalByBlockIndex);
    }

    /// <summary>
    /// Detaches the residency policy for disposal and clears the scene scratch
    /// lists. Called once by snapshot disposal under the layout gate.
    /// </summary>
    internal IMarkdownVectorSceneResidencyPolicy? DetachResidencyPolicy()
    {
        IMarkdownVectorSceneResidencyPolicy? residencyPolicy = _vectorSceneResidencyPolicy;
        _vectorSceneResidencyPolicy = null;
        _sceneProbeBlockScratch.Clear();
        _sceneBandOrdinalScratch.Clear();
        return residencyPolicy;
    }
}
