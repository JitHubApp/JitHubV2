using System;
using System.Collections.Generic;
using Markdig.Syntax.Inlines;
using MarkdownRenderer.Theming;

namespace MarkdownRenderer.Layout;

/// <summary>
/// Pure syntax helpers for inline construction: emphasis keys, style aliases,
/// containing links, image-only links and superscript text.
/// </summary>
internal static class MarkdownInlineSyntax
{
    internal static InlineRun? ApplyContainingLink(
        InlineRun? run,
        string? containingLinkUrl,
        string? containingLinkTitle)
    {
        if (run is null ||
            run is LinkRun or InlineImageRun or InlineSpacerRun ||
            string.IsNullOrWhiteSpace(containingLinkUrl))
        {
            return run;
        }

        if (run is InlineVectorSceneRun vectorScene)
        {
            vectorScene.LinkUrl = containingLinkUrl;
            vectorScene.LinkTitle = containingLinkTitle;
            vectorScene.StyleModifierKeys = AppendStyleModifier(
                vectorScene.StyleModifierKeys,
                MarkdownElementKeys.Link);
            return vectorScene;
        }

        var linked = new LinkRun(run.Text, containingLinkUrl, containingLinkTitle)
        {
            SourceSpan = run.SourceSpan,
            StyleAliases = run.StyleAliases,
            ScriptStyleAncestryKeys = run.ScriptStyleAncestryKeys,
            SafeHtmlScriptRoleIsRunElement = run.SafeHtmlScriptRoleIsRunElement,
        };
        linked.StyleModifierKeys = string.IsNullOrEmpty(run.ElementKey)
            ? run.StyleModifierKeys
            : AppendStyleModifier(run.StyleModifierKeys, run.ElementKey);
        return linked;
    }

    internal static bool ContainsMarkdownLink(ContainerInline container)
    {
        foreach (Inline child in container)
        {
            // An autolink inside emphasis is a link too; flattening the
            // emphasis would drop it.
            if (child is LinkInline or AutolinkInline)
                return true;

            if (child is ContainerInline nested && ContainsMarkdownLink(nested))
                return true;
        }

        return false;
    }

    internal static string GetEmphasisElementKey(EmphasisInline emphasis)
    {
        if (emphasis.DelimiterChar == '~' && emphasis.DelimiterCount >= 2)
            return MarkdownElementKeys.Strikethrough;
        if (emphasis.DelimiterChar == '~')
            return MarkdownElementKeys.Subscript;
        if (emphasis.DelimiterChar == '^')
            return MarkdownElementKeys.Superscript;
        if (emphasis.DelimiterChar == '+')
            return MarkdownElementKeys.Inserted;
        if (emphasis.DelimiterChar == '=')
            return MarkdownElementKeys.Marked;
        return emphasis.DelimiterCount >= 2
            ? MarkdownElementKeys.Strong
            : MarkdownElementKeys.Emphasis;
    }

    internal static IReadOnlyList<string> AppendStyleModifier(
        IReadOnlyList<string> modifiers,
        string elementKey)
    {
        var result = new string[modifiers.Count + 1];
        for (int index = 0; index < modifiers.Count; index++)
            result[index] = modifiers[index];
        result[^1] = elementKey;
        return result;
    }

    internal static IReadOnlyList<string> CombineStyleAliases(
        IReadOnlyList<string> first,
        IReadOnlyList<string> second)
    {
        if (first.Count == 0)
            return second;
        if (second.Count == 0)
            return first;

        var combined = new string[first.Count + second.Count];
        for (int index = 0; index < first.Count; index++)
            combined[index] = first[index];
        for (int index = 0; index < second.Count; index++)
            combined[first.Count + index] = second[index];
        return combined;
    }

    internal static bool TryGetOnlyImageChild(ContainerInline container, out LinkInline imageLink)
    {
        imageLink = null!;
        int count = 0;
        foreach (var child in container)
        {
            count++;
            if (count > 1)
            {
                imageLink = null!;
                return false;
            }

            if (child is LinkInline { IsImage: true } image)
            {
                imageLink = image;
                continue;
            }

            imageLink = null!;
            return false;
        }

        return imageLink is not null;
    }

    internal static bool TryGetOnlyHtmlImageChild(
        ContainerInline container,
        out HtmlInline htmlImage)
    {
        htmlImage = null!;
        int count = 0;
        foreach (Inline child in container)
        {
            if (++count > 1 || child is not HtmlInline html)
            {
                htmlImage = null!;
                return false;
            }

            htmlImage = html;
        }

        return htmlImage is not null;
    }

    internal static SourceSpan GetLinkedHtmlImageSourceSpan(
        LinkInline link,
        HtmlInline htmlImage,
        MarkdownLayoutContext context)
    {
        string source = context.SourceMap.SourceText;
        int tagStart = source.IndexOf(
            htmlImage.Tag,
            Math.Clamp(link.Span.Start, 0, source.Length),
            StringComparison.Ordinal);
        if (tagStart >= 0)
        {
            int sourceStart = tagStart > 0 && source[tagStart - 1] == '[' ? tagStart - 1 : tagStart;
            int endExclusive = tagStart + htmlImage.Tag.Length;
            if (endExclusive + 1 < source.Length &&
                source[endExclusive] == ']' &&
                source[endExclusive + 1] == '(')
            {
                int closingParenthesis = FindLinkClosingParenthesis(source, endExclusive + 2);
                if (closingParenthesis >= 0)
                    endExclusive = closingParenthesis + 1;
            }

            return new SourceSpan(sourceStart, endExclusive - sourceStart);
        }

        int start = Math.Max(0, Math.Min(link.Span.Start, htmlImage.Span.Start));
        int inclusiveEnd = Math.Max(link.Span.End, htmlImage.Span.End);
        if (!link.UrlSpan.IsEmpty)
            inclusiveEnd = Math.Max(inclusiveEnd, link.UrlSpan.End);

        inclusiveEnd = Math.Min(inclusiveEnd, source.Length - 1);
        if (inclusiveEnd + 1 < source.Length && source[inclusiveEnd + 1] == ')')
            inclusiveEnd++;

        return new SourceSpan(start, Math.Max(0, inclusiveEnd - start + 1));
    }

    internal static int FindLinkClosingParenthesis(string source, int destinationStart)
    {
        int depth = 1;
        bool escaped = false;
        for (int index = destinationStart; index < source.Length; index++)
        {
            char current = source[index];
            if (escaped)
            {
                escaped = false;
                continue;
            }

            if (current == '\\')
            {
                escaped = true;
                continue;
            }

            if (current == '(')
                depth++;
            else if (current == ')' && --depth == 0)
                return index;
        }

        return -1;
    }

    internal static string ToSuperscript(int n)
    {
        const string digits = "\u2070\u00B9\u00B2\u00B3\u2074\u2075\u2076\u2077\u2078\u2079";
        var sb = new System.Text.StringBuilder();
        foreach (char c in n.ToString())
            sb.Append(c >= '0' && c <= '9' ? digits[c - '0'] : c);
        return sb.ToString();
    }
}
