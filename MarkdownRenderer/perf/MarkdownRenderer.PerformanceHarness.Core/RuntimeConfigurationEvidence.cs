namespace MarkdownRenderer.PerformanceHarness;

public sealed class RuntimeConfigurationEvidence
{
    public string Policy { get; init; } = string.Empty;
    public bool TieredCompilationEnabled { get; init; }
    public bool TieredPgoEnabled { get; init; }
    public bool ConcurrentGcEnabled { get; init; }
    public bool ReadyToRunEnabled { get; init; }
    public List<string> OverrideEnvironmentVariables { get; init; } = [];
    public bool Passed { get; init; }
}
