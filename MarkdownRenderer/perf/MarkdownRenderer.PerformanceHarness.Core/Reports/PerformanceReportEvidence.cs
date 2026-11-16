namespace MarkdownRenderer.PerformanceHarness.Core.Reports;

public readonly record struct PerformanceReportEvidence(
    PerformanceReport Report,
    string Sha256);
