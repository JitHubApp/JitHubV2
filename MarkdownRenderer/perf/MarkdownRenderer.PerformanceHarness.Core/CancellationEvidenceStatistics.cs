namespace MarkdownRenderer.PerformanceHarness;

public sealed record CancellationEvidenceStatistics(
    double WarmupP95Milliseconds,
    double PooledP95Milliseconds,
    double RegressionP95Milliseconds,
    double RegressionDispersionPercent,
    int WarmupStaleCommits,
    int StaleCommits);
