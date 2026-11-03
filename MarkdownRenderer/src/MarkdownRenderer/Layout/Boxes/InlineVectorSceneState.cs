using System;

namespace MarkdownRenderer.Layout.Boxes;

/// <summary>
/// Vector-scene counters and pending geometry of one inline container.
/// Text-only boxes never allocate it. It is mutable and owned by exactly
/// one <see cref="InlineContainerBox"/>; private placement clones create
/// their own instance rather than sharing the source's.
/// </summary>
internal sealed class InlineVectorSceneState
{
    internal InlineSceneGeometry[] PendingSceneGeometry = Array.Empty<InlineSceneGeometry>();
    internal long SceneGeometryGeneration;
    internal long PendingSceneGeometryGeneration;
    internal int SceneCharacterRegionQueryCount;
    internal int VectorSceneRunCount;
    internal bool HasDeferredSceneMarker;
}
