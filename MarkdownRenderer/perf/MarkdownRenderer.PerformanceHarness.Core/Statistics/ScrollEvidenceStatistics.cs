namespace MarkdownRenderer.PerformanceHarness.Core.Statistics;

public sealed record ScrollEvidenceStatistics(
    IReadOnlyList<ScrollTrialEvidenceStatistics> Trials,
    double UiThreadWorkP95Milliseconds,
    double UiThreadWorkP99Milliseconds,
    double FrameTimeP95Milliseconds,
    double FramesOver16_7Percent,
    double MaximumFrameStallMilliseconds,
    double RendererOwnedAllocatedBytesPerFrameP95,
    long MaximumRendererOwnedAllocatedBytesPerFrame,
    bool RendererOwnedLohAllocationPossible,
    double RegressionUiThreadWorkP95Milliseconds,
    double RegressionUiThreadWorkP99Milliseconds,
    double RegressionFrameTimeP95Milliseconds,
    double RegressionRendererOwnedAllocatedBytesPerFrameP95);
