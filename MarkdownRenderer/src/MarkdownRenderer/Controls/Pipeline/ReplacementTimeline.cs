namespace MarkdownRenderer.Controls.Pipeline;

/// <summary>
/// The raw timestamps that partition one snapshot replacement's UI
/// publication into phases. Conversion to durations waits until a timing
/// snapshot is read, keeping the commit path allocation-light.
/// </summary>
internal sealed class ReplacementTimeline
{
    internal ReplacementTimeline(long publicationStarted) => PublicationStarted = publicationStarted;

    internal long PublicationStarted { get; }
    internal long CommitEnded { get; set; }
    internal long OverlayResetEnded { get; set; }
    internal long PlanConstructionEnded { get; set; }
    internal long EmbedRealizationEnded { get; set; }
    internal long HighlightRetirementEnded { get; set; }
    internal long HighlightSchedulingEnded { get; set; }
    internal long VisibleRealizationEnded { get; set; }
    internal long PublicationEnded { get; set; }
}
