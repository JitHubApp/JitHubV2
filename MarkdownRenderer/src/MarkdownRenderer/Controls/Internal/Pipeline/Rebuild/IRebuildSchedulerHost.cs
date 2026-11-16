using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;
using RebuildReason = MarkdownRenderer.Controls.Internal.Pipeline.Rebuild.MarkdownRebuildCoordinatorReason;

namespace MarkdownRenderer.Controls.Internal.Pipeline.Rebuild;

/// <summary>What rebuild scheduling needs from the control.</summary>
internal interface IRebuildSchedulerHost
{
    DispatcherQueue? DispatcherQueue { get; }

    bool IsLoaded { get; }

    /// <summary>The markdown source the next pipeline renders.</summary>
    string EffectiveSource { get; }

    /// <summary>Advances the performance session's deferred scene generation, tracking its retirement.</summary>
    void AdvanceDeferredSceneGeneration();

    /// <summary>Cancels lazy layout and image relayout of the published snapshot.</summary>
    void CancelSnapshotWork();

    /// <summary>Captures the touch selection restore intent and resets transient interaction state.</summary>
    void ResetInteractionForRebuild();

    /// <summary>Forgets a touch selection restore intent that no published snapshot will honour.</summary>
    void DiscardTouchSelectionRestore();

    Task RunRebuildAsync(
        CancellationToken ct,
        RebuildReason reason,
        long generation,
        long pipelineStartTimestamp,
        long sourceUtf16Bytes);
}
