namespace MarkdownRenderer.PerformanceHarness;

public sealed class RegressionComparison
{
    public string Metric { get; init; } = string.Empty;
    public string Kind { get; init; } = string.Empty;
    public double Reference { get; init; }
    public double Candidate { get; init; }
    public double AbsoluteDelta { get; init; }
    public double DeltaPercent { get; init; }
    public double BudgetPercent { get; init; }
    public double RelativeAllowance { get; init; }
    public double AbsoluteNoiseFloor { get; init; }
    public double AllowedAbsoluteDelta { get; init; }
    public double ReferenceDispersionPercent { get; init; }
    public double CandidateDispersionPercent { get; init; }
    public double StabilityBudgetPercent { get; init; }
    public string Decision { get; init; } = string.Empty;
    public bool Passed { get; init; }
}
