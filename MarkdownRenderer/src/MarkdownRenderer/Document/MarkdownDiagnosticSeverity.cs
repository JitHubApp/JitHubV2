namespace MarkdownRenderer.Document;

/// <summary>Severity assigned to a markdown parse or validation diagnostic.</summary>
public enum MarkdownDiagnosticSeverity
{
    /// <summary>Informational guidance that does not affect rendering.</summary>
    Information,

    /// <summary>A recoverable issue that may change rendered output.</summary>
    Warning,

    /// <summary>An error that prevented part of the source from being interpreted.</summary>
    Error,
}
