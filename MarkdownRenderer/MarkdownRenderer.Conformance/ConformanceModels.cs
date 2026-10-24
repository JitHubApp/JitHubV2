using System.Text.Json.Serialization;

namespace MarkdownRenderer.Conformance;

public enum CorpusSourceFormat
{
    SpecJson,
    AnnotatedSpec,
}

public sealed record CorpusDefinition(
    string Id,
    string SpecificationVersion,
    Uri SpecificationUri,
    Uri SourceUri,
    string SourceSha256,
    CorpusSourceFormat SourceFormat,
    MarkdownProfile Profile);

public sealed record SpecExample(
    int Example,
    string Section,
    string Markdown,
    string Html,
    int StartLine,
    int EndLine)
{
    /// <summary>
    /// Gets the official per-example parser extensions declared after the
    /// annotated-spec fence. An empty list means the specification's core
    /// grammar; it does not mean that every product-profile extension is on.
    /// </summary>
    public IReadOnlyList<string> Extensions { get; init; } = Array.Empty<string>();
}

[JsonConverter(typeof(JsonStringEnumConverter<ConformanceStatus>))]
public enum ConformanceStatus
{
    Pass,
    Exception,
    Fail,
}

public sealed record ConformanceResult(
    string Suite,
    int Example,
    string Section,
    ConformanceStatus Status,
    string PlanSha256,
    string ExpectedHtmlSha256,
    string ActualHtmlSha256,
    string? Failure,
    string? Issue);

public sealed record ConformanceSummary(
    int Total,
    int Passed,
    int Excepted,
    int Failed);

public sealed record ConformanceReport(
    int SchemaVersion,
    string VerificationLevel,
    string Limitation,
    DateTimeOffset GeneratedAtUtc,
    IReadOnlyList<CorpusEvidence> Corpora,
    ConformanceSummary Summary,
    IReadOnlyList<ConformanceResult> Results);

public sealed record CorpusEvidence(
    string Id,
    string Version,
    string Specification,
    string Source,
    string Sha256,
    int Examples);

public sealed record ConformanceExceptionEntry(
    string Suite,
    int Example,
    string Issue,
    string Reason);

public sealed record ConformanceExceptionFile(
    int SchemaVersion,
    IReadOnlyList<ConformanceExceptionEntry> Entries);
