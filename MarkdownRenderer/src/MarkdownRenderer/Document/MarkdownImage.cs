namespace MarkdownRenderer.Document;

/// <summary>Summary information for an image in a markdown document.</summary>
/// <param name="DisplayText">Display text associated with the image, usually its alt text.</param>
/// <param name="SourceSpan">Span of the image in the markdown source.</param>
/// <param name="BlockIndex">One-based block index assigned during document traversal.</param>
/// <param name="Source">Image URL or data URI text from the markdown source.</param>
/// <param name="AltText">Image alt text.</param>
/// <param name="Title">Optional image title.</param>
/// <param name="IsInline">True when the image came from an inline image run.</param>
public sealed record MarkdownImage(
    string DisplayText,
    SourceSpan SourceSpan,
    int BlockIndex,
    string Source,
    string AltText,
    string? Title,
    bool IsInline);
