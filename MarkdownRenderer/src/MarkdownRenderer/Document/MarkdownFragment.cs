namespace MarkdownRenderer.Document;

/// <summary>Summary information for a generic-attribute fragment target.</summary>
/// <param name="Id">Fragment id without the leading '#'.</param>
/// <param name="SourceSpan">Span of the attributed element in the markdown source.</param>
/// <param name="BlockIndex">One-based block index assigned during document traversal.</param>
public sealed record MarkdownFragment(string Id, SourceSpan SourceSpan, int BlockIndex);
