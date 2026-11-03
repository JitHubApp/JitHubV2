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

    /// <summary>Clears the scene generations and the pending geometry.</summary>
    internal void ResetGeometry()
    {
        SceneGeometryGeneration = 0;
        PendingSceneGeometryGeneration = 0;
        PendingSceneGeometry = Array.Empty<InlineSceneGeometry>();
    }

    /// <summary>Clears the vector-run counters before the owner recounts its runs.</summary>
    internal void ResetRunCounters()
    {
        VectorSceneRunCount = 0;
        HasDeferredSceneMarker = false;
    }

    /// <summary>
    /// Drops the pending geometry handed off for <paramref name="generation"/>
    /// once its consumer has indexed it; a stale generation is ignored.
    /// </summary>
    /// <param name="generation">The scene generation the consumer captured.</param>
    internal void ReleasePendingGeometry(long generation)
    {
        if (generation == SceneGeometryGeneration)
        {
            PendingSceneGeometry = Array.Empty<InlineSceneGeometry>();
            PendingSceneGeometryGeneration = 0;
        }
    }
}
