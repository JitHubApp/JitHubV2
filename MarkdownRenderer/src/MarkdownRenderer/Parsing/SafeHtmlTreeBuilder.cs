using System;
using System.Collections.Generic;
using System.Threading;
using static MarkdownRenderer.Parsing.SafeHtmlScanner;

namespace MarkdownRenderer.Parsing;

/// <summary>
/// Builds the bounded safe HTML element tree: text and element nodes, the
/// open-element stack, implicit paragraph closure, and raw-content skipping.
/// </summary>
internal static class SafeHtmlTreeBuilder
{
    // These start tags close an open paragraph in HTML's "in body" insertion
    // mode. Keep this compatibility behavior deliberately bounded: this is not
    // a general-purpose HTML5 tree builder, and it does not synthesize nodes.
    private static readonly HashSet<string> ParagraphClosingBlockStarts = new(StringComparer.OrdinalIgnoreCase)
    {
        "address", "article", "aside", "blockquote", "center", "details", "dialog", "dir", "div", "dl",
        "fieldset", "figcaption", "figure", "footer", "form", "h1", "h2", "h3", "h4", "h5", "h6",
        "header", "hgroup", "hr", "listing", "main", "menu", "nav", "ol", "p", "pre", "search",
        "section", "summary", "table", "ul",
    };

    // A paragraph is only implicitly closed when it is in button scope. These
    // elements stop the backwards stack search, matching the relevant HTML
    // scope boundaries without changing the parser's existing safety model.
    private static readonly HashSet<string> ButtonScopeBoundaries = new(StringComparer.OrdinalIgnoreCase)
    {
        "applet", "button", "caption", "marquee", "object", "table", "td", "th", "template",
    };

    internal static SafeHtmlDocument Build(
        string? html,
        SafeHtmlParseLimits limits,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string source = html ?? string.Empty;
        int parseLength = Math.Min(source.Length, limits.MaxInputLength);
        bool truncated = source.Length > parseLength;
        var root = new SafeHtmlElement("#document", null, 0, parseLength);
        List<SafeHtmlElement> stack = [root];
        int nodeCount = 1;
        int position = 0;
        var scanBudget = new SafeHtmlScanBudget(parseLength);

        while (position < parseLength && nodeCount < limits.MaxNodeCount)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int tagStart = FindCharacter(source, '<', position, parseLength, cancellationToken);
            if (tagStart < 0)
            {
                AddText(source, position, parseLength - position, stack[^1], ref nodeCount, limits.MaxNodeCount);
                position = parseLength;
                break;
            }

            if (tagStart > position)
            {
                AddText(source, position, tagStart - position, stack[^1], ref nodeCount, limits.MaxNodeCount);
                if (nodeCount >= limits.MaxNodeCount)
                {
                    break;
                }
            }

            if (!SafeHtmlTagReader.TryReadTag(
                    source,
                    tagStart,
                    parseLength,
                    limits,
                    cancellationToken,
                    ref scanBudget,
                    out SafeHtmlTag tag,
                    out int endExclusive))
            {
                if (scanBudget.IsExhausted)
                {
                    truncated = true;
                    break;
                }

                AddText(source, tagStart, 1, stack[^1], ref nodeCount, limits.MaxNodeCount);
                position = tagStart + 1;
                continue;
            }

            position = endExclusive;
            if (!ApplyTag(
                    source,
                    tag,
                    stack,
                    ref nodeCount,
                    ref position,
                    parseLength,
                    limits,
                    cancellationToken))
            {
                truncated = true;
            }
        }

        if (position < parseLength || nodeCount >= limits.MaxNodeCount)
        {
            truncated = true;
        }

        return new SafeHtmlDocument(root, truncated);
    }

    /// <summary>
    /// Applies one tag token to the open-element stack. Returns false only when
    /// the nesting ceiling kept an element from opening its scope, which marks
    /// the document truncated.
    /// </summary>
    private static bool ApplyTag(
        string source,
        in SafeHtmlTag tag,
        List<SafeHtmlElement> stack,
        ref int nodeCount,
        ref int position,
        int parseLength,
        SafeHtmlParseLimits limits,
        CancellationToken cancellationToken)
    {
        if (tag.Kind is SafeHtmlTagKind.Comment or SafeHtmlTagKind.Declaration)
        {
            return true;
        }

        if (tag.Kind == SafeHtmlTagKind.Closing)
        {
            PopThroughMatchingElement(
                stack,
                tag.Name,
                source.Substring(tag.SourceStart, tag.SourceLength),
                tag.SourceStart);
            return true;
        }

        CloseOpenParagraphForBlockStart(stack, tag.Name);

        var element = new SafeHtmlElement(
            tag.Name,
            tag.Attributes,
            tag.SourceStart,
            tag.SourceLength,
            source.Substring(tag.SourceStart, tag.SourceLength));
        stack[^1].Add(element);
        nodeCount++;

        if (tag.Kind == SafeHtmlTagKind.SelfClosing || SafeHtmlElementCategories.IsVoidElement(tag.Name))
        {
            return true;
        }

        if (SafeHtmlElementCategories.IsRawContentElement(tag.Name))
        {
            int trailingStart = position;
            position = SkipRawContent(
                source,
                position,
                parseLength,
                tag.Name,
                cancellationToken);
            element.SetTrailingMarkup(
                source.Substring(trailingStart, position - trailingStart),
                trailingStart);
            return true;
        }

        if (stack.Count - 1 < limits.MaxNestingDepth)
        {
            stack.Add(element);
            return true;
        }

        return false;
    }

    private static void AddText(
        string source,
        int start,
        int length,
        SafeHtmlElement parent,
        ref int nodeCount,
        int maxNodeCount)
    {
        if (length <= 0 || nodeCount >= maxNodeCount)
        {
            return;
        }

        parent.Add(new SafeHtmlText(source.Substring(start, length), start, length));
        nodeCount++;
    }

    private static void PopThroughMatchingElement(
        List<SafeHtmlElement> stack,
        string name,
        string rawClosingTag,
        int rawClosingStart)
    {
        for (int index = stack.Count - 1; index > 0; index--)
        {
            if (!stack[index].Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            stack[index].SetClosingTag(rawClosingTag, rawClosingStart);
            stack.RemoveRange(index, stack.Count - index);
            return;
        }
    }

    private static void CloseOpenParagraphForBlockStart(
        List<SafeHtmlElement> stack,
        string startTagName)
    {
        if (!ParagraphClosingBlockStarts.Contains(startTagName))
        {
            return;
        }

        for (int index = stack.Count - 1; index > 0; index--)
        {
            string openName = stack[index].Name;
            if (openName.Equals("p", StringComparison.OrdinalIgnoreCase))
            {
                // Implicit closure has no source token. Pop the paragraph and
                // any malformed inline descendants without fabricating a
                // closing-tag span or disturbing their original source spans.
                stack.RemoveRange(index, stack.Count - index);
                return;
            }

            if (ButtonScopeBoundaries.Contains(openName))
            {
                return;
            }
        }
    }

    private static int SkipRawContent(
        string source,
        int position,
        int parseLength,
        string name,
        CancellationToken cancellationToken)
    {
        string closingPrefix = $"</{name}";
        int closingStart = FindSequence(
            source,
            closingPrefix,
            position,
            parseLength,
            StringComparison.OrdinalIgnoreCase,
            cancellationToken);
        if (closingStart < 0)
        {
            return parseLength;
        }

        int close = FindCharacter(
            source,
            '>',
            closingStart + closingPrefix.Length,
            parseLength,
            cancellationToken);
        return close < 0 ? parseLength : close + 1;
    }
}
