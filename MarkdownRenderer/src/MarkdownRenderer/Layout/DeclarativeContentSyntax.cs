using System;
using MarkdownRenderer.Extensions;
using MarkdownRenderer.Parsing.Internal.SafeHtml;

namespace MarkdownRenderer.Layout;

/// <summary>
/// Attribute, text and length readers for declarative hosted-element content.
/// </summary>
internal static class DeclarativeContentSyntax
{
    internal static bool AllChildren(
        MarkdownContent content,
        Func<MarkdownContent, bool> predicate)
    {
        foreach (MarkdownContent child in content.Children)
        {
            if (!predicate(child))
                return false;
        }

        return true;
    }

    internal static string CollectDeclarativeText(MarkdownContent content)
    {
        var builder = new System.Text.StringBuilder();
        Append(content, builder);
        return builder.Length == 0 ? content.SemanticText ?? string.Empty : builder.ToString();

        static void Append(MarkdownContent node, System.Text.StringBuilder target)
        {
            if (node.Kind == MarkdownContentKind.Text || node.Kind == MarkdownContentKind.Image)
                target.Append(node.Text);
            foreach (MarkdownContent child in node.Children)
                Append(child, target);
        }
    }

    internal static string GetElementKey(MarkdownContent content, string fallback) =>
        content.StyleRole.IsEmpty ? fallback : content.StyleRole.Name;

    internal static string? GetAttribute(MarkdownContent content, string name) =>
        content.Attributes.TryGetValue(name, out string? value) ? value : null;

    internal static bool GetBooleanAttribute(MarkdownContent content, string name) =>
        content.Attributes.TryGetValue(name, out string? value) &&
        bool.TryParse(value, out bool parsed) && parsed;

    internal static int GetPositiveIntegerAttribute(MarkdownContent content, string name, int fallback) =>
        content.Attributes.TryGetValue(name, out string? value) &&
        int.TryParse(
            value,
            System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture,
            out int parsed)
            ? Math.Max(1, parsed)
            : fallback;

    internal static bool TryGetDeclarativeLength(
        MarkdownContent content,
        string name,
        out SafeHtmlLength length)
    {
        length = default;
        if (!content.Attributes.TryGetValue(name, out string? raw))
            return false;

        string value = raw.Trim();
        bool percent = value.EndsWith('%');
        if (percent)
            value = value[..^1].Trim();
        if (!float.TryParse(
                value,
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture,
                out float parsed) ||
            !float.IsFinite(parsed) ||
            parsed <= 0)
        {
            return false;
        }

        length = new SafeHtmlLength(
            percent ? Math.Min(parsed, 100f) : Math.Min(parsed, 8192f),
            percent);
        return true;
    }
}
