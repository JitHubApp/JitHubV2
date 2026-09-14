using System;
using System.Globalization;

namespace MarkdownRenderer.Layout;

/// <summary>
/// Immutable UTF-16 index of extended grapheme-cluster starts. A semantic
/// document owns one instance so repeated UI Automation character navigation
/// stays logarithmic and allocation-free instead of reparsing the full text.
/// </summary>
internal sealed class TextElementBoundaryIndex
{
    private readonly string _buffer;
    private readonly int[] _starts;

    public TextElementBoundaryIndex(string? buffer)
    {
        _buffer = buffer ?? string.Empty;
        TextLength = _buffer.Length;
        _starts = TextLength == 0
            ? Array.Empty<int>()
            : StringInfo.ParseCombiningCharacters(_buffer);
    }

    public int TextLength { get; }

    public (int Start, int End) FindBoundaries(int charIndex)
    {
        if (TextLength == 0) return (0, 0);
        int offset = Math.Clamp(charIndex, 0, TextLength);
        if (offset == TextLength)
            return (TextLength, TextLength);
        int start = TextElementStartAtOrBefore(offset);
        int end = Math.Min(TextLength, NextStart(start));
        return (start, end);
    }

    public int FindNextStart(int charIndex)
    {
        if (TextLength == 0) return 0;
        int offset = Math.Clamp(charIndex, 0, TextLength);
        if (offset >= TextLength) return TextLength;
        int start = TextElementStartAtOrBefore(offset);
        return Math.Min(TextLength, NextStart(start));
    }

    public int FindPreviousStart(int charIndex)
    {
        if (TextLength == 0) return 0;
        int offset = Math.Clamp(charIndex, 0, TextLength);
        if (offset <= 0) return 0;
        int current = offset == TextLength
            ? TextLength
            : TextElementStartAtOrBefore(offset);
        int previous = PreviousStart(current);
        return previous >= 0 ? previous : 0;
    }

    public int Move(int charIndex, int count, out int moved)
    {
        moved = 0;
        if (TextLength == 0 || count == 0)
            return Math.Clamp(charIndex, 0, TextLength);

        int offset = Math.Clamp(charIndex, 0, TextLength);
        int currentIndex;
        if (offset == TextLength)
        {
            currentIndex = _starts.Length;
        }
        else
        {
            int found = Array.BinarySearch(_starts, offset);
            currentIndex = found >= 0 ? found : Math.Max(0, ~found - 1);
        }

        long requestedIndex = (long)currentIndex + count;
        int targetIndex = (int)Math.Clamp(requestedIndex, 0L, _starts.Length);
        moved = targetIndex - currentIndex;
        return targetIndex == _starts.Length ? TextLength : _starts[targetIndex];
    }

    public (int Start, int End) FindWordBoundaries(int charIndex)
    {
        if (TextLength == 0) return (0, 0);
        int idx = TextElementStartAtOrBefore(
            Math.Clamp(charIndex, 0, Math.Max(0, TextLength - 1)));

        // Prefer an adjacent word when the requested position is whitespace or
        // punctuation, matching the renderer's existing pointer behavior.
        if (!IsWordTextElement(idx))
        {
            int previous = PreviousStart(idx);
            if (previous >= 0 && IsWordTextElement(previous))
            {
                idx = previous;
            }
            else
            {
                int forward = Math.Min(TextLength, NextStart(idx));
                while (forward < TextLength && !IsWordTextElement(forward))
                    forward = Math.Min(TextLength, NextStart(forward));

                if (forward < TextLength)
                    idx = forward;
                else
                    return (idx, idx);
            }
        }

        int start = idx;
        while (true)
        {
            int previous = PreviousStart(start);
            if (previous < 0 || !IsWordTextElement(previous)) break;
            start = previous;
        }

        int end = Math.Min(TextLength, NextStart(idx));
        while (end < TextLength && IsWordTextElement(end))
            end = Math.Min(TextLength, NextStart(end));

        return (start, end);
    }

    public int FindNextWordStart(int charIndex)
    {
        if (TextLength == 0) return 0;
        int idx = Math.Clamp(charIndex, 0, TextLength);
        if (idx >= TextLength) return TextLength;

        idx = TextElementStartAtOrBefore(idx);
        if (IsWordTextElement(idx))
            idx = FindWordBoundaries(idx).End;

        while (idx < TextLength && !IsWordTextElement(idx))
            idx = Math.Min(TextLength, NextStart(idx));

        return Math.Min(idx, TextLength);
    }

    public int FindPreviousWordStart(int charIndex)
    {
        if (TextLength == 0) return 0;
        int idx = Math.Clamp(charIndex, 0, TextLength);
        if (idx <= 0) return 0;

        idx = PreviousStart(idx);
        while (idx >= 0 && !IsWordTextElement(idx))
            idx = PreviousStart(idx);

        return idx < 0 ? 0 : FindWordBoundaries(idx).Start;
    }

    private bool IsWordTextElement(int start)
    {
        if (start < 0 || start >= TextLength) return false;
        return CharUnicodeInfo.GetUnicodeCategory(_buffer, start) switch
        {
            UnicodeCategory.UppercaseLetter or
            UnicodeCategory.LowercaseLetter or
            UnicodeCategory.TitlecaseLetter or
            UnicodeCategory.ModifierLetter or
            UnicodeCategory.OtherLetter or
            UnicodeCategory.DecimalDigitNumber or
            UnicodeCategory.LetterNumber or
            UnicodeCategory.OtherNumber or
            UnicodeCategory.NonSpacingMark or
            UnicodeCategory.SpacingCombiningMark or
            UnicodeCategory.ConnectorPunctuation => true,
            _ => false,
        };
    }

    private int TextElementStartAtOrBefore(int charIndex)
    {
        int index = Array.BinarySearch(_starts, charIndex);
        if (index >= 0) return _starts[index];
        index = ~index - 1;
        return _starts[Math.Clamp(index, 0, _starts.Length - 1)];
    }

    private int NextStart(int currentStart)
    {
        int index = Array.BinarySearch(_starts, currentStart);
        if (index < 0) index = ~index - 1;
        return index + 1 < _starts.Length ? _starts[index + 1] : int.MaxValue;
    }

    private int PreviousStart(int currentStart)
    {
        int index = Array.BinarySearch(_starts, currentStart);
        if (index < 0) index = ~index - 1;
        return index > 0 ? _starts[index - 1] : -1;
    }
}

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
