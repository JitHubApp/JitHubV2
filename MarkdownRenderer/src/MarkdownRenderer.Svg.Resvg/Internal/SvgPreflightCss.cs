using System.Security.Cryptography;
using System.Text;
using MarkdownRenderer.Images;

namespace MarkdownRenderer.Svg.Resvg.Internal;

/// <summary>
/// Rejects style sheets and style attributes that load resources, carry active
/// content or animate, using a CSS-aware scan of comments, strings and escapes.
/// </summary>
internal static class SvgPreflightCss
{
    internal static bool MayContainCssResourceOrActiveContent(string attributeName, ReadOnlySpan<char> value)
    {
        if (attributeName.Equals("style", StringComparison.OrdinalIgnoreCase))
            return true;

        ReadOnlySpan<char> normalized = value.Contains('\\') ? DecodeCssEscapes(value.ToString()).AsSpan() : value;
        if (normalized.Contains("url", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("@import", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("@font-face", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("@keyframes", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("animation", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("transition", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return false;
    }

    internal static bool ContainsCssIdentifier(string css, string identifier)
        => ContainsCssIdentifier(css.AsSpan(), identifier);

    internal static bool ContainsCssIdentifier(ReadOnlySpan<char> css, string identifier)
    {
        int cursor = 0;
        while (cursor < css.Length)
        {
            if (cursor + 1 < css.Length && css[cursor] == '/' && css[cursor + 1] == '*')
            {
                int commentEnd = css[(cursor + 2)..].IndexOf("*/".AsSpan(), StringComparison.Ordinal);
                if (commentEnd < 0)
                    return false;
                cursor += commentEnd + 4;
                continue;
            }
            if (css[cursor] is '\'' or '"')
            {
                char quote = css[cursor++];
                while (cursor < css.Length && css[cursor] != quote)
                {
                    if (css[cursor] == '\\' && cursor + 1 < css.Length)
                        cursor++;
                    cursor++;
                }
                if (cursor < css.Length)
                    cursor++;
                continue;
            }
            if (!IsCssIdentifierCharacter(css[cursor]))
            {
                cursor++;
                continue;
            }

            int start = cursor++;
            while (cursor < css.Length && IsCssIdentifierCharacter(css[cursor]))
                cursor++;
            ReadOnlySpan<char> token = css.Slice(start, cursor - start);
            if (token.Equals("url".AsSpan(), StringComparison.OrdinalIgnoreCase))
            {
                int functionStart = cursor;
                if (TrySkipCssUrlFunction(css, ref functionStart))
                {
                    cursor = functionStart;
                    continue;
                }
            }
            if (token.Equals(identifier.AsSpan(), StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    internal static bool TrySkipCssUrlFunction(ReadOnlySpan<char> css, ref int cursor)
    {
        int candidate = cursor;
        while (candidate < css.Length && char.IsWhiteSpace(css[candidate]))
            candidate++;
        if (candidate + 1 < css.Length && css[candidate] == '/' && css[candidate + 1] == '*')
        {
            int commentEnd = css[(candidate + 2)..].IndexOf("*/".AsSpan(), StringComparison.Ordinal);
            if (commentEnd < 0)
                return false;
            candidate += commentEnd + 4;
            while (candidate < css.Length && char.IsWhiteSpace(css[candidate]))
                candidate++;
        }
        if (candidate >= css.Length || css[candidate] != '(')
            return false;

        int depth = 1;
        cursor = candidate + 1;
        while (cursor < css.Length)
        {
            if (cursor + 1 < css.Length && css[cursor] == '/' && css[cursor + 1] == '*')
            {
                int commentEnd = css[(cursor + 2)..].IndexOf("*/".AsSpan(), StringComparison.Ordinal);
                if (commentEnd < 0)
                    return false;
                cursor += commentEnd + 4;
                continue;
            }
            if (css[cursor] is '\'' or '"')
            {
                char quote = css[cursor++];
                while (cursor < css.Length && css[cursor] != quote)
                {
                    if (css[cursor] == '\\' && cursor + 1 < css.Length)
                        cursor++;
                    cursor++;
                }
                if (cursor < css.Length)
                    cursor++;
                continue;
            }
            if (css[cursor] == '(')
                depth++;
            else if (css[cursor] == ')' && --depth == 0)
            {
                cursor++;
                return true;
            }
            cursor++;
        }
        return false;
    }

    internal static bool IsCssIdentifierCharacter(char value) =>
        char.IsAsciiLetterOrDigit(value) || value is '-' or '_' || value >= 0x80;

    internal static string RemoveInertNamespaceDeclarations(string css)
    {
        char[]? sanitized = null;
        int cursor = 0;
        while (cursor < css.Length)
        {
            if (TrySkipCssComment(css, ref cursor) || TrySkipCssString(css, ref cursor))
                continue;
            if (!css.AsSpan(cursor).StartsWith("@namespace", StringComparison.OrdinalIgnoreCase))
            {
                cursor++;
                continue;
            }

            int start = cursor;
            cursor += "@namespace".Length;
            int parentheses = 0;
            char quote = '\0';
            bool terminated = false;
            while (cursor < css.Length)
            {
                char current = css[cursor];
                if (quote != '\0')
                {
                    if (current == '\\' && cursor + 1 < css.Length)
                        cursor += 2;
                    else
                    {
                        if (current == quote)
                            quote = '\0';
                        cursor++;
                    }
                    continue;
                }
                if (current is '\'' or '"')
                {
                    quote = current;
                    cursor++;
                    continue;
                }
                if (current == '(')
                    parentheses++;
                else if (current == ')' && parentheses > 0)
                    parentheses--;
                else if (current == '{' && parentheses == 0)
                    throw SvgPreflightFailures.Unsupported("A CSS namespace declaration is malformed.");
                else if (current == ';' && parentheses == 0)
                {
                    cursor++;
                    terminated = true;
                    break;
                }
                cursor++;
            }
            if (!terminated)
                throw SvgPreflightFailures.Unsupported("A CSS namespace declaration is not terminated.");
            sanitized ??= css.ToCharArray();
            Array.Fill(sanitized, ' ', start, cursor - start);
        }
        return sanitized is null ? css : new string(sanitized);
    }

    internal static void InspectCss(
        string css,
        ResvgMarkdownSvgRendererOptions options,
        SvgResourceInspectionContext resources,
        ref long embeddedBytes,
        ref long embeddedPixels,
        CancellationToken cancellationToken,
        int embeddedSvgDepth,
        ref bool hasText,
        ref bool usesCurrentColor,
        ref bool usesColorScheme,
        bool isDeclarationList = false,
        bool trackThemeMetadata = true)
    {
        string normalized = DecodeCssEscapes(css);
        if (trackThemeMetadata)
        {
            usesCurrentColor |= SvgColorSchemeUsage.UsesCurrentColorInCss(normalized, isDeclarationList);
            usesColorScheme |= SvgColorSchemeUsage.UsesPrefersColorSchemeInCss(normalized);
        }
        if (normalized.Contains("@import", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("@font-face", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("@keyframes", StringComparison.OrdinalIgnoreCase) ||
            ContainsMotionDeclaration(normalized))
        {
            throw SvgPreflightFailures.Unsupported("External fonts, imports, and animation are forbidden.");
        }

        normalized = RemoveInertNamespaceDeclarations(normalized);
        int cursor = 0;
        while (cursor < normalized.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (TrySkipCssComment(normalized, ref cursor) || TrySkipCssString(normalized, ref cursor))
                continue;
            if (!normalized.AsSpan(cursor).StartsWith("url", StringComparison.OrdinalIgnoreCase))
            {
                cursor++;
                continue;
            }

            int afterName = cursor + 3;
            SkipCssWhitespaceAndComments(normalized, ref afterName);
            if (afterName >= normalized.Length || normalized[afterName] != '(')
            {
                cursor++;
                continue;
            }

            cursor = afterName + 1;
            SkipCssWhitespaceAndComments(normalized, ref cursor);
            ReadOnlySpan<char> target;
            if (cursor < normalized.Length && normalized[cursor] is '\'' or '"')
            {
                char quote = normalized[cursor++];
                int start = cursor;
                while (cursor < normalized.Length && normalized[cursor] != quote)
                    cursor++;
                if (cursor >= normalized.Length)
                    throw SvgPreflightFailures.Unsupported("A quoted CSS URL is not terminated.");
                target = normalized.AsSpan(start, cursor - start);
                cursor++;
                SkipCssWhitespaceAndComments(normalized, ref cursor);
                if (cursor >= normalized.Length || normalized[cursor] != ')')
                    throw SvgPreflightFailures.Unsupported("A quoted CSS URL has invalid trailing content.");
            }
            else
            {
                int start = cursor;
                while (cursor < normalized.Length && normalized[cursor] != ')' &&
                       !char.IsWhiteSpace(normalized[cursor]))
                {
                    cursor++;
                }
                target = normalized.AsSpan(start, cursor - start);
                SkipCssWhitespaceAndComments(normalized, ref cursor);
                if (cursor >= normalized.Length || normalized[cursor] != ')')
                    throw SvgPreflightFailures.Unsupported("An unquoted CSS URL is malformed.");
            }
            cursor++;
            target = target.Trim();
            if (!target.IsEmpty && target[0] == '#')
                continue;
            SvgEmbeddedReferenceInspection.InspectReference(
                target,
                options,
                resources,
                ref embeddedBytes,
                ref embeddedPixels,
                cancellationToken,
                embeddedSvgDepth,
                ref hasText,
                ref usesCurrentColor,
                ref usesColorScheme);
        }
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
            if (TrySkipCssComment(css, ref cursor) || TrySkipCssString(css, ref cursor))
                continue;

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
            if (separator >= css.Length || css[separator] != ':' || !IsMotionProperty(property))
                continue;

            int end = separator + 1;
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

            if (end >= css.Length)
                return true;
            cursor = end + 1;
        }

        return false;
    }

    internal static bool IsMotionProperty(string property)
        => property.Equals("animation", StringComparison.OrdinalIgnoreCase) ||
            property.StartsWith("animation-", StringComparison.OrdinalIgnoreCase) ||
            property.Equals("transition", StringComparison.OrdinalIgnoreCase) ||
            property.StartsWith("transition-", StringComparison.OrdinalIgnoreCase) ||
            property.Equals("-webkit-animation", StringComparison.OrdinalIgnoreCase) ||
            property.StartsWith("-webkit-animation-", StringComparison.OrdinalIgnoreCase) ||
            property.Equals("-webkit-transition", StringComparison.OrdinalIgnoreCase) ||
            property.StartsWith("-webkit-transition-", StringComparison.OrdinalIgnoreCase);

    internal static string DecodeCssEscapes(string value)
    {
        if (!value.Contains('\\'))
            return value;
        var output = new StringBuilder(value.Length);
        for (int index = 0; index < value.Length; index++)
        {
            char current = value[index];
            if (current != '\\')
            {
                output.Append(current);
                continue;
            }
            if (++index >= value.Length)
                throw SvgPreflightFailures.Unsupported("A CSS escape is incomplete.");
            if (value[index] is '\r' or '\n' or '\f')
            {
                if (value[index] == '\r' && index + 1 < value.Length && value[index + 1] == '\n')
                    index++;
                continue;
            }

            int scalar = 0;
            int digits = 0;
            while (index < value.Length && digits < 6 && Uri.IsHexDigit(value[index]))
            {
                scalar = checked((scalar * 16) + HexValue(value[index]));
                digits++;
                index++;
            }
            if (digits != 0)
            {
                if (index < value.Length && char.IsWhiteSpace(value[index]))
                {
                    if (value[index] == '\r' && index + 1 < value.Length && value[index + 1] == '\n')
                        index++;
                }
                else
                {
                    index--;
                }
                if (!Rune.IsValid(scalar) || scalar == 0)
                    throw SvgPreflightFailures.Unsupported("A CSS escape contains an invalid scalar value.");
                output.Append(char.ConvertFromUtf32(scalar));
            }
            else
            {
                output.Append(value[index]);
            }
        }
        return output.ToString();
    }

    internal static int HexValue(char value) => value switch
    {
        >= '0' and <= '9' => value - '0',
        >= 'a' and <= 'f' => value - 'a' + 10,
        >= 'A' and <= 'F' => value - 'A' + 10,
        _ => throw new ArgumentOutOfRangeException(nameof(value)),
    };

    internal static void SkipCssWhitespaceAndComments(string css, ref int cursor)
    {
        while (cursor < css.Length)
        {
            if (char.IsWhiteSpace(css[cursor]))
            {
                cursor++;
                continue;
            }
            if (TrySkipCssComment(css, ref cursor))
                continue;
            break;
        }
    }

    internal static bool TrySkipCssComment(string css, ref int cursor)
    {
        if (cursor + 1 >= css.Length || css[cursor] != '/' || css[cursor + 1] != '*')
            return false;
        int end = css.IndexOf("*/", cursor + 2, StringComparison.Ordinal);
        if (end < 0)
            throw SvgPreflightFailures.Unsupported("A CSS comment is not terminated.");
        cursor = end + 2;
        return true;
    }

    internal static bool TrySkipCssString(string css, ref int cursor)
    {
        if (css[cursor] is not ('\'' or '"'))
            return false;
        char quote = css[cursor++];
        while (cursor < css.Length && css[cursor] != quote)
            cursor++;
        if (cursor >= css.Length)
            throw SvgPreflightFailures.Unsupported("A CSS string is not terminated.");
        cursor++;
        return true;
    }
}
