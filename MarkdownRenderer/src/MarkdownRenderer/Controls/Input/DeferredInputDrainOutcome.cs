using MarkdownRenderer.Layout.Internal.LazyLayout;

namespace MarkdownRenderer.Controls.Input;

/// <summary>
/// Why one <see cref="DeferredInputReplay"/> drain pass ended. It is read after
/// the pass's snapshot lease has ended, to run post-interaction work and to
/// decide whether the queue waits, drains on or is dropped.
/// </summary>
internal struct DeferredInputDrainOutcome
{
    /// <summary>The input whose context menu or post-interaction work runs next, if any.</summary>
    internal PointerInput? PostInteractionInput;

    /// <summary>The queue head waits for its hit-test band to be measured.</summary>
    internal bool WaitingForGeometry;

    /// <summary>The queue head waits for the next layout publication.</summary>
    internal bool WaitingForLayoutPublication;

    /// <summary>The snapshot or generation the queue was hit-tested against is gone.</summary>
    internal bool StaleQueue;

    /// <summary>The band the waiting gesture needs measured.</summary>
    internal LazyLayoutBand WaitingBand;
}
