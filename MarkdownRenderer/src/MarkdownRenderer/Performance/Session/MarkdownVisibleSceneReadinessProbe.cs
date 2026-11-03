using System;
using System.Collections.Generic;
using MarkdownRenderer.Controls;
using MarkdownRenderer.Layout;
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
        if (expectedGeneration <= 0 ||
            !MarkdownProgressiveScenePlan.IsFiniteNonEmptyRect(viewport) ||
            !IsDeferredSceneReadinessCurrent(
                owner,
                expectedSnapshot,
                expectedGeneration,
                expectedLayoutRevision))
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
            if (expectedSnapshot.IsRetiringOrDisposed ||
                !IsDeferredSceneReadinessCurrent(
                    owner,
                    expectedSnapshot,
                    expectedGeneration,
                    expectedLayoutRevision))
            {
                return MarkdownVisibleSceneReadiness.NotReady;
            }
            return MarkdownVisibleSceneReadiness.Busy;
        }

        try
        {
            if (expectedSnapshot.GeometryRecoveryRequired)
                return MarkdownVisibleSceneReadiness.Failed;
            if (expectedSnapshot.IsRetiringOrDisposed)
                return MarkdownVisibleSceneReadiness.NotReady;
            if (!IsDeferredSceneReadinessCurrent(
                    owner,
                    expectedSnapshot,
                    expectedGeneration,
                    expectedLayoutRevision))
            {
                return MarkdownVisibleSceneReadiness.NotReady;
            }

            LazyLayoutBand visibleBand = LazyLayoutBand.FromViewport(
                viewport.Top,
                viewport.Height,
                overscan: 0);
            // Failure is a terminal status, not render readiness. Probe under
            // the same validated lease before the strict measured-band gate.
            if (expectedSnapshot.HasFailedScenesInBandUnderPaint(visibleBand))
                return MarkdownVisibleSceneReadiness.Failed;
            if (!expectedSnapshot.IsBandMeasured(visibleBand) ||
                !expectedSnapshot.TryGetMeasuredTopLevelBlocksInBandForSceneProbe(
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
                topLevelOrdinalByBlockIndex,
                viewport.Top,
                viewport.Bottom,
                viewport,
                expectedSnapshot.VectorSceneResidencyPolicy,
                out IReadOnlyList<string> visibleMarkerKeys,
                out bool visibleSceneResourcesReady);
            if (!scanVerified ||
                !IsDeferredSceneReadinessCurrent(
                    owner,
                    expectedSnapshot,
                    expectedGeneration,
                    expectedLayoutRevision))
            {
                return MarkdownVisibleSceneReadiness.NotReady;
            }

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
                IsDeferredSceneReadinessCurrent(
                    owner,
                    expectedSnapshot,
                    expectedGeneration,
                    expectedLayoutRevision)
                    ? MarkdownVisibleSceneReadiness.Ready
                    : MarkdownVisibleSceneReadiness.NotReady;
        }
        finally
        {
            expectedSnapshot.EndPaint();
        }
    }

    private bool IsDeferredSceneReadinessCurrent(
        MarkdownRendererControl owner,
        LayoutSnapshot expectedSnapshot,
        long expectedGeneration,
        long expectedLayoutRevision) =>
        owner.DeferredSceneReadiness.IsSnapshotCurrent(
            expectedSnapshot,
            expectedGeneration,
            expectedLayoutRevision) &&
        ReferenceEquals(owner.PerformanceSession, session);
}
