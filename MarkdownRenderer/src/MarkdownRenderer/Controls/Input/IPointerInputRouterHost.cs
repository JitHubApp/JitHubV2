using MarkdownRenderer.Layout;

namespace MarkdownRenderer.Controls.Input;

/// <summary>
/// The renderer services <see cref="PointerInputRouter"/> calls while it
/// admits, queues and completes pointer input.
/// </summary>
internal interface IPointerInputRouterHost
{
    /// <summary>
    /// Requests geometry recovery when <paramref name="snapshot"/> needs it.
    /// Returns true when recovery was requested and the input must not run.
    /// </summary>
    bool RequestGeometryRecoveryIfRequired(LayoutSnapshot? snapshot);

    /// <summary>Resumes a rebuild that waited for pointer interaction to end.</summary>
    void ResumePendingRebuildAfterPointerInteraction();

    /// <summary>
    /// Clears the selection because input outside the selection gesture took
    /// over. <paramref name="resetDeferredPointerInput"/> is false when the
    /// router is already resetting its queue.
    /// </summary>
    void ClearSelectionForExternalInteraction(bool resetClickTracking, bool resetDeferredPointerInput);

    /// <summary>
    /// Runs a link or disclosure activation that a gesture recorded during
    /// its snapshot lease.
    /// </summary>
    bool ActivateLinkIntent(
        LinkActivationIntent intent,
        LayoutSnapshot snapshot,
        long snapshotGeneration,
        long layoutRevision);
}
