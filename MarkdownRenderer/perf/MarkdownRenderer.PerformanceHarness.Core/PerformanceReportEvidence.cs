namespace MarkdownRenderer.PerformanceHarness;

public readonly record struct PerformanceReportEvidence(
    PerformanceReport Report,
    string Sha256);
