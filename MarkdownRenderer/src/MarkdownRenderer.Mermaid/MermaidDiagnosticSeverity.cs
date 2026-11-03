using System.Globalization;

namespace MarkdownRenderer.Mermaid;

/// <summary>Severity attached to a parser, layout, or scene diagnostic.</summary>
internal enum MermaidDiagnosticSeverity
{
    Information,
    Warning,
    Error,
}
