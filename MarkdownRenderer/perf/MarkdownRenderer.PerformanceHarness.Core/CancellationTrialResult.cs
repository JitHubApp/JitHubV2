namespace MarkdownRenderer.PerformanceHarness;

public sealed class CancellationTrialResult
{
    public int Ordinal { get; init; }
    public int WarmupSourceSeed { get; init; }
    public double WarmupElapsedMilliseconds { get; init; }
    public int WarmupStaleCommits { get; init; }
    public int FirstRecordedSourceSeed { get; init; }
    public int RequestedIterations { get; init; }
    public int ObservedCancellations { get; init; }
    public List<double> SamplesMilliseconds { get; init; } = [];
    public double P95Milliseconds { get; init; }
    public int StaleCommits { get; init; }
    public bool Complete { get; init; }
}
