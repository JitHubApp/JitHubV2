namespace MarkdownRenderer.Mermaid.Internal.Rendering;

/// <summary>A stable, backend-neutral Mermaid diagnostic.</summary>
internal sealed record MermaidDiagnostic(
    string Code,
    MermaidDiagnosticSeverity Severity,
    string Message,
    MermaidSourceRange? SourceRange = null);
