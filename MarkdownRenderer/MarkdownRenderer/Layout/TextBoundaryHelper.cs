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
    // Sparse text commonly contains only a handful of multi-code-unit graphemes.
    // Keep that case proportional to the exceptional clusters rather than the
    // total number of text elements. Highly clustered inputs retain the original
    // dense StringInfo representation, with a hard bound on sparse metadata.
    private const int MaximumSparseMultiUnitElements = 4096;

    private readonly string _buffer;
    private readonly int[] _starts;
    private readonly MultiUnitElement[] _multiUnitElements;
    private readonly bool _hasImplicitSingleUnitBoundaries;

    public TextElementBoundaryIndex(string? buffer)
    {
        _buffer = buffer ?? string.Empty;
        TextLength = _buffer.Length;

        if (TryBuildSparseMultiUnitElements(_buffer, out MultiUnitElement[] sparseElements))
        {
            _starts = Array.Empty<int>();
            _multiUnitElements = sparseElements;
            _hasImplicitSingleUnitBoundaries = sparseElements.Length == 0;
        }
        else
        {
            // The number of exceptional graphemes exceeded the bounded sparse
            // representation. Preserve StringInfo as the authority for this
            // dense case rather than growing an unbounded exception table.
            _starts = StringInfo.ParseCombiningCharacters(_buffer);
            _multiUnitElements = Array.Empty<MultiUnitElement>();
            _hasImplicitSingleUnitBoundaries = false;
        }
    }

    public readonly int TextLength;

    public (int Start, int End) FindBoundaries(int charIndex)
    {
        if (TextLength == 0) return (0, 0);
        int offset = Math.Clamp(charIndex, 0, TextLength);
        if (offset == TextLength)
            return (TextLength, TextLength);
        if (_hasImplicitSingleUnitBoundaries)
            return (offset, offset + 1);
        int start = TextElementStartAtOrBefore(offset);
        int end = Math.Min(TextLength, NextStart(start));
        return (start, end);
    }

    public int FindNextStart(int charIndex)
    {
        if (TextLength == 0) return 0;
        int offset = Math.Clamp(charIndex, 0, TextLength);
        if (offset >= TextLength) return TextLength;
        if (_hasImplicitSingleUnitBoundaries)
            return offset + 1;
        int start = TextElementStartAtOrBefore(offset);
        return Math.Min(TextLength, NextStart(start));
    }

    public int FindPreviousStart(int charIndex)
    {
        if (TextLength == 0) return 0;
        int offset = Math.Clamp(charIndex, 0, TextLength);
        if (offset <= 0) return 0;
        if (_hasImplicitSingleUnitBoundaries)
            return offset - 1;
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
        if (_hasImplicitSingleUnitBoundaries)
        {
            long requestedAsciiOffset = (long)offset + count;
            int targetAsciiOffset = (int)Math.Clamp(requestedAsciiOffset, 0L, TextLength);
            moved = targetAsciiOffset - offset;
            return targetAsciiOffset;
        }

        if (_multiUnitElements.Length != 0)
        {
            int elementCount = TextLength - _multiUnitElements[^1].CumulativeExtraUnits;
            int currentSparseIndex = offset == TextLength
                ? elementCount
                : SparseElementOrdinalAtOrBefore(offset);
            long requestedSparseIndex = (long)currentSparseIndex + count;
            int targetSparseIndex = (int)Math.Clamp(requestedSparseIndex, 0L, elementCount);
            moved = targetSparseIndex - currentSparseIndex;
            return targetSparseIndex == elementCount
                ? TextLength
                : SparseBoundaryOffsetAtOrdinal(targetSparseIndex);
        }

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
        if (_hasImplicitSingleUnitBoundaries)
            return Math.Clamp(charIndex, 0, TextLength - 1);
        if (_multiUnitElements.Length != 0)
        {
            int sparseIndex = LastMultiUnitElementStartingAtOrBefore(charIndex);
            if (sparseIndex < 0)
                return charIndex;

            MultiUnitElement element = _multiUnitElements[sparseIndex];
            if (charIndex < element.End)
                return element.Start;
            // This method returns a UTF-16 position, not an element ordinal.
            // After the exceptional grapheme, each later single-unit element
            // begins at its original buffer offset.
            return charIndex;
        }

        int index = Array.BinarySearch(_starts, charIndex);
        if (index >= 0) return _starts[index];
        index = ~index - 1;
        return _starts[Math.Clamp(index, 0, _starts.Length - 1)];
    }

    private int NextStart(int currentStart)
    {
        if (_hasImplicitSingleUnitBoundaries)
            return currentStart < TextLength ? currentStart + 1 : int.MaxValue;
        if (_multiUnitElements.Length != 0)
        {
            if (currentStart >= TextLength)
                return int.MaxValue;
            int normalizedStart = TextElementStartAtOrBefore(currentStart);
            int sparseIndex = FindMultiUnitElementAtExactStart(normalizedStart);
            return sparseIndex >= 0
                ? _multiUnitElements[sparseIndex].End
                : normalizedStart + 1;
        }

        int index = Array.BinarySearch(_starts, currentStart);
        if (index < 0) index = ~index - 1;
        return index + 1 < _starts.Length ? _starts[index + 1] : int.MaxValue;
    }

    private int PreviousStart(int currentStart)
    {
        if (currentStart == TextLength)
        {
            if (_hasImplicitSingleUnitBoundaries)
                return TextLength - 1;
            if (_multiUnitElements.Length != 0)
            {
                MultiUnitElement last = _multiUnitElements[^1];
                return last.End == TextLength ? last.Start : TextLength - 1;
            }
            return _starts[^1];
        }

        if (_hasImplicitSingleUnitBoundaries)
            return currentStart > 0 ? currentStart - 1 : -1;
        if (_multiUnitElements.Length != 0)
        {
            int currentOrdinal = SparseElementOrdinalAtOrBefore(currentStart);
            return currentOrdinal > 0
                ? SparseBoundaryOffsetAtOrdinal(currentOrdinal - 1)
                : -1;
        }

        int index = Array.BinarySearch(_starts, currentStart);
        if (index < 0) index = ~index - 1;
        return index > 0 ? _starts[index - 1] : -1;
    }

    private int SparseElementOrdinalAtOrBefore(int charIndex)
    {
        int index = LastMultiUnitElementStartingAtOrBefore(charIndex);
        if (index < 0)
            return charIndex;

        MultiUnitElement element = _multiUnitElements[index];
        int extraBefore = element.CumulativeExtraUnits - (element.End - element.Start - 1);
        if (charIndex < element.End)
            return element.Start - extraBefore;
        return charIndex - element.CumulativeExtraUnits;
    }

    private int SparseBoundaryOffsetAtOrdinal(int ordinal)
    {
        int low = 0;
        int high = _multiUnitElements.Length;
        while (low < high)
        {
            int middle = low + ((high - low) >> 1);
            MultiUnitElement element = _multiUnitElements[middle];
            int extraBefore = element.CumulativeExtraUnits - (element.End - element.Start - 1);
            int elementOrdinal = element.Start - extraBefore;
            if (elementOrdinal < ordinal)
                low = middle + 1;
            else
                high = middle;
        }

        int cumulativeExtra = low == 0 ? 0 : _multiUnitElements[low - 1].CumulativeExtraUnits;
        return ordinal + cumulativeExtra;
    }

    private int LastMultiUnitElementStartingAtOrBefore(int offset)
    {
        int low = 0;
        int high = _multiUnitElements.Length;
        while (low < high)
        {
            int middle = low + ((high - low) >> 1);
            if (_multiUnitElements[middle].Start <= offset)
                low = middle + 1;
            else
                high = middle;
        }
        return low - 1;
    }

    private int LowerBoundMultiUnitElementStart(int offset)
    {
        int low = 0;
        int high = _multiUnitElements.Length;
        while (low < high)
        {
            int middle = low + ((high - low) >> 1);
            if (_multiUnitElements[middle].Start < offset)
                low = middle + 1;
            else
                high = middle;
        }
        return low;
    }

    private int FindMultiUnitElementAtExactStart(int offset)
    {
        int index = LowerBoundMultiUnitElementStart(offset);
        return index < _multiUnitElements.Length && _multiUnitElements[index].Start == offset
            ? index
            : -1;
    }

    private static bool TryBuildSparseMultiUnitElements(
        string text,
        out MultiUnitElement[] elements)
    {
        MultiUnitElement[] collected = Array.Empty<MultiUnitElement>();
        int count = 0;
        int cumulativeExtraUnits = 0;
        ReadOnlySpan<char> span = text.AsSpan();

        for (int start = 0; start < span.Length;)
        {
            char current = span[start];
            int elementLength;
            if (current <= 0x7f &&
                !(current == '\r' && start + 1 < span.Length && span[start + 1] == '\n') &&
                (start + 1 >= span.Length || span[start + 1] <= 0x7f))
            {
                // Ordinary ASCII runs (including isolated ASCII controls) have
                // one-code-unit StringInfo boundaries. CRLF and ASCII followed
                // by a possible combining/format character use StringInfo.
                elementLength = 1;
            }
            else
            {
                elementLength = StringInfo.GetNextTextElementLength(span[start..]);
            }

            if (elementLength <= 0 || elementLength > span.Length - start)
            {
                elements = Array.Empty<MultiUnitElement>();
                return false;
            }

            if (elementLength > 1)
            {
                if (count == MaximumSparseMultiUnitElements)
                {
                    elements = Array.Empty<MultiUnitElement>();
                    return false;
                }

                if (count == collected.Length)
                {
                    int newCapacity = Math.Min(
                        MaximumSparseMultiUnitElements,
                        collected.Length == 0 ? 8 : collected.Length * 2);
                    Array.Resize(ref collected, newCapacity);
                }

                cumulativeExtraUnits += elementLength - 1;
                collected[count++] = new MultiUnitElement(
                    start,
                    elementLength,
                    cumulativeExtraUnits);
            }

            start += elementLength;
        }

        if (count == 0)
        {
            elements = Array.Empty<MultiUnitElement>();
            return true;
        }

        if (count != collected.Length)
            Array.Resize(ref collected, count);
        elements = collected;
        return true;
    }

    private readonly struct MultiUnitElement
    {
        public MultiUnitElement(int start, int length, int cumulativeExtraUnits)
        {
            Start = start;
            End = start + length;
            CumulativeExtraUnits = cumulativeExtraUnits;
        }

        public readonly int Start;
        public readonly int End;
        public readonly int CumulativeExtraUnits;
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
