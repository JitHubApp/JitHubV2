namespace MarkdownRenderer.Document;

/// <summary>Summary information for a definition-list item.</summary>
/// <param name="Term">Definition term text.</param>
/// <param name="Definition">Definition description text.</param>
/// <param name="SourceSpan">Span of the definition item in the markdown source.</param>
/// <param name="BlockIndex">One-based block index assigned during document traversal.</param>
/// <param name="Marker">Definition marker character, usually ':' or '~'.</param>
public sealed record MarkdownDefinitionItem(string Term, string Definition, SourceSpan SourceSpan, int BlockIndex, char Marker);
