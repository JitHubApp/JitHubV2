using System.Globalization;

namespace MarkdownRenderer.Mermaid;

/// <summary>A stable, backend-neutral Mermaid diagnostic.</summary>
internal sealed record MermaidDiagnostic(
    string Code,
    MermaidDiagnosticSeverity Severity,
    string Message,
    MermaidSourceRange? SourceRange = null);
