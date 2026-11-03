namespace MarkdownRenderer.Controls;

/// <summary>Why a rebuild was requested, in merge precedence order.</summary>
internal enum MarkdownRebuildCoordinatorReason
{
    // Keep the values ordered by merge precedence; SceneMaterialized is the
    // neutral (lowest-priority) queued reason, then Restyle, then Full.
    SceneMaterialized = 0,
    Restyle = 1,
    Full = 2,
}
