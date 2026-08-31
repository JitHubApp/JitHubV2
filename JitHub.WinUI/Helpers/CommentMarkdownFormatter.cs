using System;
using System.Linq;

namespace JitHub.WinUI.Helpers;

public static class CommentMarkdownFormatter
{
    public static string AppendQuote(string? draft, string? body)
    {
        string normalizedBody = (body ?? string.Empty)
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Trim();
        string quote = string.Join(
            "\n",
            normalizedBody.Split('\n').Select(static line => string.IsNullOrEmpty(line) ? ">" : $"> {line}"));

        if (string.IsNullOrWhiteSpace(draft))
        {
            return $"{quote}\n\n";
        }

        return $"{draft.TrimEnd()}\n\n{quote}\n\n";
    }
}
