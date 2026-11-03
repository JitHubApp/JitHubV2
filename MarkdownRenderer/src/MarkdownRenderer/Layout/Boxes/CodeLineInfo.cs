using System.Collections.Generic;
using System.Globalization;

namespace MarkdownRenderer.Layout.Boxes;

/// <summary>One logical source line of a code block: number, text range, diff kind and gutter label.</summary>
internal readonly struct CodeLineInfo(
    int number,
    int start,
    int length,
    CodeLineDiffKind diffKind,
    string label)
{
    public readonly int Number = number;
    public readonly int Start = start;
    public readonly int Length = length;
    public readonly CodeLineDiffKind DiffKind = diffKind;
    public readonly string Label = label;

    internal static List<CodeLineInfo> BuildLines(string code, bool isDiff, int startLine)
    {
        var lines = new List<CodeLineInfo>();
        if (code.Length == 0)
        {
            lines.Add(new CodeLineInfo(
                1,
                0,
                0,
                CodeLineDiffKind.None,
                startLine.ToString(CultureInfo.InvariantCulture)));
            return lines;
        }

        int start = 0;
        int number = 1;
        for (int i = 0; i <= code.Length; i++)
        {
            bool atEnd = i == code.Length;
            if (!atEnd && code[i] != '\n')
                continue;

            int end = atEnd ? i : i + 1;
            int contentLength = end - start;
            var diffKind = CodeLineDiffKind.None;
            if (isDiff && contentLength > 0)
            {
                char first = code[start];
                if (first == '+')
                    diffKind = CodeLineDiffKind.Added;
                else if (first == '-')
                    diffKind = CodeLineDiffKind.Removed;
            }

            lines.Add(new CodeLineInfo(
                number,
                start,
                contentLength,
                diffKind,
                (startLine + number - 1).ToString(CultureInfo.InvariantCulture)));
            number++;
            start = end;
        }

        return lines;
    }
}
