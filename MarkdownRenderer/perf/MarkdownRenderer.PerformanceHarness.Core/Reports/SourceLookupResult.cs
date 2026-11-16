namespace MarkdownRenderer.PerformanceHarness.Core.Reports;

public sealed class SourceLookupResult
{
    public int MappingCount { get; init; }
    public int QueryCount { get; init; }
    public long StopwatchFrequency { get; init; }
    public string MeasurementThreadAffinityMask { get; init; } = string.Empty;
    public int MeasurementProcessorGroup { get; init; }
    public int MeasurementProcessorNumber { get; init; }
    public string MeasurementThreadPriority { get; init; } = string.Empty;
    public List<long> SamplesElapsedTicks { get; init; } = [];
    public double P95Nanoseconds { get; init; }
    public double P95BudgetNanoseconds { get; init; } = 25_000;
    public long AllocatedBytes { get; init; }
    public int RegressionWarmupPasses { get; init; }
    public int RegressionBatchSize { get; init; }
    public int RegressionObservationCount { get; init; }
    public int RegressionOperationCount { get; init; }
    public double RegressionP95Nanoseconds { get; init; }
    public long RegressionAllocatedBytes { get; init; }
    public string RegressionEstimator { get; init; } = string.Empty;
    public int RegressionTrialCount { get; init; }
    public List<SourceLookupTrialResult> RegressionTrials { get; init; } = [];
    public bool Passed { get; init; }
}
