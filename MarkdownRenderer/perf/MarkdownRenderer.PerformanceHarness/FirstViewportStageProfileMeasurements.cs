using MarkdownRenderer.Controls;

namespace MarkdownRenderer.PerformanceHarness;

/// <summary>
/// The diagnostic counters captured around one profiled first-viewport
/// presentation.
/// </summary>
internal sealed record FirstViewportStageProfileMeasurements(
    MarkdownPipelineTimingSnapshot Timing,
    long ProcessAllocatedBytes,
    long? UiThreadAllocatedBytes,
    FirstViewportStageProfileThreadMetrics? ThreadMetrics);
