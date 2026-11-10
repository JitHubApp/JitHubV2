using System;
using System.Threading;
using MarkdownRenderer.Performance.Internal.CompletedScenes;

namespace MarkdownRenderer.Performance.Internal.ProgressiveScenes;

/// <summary>
/// The progressive scene plan's active-work state, as seen by its publisher.
/// Members ending in NoLock are called under the plan gate that the publisher
/// shares; the others take it themselves.
/// </summary>
internal interface IDeferredScenePublicationHost
{
    /// <summary>Whether this producer is still the marker's active, visible work.</summary>
    bool IsVisible(string marker, ActiveSceneWork activeWork);

    /// <summary>Whether a prepared result for this producer may still be published.</summary>
    bool IsPublicationCurrentNoLock(
        string marker,
        ActiveSceneWork activeWork,
        CancellationToken generationToken);

    /// <summary>Retires a producer that ends without publishing, retrying the band if it was superseded.</summary>
    void CompleteWithoutPublication(
        string marker,
        ActiveSceneWork activeWork,
        Action? onRetryVisibleBand);

    /// <summary>Restores a completed result from its weak descriptor on a worker thread.</summary>
    bool TryRestoreOnWorker(
        string marker,
        CancellationToken cancellationToken,
        out MarkdownDeferredSceneResult? result,
        out MarkdownCompletedSceneDescriptor? descriptor);
}
