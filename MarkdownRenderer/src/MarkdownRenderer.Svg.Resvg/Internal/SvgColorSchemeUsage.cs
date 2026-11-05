using System.Collections.Frozen;
using System.Security.Cryptography;
using System.Text;
using System.Xml;
using MarkdownRenderer.Images;

namespace MarkdownRenderer.Svg.Resvg.Internal;

/// <summary>
/// Detects whether a document's paint depends on <c>currentColor</c> or on the
/// <c>prefers-color-scheme</c> media feature, so the host knows to re-render on theme change.
/// </summary>
internal static class SvgColorSchemeUsage
{
    private static readonly FrozenSet<string> CurrentColorPresentationAttributes = FrozenSet.Create(
        StringComparer.OrdinalIgnoreCase,
        "color", "fill", "flood-color", "lighting-color", "stop-color", "stroke",
        "text-decoration", "text-decoration-color");

    internal static bool IsCurrentColorPresentationAttribute(string attributeName) =>
        CurrentColorPresentationAttributes.Contains(attributeName);

    internal static bool UsesCurrentColorInCss(string css, bool isDeclarationList)
    {
        int cursor = 0;
        int declarationStart = 0;
        int blockDepth = 0;
        int parentheses = 0;
        while (cursor < css.Length)
        {
            if (SvgPreflightCss.TrySkipCssComment(css, ref cursor) || SvgPreflightCss.TrySkipCssString(css, ref cursor))
                continue;

            char current = css[cursor];
            if (current == '(')
            {
                parentheses++;
            }
            else if (current == ')' && parentheses > 0)
            {
                parentheses--;
            }
            else if (parentheses == 0 && current == '{')
            {
                blockDepth++;
                declarationStart = cursor + 1;
            }
            else if (parentheses == 0 && current == ';')
            {
                if ((blockDepth > 0 || isDeclarationList) &&
                    DeclarationValueUsesCurrentColor(css.AsSpan(declarationStart, cursor - declarationStart)))
                {
                    return true;
                }
                declarationStart = cursor + 1;
            }
            else if (parentheses == 0 && current == '}')
            {
                if (blockDepth > 0 &&
                    DeclarationValueUsesCurrentColor(css.AsSpan(declarationStart, cursor - declarationStart)))
                {
                    return true;
                }
                if (blockDepth > 0)
                    blockDepth--;
                declarationStart = cursor + 1;
            }
            cursor++;
        }

        return (blockDepth > 0 || isDeclarationList) &&
            DeclarationValueUsesCurrentColor(css.AsSpan(declarationStart));
    }

    internal static bool DeclarationValueUsesCurrentColor(ReadOnlySpan<char> declaration)
    {
        int parentheses = 0;
        int cursor = 0;
        while (cursor < declaration.Length)
        {
            if (cursor + 1 < declaration.Length && declaration[cursor] == '/' && declaration[cursor + 1] == '*')
            {
                int commentEnd = declaration[(cursor + 2)..].IndexOf("*/".AsSpan(), StringComparison.Ordinal);
                if (commentEnd < 0)
                    return false;
                cursor += commentEnd + 4;
                continue;
            }
            if (declaration[cursor] is '\'' or '"')
            {
                char quote = declaration[cursor++];
                while (cursor < declaration.Length && declaration[cursor] != quote)
                {
                    if (declaration[cursor] == '\\' && cursor + 1 < declaration.Length)
                        cursor++;
                    cursor++;
                }
                if (cursor < declaration.Length)
                    cursor++;
                continue;
            }

            if (declaration[cursor] == '(')
                parentheses++;
            else if (declaration[cursor] == ')' && parentheses > 0)
                parentheses--;
            else if (declaration[cursor] == ':' && parentheses == 0)
            {
                ReadOnlySpan<char> property = declaration[..cursor].Trim();
                if (!property.IsEmpty && property[0] != '@')
                    return SvgPreflightCss.ContainsCssIdentifier(declaration[(cursor + 1)..], "currentColor");
                return false;
            }
            cursor++;
        }

        return false;
    }

    internal static bool UsesPrefersColorSchemeInCss(string css)
    {
        int cursor = 0;
        while (cursor < css.Length)
        {
            if (SvgPreflightCss.TrySkipCssComment(css, ref cursor) || SvgPreflightCss.TrySkipCssString(css, ref cursor))
                continue;
            if (!css.AsSpan(cursor).StartsWith("@media", StringComparison.OrdinalIgnoreCase) ||
                (cursor + 6 < css.Length && SvgPreflightCss.IsCssIdentifierCharacter(css[cursor + 6])))
            {
                cursor++;
                continue;
            }

            int conditionStart = cursor + 6;
            int conditionEnd = conditionStart;
            int parentheses = 0;
            while (conditionEnd < css.Length)
            {
                if (SvgPreflightCss.TrySkipCssComment(css, ref conditionEnd) || SvgPreflightCss.TrySkipCssString(css, ref conditionEnd))
                    continue;
                if (css[conditionEnd] == '(')
                    parentheses++;
                else if (css[conditionEnd] == ')' && parentheses > 0)
                    parentheses--;
                else if (css[conditionEnd] == '{' && parentheses == 0)
                    break;
                conditionEnd++;
            }

            if (SvgPreflightCss.ContainsCssIdentifier(css[conditionStart..conditionEnd], "prefers-color-scheme"))
                return true;
            cursor = conditionEnd < css.Length ? conditionEnd + 1 : conditionEnd;
        }

        return false;
    }

    internal static bool UsesPrefersColorSchemeMediaCondition(string value) =>
        SvgPreflightCss.ContainsCssIdentifier(value, "prefers-color-scheme");
}
