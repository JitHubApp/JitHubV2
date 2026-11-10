using System;
using Microsoft.UI.Xaml.Automation.Text;
using MarkdownRenderer.Layout;

namespace MarkdownRenderer.Accessibility.Internal.TextRanges;

/// <summary>
/// Character, word, line, paragraph and document boundaries over the semantic
/// text, as UIA text-unit moves and expansions see them.
/// </summary>
internal static class MarkdownTextUnitNavigation
{
    internal static int MoveOffset(
        string text,
        TextElementBoundaryIndex textElements,
        int offset,
        TextUnit unit,
        int count,
        out int moved)
    {
        if (unit == TextUnit.Character)
            return textElements.Move(offset, count, out moved);

        moved = 0;
        int current = Math.Clamp(offset, 0, text.Length);
        int direction = Math.Sign(count);
        long steps = Math.Min(Math.Abs((long)count), (long)text.Length + 1);
        for (long i = 0; i < steps; i++)
        {
            int next = unit switch
            {
                TextUnit.Word => direction > 0
                    ? textElements.FindNextWordStart(current)
                    : textElements.FindPreviousWordStart(current),
                TextUnit.Line or TextUnit.Paragraph => direction > 0 ? NextLineStart(text, current) : PreviousLineStart(text, current),
                TextUnit.Document => direction > 0 ? text.Length : 0,
                _ => current,
            };
            next = Math.Clamp(next, 0, text.Length);
            if (next == current) break;
            current = next;
            moved += direction;
        }

        return current;
    }

    internal static int UnitStart(
        string text,
        TextElementBoundaryIndex textElements,
        int offset,
        TextUnit unit)
    {
        offset = Math.Clamp(offset, 0, text.Length);
        if (offset >= text.Length) return text.Length;

        return unit switch
        {
            TextUnit.Character => textElements.FindBoundaries(offset).Start,
            TextUnit.Word => textElements.FindWordBoundaries(offset).Start,
            TextUnit.Line or TextUnit.Paragraph => FindLineBoundaries(text, offset).Start,
            TextUnit.Document => 0,
            _ => offset,
        };
    }

    internal static int UnitEnd(
        string text,
        TextElementBoundaryIndex textElements,
        int start,
        TextUnit unit)
    {
        start = Math.Clamp(start, 0, text.Length);
        if (start >= text.Length) return text.Length;

        return unit switch
        {
            TextUnit.Character => textElements.FindBoundaries(start).End,
            TextUnit.Word => textElements.FindWordBoundaries(start).End,
            TextUnit.Line or TextUnit.Paragraph => FindLineBoundaries(text, start).End,
            TextUnit.Document => text.Length,
            _ => start,
        };
    }

    internal static TextUnit NormalizeSupportedTextUnit(TextUnit unit) =>
        unit == TextUnit.Page ? TextUnit.Document : unit;

    internal static (int Start, int End) FindLineBoundaries(string text, int offset)
    {
        if (text.Length == 0) return (0, 0);
        offset = Math.Clamp(offset, 0, text.Length);
        int start = offset;
        while (start > 0 && text[start - 1] != '\n') start--;
        int end = offset;
        while (end < text.Length && text[end] != '\n') end++;
        return (start, end);
    }

    private static int NextLineStart(string text, int offset)
    {
        int i = Math.Clamp(offset, 0, text.Length);
        while (i < text.Length && text[i] != '\n') i++;
        return Math.Min(text.Length, i + 1);
    }

    private static int PreviousLineStart(string text, int offset)
    {
        int i = Math.Clamp(offset - 1, 0, Math.Max(0, text.Length - 1));
        while (i > 0 && text[i - 1] != '\n') i--;
        return i;
    }
}
