using System;
using System.Collections.Generic;
using System.Threading;
using Windows.Foundation;
using MarkdownRenderer.Layout.Internal.Inlines;
using MarkdownRenderer.Layout.Internal.LazyLayout;
using MarkdownRenderer.Layout.Internal.Placement;
using MarkdownRenderer.Layout.Internal.Vectors;

namespace MarkdownRenderer.Performance.Internal.Residency;

/// <summary>
/// Optional, session-provided policy for retaining native vector drawing
/// resources in the current lazy-layout band. Boxes keep ownership of their
/// scenes and resources; this policy only coordinates preparation and release.
/// </summary>
internal interface IMarkdownVectorSceneResidencyPolicy : IDisposable
{
    bool HasPendingGraphRebinds => false;

    // Scalar wake state only. A deferred capacity fallback is settled for
    // ordinary layout work, but is not a native-ready fidelity result.
    bool HasCapacityDeferredScenes => false;
    long ActiveCapacityGeneration => 0;

    int ActiveSceneCount { get; }

    long ActiveEstimatedBytes { get; }

    int InlineGeometryIndexBuildCount { get; }

    int CommandGraphEstimateCount { get; }

    int ActiveInlineSceneGeometryIndexCount { get; }

    List<string> VisibleDeferredSceneMarkerScratch { get; }

    bool AreNativeResourcesPrepared(
        IReadOnlyList<int> topLevelOrdinals,
        LazyLayoutBand band,
        LayoutPlacementGeneration placements,
        IReadOnlyDictionary<int, int> topLevelOrdinalByBlockIndex);
    bool IsBandWorkSettled(
        IReadOnlyList<int> topLevelOrdinals,
        LazyLayoutBand band,
        LayoutPlacementGeneration placements,
        IReadOnlyDictionary<int, int> topLevelOrdinalByBlockIndex) =>
        AreNativeResourcesPrepared(topLevelOrdinals, band, placements, topLevelOrdinalByBlockIndex);
    bool HasFailedScenes(
        LazyLayoutBand band,
        LayoutPlacementGeneration placements,
        IReadOnlyDictionary<int, int> topLevelOrdinalByBlockIndex) => false;
    IReadOnlyList<VectorSceneBox> GraphStatusChanges => Array.Empty<VectorSceneBox>();
    void ClearGraphStatusChanges() { }

    /// <summary>Atomically prepares the requested band before releasing its predecessor.</summary>
    bool ReplaceActiveBand(
        IReadOnlyList<int> topLevelOrdinals,
        LazyLayoutBand band,
        LayoutPlacementGeneration placements,
        IReadOnlyDictionary<int, int> topLevelOrdinalByBlockIndex,
        CancellationToken cancellationToken);

    /// <summary>
    /// Appends deferred markers for visible inline scenes using the active
    /// scalar geometry index. Returns false when that index is missing or stale;
    /// UI readiness callers must fail closed and request background realization.
    /// </summary>
    bool TryAppendVisibleInlineSceneMarkers(
        InlineContainerBox inline,
        double viewportTop,
        double viewportBottom,
        Rect? viewport,
        List<string> markerKeys,
        ref bool visibleSceneResourcesReady);

    /// <summary>Releases partial scene work for a failed band, excluding current pins.</summary>
    void ReleaseUnpinnedResources(
        IReadOnlyList<int> topLevelOrdinals,
        LazyLayoutBand band,
        LayoutPlacementGeneration placements,
        IReadOnlyDictionary<int, int> topLevelOrdinalByBlockIndex);
}
