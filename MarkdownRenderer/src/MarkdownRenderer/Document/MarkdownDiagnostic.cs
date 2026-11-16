namespace MarkdownRenderer.Document;

/// <summary>
/// Describes a parser or profile validation result without exposing parser-specific types.
/// </summary>
/// <param name="Code">Stable machine-readable diagnostic code.</param>
/// <param name="Severity">Diagnostic severity.</param>
/// <param name="Message">Human-readable diagnostic message.</param>
/// <param name="SourceSpan">Half-open UTF-16 source span associated with the diagnostic.</param>
public sealed record MarkdownDiagnostic(
    string Code,
    MarkdownDiagnosticSeverity Severity,
    string Message,
    SourceSpan SourceSpan);
