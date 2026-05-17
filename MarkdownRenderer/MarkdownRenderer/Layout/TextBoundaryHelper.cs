using System;
using System.Globalization;

namespace MarkdownRenderer.Layout;

/// <summary>
/// Pure-logic helpers for finding word and line boundaries in an inline text buffer.
/// Extracted as a static class so the algorithm can be unit-tested independently
/// of Win2D / WinUI infrastructure.
/// </summary>
internal static class TextBoundaryHelper
{
    /// <summary>
    /// Returns the [start, end) char offsets of the word that contains (or is nearest to)
    /// the given <paramref name="charIndex"/> in <paramref name="buffer"/>.
    /// A "word" is a maximal sequence of Unicode word characters. Punctuation
    /// and whitespace are treated as boundaries, while combining marks stay
    /// attached to their base character through text-element segmentation.
    /// </summary>
    public static (int Start, int End) FindWordBoundaries(string buffer, int charIndex)
    {
        if (string.IsNullOrEmpty(buffer)) return (0, 0);
        var starts = StringInfo.ParseCombiningCharacters(buffer);
        int idx = TextElementStartAtOrBefore(starts, Math.Clamp(charIndex, 0, Math.Max(0, buffer.Length - 1)));

        // If the cursor landed on a boundary character, snap left or right to a word.
        if (!IsWordTextElement(buffer, idx))
        {
            int previous = PreviousTextElementStart(starts, idx);
            if (previous >= 0 && IsWordTextElement(buffer, previous))
            {
                idx = previous;
            }
            else
            {
                int fwd = Math.Min(buffer.Length, NextTextElementStart(starts, idx));
                while (fwd < buffer.Length && !IsWordTextElement(buffer, fwd))
                    fwd = Math.Min(buffer.Length, NextTextElementStart(starts, fwd));

                if (fwd < buffer.Length)
                    idx = fwd;
                else
                    return (idx, idx);
            }
        }

        // Walk left to the start of the word.
        int start = idx;
        while (true)
        {
            int previous = PreviousTextElementStart(starts, start);
            if (previous < 0 || !IsWordTextElement(buffer, previous)) break;
            start = previous;
        }

        // Walk right to the end of the word.
        int end = Math.Min(buffer.Length, NextTextElementStart(starts, idx));
        while (end < buffer.Length && IsWordTextElement(buffer, end))
            end = Math.Min(buffer.Length, NextTextElementStart(starts, end));

        return (start, end);
    }

    public static int FindNextWordStart(string buffer, int charIndex)
    {
        if (string.IsNullOrEmpty(buffer)) return 0;
        var starts = StringInfo.ParseCombiningCharacters(buffer);
        int idx = Math.Clamp(charIndex, 0, buffer.Length);
        if (idx >= buffer.Length) return buffer.Length;

        idx = TextElementStartAtOrBefore(starts, idx);
        if (IsWordTextElement(buffer, idx))
            idx = FindWordBoundaries(buffer, idx).End;

        while (idx < buffer.Length && !IsWordTextElement(buffer, idx))
            idx = Math.Min(buffer.Length, NextTextElementStart(starts, idx));

        return Math.Min(idx, buffer.Length);
    }

    public static int FindPreviousWordStart(string buffer, int charIndex)
    {
        if (string.IsNullOrEmpty(buffer)) return 0;
        var starts = StringInfo.ParseCombiningCharacters(buffer);
        int idx = Math.Clamp(charIndex, 0, buffer.Length);
        if (idx <= 0) return 0;

        idx = PreviousTextElementStart(starts, idx);
        while (idx >= 0 && !IsWordTextElement(buffer, idx))
            idx = PreviousTextElementStart(starts, idx);

        return idx < 0 ? 0 : FindWordBoundaries(buffer, idx).Start;
    }

    private static bool IsWordTextElement(string buffer, int start)
    {
        if (start < 0 || start >= buffer.Length) return false;
        return CharUnicodeInfo.GetUnicodeCategory(buffer, start) switch
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

    private static int TextElementStartAtOrBefore(int[] starts, int charIndex)
    {
        int index = Array.BinarySearch(starts, charIndex);
        if (index >= 0) return starts[index];
        index = ~index - 1;
        return starts[Math.Clamp(index, 0, starts.Length - 1)];
    }

    private static int NextTextElementStart(int[] starts, int currentStart)
    {
        int index = Array.BinarySearch(starts, currentStart);
        if (index < 0) index = ~index - 1;
        return index + 1 < starts.Length ? starts[index + 1] : int.MaxValue;
    }

    private static int PreviousTextElementStart(int[] starts, int currentStart)
    {
        int index = Array.BinarySearch(starts, currentStart);
        if (index < 0) index = ~index - 1;
        return index > 0 ? starts[index - 1] : -1;
    }
}
