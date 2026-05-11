using System;

namespace MarkdownRenderer.Layout;

/// <summary>
/// Pure-logic helpers for finding word and line boundaries in an inline text buffer.
/// Extracted as a static class so the algorithm can be unit-tested independently
/// of Win2D / WinUI infrastructure.
/// </summary>
public static class TextBoundaryHelper
{
    /// <summary>
    /// Returns the [start, end) char offsets of the word that contains (or is nearest to)
    /// the given <paramref name="charIndex"/> in <paramref name="buffer"/>.
    /// A "word" is a maximal sequence of non-whitespace characters.
    /// </summary>
    public static (int Start, int End) FindWordBoundaries(string buffer, int charIndex)
    {
        if (string.IsNullOrEmpty(buffer)) return (0, 0);
        int idx = Math.Clamp(charIndex, 0, Math.Max(0, buffer.Length - 1));

        // If the cursor landed on whitespace, snap left or right to a word.
        if (idx < buffer.Length && char.IsWhiteSpace(buffer[idx]))
        {
            if (idx > 0 && !char.IsWhiteSpace(buffer[idx - 1]))
                idx--;  // prefer the word just to the left
            else
            {
                while (idx < buffer.Length && char.IsWhiteSpace(buffer[idx]))
                    idx++;
            }
        }

        // Walk left to the start of the word.
        int start = idx;
        while (start > 0 && !char.IsWhiteSpace(buffer[start - 1]))
            start--;

        // Walk right to the end of the word.
        int end = idx;
        while (end < buffer.Length && !char.IsWhiteSpace(buffer[end]))
            end++;

        return (start, end);
    }
}
