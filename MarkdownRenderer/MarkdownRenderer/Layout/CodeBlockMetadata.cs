using System;
using System.Collections.Generic;
using System.Globalization;
using Markdig.Syntax;

namespace MarkdownRenderer.Layout;

internal sealed class CodeBlockMetadata
{
    public const string PlainCodeLabel = "Code";

    private static readonly char[] Whitespace = [' ', '\t', '\r', '\n'];

    private CodeBlockMetadata(
        string? language,
        string displayLanguage,
        string? title,
        string? fileName,
        CodeLineRangeSet highlightedLines,
        bool? showLineNumbers,
        int startLine,
        bool isDiff,
        string stableKey)
    {
        Language = language;
        LanguageDisplay = displayLanguage;
        Title = title;
        FileName = fileName;
        HighlightedLines = highlightedLines;
        ShowLineNumbers = showLineNumbers;
        StartLine = startLine;
        IsDiff = isDiff;
        StableKey = stableKey;
    }

    public string? Language { get; }
    public string LanguageDisplay { get; }
    public string? Title { get; }
    public string? FileName { get; }
    public string HeaderText => !string.IsNullOrWhiteSpace(Title)
        ? Title!
        : !string.IsNullOrWhiteSpace(FileName)
            ? FileName!
            : LanguageDisplay;
    public CodeLineRangeSet HighlightedLines { get; }
    public bool? ShowLineNumbers { get; }
    public int StartLine { get; }
    public bool IsDiff { get; }
    public string StableKey { get; }

    public static CodeBlockMetadata FromBlock(LeafBlock block, string displayedCodeText)
    {
        string info = string.Empty;
        if (block is FencedCodeBlock fenced)
        {
            var languageInfo = fenced.Info?.ToString() ?? string.Empty;
            var arguments = fenced.Arguments?.ToString() ?? string.Empty;
            info = string.IsNullOrWhiteSpace(arguments)
                ? languageInfo
                : string.IsNullOrWhiteSpace(languageInfo)
                    ? arguments
                    : languageInfo + " " + arguments;
        }

        string? language = null;
        string? title = null;
        string? fileName = null;
        var highlightedLines = CodeLineRangeSet.Empty;
        bool? showLineNumbers = null;
        int startLine = 1;
        bool isDiff = false;
        bool languageConsumed = false;

        foreach (var token in TokenizeInfo(info))
        {
            if (token.Length == 0)
                continue;

            if (token.StartsWith("{", StringComparison.Ordinal) &&
                token.EndsWith("}", StringComparison.Ordinal) &&
                token.Length > 2)
            {
                highlightedLines = CodeLineRangeSet.Parse(token.Substring(1, token.Length - 2));
                continue;
            }

            if (token.Equals("showLineNumbers", StringComparison.OrdinalIgnoreCase))
            {
                showLineNumbers = true;
                continue;
            }

            if (token.Equals("noLineNumbers", StringComparison.OrdinalIgnoreCase))
            {
                showLineNumbers = false;
                continue;
            }

            if (token.Equals("diff", StringComparison.OrdinalIgnoreCase))
            {
                isDiff = true;
                continue;
            }

            var eq = token.IndexOf('=');
            if (eq > 0)
            {
                var key = token.Substring(0, eq).Trim();
                var value = Unquote(token.Substring(eq + 1).Trim());
                switch (key.ToLowerInvariant())
                {
                    case "filename":
                    case "file":
                        fileName = value;
                        break;
                    case "title":
                        title = value;
                        break;
                    case "startline":
                    case "start":
                        if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedStart))
                            startLine = Math.Max(1, parsedStart);
                        break;
                    case "diff":
                        if (TryParseBoolean(value, out var parsedDiff))
                            isDiff = parsedDiff;
                        break;
                }

                continue;
            }

            if (!languageConsumed)
            {
                language = NormalizeLanguage(token);
                languageConsumed = true;
                if (string.Equals(language, "diff", StringComparison.OrdinalIgnoreCase))
                    isDiff = true;
            }
        }

        string stableKey = CreateStableKey(block, CopyPayload(displayedCodeText));
        return new CodeBlockMetadata(
            language,
            DisplayLanguage(language),
            title,
            fileName,
            highlightedLines,
            showLineNumbers,
            startLine,
            isDiff,
            stableKey);
    }

    public static string CopyPayload(string? displayedCodeText) => NormalizeCodeLineEndings(displayedCodeText);

    public static string NormalizeCodeLineEndings(string? displayedCodeText)
    {
        if (string.IsNullOrEmpty(displayedCodeText))
            return string.Empty;

        var value = displayedCodeText!;
        if (value.IndexOf('\r') < 0)
            return value;

        return value.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
    }

    public static string? NormalizeLanguage(string? language)
    {
        var value = language?.Trim() ?? string.Empty;
        if (value.Length == 0)
            return null;

        return value.ToLowerInvariant() switch
        {
            "csharp" or "cs" => "csharp",
            "javascript" or "js" => "javascript",
            "typescript" or "ts" => "typescript",
            "python" or "py" => "python",
            "powershell" or "pwsh" or "ps1" => "powershell",
            "yaml" or "yml" => "yaml",
            "markdown" or "md" => "markdown",
            "bash" or "sh" or "shell" => "bash",
            _ => value,
        };
    }

    public static string DisplayLanguage(string? language)
    {
        var value = NormalizeLanguage(language) ?? string.Empty;
        if (value.Length == 0)
            return PlainCodeLabel;

        return value switch
        {
            "csharp" => "C#",
            "javascript" => "JavaScript",
            "typescript" => "TypeScript",
            "jsx" => "JSX",
            "tsx" => "TSX",
            "python" => "Python",
            "powershell" => "PowerShell",
            "json" => "JSON",
            "yaml" => "YAML",
            "xml" => "XML",
            "html" => "HTML",
            "css" => "CSS",
            "markdown" => "Markdown",
            "bash" => "Shell",
            "diff" => "Diff",
            _ => value,
        };
    }

    private static string CreateStableKey(LeafBlock block, string code)
        => string.Create(CultureInfo.InvariantCulture, $"{block.Span.Start}:{block.Span.Length}:{Fnv1A(code):X16}");

    private static ulong Fnv1A(string value)
    {
        const ulong offset = 14695981039346656037UL;
        const ulong prime = 1099511628211UL;
        ulong hash = offset;
        foreach (var ch in value)
        {
            hash ^= ch;
            hash *= prime;
        }

        return hash;
    }

    private static bool TryParseBoolean(string value, out bool result)
    {
        if (bool.TryParse(value, out result))
            return true;
        if (value.Equals("1", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("yes", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("on", StringComparison.OrdinalIgnoreCase))
        {
            result = true;
            return true;
        }

        if (value.Equals("0", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("no", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("off", StringComparison.OrdinalIgnoreCase))
        {
            result = false;
            return true;
        }

        result = false;
        return false;
    }

    private static string Unquote(string value)
    {
        if (value.Length >= 2)
        {
            char first = value[0];
            char last = value[value.Length - 1];
            if ((first == '"' && last == '"') || (first == '\'' && last == '\''))
                return value.Substring(1, value.Length - 2);
        }

        return value;
    }

    private static IEnumerable<string> TokenizeInfo(string info)
    {
        if (string.IsNullOrWhiteSpace(info))
            yield break;

        int i = 0;
        while (i < info.Length)
        {
            while (i < info.Length && Array.IndexOf(Whitespace, info[i]) >= 0)
                i++;
            if (i >= info.Length)
                yield break;

            int start = i;
            char quote = '\0';
            int braceDepth = 0;
            while (i < info.Length)
            {
                char ch = info[i];
                if (quote != '\0')
                {
                    if (ch == quote)
                        quote = '\0';
                    i++;
                    continue;
                }

                if (ch == '"' || ch == '\'')
                {
                    quote = ch;
                    i++;
                    continue;
                }

                if (ch == '{')
                    braceDepth++;
                else if (ch == '}' && braceDepth > 0)
                    braceDepth--;
                else if (braceDepth == 0 && Array.IndexOf(Whitespace, ch) >= 0)
                    break;

                i++;
            }

            yield return info.Substring(start, i - start);
        }
    }
}

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
