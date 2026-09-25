namespace MarkdownRenderer.Controls;

/// <summary>
/// Internal wall-clock stages of the last committed initial presentation.
/// Parse includes extension work; setup includes theme/context and work-plan
/// construction; layout includes worker scheduling; UI publication includes
/// realization and invalidation, but not the first paint.
/// </summary>
internal readonly record struct MarkdownPipelineTimingSnapshot(
    long Generation,
    long SourceUtf16Bytes,
    double ParseMilliseconds,
    double SetupMilliseconds,
    double LayoutMilliseconds,
    double PublicationMilliseconds);
