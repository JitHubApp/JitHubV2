using System;
using System.Net;
using System.Text;

namespace JitHub.Services.CodeViewer;

/// <summary>
/// Keeps GitHub's already-rendered README HTML inside valid CommonMark HTML
/// blocks when a quoted tag attribute or nested element contains blank lines.
/// </summary>
internal static class GitHubRenderedReadmeHtmlNormalizer
{
    /// <summary>
    /// Replaces physical line endings inside a root HTML element with decoded
    /// newline entities (or separating whitespace inside an unquoted tag),
    /// while retaining blank-line boundaries between root elements.
    /// CommonMark otherwise terminates a type-6 HTML block at any blank line,
    /// including blank lines in syntax-highlight source and in GitHub's
    /// multiline <c>data-snippet-clipboard-copy-content</c> attribute. Keeping
    /// each root element on one physical line lets the bounded safe-HTML parser
    /// own complete elements without turning a large README into one unbounded
    /// parse unit. Entity decoding restores the exact newlines in <c>pre</c>
    /// content and ordinary HTML whitespace still collapses.
    /// </summary>
    internal static string NormalizeForMarkdownPipeline(string? html)
    {
        if (string.IsNullOrEmpty(html) || html.IndexOf('<', StringComparison.Ordinal) < 0)
            return html ?? string.Empty;

        html = RemoveGitHubTransportWrappers(UnwrapGitHubReadmeShell(html));

        StringBuilder? normalized = null;
        bool inTag = false;
        char quote = '\0';
        int tagStart = -1;
        int elementDepth = 0;
        for (int index = 0; index < html.Length; index++)
        {
            char value = html[index];
            if (!inTag)
            {
                if (value == '<')
                {
                    inTag = true;
                    tagStart = index;
                    normalized?.Append(value);
                    continue;
                }

                if (value is '\r' or '\n')
                {
                    normalized ??= new StringBuilder(html.Length + 16).Append(html, 0, index);
                    if (value == '\r' && index + 1 < html.Length && html[index + 1] == '\n')
                        index++;
                    if (elementDepth == 0)
                        AppendBlockBoundary(normalized);
                    else
                        normalized.Append("&#10;");
                    continue;
                }

                normalized?.Append(value);
                continue;
            }

            if (quote == '\0' && value is '\'' or '"')
            {
                quote = value;
                normalized?.Append(value);
                continue;
            }

            if (quote != '\0' && value == quote)
            {
                quote = '\0';
                normalized?.Append(value);
                continue;
            }

            if (quote == '\0' && value == '>')
            {
                inTag = false;
                normalized?.Append(value);
                UpdateElementDepth(html.AsSpan(tagStart, index - tagStart + 1), ref elementDepth);
                tagStart = -1;
                continue;
            }

            if (value is not ('\r' or '\n'))
            {
                normalized?.Append(value);
                continue;
            }

            normalized ??= new StringBuilder(html.Length + 16).Append(html, 0, index);
            if (value == '\r' && index + 1 < html.Length && html[index + 1] == '\n')
                index++;

            // Preserve quoted attribute semantics when the safe HTML parser
            // decodes the entity. Outside a quote, ordinary HTML whitespace is
            // equivalent to a single space and cannot merge attributes.
            normalized.Append(quote == '\0' ? " " : "&#10;");
        }

        return ConvertGitHubMermaidTransports(normalized?.ToString() ?? html);
    }

    private static string UnwrapGitHubReadmeShell(string html)
    {
        int divStart = SkipWhitespaceForward(html, 0);
        if (!TryReadOpeningTag(html, divStart, "div", out int divEnd) ||
            html.AsSpan(divStart, divEnd - divStart).IndexOf("id=\"readme\"", StringComparison.OrdinalIgnoreCase) < 0)
        {
            return html;
        }

        int articleStart = SkipWhitespaceForward(html, divEnd);
        if (!TryReadOpeningTag(html, articleStart, "article", out int articleEnd) ||
            html.AsSpan(articleStart, articleEnd - articleStart).IndexOf("markdown-body", StringComparison.OrdinalIgnoreCase) < 0)
        {
            return html;
        }

        int end = SkipWhitespaceBackward(html, html.Length);
        if (!TryConsumeClosingTagBackward(html, ref end, "div") ||
            !TryConsumeClosingTagBackward(html, ref end, "article") ||
            end < articleEnd)
        {
            return html;
        }

        // GitHub's transport-only shell is not authored README content. Removing
        // it exposes each authored root element as its own bounded parse unit.
        return html.Substring(articleEnd, end - articleEnd);
    }

    private static string RemoveGitHubTransportWrappers(string html)
    {
        // GitHub inserts this nonstandard element around tables for its web
        // accessibility behavior (including the historical misspelling). It is
        // not authored README content, and CommonMark otherwise treats the
        // entire one-line wrapper as literal text instead of exposing the table.
        html = RemoveTags(
            RemoveTags(html, "markdown-accessiblity-table"),
            "markdown-accessibility-table");
        // GitHub adds this custom transport wrapper around an ordinary
        // standards-based <picture>. It carries no authored semantics, but an
        // HTML/Markdown parser can otherwise treat the unknown custom element
        // as literal text and never expose its image to the renderer.
        html = RemoveTags(html, "themed-picture");
        return UnwrapGeneratedMediaDisclosures(html);
    }

    private static string ConvertGitHubMermaidTransports(string html)
    {
        StringBuilder? result = null;
        int copiedThrough = 0;
        int searchFrom = 0;
        while (TryFindOpeningTag(html, "section", searchFrom, out int start, out int openingEnd))
        {
            string openingTag = html.Substring(start, openingEnd - start);
            if (!TryGetAttribute(openingTag, "data-type", out string type) ||
                !type.Equals("mermaid", StringComparison.OrdinalIgnoreCase) ||
                !TryFindMatchingElement(html, "section", openingEnd, out int closingStart, out int closingEnd))
            {
                searchFrom = openingEnd;
                continue;
            }

            if (!TryGetAttribute(openingTag, "data-plain", out string encodedSource) &&
                !TryFindAttributeInRange(
                    html,
                    openingEnd,
                    closingStart,
                    "data-plain",
                    out encodedSource))
            {
                searchFrom = closingEnd;
                continue;
            }

            string source = (WebUtility.HtmlDecode(encodedSource) ?? string.Empty)
                .Replace("\r\n", "\n", StringComparison.Ordinal)
                .Replace('\r', '\n')
                .Trim('\n');
            if (source.Length == 0)
            {
                searchFrom = closingEnd;
                continue;
            }

            result ??= new StringBuilder(html.Length);
            if (start > copiedThrough)
                result.Append(html, copiedThrough, start - copiedThrough);
            AppendBlockBoundary(result);
            int fenceLength = Math.Max(3, MaximumRun(source, '~') + 1);
            result.Append('~', fenceLength).Append("mermaid\n");
            result.Append(source).Append('\n');
            result.Append('~', fenceLength);
            AppendBlockBoundary(result);
            copiedThrough = closingEnd;
            searchFrom = closingEnd;
        }

        if (result is null)
            return html;

        result.Append(html, copiedThrough, html.Length - copiedThrough);
        return result.ToString();
    }

    private static string UnwrapGeneratedMediaDisclosures(string html)
    {
        StringBuilder? result = null;
        int copiedThrough = 0;
        int searchFrom = 0;
        while (TryFindOpeningTag(html, "details", searchFrom, out int start, out int openingEnd))
        {
            string openingTag = html.Substring(start, openingEnd - start);
            if (!TryGetAttribute(openingTag, "class", out string classes) ||
                !HasClassToken(classes, "details-reset") ||
                !TryFindMatchingElement(html, "details", openingEnd, out int closingStart, out int closingEnd))
            {
                searchFrom = openingEnd;
                continue;
            }

            ReadOnlySpan<char> content = html.AsSpan(openingEnd, closingStart - openingEnd);
            if (content.IndexOf("<video", StringComparison.OrdinalIgnoreCase) < 0 &&
                content.IndexOf("<audio", StringComparison.OrdinalIgnoreCase) < 0)
            {
                searchFrom = openingEnd;
                continue;
            }

            result ??= new StringBuilder(html.Length);
            if (start > copiedThrough)
                result.Append(html, copiedThrough, start - copiedThrough);
            result.Append(html, openingEnd, closingStart - openingEnd);
            copiedThrough = closingEnd;
            searchFrom = closingEnd;
        }

        if (result is null)
            return html;

        result.Append(html, copiedThrough, html.Length - copiedThrough);
        return result.ToString();
    }

    private static bool TryFindOpeningTag(
        string html,
        string name,
        int searchFrom,
        out int start,
        out int end)
    {
        start = -1;
        end = -1;
        while ((start = html.IndexOf('<', Math.Max(0, searchFrom))) >= 0)
        {
            if (TryReadOpeningTag(html, start, name, out end))
                return true;
            searchFrom = start + 1;
        }

        return false;
    }

    private static bool TryFindMatchingElement(
        string html,
        string name,
        int searchFrom,
        out int closingStart,
        out int closingEnd)
    {
        int depth = 1;
        closingStart = -1;
        closingEnd = -1;
        while (searchFrom < html.Length)
        {
            int candidate = html.IndexOf('<', searchFrom);
            if (candidate < 0)
                return false;

            if (TryReadOpeningTag(html, candidate, name, out int nestedEnd))
            {
                depth++;
                searchFrom = nestedEnd;
                continue;
            }

            if (TryReadClosingTag(html, candidate, name, out int closeEnd))
            {
                depth--;
                if (depth == 0)
                {
                    closingStart = candidate;
                    closingEnd = closeEnd;
                    return true;
                }

                searchFrom = closeEnd;
                continue;
            }

            searchFrom = candidate + 1;
        }

        return false;
    }

    private static bool TryReadClosingTag(string html, int start, string name, out int end)
    {
        end = start;
        string prefix = $"</{name}";
        if (start < 0 || start + prefix.Length > html.Length ||
            !html.AsSpan(start, prefix.Length).Equals(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        int boundary = start + prefix.Length;
        if (boundary >= html.Length || !(char.IsWhiteSpace(html[boundary]) || html[boundary] == '>'))
            return false;
        int tagEnd = html.IndexOf('>', boundary);
        if (tagEnd < 0)
            return false;
        end = tagEnd + 1;
        return true;
    }

    private static bool TryFindAttributeInRange(
        string html,
        int start,
        int end,
        string attributeName,
        out string value)
    {
        value = string.Empty;
        int searchFrom = start;
        while (searchFrom < end)
        {
            int tagStart = html.IndexOf('<', searchFrom, end - searchFrom);
            if (tagStart < 0 || !TryReadAnyOpeningTag(html, tagStart, end, out int tagEnd))
            {
                searchFrom = tagStart < 0 ? end : tagStart + 1;
                continue;
            }

            if (TryGetAttribute(
                    html.Substring(tagStart, tagEnd - tagStart),
                    attributeName,
                    out value))
            {
                return true;
            }
            searchFrom = tagEnd;
        }

        return false;
    }

    private static bool TryReadAnyOpeningTag(
        string html,
        int start,
        int maximumEnd,
        out int end)
    {
        end = start;
        if (start < 0 || start + 1 >= maximumEnd || html[start] != '<' ||
            html[start + 1] is '/' or '!' or '?')
        {
            return false;
        }

        char quote = '\0';
        for (int index = start + 1; index < maximumEnd; index++)
        {
            char value = html[index];
            if (quote == '\0' && value is '\'' or '"')
                quote = value;
            else if (quote != '\0' && value == quote)
                quote = '\0';
            else if (quote == '\0' && value == '>')
            {
                end = index + 1;
                return true;
            }
        }

        return false;
    }

    private static bool TryGetAttribute(string tag, string name, out string value)
    {
        value = string.Empty;
        int cursor = 1;
        while (cursor < tag.Length && !char.IsWhiteSpace(tag[cursor]) && tag[cursor] != '>')
            cursor++;
        while (cursor < tag.Length)
        {
            while (cursor < tag.Length && char.IsWhiteSpace(tag[cursor]))
                cursor++;
            int nameStart = cursor;
            while (cursor < tag.Length && IsTagNameCharacter(tag[cursor]))
                cursor++;
            if (cursor == nameStart)
            {
                cursor++;
                continue;
            }

            bool matches = tag.AsSpan(nameStart, cursor - nameStart)
                .Equals(name, StringComparison.OrdinalIgnoreCase);
            while (cursor < tag.Length && char.IsWhiteSpace(tag[cursor]))
                cursor++;
            if (cursor >= tag.Length || tag[cursor] != '=')
                continue;
            cursor++;
            while (cursor < tag.Length && char.IsWhiteSpace(tag[cursor]))
                cursor++;
            if (cursor >= tag.Length || tag[cursor] is not ('\'' or '"'))
                continue;
            char quote = tag[cursor++];
            int valueStart = cursor;
            while (cursor < tag.Length && tag[cursor] != quote)
                cursor++;
            if (cursor >= tag.Length)
                return false;
            if (matches)
            {
                value = tag.Substring(valueStart, cursor - valueStart);
                return true;
            }
            cursor++;
        }

        return false;
    }

    private static bool HasClassToken(string classes, string expected)
    {
        foreach (string token in classes.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            if (token.Equals(expected, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private static int MaximumRun(string value, char character)
    {
        int maximum = 0;
        int current = 0;
        foreach (char candidate in value)
        {
            current = candidate == character ? current + 1 : 0;
            maximum = Math.Max(maximum, current);
        }
        return maximum;
    }

    private static string RemoveTags(string html, string tagName)
    {
        StringBuilder? result = null;
        int copiedThrough = 0;
        int searchFrom = 0;
        while (searchFrom < html.Length)
        {
            int start = html.IndexOf('<', searchFrom);
            if (start < 0)
                break;

            int cursor = start + 1;
            if (cursor < html.Length && html[cursor] == '/')
                cursor++;

            if (cursor + tagName.Length > html.Length ||
                !html.AsSpan(cursor, tagName.Length).Equals(tagName, StringComparison.OrdinalIgnoreCase))
            {
                searchFrom = start + 1;
                continue;
            }

            int boundary = cursor + tagName.Length;
            if (boundary >= html.Length || !(char.IsWhiteSpace(html[boundary]) || html[boundary] is '>' or '/'))
            {
                searchFrom = start + 1;
                continue;
            }

            int end = html.IndexOf('>', boundary);
            if (end < 0)
                break;

            if (result is null)
                result = new StringBuilder(html.Length).Append(html, 0, start);
            else
                result.Append(html, copiedThrough, start - copiedThrough);
            copiedThrough = end + 1;
            searchFrom = copiedThrough;
        }

        if (result is null)
            return html;

        result.Append(html, copiedThrough, html.Length - copiedThrough);
        return result.ToString();
    }

    private static bool TryReadOpeningTag(string html, int start, string name, out int end)
    {
        end = start;
        ReadOnlySpan<char> prefix = $"<{name}".AsSpan();
        if (start < 0 || start + prefix.Length > html.Length ||
            !html.AsSpan(start, prefix.Length).Equals(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        int boundary = start + prefix.Length;
        if (boundary >= html.Length || !(char.IsWhiteSpace(html[boundary]) || html[boundary] == '>'))
            return false;

        char quote = '\0';
        for (int index = boundary; index < html.Length; index++)
        {
            char value = html[index];
            if (quote == '\0' && value is '\'' or '"')
                quote = value;
            else if (quote != '\0' && value == quote)
                quote = '\0';
            else if (quote == '\0' && value == '>')
            {
                end = index + 1;
                return true;
            }
        }

        return false;
    }

    private static bool TryConsumeClosingTagBackward(string html, ref int end, string name)
    {
        end = SkipWhitespaceBackward(html, end);
        string closingTag = $"</{name}>";
        int start = end - closingTag.Length;
        if (start < 0 || !html.AsSpan(start, closingTag.Length).Equals(closingTag, StringComparison.OrdinalIgnoreCase))
            return false;

        end = SkipWhitespaceBackward(html, start);
        return true;
    }

    private static int SkipWhitespaceForward(string value, int index)
    {
        while (index < value.Length && char.IsWhiteSpace(value[index]))
            index++;
        return index;
    }

    private static int SkipWhitespaceBackward(string value, int end)
    {
        while (end > 0 && char.IsWhiteSpace(value[end - 1]))
            end--;
        return end;
    }

    private static void AppendBlockBoundary(StringBuilder destination)
    {
        int trailingNewlines = 0;
        for (int index = destination.Length - 1; index >= 0 && destination[index] == '\n'; index--)
            trailingNewlines++;

        if (trailingNewlines < 2)
            destination.Append('\n', 2 - trailingNewlines);
    }

    private static void UpdateElementDepth(ReadOnlySpan<char> tag, ref int elementDepth)
    {
        if (tag.Length < 3 || tag[0] != '<')
            return;

        int cursor = 1;
        while (cursor < tag.Length && char.IsWhiteSpace(tag[cursor]))
            cursor++;

        if (cursor >= tag.Length || tag[cursor] is '!' or '?')
            return;

        bool closing = tag[cursor] == '/';
        if (closing)
        {
            cursor++;
            while (cursor < tag.Length && char.IsWhiteSpace(tag[cursor]))
                cursor++;
        }

        int nameStart = cursor;
        while (cursor < tag.Length && IsTagNameCharacter(tag[cursor]))
            cursor++;
        if (cursor == nameStart)
            return;

        ReadOnlySpan<char> name = tag[nameStart..cursor];
        if (closing)
        {
            elementDepth = Math.Max(0, elementDepth - 1);
            return;
        }

        int end = tag.Length - 2;
        while (end >= 0 && char.IsWhiteSpace(tag[end]))
            end--;
        bool selfClosing = end >= 0 && tag[end] == '/';
        if (!selfClosing && !IsVoidElement(name))
            elementDepth++;
    }

    private static bool IsTagNameCharacter(char value) =>
        char.IsAsciiLetterOrDigit(value) || value is ':' or '-' or '_';

    private static bool IsVoidElement(ReadOnlySpan<char> name) =>
        name.Equals("area", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("base", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("br", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("col", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("embed", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("hr", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("img", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("input", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("link", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("meta", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("param", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("source", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("track", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("wbr", StringComparison.OrdinalIgnoreCase);
}
