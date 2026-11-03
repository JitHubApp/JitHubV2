using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Xml;
using MarkdownRenderer.Images;

namespace MarkdownRenderer.Svg.Resvg.Internal;

/// <summary>
/// The resource-budget pass's CSS checks: motion declarations, inert namespace
/// and self-contained font-face removal, and url() references.
/// </summary>
internal static class SvgResourceBudgetCss
{
    internal static SvgResourceBudgetResult ValidateCss(string css)
    {
        // Charts such as star-history embed their lettering font as a
        // data: @font-face. The renderer never loads web fonts; the static
        // snapshot removes the block and text uses a system fallback font.
        // Only a self-contained block may be set aside here; any other
        // @font-face source is an external resource.
        if (css.IndexOf("@font-face", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            if (TryRemoveSelfContainedFontFaces(css, out string withoutFontFaces, out bool externalSource))
                css = withoutFontFaces;
            else if (externalSource)
                return SvgResourceBudgetResult.Reject("external-resource");
            // A malformed block is left in place and rejected as active content below.
        }

        if (css.IndexOf("@import", StringComparison.OrdinalIgnoreCase) >= 0 ||
            css.IndexOf("@font-face", StringComparison.OrdinalIgnoreCase) >= 0 ||
            css.IndexOf("@keyframes", StringComparison.OrdinalIgnoreCase) >= 0 ||
            ContainsMotionDeclaration(css))
        {
            return SvgResourceBudgetResult.Reject("active-content");
        }

        return TryRemoveInertNamespaceDeclarations(css, out string inspectable)
            ? ValidateCssUrls(inspectable)
            : SvgResourceBudgetResult.Reject("invalid-css-namespace");
    }

    internal static bool ContainsMotionDeclaration(string css)
    {
        int cursor = 0;
        while (cursor < css.Length)
        {
            if (char.IsWhiteSpace(css[cursor]))
            {
                cursor++;
                continue;
            }
            if (cursor + 1 < css.Length && css[cursor] == '/' && css[cursor + 1] == '*')
            {
                int commentEnd = css.IndexOf("*/", cursor + 2, StringComparison.Ordinal);
                if (commentEnd < 0)
                    return false;
                cursor = commentEnd + 2;
                continue;
            }
            if (css[cursor] is '\'' or '"')
            {
                SkipCssString(css, ref cursor);
                continue;
            }

            int nameStart = cursor;
            while (cursor < css.Length &&
                (char.IsAsciiLetterOrDigit(css[cursor]) || css[cursor] is '-' or '_'))
            {
                cursor++;
            }
            if (cursor == nameStart)
            {
                cursor++;
                continue;
            }

            string property = css[nameStart..cursor];
            int separator = cursor;
            while (separator < css.Length && char.IsWhiteSpace(css[separator]))
                separator++;
            if (separator >= css.Length || css[separator] != ':' || !SvgContentRules.IsMotionProperty(property))
                continue;

            if (EndsDeclaration(css, separator + 1, out int end))
                return true;
            cursor = end + 1;
        }

        return false;
    }

    /// <summary>
    /// Scans a declaration value from <paramref name="valueStart"/>, honoring
    /// quotes, escapes and parentheses.
    /// </summary>
    /// <param name="css">The style sheet.</param>
    /// <param name="valueStart">The first character after the colon.</param>
    /// <param name="end">The block opener's index when the value is a selector or prelude.</param>
    /// <returns>
    /// <see langword="true"/> when the value ends at a terminator or the end of
    /// the sheet; <see langword="false"/> when a block opener comes first.
    /// </returns>
    private static bool EndsDeclaration(string css, int valueStart, out int end)
    {
        end = valueStart;
        char quote = '\0';
        int parentheses = 0;
        while (end < css.Length)
        {
            char current = css[end];
            if (quote != '\0')
            {
                if (current == '\\' && end + 1 < css.Length)
                    end += 2;
                else
                {
                    if (current == quote)
                        quote = '\0';
                    end++;
                }
                continue;
            }
            if (current is '\'' or '"')
                quote = current;
            else if (current == '(')
                parentheses++;
            else if (current == ')' && parentheses > 0)
                parentheses--;
            else if (parentheses == 0 && current == '{')
                break;
            else if (parentheses == 0 && current is (';' or '}'))
                return true;
            end++;
        }
        return end >= css.Length;
    }

    internal static void SkipCssString(string css, ref int cursor)
    {
        char quote = css[cursor++];
        while (cursor < css.Length)
        {
            if (css[cursor] == '\\' && cursor + 1 < css.Length)
            {
                cursor += 2;
                continue;
            }
            if (css[cursor++] == quote)
                return;
        }
    }

    internal static bool TryRemoveInertNamespaceDeclarations(string css, out string inspectable)
    {
        char[]? sanitized = null;
        int cursor = 0;
        while (cursor < css.Length)
        {
            if (cursor + 1 < css.Length && css[cursor] == '/' && css[cursor + 1] == '*')
            {
                int commentEnd = css.IndexOf("*/", cursor + 2, StringComparison.Ordinal);
                if (commentEnd < 0)
                {
                    inspectable = string.Empty;
                    return false;
                }
                cursor = commentEnd + 2;
                continue;
            }
            if (css[cursor] is '\'' or '"')
            {
                char quote = css[cursor++];
                while (cursor < css.Length && css[cursor] != quote)
                    cursor += css[cursor] == '\\' && cursor + 1 < css.Length ? 2 : 1;
                if (cursor >= css.Length)
                {
                    inspectable = string.Empty;
                    return false;
                }
                cursor++;
                continue;
            }
            if (!css.AsSpan(cursor).StartsWith("@namespace", StringComparison.OrdinalIgnoreCase))
            {
                cursor++;
                continue;
            }

            int start = cursor;
            cursor += "@namespace".Length;
            if (!TryFindNamespaceStatementEnd(css, ref cursor))
            {
                inspectable = string.Empty;
                return false;
            }
            sanitized ??= css.ToCharArray();
            Array.Fill(sanitized, ' ', start, cursor - start);
        }

        inspectable = sanitized is null ? css : new string(sanitized);
        return true;
    }

    /// <summary>
    /// Advances past an <c>@namespace</c> statement's prelude to just after
    /// its terminating semicolon, honoring quotes, escapes and parentheses.
    /// </summary>
    /// <param name="css">The style sheet.</param>
    /// <param name="cursor">The first character after <c>@namespace</c>; on return, the scan position.</param>
    /// <returns><see langword="false"/> when a block opener or the end of the sheet comes first.</returns>
    private static bool TryFindNamespaceStatementEnd(string css, ref int cursor)
    {
        int parentheses = 0;
        char statementQuote = '\0';
        bool terminated = false;
        while (cursor < css.Length)
        {
            char current = css[cursor];
            if (statementQuote != '\0')
            {
                if (current == '\\' && cursor + 1 < css.Length)
                    cursor += 2;
                else
                {
                    if (current == statementQuote)
                        statementQuote = '\0';
                    cursor++;
                }
                continue;
            }
            if (current is '\'' or '"')
            {
                statementQuote = current;
                cursor++;
                continue;
            }
            if (current == '(')
                parentheses++;
            else if (current == ')' && parentheses > 0)
                parentheses--;
            else if (current == '{' && parentheses == 0)
                break;
            else if (current == ';' && parentheses == 0)
            {
                cursor++;
                terminated = true;
                break;
            }
            cursor++;
        }
        return terminated;
    }

    /// <summary>
    /// Removes every <c>@font-face { … }</c> block whose <c>url()</c> sources
    /// are all <c>data:</c> URLs (or that has none). Returns false when a block
    /// is unterminated (<paramref name="externalSource"/> false) or references
    /// anything other than <c>data:</c> (<paramref name="externalSource"/> true).
    /// </summary>
    internal static bool TryRemoveSelfContainedFontFaces(string css, out string remaining, out bool externalSource)
    {
        externalSource = false;
        var builder = new System.Text.StringBuilder(css.Length);
        int cursor = 0;
        while (true)
        {
            int start = css.IndexOf("@font-face", cursor, StringComparison.OrdinalIgnoreCase);
            if (start < 0)
            {
                builder.Append(css, cursor, css.Length - cursor);
                remaining = builder.ToString();
                return true;
            }

            int open = css.IndexOf('{', start);
            int close = open < 0 ? -1 : css.IndexOf('}', open + 1);
            if (open < 0 || close < 0 || css.IndexOf('{', open + 1, close - open - 1) >= 0)
            {
                remaining = css;
                return false;
            }

            string body = css.Substring(open + 1, close - open - 1);
            int searchFrom = 0;
            while (true)
            {
                int url = body.IndexOf("url(", searchFrom, StringComparison.OrdinalIgnoreCase);
                if (url < 0)
                    break;
                int argumentStart = url + 4;
                int argumentEnd = body.IndexOf(')', argumentStart);
                if (argumentEnd < 0)
                {
                    remaining = css;
                    return false;
                }
                ReadOnlySpan<char> argument = body.AsSpan(argumentStart, argumentEnd - argumentStart).Trim();
                if (argument.Length >= 2 &&
                    ((argument[0] == '\'' && argument[^1] == '\'') || (argument[0] == '"' && argument[^1] == '"')))
                {
                    argument = argument[1..^1].Trim();
                }
                if (!argument.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                {
                    remaining = css;
                    externalSource = true;
                    return false;
                }
                searchFrom = argumentEnd + 1;
            }

            builder.Append(css, cursor, start - cursor);
            cursor = close + 1;
        }
    }

    internal static SvgResourceBudgetResult ValidateCssUrls(string value)
    {
        int searchFrom = 0;
        while (true)
        {
            int urlIndex = value.IndexOf("url(", searchFrom, StringComparison.OrdinalIgnoreCase);
            if (urlIndex < 0)
                return SvgResourceBudgetResult.Success;

            int argumentStart = urlIndex + 4;
            int close = value.IndexOf(')', argumentStart);
            if (close < 0)
                return SvgResourceBudgetResult.Reject("invalid-css-url");

            ReadOnlySpan<char> argument = value.AsSpan(argumentStart, close - argumentStart).Trim();
            if (argument.Length >= 2 &&
                ((argument[0] == '\'' && argument[^1] == '\'') ||
                 (argument[0] == '"' && argument[^1] == '"')))
            {
                argument = argument[1..^1].Trim();
            }

            if (argument.IsEmpty || argument[0] != '#')
                return SvgResourceBudgetResult.Reject("external-resource");

            searchFrom = close + 1;
        }
    }

    internal static bool ContainsCssUrl(string value) =>
        value.IndexOf("url(", StringComparison.OrdinalIgnoreCase) >= 0;
}
