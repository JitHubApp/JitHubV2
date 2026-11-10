using System;
using System.Globalization;

namespace MarkdownRenderer.Parsing.Internal.SafeHtml;

/// <summary>
/// Interprets presentational attribute values: <c>align</c>, and lengths from
/// an attribute or the matching inline-style declaration.
/// </summary>
internal static class SafeHtmlAttributeValues
{
    internal static SafeHtmlAlignment GetAlignment(SafeHtmlElement element) =>
        element.TryGetAttribute("align", out string value)
            ? ParseAlignment(value)
            : SafeHtmlAlignment.Inherit;

    internal static SafeHtmlAlignment GetAlignment(SafeHtmlTag tag) =>
        tag.TryGetAttribute("align", out string value)
            ? ParseAlignment(value)
            : SafeHtmlAlignment.Inherit;

    internal static bool TryGetLength(SafeHtmlElement element, string name, out SafeHtmlLength length)
    {
        if (element.TryGetAttribute(name, out string value) && TryParseLength(value, out length))
        {
            return true;
        }

        if (element.TryGetAttribute("style", out string style) &&
            TryGetStyleDeclaration(style, name, out value) &&
            TryParseLength(value, out length))
        {
            return true;
        }

        length = default;
        return false;
    }

    internal static bool TryGetLength(SafeHtmlTag tag, string name, out SafeHtmlLength length)
    {
        if (tag.TryGetAttribute(name, out string value) && TryParseLength(value, out length))
        {
            return true;
        }

        if (tag.TryGetAttribute("style", out string style) &&
            TryGetStyleDeclaration(style, name, out value) &&
            TryParseLength(value, out length))
        {
            return true;
        }

        length = default;
        return false;
    }

    private static SafeHtmlAlignment ParseAlignment(string value) => value.Trim().ToLowerInvariant() switch
    {
        "left" => SafeHtmlAlignment.Left,
        "center" or "middle" => SafeHtmlAlignment.Center,
        "right" => SafeHtmlAlignment.Right,
        _ => SafeHtmlAlignment.Inherit,
    };

    private static bool TryParseLength(string value, out SafeHtmlLength length)
    {
        string candidate = value.Trim();
        bool percent = candidate.EndsWith('%');
        if (percent)
        {
            candidate = candidate[..^1].Trim();
        }
        else if (candidate.EndsWith("px", StringComparison.OrdinalIgnoreCase))
        {
            candidate = candidate[..^2].Trim();
        }

        if (!float.TryParse(candidate, NumberStyles.Float, CultureInfo.InvariantCulture, out float parsed) ||
            !float.IsFinite(parsed) ||
            parsed <= 0)
        {
            length = default;
            return false;
        }

        length = new SafeHtmlLength(percent ? Math.Min(parsed, 100) : Math.Min(parsed, SafeHtmlParser.MaximumAbsoluteLength), percent);
        return true;
    }

    private static bool TryGetStyleDeclaration(string style, string name, out string value)
    {
        value = string.Empty;
        ReadOnlySpan<char> remaining = style.AsSpan();
        while (!remaining.IsEmpty)
        {
            int separator = remaining.IndexOf(';');
            ReadOnlySpan<char> declaration = separator < 0 ? remaining : remaining[..separator];
            remaining = separator < 0 ? ReadOnlySpan<char>.Empty : remaining[(separator + 1)..];
            int colon = declaration.IndexOf(':');
            if (colon <= 0)
            {
                continue;
            }

            if (!declaration[..colon].Trim().Equals(name.AsSpan(), StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            value = declaration[(colon + 1)..].Trim().ToString();
            return true;
        }

        return false;
    }
}
