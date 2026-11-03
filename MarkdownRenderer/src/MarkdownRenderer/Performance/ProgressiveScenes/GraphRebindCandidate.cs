using MarkdownRenderer.Layout;
using MarkdownRenderer.Layout.Boxes;

namespace MarkdownRenderer.Performance.ProgressiveScenes;

/// <summary>A detached vector-scene owner and the preparation captured with it.</summary>
internal readonly struct GraphRebindCandidate(VectorSceneBox box, VectorScenePreparation preparation)
{
    internal readonly VectorSceneBox Box = box;
    internal readonly VectorScenePreparation Preparation = preparation;
}
