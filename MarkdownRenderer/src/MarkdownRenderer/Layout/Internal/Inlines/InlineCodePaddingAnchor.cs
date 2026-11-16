namespace MarkdownRenderer.Layout.Internal.Inlines;

/// <summary>
/// A text element and caret side at a visual outer edge of a
/// mixed-direction inline-code span.
/// </summary>
internal readonly struct InlineCodePaddingAnchor
{
    public InlineCodePaddingAnchor(int characterIndex, int characterCount, bool isLeading)
    {
        CharacterIndex = characterIndex;
        CharacterCount = characterCount;
        IsLeading = isLeading;
    }

    public int CharacterIndex { get; }
    public int CharacterCount { get; }
    public bool IsLeading { get; }
}
