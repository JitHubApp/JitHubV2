using MarkdownRenderer.PerformanceHarness.Core.Configuration;

namespace MarkdownRenderer.PerformanceHarness.Core.Reports;

public sealed class ScrollResult
{
    public string Corpus { get; init; } = string.Empty;
    public int SourceUtf16Bytes { get; init; }
    public long StopwatchFrequency { get; init; }
    public int RequestedFrames { get; init; }
    public int MeasuredFrameIntervals { get; init; }
    public int FramesWithRendererWork { get; init; }
    public string RegressionEstimator { get; init; } = string.Empty;
    public List<ScrollTrialResult> Trials { get; init; } = [];
    public double RegressionUiThreadWorkP95Milliseconds { get; init; }
    public double RegressionUiThreadWorkP99Milliseconds { get; init; }
    public double RegressionFrameTimeP95Milliseconds { get; init; }
    public double RegressionRendererOwnedAllocatedBytesPerFrameP95 { get; init; }
    public double UiThreadWorkP95Milliseconds { get; init; }
    public double UiThreadWorkP99Milliseconds { get; init; }
    public double ScrollCallbackWorkP95Milliseconds { get; init; }
    public double PaintCallbackWorkP95Milliseconds { get; init; }
    public double UiThreadWorkP95BudgetMilliseconds { get; init; } = 4;
    public double UiThreadWorkP99BudgetMilliseconds { get; init; } = 6;
    public double FrameTimeP95Milliseconds { get; init; }
    public double FrameTimeP95BudgetMilliseconds { get; init; } =
        PerformanceMeasurementContract.ScrollFrameTimeP95BudgetMilliseconds;
    public double FramesOver16_7Percent { get; init; }
    public double FramesOver16_7PercentBudget { get; init; } = 1;
    public double MaximumFrameStallMilliseconds { get; init; }
    public double MaximumFrameStallBudgetMilliseconds { get; init; } = 50;
    public double AllocatedBytesPerFrameP95 { get; init; }
    public long MaximumAllocatedBytesPerFrame { get; init; }
    public string RawAllocationScope { get; init; } =
        "all managed allocations observed on the UI thread inside renderer scroll and paint callbacks";
    public string AllocationGateBasis { get; init; } = "renderer-owned-managed";
    public double RendererOwnedAllocatedBytesPerFrameP95 { get; init; }
    public long MaximumRendererOwnedAllocatedBytesPerFrame { get; init; }
    public double PlatformProjectionAllocatedBytesPerFrameP95 { get; init; }
    public long MaximumPlatformProjectionAllocatedBytesPerFrame { get; init; }
    public string PlatformProjectionAllocationScope { get; init; } =
        "managed projection allocations measured around WinUI and Win2D calls; retained separately from renderer-owned allocations";
    public double ScrollAllocatedBytesPerFrameP95 { get; init; }
    public double PaintAllocatedBytesPerFrameP95 { get; init; }
    public double ScrollLazyLayoutAllocatedBytesP95 { get; init; }
    public double ScrollAdornerAllocatedBytesP95 { get; init; }
    public double ScrollRealizationAllocatedBytesP95 { get; init; }
    public double ScrollHighlightingAllocatedBytesP95 { get; init; }
    public double PaintSchedulingAllocatedBytesP95 { get; init; }
    public double PaintPlatformSessionAllocatedBytesP95 { get; init; }
    public double SnapshotPaintAllocatedBytesP95 { get; init; }
    public double InteractivePaintAllocatedBytesP95 { get; init; }
    public double InlinePaintAllocatedBytesP95 { get; init; }
    public double TablePaintAllocatedBytesP95 { get; init; }
    public double CodePaintAllocatedBytesP95 { get; init; }
    public double OtherPaintAllocatedBytesP95 { get; init; }
    public long AllocationBudgetBytesPerFrame { get; init; } = 4096;
    public bool LohAllocationPossible { get; init; }
    public bool RendererOwnedLohAllocationPossible { get; init; }
    public long MaximumRealizedCodeActions { get; init; }
    public long MaximumOffscreenRealizedCodeActions { get; init; }
    public int Gen2Collections { get; init; }
    public bool Passed { get; init; }
}
