using System;
using System.Collections.Generic;
using MarkdownRenderer.Controls;
using MarkdownRenderer.Layout.Internal.Boxes;
using MarkdownRenderer.Layout.Internal.LazyLayout;
using MarkdownRenderer.Layout.Internal.Placement;
using MarkdownRenderer.Layout.Internal.Snapshots;
using Windows.Foundation;

namespace MarkdownRenderer.Performance;

/// <summary>
/// Answers whether every deferred scene in a control's visible viewport is
/// rendered, probing under a validated paint lease and re-checking snapshot
/// currency around each step.
/// </summary>
internal sealed class MarkdownVisibleSceneReadinessProbe(IMarkdownPerformanceSessionInternal session)
{
    internal MarkdownVisibleSceneReadiness GetReadiness(
        MarkdownRendererControl owner,
        LayoutSnapshot expectedSnapshot,
        long expectedGeneration,
        long expectedLayoutRevision,
        Rect viewport)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(expectedSnapshot);
        var expectation = new DeferredSceneReadinessExpectation(
            owner,
            expectedSnapshot,
            expectedGeneration,
            expectedLayoutRevision);
        if (expectedGeneration <= 0 ||
            !MarkdownProgressiveScenePlan.IsFiniteNonEmptyRect(viewport) ||
            !expectation.IsCurrent(session))
        {
            return MarkdownVisibleSceneReadiness.NotReady;
        }

        // A failed geometry rollback is not a transient reader collision. The
        // control replaces this snapshot; reporting Busy here would ask UIA to
        // keep retrying against geometry that the snapshot has explicitly
        // stopped exposing.
        if (expectedSnapshot.GeometryRecoveryRequired)
            return MarkdownVisibleSceneReadiness.Failed;
        if (expectedSnapshot.IsRetiringOrDisposed)
            return MarkdownVisibleSceneReadiness.NotReady;
        if (!expectedSnapshot.TryBeginPaint())
        {
            if (expectedSnapshot.GeometryRecoveryRequired)
                return MarkdownVisibleSceneReadiness.Failed;
            if (expectedSnapshot.IsRetiringOrDisposed || !expectation.IsCurrent(session))
                return MarkdownVisibleSceneReadiness.NotReady;
            return MarkdownVisibleSceneReadiness.Busy;
        }

        try
        {
            return ProbeUnderPaint(expectation, viewport);
        }
        finally
        {
            expectedSnapshot.EndPaint();
        }
    }

    // Runs while the caller holds the snapshot's paint lease.
    private MarkdownVisibleSceneReadiness ProbeUnderPaint(
        DeferredSceneReadinessExpectation expectation,
        Rect viewport)
    {
        LayoutSnapshot snapshot = expectation.Snapshot;
        if (snapshot.GeometryRecoveryRequired)
            return MarkdownVisibleSceneReadiness.Failed;
        if (snapshot.IsRetiringOrDisposed)
            return MarkdownVisibleSceneReadiness.NotReady;
        if (!expectation.IsCurrent(session))
            return MarkdownVisibleSceneReadiness.NotReady;

        LazyLayoutBand visibleBand = LazyLayoutBand.FromViewport(
            viewport.Top,
            viewport.Height,
            overscan: 0);
        // Failure is a terminal status, not render readiness. Probe under
        // the same validated lease before the strict measured-band gate.
        if (snapshot.HasFailedScenesInBandUnderPaint(visibleBand))
            return MarkdownVisibleSceneReadiness.Failed;
        if (!snapshot.IsBandMeasured(visibleBand) ||
            !snapshot.TryGetMeasuredTopLevelBlocksInBandForSceneProbe(
                viewport.Top,
                viewport.Bottom,
                out IReadOnlyList<BlockBox> visibleBlocks,
                out IReadOnlyList<int> visibleOrdinals,
                out LayoutPlacementGeneration placements,
                out IReadOnlyDictionary<int, int> topLevelOrdinalByBlockIndex))
        {
            return MarkdownVisibleSceneReadiness.NotReady;
        }

        bool scanVerified = MarkdownProgressiveScenePlan.TryCollectVisibleDeferredSceneMarkers(
            visibleBlocks,
            visibleOrdinals,
            placements,
            snapshot.PlacementTransform,
            topLevelOrdinalByBlockIndex,
            viewport.Top,
            viewport.Bottom,
            viewport,
            snapshot.VectorSceneResidencyPolicy,
            out IReadOnlyList<string> visibleMarkerKeys,
            out bool visibleSceneResourcesReady);
        if (!scanVerified || !expectation.IsCurrent(session))
            return MarkdownVisibleSceneReadiness.NotReady;

        MarkdownRendererControl owner = expectation.Owner;
        bool pending = MarkdownDeferredSceneReadiness.HasPendingVisiblePlaceholder(
            // The exact visible marker identities are authoritative here;
            // keep checking them even if aggregate plan bookkeeping says
            // no work remains.
            hasRegisteredWork: true,
            visibleBandVerified: true,
            visiblePlaceholderKeys: visibleMarkerKeys,
            isRegisteredMarker: marker =>
                session.IsRegisteredDeferredSceneMarker(owner, marker));
        return !pending &&
            visibleSceneResourcesReady &&
            expectation.IsCurrent(session)
                ? MarkdownVisibleSceneReadiness.Ready
                : MarkdownVisibleSceneReadiness.NotReady;
    }
}
