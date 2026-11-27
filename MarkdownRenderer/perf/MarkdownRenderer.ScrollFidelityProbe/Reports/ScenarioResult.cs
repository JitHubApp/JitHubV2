namespace MarkdownRenderer.ScrollFidelityProbe.Reports;

/// <summary>
/// Unrendered-area statistics for one scenario. Blank percentages are the
/// share of the viewport's rows inside uniform bands longer than the
/// calibrated threshold, per presented frame.
/// </summary>
internal sealed class ScenarioResult
{
    public required string Name { get; init; }
    public required string Description { get; init; }
    public double FirstRenderMs { get; init; }
    public int Frames { get; init; }
    public double DurationMs { get; init; }
    public double BlankMeanPercent { get; init; }
    public double BlankP50Percent { get; init; }
    public double BlankP95Percent { get; init; }
    public double BlankP99Percent { get; init; }
    public double BlankMaxPercent { get; init; }
    public double FramesWithBlankPercent { get; init; }
    public double BlankViewportSeconds { get; init; }
    public double ScrollVelocityP50DipsPerSecond { get; init; }
    public double ScrollVelocityP95DipsPerSecond { get; init; }
    public double UiTickP50Ms { get; init; }
    public double UiTickP95Ms { get; init; }
    public double UiTickP99Ms { get; init; }
    public double UiTickMaxMs { get; init; }
    public int UiTicksOver33Ms { get; init; }
    public List<double> JumpFillMs { get; init; } = [];
    public List<double[]> Series { get; init; } = [];
}
