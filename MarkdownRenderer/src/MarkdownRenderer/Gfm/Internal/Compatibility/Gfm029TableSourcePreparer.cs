using System;
using System.Collections.Generic;

namespace MarkdownRenderer.Gfm.Internal.Compatibility;

/// <summary>
/// Rewrites pipe-table source so current Markdig recognizes exactly the
/// tables 0.29-gfm accepts: it appends the missing trailing pipe to body rows
/// and masks separator rows whose column count does not match the header.
/// The returned <see cref="Gfm029Compatibility.PreparedSource"/> records each
/// edit so parser coordinates can be mapped back to the authored source.
/// </summary>
internal static class Gfm029TableSourcePreparer
{
    internal static Gfm029Compatibility.PreparedSource Prepare(string source, bool enableTables)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!enableTables || source.Length == 0 || source.AsSpan().IndexOf('|') < 0)
            return Gfm029Compatibility.PreparedSource.Unchanged(source);

        List<Gfm029SourceLine> lines = GetLines(source);
        var insertAt = new HashSet<int>();
        var maskAt = new HashSet<int>();
        for (int lineIndex = 0; lineIndex + 1 < lines.Count; lineIndex++)
        {
            Gfm029SourceLine header = lines[lineIndex];
            Gfm029SourceLine separator = lines[lineIndex + 1];
            ReadOnlySpan<char> headerText = source.AsSpan(header.Start, header.Length);
            ReadOnlySpan<char> separatorText = source.AsSpan(separator.Start, separator.Length);
            int headerColumns = CountTableCells(headerText);
            if (headerColumns <= 0 ||
                !TryParseSeparatorRow(separatorText, out int separatorColumns, out int firstDash))
            {
                continue;
            }

            if (headerColumns != separatorColumns)
            {
                maskAt.Add(separator.Start + firstDash);
                lineIndex++;
                continue;
            }

            int bodyIndex = lineIndex + 2;
            for (; bodyIndex < lines.Count; bodyIndex++)
            {
                Gfm029SourceLine body = lines[bodyIndex];
                ReadOnlySpan<char> bodyText = source.AsSpan(body.Start, body.Length);
                if (bodyText.Trim().IsEmpty || StartsBlockBoundary(bodyText))
                    break;
                if (CountUnescapedPipes(bodyText) == 0)
                    insertAt.Add(body.Start + body.Length);
            }

            lineIndex = Math.Max(lineIndex + 1, bodyIndex - 1);
        }

        if (insertAt.Count == 0 && maskAt.Count == 0)
            return Gfm029Compatibility.PreparedSource.Unchanged(source);

        var transformed = new System.Text.StringBuilder(source.Length + insertAt.Count);
        var transformedInsertions = new List<int>(insertAt.Count);
        var transformedMasks = new List<int>(maskAt.Count);
        for (int index = 0; index <= source.Length; index++)
        {
            if (insertAt.Contains(index))
            {
                transformedInsertions.Add(transformed.Length);
                transformed.Append('|');
            }
            if (index < source.Length)
            {
                bool isMasked = maskAt.Contains(index);
                if (isMasked)
                    transformedMasks.Add(transformed.Length);
                transformed.Append(isMasked ? Gfm029Compatibility.MaskedTableSeparator : source[index]);
            }
        }

        return new Gfm029Compatibility.PreparedSource(
            transformed.ToString(),
            transformedInsertions.ToArray(),
            transformedMasks.ToArray());
    }

    private static List<Gfm029SourceLine> GetLines(string source)
    {
        var lines = new List<Gfm029SourceLine>();
        int start = 0;
        while (start < source.Length)
        {
            int end = start;
            while (end < source.Length && source[end] is not '\r' and not '\n')
                end++;
            lines.Add(new Gfm029SourceLine(start, end - start));
            if (end < source.Length && source[end] == '\r' &&
                end + 1 < source.Length && source[end + 1] == '\n')
            {
                end++;
            }
            start = end + 1;
        }

        if (source.Length == 0 || source[^1] is '\r' or '\n')
            lines.Add(new Gfm029SourceLine(source.Length, 0));
        return lines;
    }

    private static int CountTableCells(ReadOnlySpan<char> line)
    {
        int first = 0;
        while (first < line.Length && char.IsWhiteSpace(line[first]))
            first++;
        int last = line.Length - 1;
        while (last >= first && char.IsWhiteSpace(line[last]))
            last--;
        if (last < first)
            return 0;

        int pipes = CountUnescapedPipes(line);
        if (pipes == 0)
            return 0;
        bool leading = line[first] == '|' && !IsEscaped(line, first);
        bool trailing = line[last] == '|' && !IsEscaped(line, last);
        return pipes + 1 - (leading ? 1 : 0) - (trailing ? 1 : 0);
    }

    private static bool TryParseSeparatorRow(
        ReadOnlySpan<char> line,
        out int columns,
        out int firstDash)
    {
        columns = CountTableCells(line);
        firstDash = line.IndexOf('-');
        if (columns <= 0 || firstDash < 0)
            return false;

        int segmentStart = 0;
        int parsedColumns = 0;
        for (int index = 0; index <= line.Length; index++)
        {
            bool atDelimiter = index < line.Length &&
                line[index] == '|' &&
                !IsEscaped(line, index);
            if (!atDelimiter && index != line.Length)
                continue;

            ReadOnlySpan<char> segment = line[segmentStart..index].Trim();
            bool boundaryEmpty = segment.IsEmpty &&
                (segmentStart == 0 || index == line.Length);
            if (!boundaryEmpty)
            {
                if (!IsSeparatorCell(segment))
                    return false;
                parsedColumns++;
            }
            segmentStart = index + 1;
        }

        return parsedColumns == columns;
    }

    private static bool IsSeparatorCell(ReadOnlySpan<char> cell)
    {
        if (!cell.IsEmpty && cell[0] == ':')
            cell = cell[1..];
        if (!cell.IsEmpty && cell[^1] == ':')
            cell = cell[..^1];
        if (cell.IsEmpty)
            return false;
        foreach (char value in cell)
        {
            if (value != '-')
                return false;
        }
        return true;
    }

    private static int CountUnescapedPipes(ReadOnlySpan<char> line)
    {
        int count = 0;
        for (int index = 0; index < line.Length; index++)
        {
            if (line[index] == '|' && !IsEscaped(line, index))
                count++;
        }
        return count;
    }

    private static bool IsEscaped(ReadOnlySpan<char> line, int index)
    {
        int slashes = 0;
        for (int cursor = index - 1; cursor >= 0 && line[cursor] == '\\'; cursor--)
            slashes++;
        return (slashes & 1) != 0;
    }

    private static bool StartsBlockBoundary(ReadOnlySpan<char> line)
    {
        int indentation = 0;
        while (indentation < line.Length && line[indentation] == ' ')
            indentation++;
        if (indentation >= 4)
            return true;
        ReadOnlySpan<char> text = line[indentation..];
        if (text.IsEmpty || text[0] is '>' or '<')
            return true;
        if (text.StartsWith("```", StringComparison.Ordinal) ||
            text.StartsWith("~~~", StringComparison.Ordinal))
        {
            return true;
        }
        if (text[0] == '#')
        {
            int hashes = 1;
            while (hashes < text.Length && text[hashes] == '#')
                hashes++;
            if (hashes <= 6 && (hashes == text.Length || char.IsWhiteSpace(text[hashes])))
                return true;
        }
        if (text.Length >= 2 && text[0] is '-' or '+' or '*' && char.IsWhiteSpace(text[1]))
            return true;

        int digitCount = 0;
        while (digitCount < text.Length && digitCount < 9 && char.IsAsciiDigit(text[digitCount]))
            digitCount++;
        if (digitCount > 0 && digitCount + 1 < text.Length &&
            text[digitCount] is '.' or ')' && char.IsWhiteSpace(text[digitCount + 1]))
        {
            return true;
        }

        char? thematic = null;
        int thematicCount = 0;
        bool thematicOnly = true;
        foreach (char value in text)
        {
            if (char.IsWhiteSpace(value))
                continue;
            if (value is not '-' and not '_' and not '*')
            {
                thematicOnly = false;
                break;
            }
            thematic ??= value;
            if (thematic != value)
            {
                thematicOnly = false;
                break;
            }
            thematicCount++;
        }
        if (thematicOnly && thematicCount >= 3)
            return true;

        return text[0] == '[' && text.IndexOf("]:", StringComparison.Ordinal) > 0;
    }
}
