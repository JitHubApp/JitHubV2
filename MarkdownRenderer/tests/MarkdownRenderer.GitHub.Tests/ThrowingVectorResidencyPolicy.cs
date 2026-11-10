using MarkdownRenderer.Layout.Internal.Inlines;
using MarkdownRenderer.Layout.Internal.LazyLayout;
using MarkdownRenderer.Layout.Internal.Placement;
using MarkdownRenderer.Performance;
using MarkdownRenderer.Performance.Internal.Residency;

namespace MarkdownRenderer.GitHub.Tests;

/// <summary>
/// A vector residency policy whose Nth band replacement throws, to exercise lazy
/// commit rollback.
/// </summary>
internal sealed class ThrowingVectorResidencyPolicy : IMarkdownVectorSceneResidencyPolicy
{
    internal const string FailureMessage = "test residency preparation failure";
    private readonly int _throwOnCall;
    private int _replaceCallCount;

    internal ThrowingVectorResidencyPolicy(int throwOnCall)
        => _throwOnCall = throwOnCall;

    public int ActiveSceneCount => 0;
    public long ActiveEstimatedBytes => 0;
    public int InlineGeometryIndexBuildCount => 0;
    public int CommandGraphEstimateCount => 0;
    public int ActiveInlineSceneGeometryIndexCount => 0;
    public List<string> VisibleDeferredSceneMarkerScratch { get; } = new();

    public bool AreNativeResourcesPrepared(
        IReadOnlyList<int> topLevelOrdinals,
        LazyLayoutBand band,
        LayoutPlacementGeneration placements,
        IReadOnlyDictionary<int, int> topLevelOrdinalByBlockIndex)
        => false;

    public bool ReplaceActiveBand(
        IReadOnlyList<int> topLevelOrdinals,
        LazyLayoutBand band,
        LayoutPlacementGeneration placements,
        IReadOnlyDictionary<int, int> topLevelOrdinalByBlockIndex,
        CancellationToken cancellationToken)
    {
        if (++_replaceCallCount == _throwOnCall)
            throw new InvalidOperationException(FailureMessage);
        return false;
    }

    public bool TryAppendVisibleInlineSceneMarkers(
        InlineContainerBox inline,
        double viewportTop,
        double viewportBottom,
        Windows.Foundation.Rect? viewport,
        List<string> markerKeys,
        ref bool visibleSceneResourcesReady)
        => false;

    public void ReleaseUnpinnedResources(
        IReadOnlyList<int> topLevelOrdinals,
        LazyLayoutBand band,
        LayoutPlacementGeneration placements,
        IReadOnlyDictionary<int, int> topLevelOrdinalByBlockIndex)
    {
    }

    public void Dispose()
    {
    }
}
