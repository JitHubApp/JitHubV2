namespace MarkdownRenderer.Document;

/// <summary>
/// Logical selection range in the rendered markdown document.
/// </summary>
/// <param name="Start">Range start position.</param>
/// <param name="End">Range end position.</param>
public readonly record struct DocumentRange(DocumentPosition Start, DocumentPosition End)
{
    /// <summary>Gets whether the range contains no characters.</summary>
    public bool IsEmpty => Start.CompareTo(End) == 0;

    /// <summary>Returns the range with start and end sorted in document order.</summary>
    public DocumentRange Normalized() => Start <= End ? this : new DocumentRange(End, Start);

    /// <summary>Gets an empty range.</summary>
    public static DocumentRange Empty => new(DocumentPosition.Zero, DocumentPosition.Zero);
}
