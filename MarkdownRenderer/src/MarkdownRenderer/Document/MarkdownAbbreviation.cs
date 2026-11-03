namespace MarkdownRenderer.Document;

/// <summary>Summary information for an abbreviation occurrence.</summary>
/// <param name="DisplayText">Abbreviation text shown in the document.</param>
/// <param name="SourceSpan">Span of the abbreviation occurrence in the markdown source.</param>
/// <param name="BlockIndex">One-based block index assigned during document traversal.</param>
/// <param name="Expansion">Expanded abbreviation text.</param>
public sealed record MarkdownAbbreviation(string DisplayText, SourceSpan SourceSpan, int BlockIndex, string Expansion);
