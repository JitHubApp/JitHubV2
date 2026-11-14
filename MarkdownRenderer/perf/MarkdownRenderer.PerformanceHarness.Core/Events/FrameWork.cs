namespace MarkdownRenderer.PerformanceHarness.Core.Events;

public sealed record FrameWork
{
    public long UiThreadWorkElapsedTicks { get; set; }
    public double ElapsedMilliseconds { get; set; }
    public long AllocatedBytes { get; set; }
    public int ScrollCallbacks { get; set; }
    public int PaintCallbacks { get; set; }
    public double ScrollElapsedMilliseconds { get; set; }
    public double PaintElapsedMilliseconds { get; set; }
    public long ScrollAllocatedBytes { get; set; }
    public long PaintAllocatedBytes { get; set; }
    public long ScrollLazyLayoutAllocatedBytes { get; set; }
    public long ScrollAdornerAllocatedBytes { get; set; }
    public long ScrollRealizationAllocatedBytes { get; set; }
    public long ScrollHighlightingAllocatedBytes { get; set; }
    public long PaintSchedulingAllocatedBytes { get; set; }
    public long PaintPlatformSessionAllocatedBytes { get; set; }
    public long SnapshotPaintAllocatedBytes { get; set; }
    public long InteractivePaintAllocatedBytes { get; set; }
    public long InlinePaintAllocatedBytes { get; set; }
    public long TablePaintAllocatedBytes { get; set; }
    public long CodePaintAllocatedBytes { get; set; }
    public long OtherPaintAllocatedBytes { get; set; }
    public long MaximumRealizedCodeActions { get; set; }
    public long MaximumOffscreenCodeActions { get; set; }
}
