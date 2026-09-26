namespace MarkdownRenderer.Controls;

/// <summary>
/// Internal wall-clock stages of the last committed initial presentation.
/// Parse includes extension work; setup includes theme/context and work-plan
/// construction; layout includes worker scheduling; UI publication includes
/// realization and invalidation, but not the first paint. Publication's five
/// phases partition that wall interval so slow commits can be attributed.
/// Raw publication timestamps defer audit-only conversion until evidence
/// capture, keeping the normal UI commit path allocation-free and small.
/// </summary>
internal readonly struct MarkdownPipelineTimingSnapshot
{
    internal readonly long Generation;
    internal readonly long SourceUtf16Bytes;
    internal readonly double ParseMilliseconds;
    internal readonly double SetupMilliseconds;
    internal readonly double ThemeSnapshotMilliseconds;
    internal readonly double LayoutMilliseconds;
    internal readonly long PublicationStartedTimestamp;
    internal readonly long CommitEndedTimestamp;
    internal readonly long OverlayResetEndedTimestamp;
    internal readonly long PlanConstructionEndedTimestamp;
    internal readonly long EmbedRealizationEndedTimestamp;
    internal readonly long HighlightRetirementEndedTimestamp;
    internal readonly long HighlightSchedulingEndedTimestamp;
    internal readonly long VisibleRealizationEndedTimestamp;
    internal readonly long PublicationEndedTimestamp;

    internal MarkdownPipelineTimingSnapshot(
        long generation,
        long sourceUtf16Bytes,
        double parseMilliseconds,
        double setupMilliseconds,
        double themeSnapshotMilliseconds,
        double layoutMilliseconds,
        long publicationStartedTimestamp,
        long commitEndedTimestamp,
        long overlayResetEndedTimestamp,
        long planConstructionEndedTimestamp,
        long embedRealizationEndedTimestamp,
        long highlightRetirementEndedTimestamp,
        long highlightSchedulingEndedTimestamp,
        long visibleRealizationEndedTimestamp,
        long publicationEndedTimestamp)
    {
        Generation = generation;
        SourceUtf16Bytes = sourceUtf16Bytes;
        ParseMilliseconds = parseMilliseconds;
        SetupMilliseconds = setupMilliseconds;
        ThemeSnapshotMilliseconds = themeSnapshotMilliseconds;
        LayoutMilliseconds = layoutMilliseconds;
        PublicationStartedTimestamp = publicationStartedTimestamp;
        CommitEndedTimestamp = commitEndedTimestamp;
        OverlayResetEndedTimestamp = overlayResetEndedTimestamp;
        PlanConstructionEndedTimestamp = planConstructionEndedTimestamp;
        EmbedRealizationEndedTimestamp = embedRealizationEndedTimestamp;
        HighlightRetirementEndedTimestamp = highlightRetirementEndedTimestamp;
        HighlightSchedulingEndedTimestamp = highlightSchedulingEndedTimestamp;
        VisibleRealizationEndedTimestamp = visibleRealizationEndedTimestamp;
        PublicationEndedTimestamp = publicationEndedTimestamp;
    }
}
