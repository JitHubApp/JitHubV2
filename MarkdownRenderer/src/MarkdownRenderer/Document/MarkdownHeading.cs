namespace MarkdownRenderer.Document;

/// <summary>Summary information for a heading in a markdown document.</summary>
/// <param name="DisplayText">Text displayed for the heading.</param>
/// <param name="SourceSpan">Span of the heading in the markdown source.</param>
/// <param name="BlockIndex">One-based block index assigned during document traversal.</param>
/// <param name="Level">Heading level, from 1 through 6.</param>
public sealed record MarkdownHeading(string DisplayText, SourceSpan SourceSpan, int BlockIndex, int Level);
