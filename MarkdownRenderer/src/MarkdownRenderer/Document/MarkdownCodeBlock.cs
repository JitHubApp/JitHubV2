namespace MarkdownRenderer.Document;

/// <summary>Summary information for a code block in a markdown document.</summary>
/// <param name="DisplayText">Code text displayed for the block.</param>
/// <param name="SourceSpan">Span of the code block in the markdown source.</param>
/// <param name="BlockIndex">One-based block index assigned during document traversal.</param>
/// <param name="Language">Optional fenced-code language identifier.</param>
public sealed record MarkdownCodeBlock(string DisplayText, SourceSpan SourceSpan, int BlockIndex, string? Language);
