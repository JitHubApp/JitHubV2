namespace MarkdownRenderer.PerformanceHarness;

public readonly record struct FirstViewportStageProfileObservation(
    long Sequence,
    long Generation,
    long SourceUtf16Bytes,
    FirstViewportStageProfileThreadMetrics Metrics);
