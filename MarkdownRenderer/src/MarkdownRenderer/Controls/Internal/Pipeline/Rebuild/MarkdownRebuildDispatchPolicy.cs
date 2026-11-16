namespace MarkdownRenderer.Controls.Internal.Pipeline.Rebuild;

/// <summary>Chooses rebuild dispatch priority and scene generation advancement.</summary>
internal static class MarkdownRebuildDispatchPolicy
{
    // Materializing one progressive scene does not invalidate sibling scene work.
    internal static bool AdvancesDeferredSceneGeneration(bool sceneMaterialized) => !sceneMaterialized;

    internal static MarkdownRebuildDispatchPriority SelectPriority(bool requiresFullRebuild) =>
        requiresFullRebuild
            ? MarkdownRebuildDispatchPriority.Normal
            : MarkdownRebuildDispatchPriority.Low;
}
