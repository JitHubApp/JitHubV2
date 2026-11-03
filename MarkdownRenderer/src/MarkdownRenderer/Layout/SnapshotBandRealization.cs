using System;
using System.Collections.Generic;
using System.Threading;
using Microsoft.UI.Xaml;
using Windows.Foundation;
using MarkdownRenderer.Layout.Boxes;
using MarkdownRenderer.Performance;

namespace MarkdownRenderer.Layout;

/// <summary>
/// Lazy top-level measurement and vector-scene residency for viewport bands:
/// enables lazy layout, measures newly visible roots, refreshes native scene
/// residency against the visible band, and answers band readiness queries.
/// </summary>
internal sealed class SnapshotBandRealization
{
    private readonly LayoutSnapshotGeometry _geometry;
    private readonly GeometryRecoveryLatch _recovery;
    private readonly SnapshotRetirement _retirement;
    private readonly LayoutPlanCandidateIndex _planCandidates;
    private readonly List<int> _sceneBandOrdinalScratch = [];
    private readonly List<BlockBox> _sceneProbeBlockScratch = [];
    private IMarkdownVectorSceneResidencyPolicy? _vectorSceneResidencyPolicy;

    internal SnapshotBandRealization(
        LayoutSnapshotGeometry geometry,
        GeometryRecoveryLatch recovery,
        SnapshotRetirement retirement,
        LayoutPlanCandidateIndex planCandidates,
        IMarkdownVectorSceneResidencyPolicy? vectorSceneResidencyPolicy)
    {
        _geometry = geometry;
        _recovery = recovery;
        _retirement = retirement;
        _planCandidates = planCandidates;        _vectorSceneResidencyPolicy = vectorSceneResidencyPolicy;
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
    /// Enables viewport-relative top-level measurement and realizes the first
    /// viewport band. The block tree and source map already exist, but expensive
    /// text/native layout objects are created only as bands are measured.
    /// </summary>
    internal void EnableLazyLayout(
        float availableWidth,
        double viewportTop,
        double viewportHeight,
        double overscan,
        CancellationToken cancellationToken,
        LazyLayoutBand? initialVisibleVectorBand = null)
    {
        lock (_geometry.Gate)
        {
            if (_recovery.IsRequired)
                return;

            if (_geometry.LazyLayoutEnabled)
                return;

            _geometry.LazyLayoutEnabled = true;
            _geometry.AvailableWidth = Math.Max(1f, availableWidth);
            _geometry.MeasuredTopLevelBlocks = new bool[_geometry.Blocks.Count];
            _planCandidates.ResetMeasuredRootOrdinalsNoLock();
            _geometry.MeasuredTopLevelBlockCount = 0;
            _geometry.ReflowNoLock();
        }

        LazyLayoutBand realizationBand = LazyLayoutBand.FromViewport(
            viewportTop, viewportHeight, overscan);
        LazyLayoutBand visibleVectorBand = initialVisibleVectorBand ?? realizationBand;
        // Rect projection can extend a fractional visible edge even when the
        // caller requests zero overscan. Every admitted scene still requires
        // measured geometry, including that projected edge.
        realizationBand = new LazyLayoutBand(
            Math.Min(realizationBand.Top, visibleVectorBand.Top),
            Math.Max(realizationBand.Bottom, visibleVectorBand.Bottom));
        EnsureMeasuredBand(realizationBand, visibleVectorBand, cancellationToken);
    }

    internal LazyLayoutCommit EnsureMeasuredViewport(
        double viewportTop,
        double viewportHeight,
        double overscan,
        CancellationToken cancellationToken)
        => EnsureMeasuredBand(LazyLayoutBand.FromViewport(viewportTop, viewportHeight, overscan), cancellationToken);

    /// <summary>
    /// Returns true when every block intersecting the requested estimated or
    /// measured band already owns its native layout objects. The UI-thread
    /// viewport path uses this before dispatching a background realization so
    /// a genuinely warm scroll performs no task/CTS/delegate allocation.
    /// </summary>
    internal bool IsBandMeasured(LazyLayoutBand band)
        => IsBandMeasured(band, acceptTerminalFailure: false);

    // Scheduling completion is distinct from successful rendering. A typed
    // terminal fallback needs no more producer work for this committed band,
    // but must never make the rendering/readiness gate pass.
    internal bool IsBandWorkSettled(LazyLayoutBand band)
        => IsBandMeasured(band, acceptTerminalFailure: true);

    /// <summary>
    /// Checks realization coverage for the broader layout band while evaluating
    /// vector-scene readiness only for the actual visible band. Look-ahead
    /// geometry can therefore be measured without admitting its native scenes.
   /// </summary>
    internal bool IsBandWorkSettled(
        LazyLayoutBand realizationBand,
        LazyLayoutBand visibleVectorBand)
        => IsBandMeasured(realizationBand, visibleVectorBand, acceptTerminalFailure: true);

    /// <summary>
    /// Returns whether geometry for a realization band is already measured,
    /// without requiring its vector scenes to own native resources. This lets
    /// the control suppress repeated capacity retries while still scheduling
    /// genuinely missing layout work.
    /// </summary>
    internal bool IsRealizationBandMeasured(LazyLayoutBand realizationBand)
    {
        if (!Monitor.TryEnter(_geometry.Gate))
            return false;

        try
        {
            return !_recovery.IsRequired && IsRealizationBandMeasuredNoLock(realizationBand);
        }
        finally
        {
            Monitor.Exit(_geometry.Gate);
        }
    }

    /// <summary>
    /// Checks whether every root a pointer hit-test could inspect in this band
    /// already has authoritative measured geometry. The caller must hold the
    /// snapshot interaction/read lease; this method performs no layout work.
    /// </summary>
    internal bool IsInputHitTestBandMeasuredUnderInteraction(LazyLayoutBand band)
    {
        if (!Monitor.IsEntered(_geometry.Gate) ||
            _retirement.IsRetiringOrDisposed ||
            !double.IsFinite(band.Top) ||
            !double.IsFinite(band.Bottom) ||
            band.Top < 0 ||
            band.Bottom < band.Top ||
            _recovery.IsRequired)
        {
            return false;
        }

        if (band.Top == 0 && band.Bottom == 0)
            return true;

        return IsRealizationBandMeasuredNoLock(band);
    }

    internal bool IsBandMeasured(LazyLayoutBand band, bool acceptTerminalFailure)
        => IsBandMeasured(band, band, acceptTerminalFailure);

    internal bool IsBandMeasured(
        LazyLayoutBand realizationBand,
        LazyLayoutBand visibleVectorBand,
        bool acceptTerminalFailure)
    {
        if (!Monitor.TryEnter(_geometry.Gate))
            return false;

        try
        {
            if (_recovery.IsRequired)
                return false;

            if (!IsRealizationBandMeasuredNoLock(realizationBand))
                return false;

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
        finally
        {
            Monitor.Exit(_geometry.Gate);
        }
    }

    private bool IsRealizationBandMeasuredNoLock(LazyLayoutBand band)
    {
        if (!_geometry.LazyLayoutEnabled || _geometry.MeasuredTopLevelBlocks is null)
            return true;

        var range = _geometry.FindViewportRangeNoLock(band.Top, band.Bottom);
        for (int i = range.Start; i < range.End; i++)
        {
            LayoutPlacementGeneration placements = _geometry.CurrentPlacementGenerationNoLock();
            int ordinal = placements.GetBlockOrdinal(i);
            LayoutRootPlacement placement = placements.GetPlacement(ordinal);
            if (band.Intersects(placement.Top, placement.Bottom) &&
                !_geometry.MeasuredTopLevelBlocks[ordinal])
            {
                return false;
            }
        }

        return true;
    }

    internal LazyLayoutCommit EnsureMeasuredBand(LazyLayoutBand band, CancellationToken cancellationToken)
        => EnsureMeasuredBand(band, band, cancellationToken);

    /// <summary>
    /// Measures/reflows only the realization band, then refreshes vector
    /// residency against the visible band. Eager snapshots still refresh
    /// residency but never repeat their full document measure pass.
    /// </summary>
    internal LazyLayoutCommit EnsureMeasuredBand(
        LazyLayoutBand realizationBand,
        LazyLayoutBand visibleVectorBand,
        CancellationToken cancellationToken)
    {
        CancellationToken effectiveCancellationToken = cancellationToken.CanBeCanceled
            ? cancellationToken
            : LayoutPassCancellation.GetEffectiveToken(CancellationToken.None);
        using var cancellationScope = LayoutPassCancellation.Push(effectiveCancellationToken);
        lock (_geometry.Gate)
        {
            effectiveCancellationToken.ThrowIfCancellationRequested();
            // A realization can already be queued when its snapshot is
            // replaced. Retirement and measurement share this lock, so check
            // the terminal state only after acquiring it: if retirement won
            // the race, no disposed native layout may be touched.
            if (_retirement.IsRetirementRequested ||
                _retirement.IsDisposed)
            {
                return LazyLayoutCommit.Unchanged(_geometry.Size.Height, _geometry.LayoutRevision);
            }

            if (_recovery.IsRequired)
                return LazyLayoutCommit.Unchanged(_geometry.Size.Height, _geometry.LayoutRevision);

            if ((!_geometry.LazyLayoutEnabled || _geometry.MeasuredTopLevelBlocks is null) &&
                _vectorSceneResidencyPolicy is null)
                return LazyLayoutCommit.Unchanged(_geometry.Size.Height, _geometry.LayoutRevision);

            double oldHeight = _geometry.Size.Height;
            long oldRevision = _geometry.LayoutRevision;
            int oldMeasuredTopLevelBlockCount = _geometry.MeasuredTopLevelBlockCount;
            bool oldMeasuredLayoutPlanRootOrdinalsAreSorted =
                _planCandidates.MeasuredRootOrdinalsAreSorted;
            int firstMeasureAttempted = _geometry.Blocks.Count;
            List<int>? measuredOrdinals = null;
            List<int>? requestedSceneBandOrdinals = null;
            bool nativeSceneResourcesChanged = false;
            bool priorPaintOnlyGeometryTainted = _geometry.PaintOnlyGeometryTainted;

            try
            {
                if (_geometry.LazyLayoutEnabled && _geometry.MeasuredTopLevelBlocks is not null)
                {
                    float contentWidth = _geometry.GetContentWidthNoLock();
                    while (true)
                    {
                        effectiveCancellationToken.ThrowIfCancellationRequested();
                        var range = _geometry.FindViewportRangeNoLock(realizationBand.Top, realizationBand.Bottom);
                        List<int>? newlyMeasured = null;
                        int firstAffectedOrdinal = _geometry.Blocks.Count;
                bool geometryUnchanged = _geometry.CurrentPlacementGenerationNoLock().IsDocumentOrder &&
                            !priorPaintOnlyGeometryTainted;
                        for (int i = range.Start; i < range.End; i++)
                        {
                            effectiveCancellationToken.ThrowIfCancellationRequested();
                            int ordinal = _geometry.CurrentPlacementGenerationNoLock().GetBlockOrdinal(i);
                            BlockBox block = _geometry.Blocks[ordinal];
                            LayoutRootPlacement committedPlacement = _geometry.CurrentPlacementGenerationNoLock().GetPlacement(ordinal);
                            if (_geometry.MeasuredTopLevelBlocks[ordinal] ||
                                !realizationBand.Intersects(committedPlacement.Top, committedPlacement.Bottom))
                            {
                                continue;
                            }

                            // Measure resets most block bounds to local (0,0).
                            // Preserve the candidate's old top until reflow assigns
                            // its authoritative position after new heights settle.
                            Rect priorBounds = block.Bounds;
                            Thickness priorMargin = block.Margin;
                            bool priorFlowWithPrevious = ordinal > 0 &&
                                StackBox.SharesSafeHtmlInlineFlow(_geometry.Blocks[ordinal - 1], block);
                            bool priorFlowWithNext = ordinal < _geometry.Blocks.Count - 1 &&
                                StackBox.SharesSafeHtmlInlineFlow(block, _geometry.Blocks[ordinal + 1]);
                            // A factory/Arrange can mutate then throw before this
                            // batch gets copied into measuredOrdinals. Rollback must
                            // include that attempted root, not just completed ones.
                            firstMeasureAttempted = Math.Min(firstMeasureAttempted, ordinal);
                            _geometry.PaintOnlyGeometryTainted = true;
                            block.Measure(contentWidth);
                            block.Arrange(
                                (float)_geometry.DocumentPadding.Left,
                                (float)priorBounds.Top,
                                contentWidth);
                            bool flowWithPrevious = ordinal > 0 &&
                                StackBox.SharesSafeHtmlInlineFlow(_geometry.Blocks[ordinal - 1], block);
                            bool flowWithNext = ordinal < _geometry.Blocks.Count - 1 &&
                                StackBox.SharesSafeHtmlInlineFlow(block, _geometry.Blocks[ordinal + 1]);
                            geometryUnchanged &= LayoutSnapshotGeometry.IsFiniteBounds(priorBounds) &&
                                double.IsFinite(priorBounds.Right) && double.IsFinite(priorBounds.Bottom) &&
                                priorBounds.Width >= 0 && priorBounds.Height >= 0 &&
                                block.Bounds.Equals(priorBounds) &&
                                block.Margin.Equals(priorMargin) &&
                                flowWithPrevious == priorFlowWithPrevious &&
                                flowWithNext == priorFlowWithNext;
                            firstAffectedOrdinal = Math.Min(firstAffectedOrdinal,
                                flowWithPrevious != priorFlowWithPrevious ? Math.Max(0, ordinal - 1) : ordinal);
                            (newlyMeasured ??= []).Add(ordinal);
                        }

                        if (newlyMeasured is null)
                            break;

                        foreach (int ordinal in newlyMeasured)
                        {
                            // Keep rollback ownership before publishing the bit;
                            // growing this list can fail without changing the mask.
                            (measuredOrdinals ??= []).Add(ordinal);
                            _geometry.MeasuredTopLevelBlocks[ordinal] = true;
                            _geometry.MeasuredTopLevelBlockCount++;
                            _planCandidates.AddMeasuredLayoutPlanRootOrdinalNoLock(ordinal);
                        }

                        // Recompute all candidate ordinals after changed heights
                        // shift downstream blocks. The next pass measures every
                        // previously unmeasured block newly entering the band.
                        if (!geometryUnchanged)
                            _geometry.ReflowNoLock(incrementRevision: false, firstOrdinal: firstAffectedOrdinal);
                    }
                }

                if (_vectorSceneResidencyPolicy is { } residencyPolicy)
                {
                    LayoutPlacementGeneration scenePlacements = _geometry.CurrentPlacementGenerationNoLock();
                    var range = _geometry.FindViewportRangeNoLock(visibleVectorBand.Top, visibleVectorBand.Bottom);
                    requestedSceneBandOrdinals = new List<int>(range.End - range.Start);
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

                    nativeSceneResourcesChanged = residencyPolicy.ReplaceActiveBand(
                        requestedSceneBandOrdinals,
                        visibleVectorBand,
                        scenePlacements,
                        _geometry.TopLevelOrdinalByBlockIndex,
                        effectiveCancellationToken);
                }

                // The measured-root index is consumed by the UI-thread
                // realization-plan publisher. Finalize any out-of-order band
                // arrivals while this mutation still owns the layout gate,
                // before the new geometry/metadata can be observed.
                _planCandidates.SortMeasuredLayoutPlanRootOrdinalsNoLock();

                // ReflowNoLock already published the candidate geometry and
                // viewport index under this lock. Commit shared estimates and
                // advance the revision only after all visible native resources
                // have been prepared successfully.
                if (measuredOrdinals is { Count: > 0 })
                {
                    foreach (int ordinal in measuredOrdinals)
                    {
                        _geometry.SharedLayoutMetrics?.RecordHeight(
                            ordinal,
                            (float)_geometry.Blocks[ordinal].Bounds.Height);
                    }
                    _geometry.LayoutRevision = oldRevision + 1;
                    _geometry.PreserveLazyReflowContinuationRevisionNoLock(oldRevision, _geometry.LayoutRevision);
                }
            }
            catch (Exception realizationFailure)
            {
                // Even when outer bounds and measured bits are rolled back,
                // Measure/Arrange may have changed nested native state. A
                // canceled pass after its first attempted root is not a
                // certifiable snapshot repair; only replacement can recover it.
                if (firstMeasureAttempted < _geometry.Blocks.Count)
                    _recovery.MarkGeometryRecoveryRequiredNoLock(realizationFailure);

                // A failed preparation may have created a prefix of the new
                // band before cancellation/device loss. Release those unpinned
                // resources while candidate geometry is still available; the
                // policy's own transactional catch is defensive duplication.
                if (requestedSceneBandOrdinals is not null &&
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

                // Measure can mutate a box before observing cancellation or a
                // device-loss exception. Restore the last logically committed
                // measured/estimated arrangement before another thread can
                // inspect this snapshot.
                if (measuredOrdinals is { Count: > 0 })
                {
                    foreach (int ordinal in measuredOrdinals)
                        _geometry.MeasuredTopLevelBlocks![ordinal] = false;
                    _geometry.MeasuredTopLevelBlockCount = oldMeasuredTopLevelBlockCount;
                    _planCandidates.RemoveUnmeasuredLayoutPlanRootOrdinalsNoLock();
                    _planCandidates.MeasuredRootOrdinalsAreSorted =
                        oldMeasuredLayoutPlanRootOrdinalsAreSorted;
                }
                _geometry.LayoutRevision = oldRevision;
                // Include the predecessor: an unsuccessful Measure may also
                // have changed the attempted root's safe-HTML flow identity.
                try
                {
                    using var rollbackScope = LayoutPassCancellation.Push(CancellationToken.None);
                    if (firstMeasureAttempted < _geometry.Blocks.Count)
                    {
                        // Capacity-only residency refreshes do not mutate
                        // arrangement. In particular, eager snapshots must
                        // not trigger even a suffix Reflow when native
                        // admission is canceled or fails.
                        _geometry.ReflowNoLock(incrementRevision: false,
                            firstOrdinal: Math.Max(0, firstMeasureAttempted - 1));
                    }
                }
                catch (Exception rollbackFailure)
                {
                    if (firstMeasureAttempted < _geometry.Blocks.Count)
                        _recovery.MarkGeometryRecoveryRequiredNoLock(realizationFailure, rollbackFailure);

                    // A custom/native Arrange can fail again during recovery.
                    // Do not expose partially restored bounds under the previous
                    // revision. Retirement rejects new read/paint leases and
                    // drains resources asynchronously after this gate releases.
                    _geometry.LazyReflowContinuationRevision = null;
                    _ = _retirement.Retire();
                    throw new AggregateException(
                        "Lazy realization and placement recovery both failed.",
                        realizationFailure, rollbackFailure);
                }
                throw;
            }

            try
            {
                _geometry.PublishCommittedMetadataNoLock();
                _geometry.PaintOnlyGeometryTainted = priorPaintOnlyGeometryTainted;
                LazyLayoutCommit commit = measuredOrdinals is { Count: > 0 }
                    ? new LazyLayoutCommit(
                        true,
                        measuredOrdinals.Count,
                        oldHeight,
                        _geometry.Size.Height,
                        _geometry.LayoutRevision)
                    : LazyLayoutCommit.Unchanged(_geometry.Size.Height, _geometry.LayoutRevision);
                return commit with
                {
                    NativeSceneResourcesChanged = nativeSceneResourcesChanged,
                    SceneGraphRebindRequired = _vectorSceneResidencyPolicy?.HasPendingGraphRebinds == true,
                };
            }
            catch (Exception failure)
            {
                if (firstMeasureAttempted < _geometry.Blocks.Count)
                    _recovery.MarkGeometryRecoveryRequiredNoLock(failure);
                throw;
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
    internal IMarkdownVectorSceneResidencyPolicy? DetachResidencyPolicyNoLock()
    {
        IMarkdownVectorSceneResidencyPolicy? residencyPolicy = _vectorSceneResidencyPolicy;
        _vectorSceneResidencyPolicy = null;
        _sceneProbeBlockScratch.Clear();
        _sceneBandOrdinalScratch.Clear();
        return residencyPolicy;
    }
}
