namespace MarkdownRenderer.Layout.Internal.Text;

/// <summary>
/// Pure-logic helpers for finding word and line boundaries in an inline text buffer.
/// Extracted as a static class so the algorithm can be unit-tested independently
/// of Win2D / WinUI infrastructure.
/// </summary>
internal static class TextBoundaryHelper
{
    /// <summary>Returns the UTF-16 range of the extended grapheme cluster at an offset.</summary>
    public static (int Start, int End) FindTextElementBoundaries(string buffer, int charIndex)
    {
        return new TextElementBoundaryIndex(buffer).FindBoundaries(charIndex);
    }

    /// <summary>Moves to the next extended grapheme-cluster boundary.</summary>
    public static int FindNextTextElementStart(string buffer, int charIndex)
    {
        return new TextElementBoundaryIndex(buffer).FindNextStart(charIndex);
    }

    /// <summary>Moves to the previous extended grapheme-cluster boundary.</summary>
    public static int FindPreviousTextElementStart(string buffer, int charIndex)
    {
        return new TextElementBoundaryIndex(buffer).FindPreviousStart(charIndex);
    }

    /// <summary>
    /// Moves across extended grapheme-cluster boundaries while parsing the
    /// boundary table only once, even for a large UI Automation move count.
    /// </summary>
    public static int MoveByTextElements(
        string buffer,
        int charIndex,
        int count,
        out int moved)
    {
        return new TextElementBoundaryIndex(buffer).Move(charIndex, count, out moved);
    }

    /// <summary>
    /// Returns the [start, end) char offsets of the word that contains (or is nearest to)
    /// the given <paramref name="charIndex"/> in <paramref name="buffer"/>.
    /// A "word" is a maximal sequence of Unicode word characters. Punctuation
    /// and whitespace are treated as boundaries, while combining marks stay
    /// attached to their base character through text-element segmentation.
    /// Non-whitespace Unicode symbols and emoji are returned as their own
    /// complete grapheme; whitespace and punctuation keep nearest-word behavior.
    /// </summary>
    public static (int Start, int End) FindWordBoundaries(string buffer, int charIndex)
    {
        return new TextElementBoundaryIndex(buffer).FindWordBoundaries(charIndex);
    }

    public static int FindNextWordStart(string buffer, int charIndex)
    {
        return new TextElementBoundaryIndex(buffer).FindNextWordStart(charIndex);
    }

    public static int FindPreviousWordStart(string buffer, int charIndex)
    {
        return new TextElementBoundaryIndex(buffer).FindPreviousWordStart(charIndex);
    }
}
