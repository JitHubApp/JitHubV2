namespace MarkdownRenderer.PerformanceHarness;

/// <summary>
/// One first-viewport condition (source size and cache mode) and the warmup
/// and measured trials recorded for it.
/// </summary>
internal sealed class FirstViewportScenario(
    int condition,
    int sourceBytes,
    string source,
    string mode,
    long parseCacheBudgetBytes)
{
    internal int Condition { get; } = condition;
    internal int SourceBytes { get; } = sourceBytes;
    internal string Source { get; } = source;
    internal string Mode { get; } = mode;
    internal long ParseCacheBudgetBytes { get; } = parseCacheBudgetBytes;
    internal List<FirstViewportTrialResult> WarmupTrials { get; } = [];
    internal List<FirstViewportTrialResult> Trials { get; } = [];
}
