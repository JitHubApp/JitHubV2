namespace MarkdownRenderer.PerformanceHarness;

public sealed class MeasurementVisibilitySample
{
    public long ElapsedTicks { get; init; }
    public string Source { get; init; } = string.Empty;
    public bool CaptureSucceeded { get; init; }
    public bool TargetWasForeground { get; init; }
    public bool TargetWasVisible { get; init; }
    public bool TargetWasMinimized { get; init; }
    public bool TargetWasCloaked { get; init; }
    public bool TargetWasWithinWorkArea { get; init; }
    public bool TargetWasUnoccluded { get; init; }
}
