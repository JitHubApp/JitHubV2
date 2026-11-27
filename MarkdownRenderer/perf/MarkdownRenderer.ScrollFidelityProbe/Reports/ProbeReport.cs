namespace MarkdownRenderer.ScrollFidelityProbe.Reports;

/// <summary>The probe's JSON report.</summary>
internal sealed class ProbeReport
{
    public required string Label { get; init; }
    public DateTimeOffset StartedUtc { get; init; }
    public int CorpusUtf16Characters { get; set; }
    public bool PerformanceSession { get; init; }
    public double RasterizationScale { get; set; }
    public int ViewportWidthPx { get; set; }
    public int ViewportHeightPx { get; set; }
    public int CalibrationLongestUniformRunPx { get; set; }
    public int BlankRunThresholdPx { get; set; }
    public long CapturedFrames { get; set; }
    public double PeakWorkingSetMb { get; set; }
    public double PrivateMemoryMbAfterCalibration { get; set; }
    public List<ScenarioResult> Scenarios { get; } = [];
    public List<string> Failures { get; } = [];
}
