namespace MarkdownRenderer.PerformanceHarness;

public sealed class SourceLookupTrialResult
{
    public int Ordinal { get; init; }
    public int ObservationCount { get; init; }
    public int OperationCount { get; init; }
    public List<long> SamplesElapsedTicks { get; init; } = [];
    public List<double> SamplesNanosecondsPerLookup { get; init; } = [];
    public double P95Nanoseconds { get; init; }
    public long AllocatedBytes { get; init; }
    // Optional diagnostic telemetry only; none of these fields participates in
    // the source-lookup acceptance verdict or dispersion calculation.
    [PerformanceDiagnosticOnly]
    public DateTimeOffset StartedUtc { get; init; }
    [PerformanceDiagnosticOnly]
    public DateTimeOffset CompletedUtc { get; init; }
    [PerformanceDiagnosticOnly]
    public long ElapsedStopwatchTicks { get; init; }
    [PerformanceDiagnosticOnly]
    public long ThreadCpuTime100NsTicks { get; init; } = -1;
    [PerformanceDiagnosticOnly]
    public ulong? ThreadCycleDelta { get; init; }
    [PerformanceDiagnosticOnly]
    public int Gen0Collections { get; init; }
    [PerformanceDiagnosticOnly]
    public int Gen1Collections { get; init; }
    [PerformanceDiagnosticOnly]
    public int Gen2Collections { get; init; }
    public bool Complete { get; init; }
}
