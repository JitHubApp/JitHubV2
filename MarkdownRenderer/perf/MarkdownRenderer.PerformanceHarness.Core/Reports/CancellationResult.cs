using MarkdownRenderer.PerformanceHarness.Core.Scenarios;

namespace MarkdownRenderer.PerformanceHarness.Core.Reports;

public sealed class CancellationResult
{
    public int RequestedIterations { get; init; }
    public int IterationsPerTrial { get; init; }
    public int WarmupIterations { get; init; }
    public double WarmupElapsedMilliseconds { get; init; }
    public int WarmupStaleCommits { get; init; }
    public int ObservedCancellations { get; init; }
    public string TrialStartPolicy { get; init; } = string.Empty;
    public string RegressionEstimator { get; init; } = string.Empty;
    public List<CancellationTrialResult> Trials { get; init; } = [];
    public List<double> SamplesMilliseconds { get; init; } = [];
    public double P95Milliseconds { get; init; }
    public double RegressionP95Milliseconds { get; init; }
    public double P95BudgetMilliseconds { get; init; } =
        CancellationMeasurementPlan.AbsoluteBudgetMilliseconds;
    public int StaleCommits { get; init; }
    public bool Passed { get; init; }
}
