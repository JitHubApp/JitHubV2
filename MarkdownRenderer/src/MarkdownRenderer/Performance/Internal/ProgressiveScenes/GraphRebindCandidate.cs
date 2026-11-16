using MarkdownRenderer.Layout.Internal.Vectors;
using MarkdownRenderer.Performance.Internal.Residency;

namespace MarkdownRenderer.Performance.Internal.ProgressiveScenes;

/// <summary>A detached vector-scene owner and the preparation captured with it.</summary>
internal readonly struct GraphRebindCandidate(VectorSceneBox box, VectorScenePreparation preparation)
{
    internal readonly VectorSceneBox Box = box;
    internal readonly VectorScenePreparation Preparation = preparation;
}
