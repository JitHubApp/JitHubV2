namespace MarkdownRenderer.Document;

/// <summary>Summary information for a footnote definition.</summary>
/// <param name="Label">Footnote label from the markdown source.</param>
/// <param name="DisplayText">Rendered footnote definition text.</param>
/// <param name="SourceSpan">Span of the footnote definition in the markdown source.</param>
/// <param name="BlockIndex">One-based block index assigned during document traversal.</param>
/// <param name="Order">Markdig display order when available.</param>
public sealed record MarkdownFootnote(string Label, string DisplayText, SourceSpan SourceSpan, int BlockIndex, int Order);
