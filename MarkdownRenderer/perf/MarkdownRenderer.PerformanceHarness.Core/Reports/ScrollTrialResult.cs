namespace MarkdownRenderer.PerformanceHarness.Core.Reports;

public sealed class ScrollTrialResult
{
    public int Ordinal { get; init; }
    public long FirstLogicalFrameId { get; init; }
    public int RequestedFrames { get; init; }
    public int MeasuredFrameIntervals { get; init; }
    public int FramesWithRendererWork { get; init; }
    public List<long> UiThreadWorkElapsedTicks { get; init; } = [];
    public List<long> FrameIntervalElapsedTicks { get; init; } = [];
    public List<long> RendererOwnedAllocatedBytesPerFrame { get; init; } = [];
    public double UiThreadWorkP95Milliseconds { get; init; }
    public double UiThreadWorkP99Milliseconds { get; init; }
    public double ScrollCallbackWorkP95Milliseconds { get; init; }
    public double PaintCallbackWorkP95Milliseconds { get; init; }
    public double FrameTimeP95Milliseconds { get; init; }
    public double FramesOver16_7Percent { get; init; }
    public double MaximumFrameStallMilliseconds { get; init; }
    public double AllocatedBytesPerFrameP95 { get; init; }
    public long MaximumAllocatedBytesPerFrame { get; init; }
    public double RendererOwnedAllocatedBytesPerFrameP95 { get; init; }
    public long MaximumRendererOwnedAllocatedBytesPerFrame { get; init; }
    public double PlatformProjectionAllocatedBytesPerFrameP95 { get; init; }
    public long MaximumPlatformProjectionAllocatedBytesPerFrame { get; init; }
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
    public long MaximumRealizedCodeActions { get; init; }
    public long MaximumOffscreenRealizedCodeActions { get; init; }
    public int Gen2Collections { get; init; }
    public bool Complete { get; init; }
}
