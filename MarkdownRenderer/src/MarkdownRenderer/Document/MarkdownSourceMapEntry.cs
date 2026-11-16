namespace MarkdownRenderer.Document;

/// <summary>
/// Maps a semantic document element to a half-open UTF-16 source span.
/// </summary>
/// <param name="Kind">Semantic element kind.</param>
/// <param name="SourceSpan">Half-open UTF-16 span in the document source.</param>
/// <param name="BlockIndex">One-based block index assigned during document traversal.</param>
public sealed record MarkdownSourceMapEntry(
    MarkdownSourceElementKind Kind,
    SourceSpan SourceSpan,
    int BlockIndex);
