namespace MarkdownRenderer.PerformanceHarness;

public readonly record struct FirstViewportStageProfileThreadMetrics(
    double? UiThreadCpuMilliseconds,
    int? ProcessGen0Collections,
    int? ProcessGen1Collections,
    int? ProcessGen2Collections);
