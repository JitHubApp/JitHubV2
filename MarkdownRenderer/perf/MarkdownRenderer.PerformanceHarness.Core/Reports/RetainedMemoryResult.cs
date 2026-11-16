namespace MarkdownRenderer.PerformanceHarness.Core.Reports;

public sealed class RetainedMemoryResult
{
    public string Corpus { get; init; } = string.Empty;
    public int SourceUtf16Bytes { get; init; }
    public long ManagedDeltaBytes { get; init; }
    public long PrivateDeltaBytes { get; init; }
    public long BudgetBytes { get; init; }
    public bool Passed { get; init; }
}
