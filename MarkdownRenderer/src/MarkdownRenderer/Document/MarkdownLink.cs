namespace MarkdownRenderer.Document;

/// <summary>Summary information for a non-image link in a markdown document.</summary>
/// <param name="DisplayText">Text displayed for the link.</param>
/// <param name="SourceSpan">Span of the link in the markdown source.</param>
/// <param name="BlockIndex">One-based block index assigned during document traversal.</param>
/// <param name="Url">Resolved link URL text from the markdown source.</param>
/// <param name="Title">Optional link title.</param>
public sealed record MarkdownLink(string DisplayText, SourceSpan SourceSpan, int BlockIndex, string Url, string? Title);
