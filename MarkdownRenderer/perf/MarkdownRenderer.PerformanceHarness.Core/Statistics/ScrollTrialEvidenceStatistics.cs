namespace MarkdownRenderer.PerformanceHarness.Core.Statistics;

public readonly record struct ScrollTrialEvidenceStatistics(
    double UiThreadWorkP95Milliseconds,
    double UiThreadWorkP99Milliseconds,
    double FrameTimeP95Milliseconds,
    double FramesOver16_7Percent,
    double MaximumFrameStallMilliseconds,
    double RendererOwnedAllocatedBytesPerFrameP95,
    long MaximumRendererOwnedAllocatedBytesPerFrame);
