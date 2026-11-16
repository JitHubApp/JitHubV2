using MarkdownRenderer.PerformanceHarness.Core.Configuration;

namespace MarkdownRenderer.PerformanceHarness.Core.Reports;

public sealed class RegressionResult
{
    public string Mode { get; init; } = string.Empty;
    public string? ReferencePath { get; init; }
    public string? ReferenceReportSha256 { get; init; }
    public string? ReferenceBuildIdentity { get; init; }
    public bool ReferenceEligible { get; init; }
    public bool MachineComparable { get; init; }
    public int ComparedLatencyMetrics { get; init; }
    public int ComparedAllocationMetrics { get; init; }
    public string DecisionPolicy { get; init; } =
        PerformanceMeasurementContract.RegressionDecisionPolicy;
    public double LatencyRegressionBudgetPercent { get; init; } = 5;
    public double AllocationRegressionBudgetPercent { get; init; } = 2;
    public List<RegressionComparison> Comparisons { get; init; } = [];
    public bool Passed { get; init; }
}
