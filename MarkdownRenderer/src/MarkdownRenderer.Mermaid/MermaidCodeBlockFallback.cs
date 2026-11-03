namespace MarkdownRenderer.Mermaid;

/// <summary>The content to show atomically when a native diagram cannot be rendered.</summary>
internal sealed record MermaidCodeBlockFallback(string Language, string Source);
