namespace MarkdownRenderer.Controls;

/// <summary>
/// Internal wall-clock stages of the last committed initial presentation.
/// Parse includes extension work; setup includes theme/context and work-plan
/// construction; layout includes worker scheduling; UI publication includes
/// realization and invalidation, but not the first paint. Publication's five
/// phases partition that wall interval so slow commits can be attributed.
/// </summary>
internal readonly record struct MarkdownPipelineTimingSnapshot(
    long Generation,
    long SourceUtf16Bytes,
    double ParseMilliseconds,
    double SetupMilliseconds,
    double LayoutMilliseconds,
    double PublicationMilliseconds,
    double CommitMilliseconds,
    double OverlayResetMilliseconds,
    double PlanConstructionMilliseconds,
    double VisibleRealizationMilliseconds,
    double EmbedRealizationMilliseconds,
    double HighlightSchedulingMilliseconds,
    double HighlightRetirementMilliseconds,
    double HighlightBandSchedulingMilliseconds,
    double AdornmentFocusMilliseconds,
    double FinalNotificationMilliseconds);
