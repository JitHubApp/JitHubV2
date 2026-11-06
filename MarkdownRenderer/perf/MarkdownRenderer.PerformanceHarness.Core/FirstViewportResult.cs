namespace MarkdownRenderer.PerformanceHarness;

public sealed class FirstViewportResult
{
    public string Corpus { get; init; } = string.Empty;
    public string Mode { get; init; } = string.Empty;
    public int SourceUtf16Bytes { get; init; }
    public long ParseCacheBudgetBytes { get; init; }
    public string EngineReuseMode { get; init; } = string.Empty;
    public string TrialStartPolicy { get; init; } = string.Empty;
    public string SchedulePolicy { get; init; } = string.Empty;
    public string RegressionEstimator { get; init; } = string.Empty;
    public string StationarityPolicy { get; init; } = string.Empty;
    public string MeasurementThreadAffinityMask { get; init; } = string.Empty;
    public int MeasurementProcessorGroup { get; init; }
    public int MeasurementProcessorNumber { get; init; }
    public string MeasurementThreadPriority { get; init; } = string.Empty;
    public List<FirstViewportTrialResult> WarmupTrials { get; init; } = [];
    public List<FirstViewportTrialResult> Trials { get; init; } = [];
    public List<double> SamplesMilliseconds { get; init; } = [];
    public double P95Milliseconds { get; init; }
    public List<double> PublicationSamplesMilliseconds { get; init; } = [];
    public double PublicationMaximumMilliseconds { get; init; }
    public double PublicationBudgetMilliseconds { get; init; }
    public double RegressionP95Milliseconds { get; init; }
    public double RegressionDispersionPercent { get; init; }
    public double TheilSenSlopeMillisecondsPerGlobalOrdinal { get; init; }
    public double ProjectedDriftMilliseconds { get; init; }
    public double WarmupBoundaryShiftMilliseconds { get; init; }
    public double StationarityAllowanceMilliseconds { get; init; }
    public bool StationarityEvaluated { get; init; }
    public bool StationarityPassed { get; init; }
    public double BudgetMilliseconds { get; init; }
    public bool Passed { get; init; }
}
