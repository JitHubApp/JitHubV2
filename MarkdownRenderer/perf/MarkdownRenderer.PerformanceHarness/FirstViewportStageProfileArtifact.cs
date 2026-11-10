using MarkdownRenderer.PerformanceHarness.Core.Reports;

namespace MarkdownRenderer.PerformanceHarness;

/// <summary>
/// Separate, opt-in, non-gating diagnostic output for attributing a selected
/// first-viewport pipeline. This deliberately does not extend the release
/// report schema.
/// </summary>
internal sealed class FirstViewportStageProfileArtifact
{
    internal const int CurrentSchemaVersion = 2;
    internal const string ProfileKind = "markdown-first-viewport-stage-profile";

    public int SchemaVersion { get; init; }
    public string Kind { get; init; } = string.Empty;
    public string EvidenceClassification { get; init; } = string.Empty;
    public string BuildIdentity { get; init; } = string.Empty;
    public string PerformanceReportPath { get; init; } = string.Empty;
    public BuildArtifactHashes BuildArtifacts { get; init; } = new();
    public DateTimeOffset StartedUtc { get; init; }
    public DateTimeOffset CompletedUtc { get; init; }
    public int SourceUtf16Bytes { get; init; }
    public string Corpus { get; init; } = string.Empty;
    public bool LayoutDiagnosticsEnabled { get; init; }
    public string AllocationScope { get; init; } = string.Empty;
    public List<FirstViewportStageProfileSample> Presentations { get; init; } = [];
}
