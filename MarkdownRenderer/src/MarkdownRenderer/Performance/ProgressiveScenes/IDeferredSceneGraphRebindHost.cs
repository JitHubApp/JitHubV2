using System.Threading;

namespace MarkdownRenderer.Performance.ProgressiveScenes;

/// <summary>
/// The progressive scene plan's generation and retirement state, as seen by
/// its graph rebinder. Members ending in NoLock are read under the plan gate
/// that the rebinder shares.
/// </summary>
internal interface IDeferredSceneGraphRebindHost
{
    /// <summary>Whether the plan has retired.</summary>
    bool IsDisposedNoLock { get; }

    /// <summary>The plan's current relayout generation.</summary>
    long GenerationNoLock { get; }

    /// <summary>The current generation's cancellation token.</summary>
    CancellationToken GenerationTokenNoLock { get; }

    /// <summary>The current generation, read without the plan gate.</summary>
    long CurrentGeneration { get; }

    /// <summary>Restores a completed result from its weak descriptor on a worker thread.</summary>
    bool TryRestoreOnWorker(
        string marker,
        CancellationToken cancellationToken,
        out MarkdownDeferredSceneResult? result,
        out MarkdownCompletedSceneDescriptor? descriptor);
}
