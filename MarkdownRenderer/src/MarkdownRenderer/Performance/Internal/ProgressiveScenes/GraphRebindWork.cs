using MarkdownRenderer.Layout.Internal.LazyLayout;
using MarkdownRenderer.Layout.Internal.Snapshots;
using MarkdownRenderer.Performance.Internal.Residency;

namespace MarkdownRenderer.Performance.Internal.ProgressiveScenes;

/// <summary>One registered graph-rebind producer and the snapshot band it targets.</summary>
internal sealed class GraphRebindWork(
    ActiveSceneWork work, LayoutSnapshot snapshot, MarkdownVectorSceneResidencyPolicy policy,
    VectorScenePreparation preparation, long revision, LazyLayoutBand band)
{
    internal readonly ActiveSceneWork Work = work;
    internal readonly LayoutSnapshot Snapshot = snapshot;
    internal readonly MarkdownVectorSceneResidencyPolicy Policy = policy;
    internal readonly VectorScenePreparation Preparation = preparation;
    internal readonly long Revision = revision;
    internal readonly LazyLayoutBand Band = band;
    internal volatile bool WasCanceled;
}
