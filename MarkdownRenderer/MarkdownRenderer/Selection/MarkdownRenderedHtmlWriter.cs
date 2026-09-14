using System;
using System.Net;
using System.Text;
using MarkdownRenderer.Accessibility;
using MarkdownRenderer.Layout;
using MarkdownRenderer.Theming;

namespace MarkdownRenderer.Selection;

/// <summary>
/// Serializes a selected range from the committed semantic display plan into
/// a conservative CF_HTML fragment. Source HTML is never reparsed or executed.
/// </summary>
internal static class MarkdownRenderedHtmlWriter
{
    public static string BuildFragment(
        MarkdownSemanticDocument document,
        int textStart,
        int textEnd)
    {
        textStart = Math.Clamp(textStart, 0, document.Text.Length);
        textEnd = Math.Clamp(textEnd, textStart, document.Text.Length);
        if (textEnd <= textStart)
            return string.Empty;

        var output = new StringBuilder(Math.Max(64, textEnd - textStart + 32));
        output.Append("<div data-markdown-renderer=\"1\">");
        int cursor = textStart;
        int spanStart = document.GetTextSpanStartIndex(textStart);
        for (int i = spanStart; i < document.TextSpans.Count; i++)
        {
            MarkdownTextSpan span = document.TextSpans[i];
            if (span.TextEnd <= textStart)
                continue;
            if (span.TextStart >= textEnd)
                break;

            int start = Math.Max(textStart, span.TextStart);
            int end = Math.Min(textEnd, span.TextEnd);
            if (start > cursor)
                AppendEncoded(output, document.Text, cursor, start);
            if (end > start)
                AppendSpan(output, document, span, start, end);
            cursor = Math.Max(cursor, end);
        }

        if (cursor < textEnd)
            AppendEncoded(output, document.Text, cursor, textEnd);
        output.Append("</div>");
        return output.ToString();
    }

    private static void AppendSpan(
        StringBuilder output,
        MarkdownSemanticDocument document,
        MarkdownTextSpan span,
        int start,
        int end)
    {
        string? open = null;
        string? close = null;
        if (span.InlineRun is LinkRun link && TryGetSafeLink(link.Url, out string safeUrl))
        {
            open = $"<a href=\"{WebUtility.HtmlEncode(safeUrl)}\">";
            close = "</a>";
        }
        else if (span.InlineRun is InlineImageRun)
        {
            open = "<span role=\"img\">";
            close = "</span>";
        }
        else if (span.InlineRun is AbbreviationRun abbreviation &&
                 !string.IsNullOrWhiteSpace(abbreviation.Expansion))
        {
            open = $"<abbr title=\"{WebUtility.HtmlEncode(abbreviation.Expansion)}\">";
            close = "</abbr>";
        }
        else if (span.ImageBox is not null)
        {
            open = "<span role=\"img\">";
            close = "</span>";
        }
        else
        {
            string elementKey = string.IsNullOrEmpty(span.InlineRun?.ElementKey)
                ? span.InlineBox?.ElementKey ?? string.Empty
                : span.InlineRun.ElementKey;
            (open, close) = TagsFor(elementKey);
        }

        if (open is not null)
            output.Append(open);
        AppendEncoded(output, document.Text, start, end);
        if (close is not null)
            output.Append(close);
    }

    private static (string? Open, string? Close) TagsFor(string elementKey) => elementKey switch
    {
        MarkdownElementKeys.Strong => ("<strong>", "</strong>"),
        MarkdownElementKeys.Emphasis => ("<em>", "</em>"),
        MarkdownElementKeys.Strikethrough => ("<s>", "</s>"),
        MarkdownElementKeys.CodeInline => ("<code>", "</code>"),
        MarkdownElementKeys.CodeBlock => ("<code>", "</code>"),
        MarkdownElementKeys.Subscript => ("<sub>", "</sub>"),
        MarkdownElementKeys.Superscript => ("<sup>", "</sup>"),
        MarkdownElementKeys.Inserted => ("<ins>", "</ins>"),
        MarkdownElementKeys.Marked => ("<mark>", "</mark>"),
        _ => (null, null),
    };

    private static void AppendEncoded(
        StringBuilder output,
        string text,
        int start,
        int end)
    {
        string encoded = WebUtility.HtmlEncode(text.Substring(start, end - start));
        output.Append(encoded
            .Replace("\r\n", "<br />", StringComparison.Ordinal)
            .Replace("\r", "<br />", StringComparison.Ordinal)
            .Replace("\n", "<br />", StringComparison.Ordinal));
    }

    private static bool TryGetSafeLink(string value, out string safeUrl)
    {
        safeUrl = string.Empty;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp &&
             uri.Scheme != Uri.UriSchemeHttps &&
             uri.Scheme != Uri.UriSchemeMailto))
        {
            return false;
        }

        safeUrl = uri.AbsoluteUri;
        return true;
    }
}
