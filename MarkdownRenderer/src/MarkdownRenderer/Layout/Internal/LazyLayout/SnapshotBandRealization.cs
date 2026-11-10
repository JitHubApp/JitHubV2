using System;
using System.Threading;
using MarkdownRenderer.Layout.Internal.Placement;
using MarkdownRenderer.Layout.Internal.Snapshots;

namespace MarkdownRenderer.Layout.Internal.LazyLayout;

/// <summary>
/// Lazy top-level measurement for viewport bands: enables lazy layout,
/// measures newly visible roots, asks the snapshot's scene residency to
/// refresh against the visible band, and answers band readiness queries.
/// </summary>
internal sealed class SnapshotBandRealization
{
    private readonly LayoutSnapshotGeometry _geometry;
    private readonly GeometryRecoveryLatch _recovery;
    private readonly SnapshotRetirement _retirement;
    private readonly LayoutPlanCandidateIndex _planCandidates;
    private readonly LazyBandMeasurement _measurement;
    private readonly SnapshotSceneResidency _scenes;

    internal SnapshotBandRealization(
        LayoutSnapshotGeometry geometry,
        GeometryRecoveryLatch recovery,
        SnapshotRetirement retirement,
        LayoutPlanCandidateIndex planCandidates,
        SnapshotSceneResidency scenes)
    {
        _geometry = geometry;
        _recovery = recovery;
        _retirement = retirement;
        _planCandidates = planCandidates;
        _measurement = new LazyBandMeasurement(geometry, planCandidates);
        _scenes = scenes;
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

            return _scenes.AreVisibleBandScenesReady(visibleVectorBand, acceptTerminalFailure);
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
        lock (_geometry.Gate)
        {
            // A band realized inside an enclosing pass (the eager build's
            // residency band) inherits that pass's token. The gate is
            // reentrant, so the enclosing pass already owns this state.
            CancellationToken effectiveCancellationToken = cancellationToken.CanBeCanceled
                ? cancellationToken
                : _geometry.PassCancellation.GetEffectiveToken(CancellationToken.None);
            using var cancellationScope = _geometry.PassCancellation.Push(effectiveCancellationToken);
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
                _scenes.VectorSceneResidencyPolicy is null)
                return LazyLayoutCommit.Unchanged(_geometry.Size.Height, _geometry.LayoutRevision);

            var pass = new LazyBandRealizationPass(_geometry, _planCandidates, effectiveCancellationToken);
            try
            {
                if (_geometry.LazyLayoutEnabled && _geometry.MeasuredTopLevelBlocks is not null)
                    _measurement.MeasureBandNoLock(pass, realizationBand);

                if (_scenes.VectorSceneResidencyPolicy is { } residencyPolicy)
                    _scenes.Refresh(pass, residencyPolicy, visibleVectorBand);

                // The measured-root index is consumed by the UI-thread
                // realization-plan publisher. Finalize any out-of-order band
                // arrivals while this mutation still owns the layout gate,
                // before the new geometry/metadata can be observed.
                _planCandidates.SortMeasuredLayoutPlanRootOrdinalsNoLock();

                // ReflowNoLock already published the candidate geometry and
                // viewport index under this lock. Commit shared estimates and
                // advance the revision only after all visible native resources
                // have been prepared successfully.
                _measurement.CommitMeasuredOrdinalsNoLock(pass);
            }
            catch (Exception realizationFailure)
            {
                RollBackFailedRealizationNoLock(pass, visibleVectorBand, realizationFailure);
                throw;
            }

            return PublishRealizationNoLock(pass);
        }
    }

    private void RollBackFailedRealizationNoLock(
        LazyBandRealizationPass pass,
        LazyLayoutBand visibleVectorBand,
        Exception realizationFailure)
    {
        int firstMeasureAttempted = pass.FirstMeasureAttempted;
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
        _scenes.ReleaseUnpinnedAfterFailedRealization(pass, visibleVectorBand);

        // Measure can mutate a box before observing cancellation or a
        // device-loss exception. Restore the last logically committed
        // measured/estimated arrangement before another thread can
        // inspect this snapshot.
        _measurement.RestoreMeasuredOrdinalsNoLock(pass);
        _geometry.LayoutRevision = pass.OldRevision;
        // Include the predecessor: an unsuccessful Measure may also
        // have changed the attempted root's safe-HTML flow identity.
        try
        {
            using var rollbackScope = _geometry.PassCancellation.Push(CancellationToken.None);
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
    }

    private LazyLayoutCommit PublishRealizationNoLock(LazyBandRealizationPass pass)
    {
        try
        {
            _geometry.PublishCommittedMetadataNoLock();
            _geometry.PaintOnlyGeometryTainted = pass.PriorPaintOnlyGeometryTainted;
            LazyLayoutCommit commit = pass.MeasuredOrdinals is { Count: > 0 } measuredOrdinals
                ? new LazyLayoutCommit(
                    true,
                    measuredOrdinals.Count,
                    pass.OldHeight,
                    _geometry.Size.Height,
                    _geometry.LayoutRevision)
                : LazyLayoutCommit.Unchanged(_geometry.Size.Height, _geometry.LayoutRevision);
            return commit with
            {
                NativeSceneResourcesChanged = pass.NativeSceneResourcesChanged,
                SceneGraphRebindRequired = _scenes.VectorSceneResidencyPolicy?.HasPendingGraphRebinds == true,
            };
        }
        catch (Exception failure)
        {
            if (pass.FirstMeasureAttempted < _geometry.Blocks.Count)
                _recovery.MarkGeometryRecoveryRequiredNoLock(failure);
            throw;
        }
    }
}
