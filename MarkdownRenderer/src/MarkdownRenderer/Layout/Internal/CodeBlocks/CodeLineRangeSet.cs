using System;
using System.Collections.Generic;
using System.Globalization;

namespace MarkdownRenderer.Layout.Internal.CodeBlocks;

internal readonly struct CodeLineRangeSet
{
    private readonly IReadOnlyList<(int Start, int End)> _ranges;

    private CodeLineRangeSet(IReadOnlyList<(int Start, int End)> ranges)
    {
        _ranges = ranges;
    }

    public static CodeLineRangeSet Empty { get; } = new(Array.Empty<(int Start, int End)>());
    public bool IsEmpty => _ranges.Count == 0;

    public bool Contains(int lineNumber)
    {
        foreach (var (start, end) in _ranges)
        {
            if (lineNumber >= start && lineNumber <= end)
                return true;
        }

        return false;
    }

    public static CodeLineRangeSet Parse(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return Empty;

        var ranges = new List<(int Start, int End)>();
        foreach (var rawPart in value.Split(','))
        {
            var part = rawPart.Trim();
            if (part.Length == 0)
                continue;

            int dash = part.IndexOf('-');
            if (dash > 0)
            {
                if (int.TryParse(part.Substring(0, dash), NumberStyles.Integer, CultureInfo.InvariantCulture, out var start) &&
                    int.TryParse(part.Substring(dash + 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out var end))
                {
                    start = Math.Max(1, start);
                    end = Math.Max(start, end);
                    ranges.Add((start, end));
                }
            }
            else if (int.TryParse(part, NumberStyles.Integer, CultureInfo.InvariantCulture, out var line))
            {
                line = Math.Max(1, line);
                ranges.Add((line, line));
            }
        }

        return ranges.Count == 0 ? Empty : new CodeLineRangeSet(ranges);
    }
}
