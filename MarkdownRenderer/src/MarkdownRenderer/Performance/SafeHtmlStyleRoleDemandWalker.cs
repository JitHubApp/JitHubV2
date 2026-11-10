using System;
using System.Collections.Generic;
using System.Threading;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using MarkdownRenderer.Layout;
using MarkdownRenderer.Parsing.Internal.SafeHtml;

namespace MarkdownRenderer.Performance;

/// <summary>
/// Records the style roles a raw HTML block can read through the SafeHtml
/// renderer, mirroring its traversal, and marks the result incomplete for the
/// shapes that SafeHtml reparses as Markdown.
/// </summary>
internal sealed class SafeHtmlStyleRoleDemandWalker
{
    private readonly MarkdownStyleRoleDemandAccumulator _demand;
    private readonly CancellationToken _cancellationToken;

    internal SafeHtmlStyleRoleDemandWalker(
        MarkdownStyleRoleDemandAccumulator demand,
        CancellationToken cancellationToken)
    {
        _demand = demand;
        _cancellationToken = cancellationToken;
    }

    internal void VisitHtmlBlock(HtmlBlock htmlBlock)
    {
        string source = htmlBlock.Lines.ToString();

        // SafeHtml can reparse Markdown in two narrowly defined cases:
        // Markdown after a void-tag-only prefix, and one-text-child
        // presentation containers. The nested pipeline can produce any
        // Markdown/extension role, so retain full resolution instead of
        // trying to duplicate that pipeline here. Detect the shape even
        // when the feature is currently disabled: this keeps the result
        // independent of optional-pack registration and registry cache
        // identity.
        if (HasMarkdownTailAfterVoidTags(source))
        {
            _demand.MarkIncomplete(htmlBlock);
            return;
        }

        SafeHtmlDocument document = SafeHtmlParser.Parse(
            source,
            SafeHtmlParseLimits.Default,
            _cancellationToken);
        if (document.IsTruncated || HasMarkdownContainerReparseCandidate(document.Root))
        {
            _demand.MarkIncomplete(htmlBlock);
            return;
        }

        VisitSafeHtmlNodes(document.Root.Children);
    }

    private bool HasMarkdownContainerReparseCandidate(SafeHtmlElement root)
    {
        foreach (SafeHtmlNode node in root.Children)
        {
            _cancellationToken.ThrowIfCancellationRequested();
            if (node is not SafeHtmlElement element ||
                SafeHtmlParser.IsSuppressedElement(element.Name))
            {
                continue;
            }

            if (element.Name is ("article" or "aside" or "center" or "div" or "main" or "section") &&
                element.Children.Count == 1 &&
                element.Children[0] is SafeHtmlText text &&
                !string.IsNullOrWhiteSpace(text.RawText))
            {
                return true;
            }

            if (HasMarkdownContainerReparseCandidate(element))
                return true;
        }

        return false;
    }

    private bool HasMarkdownTailAfterVoidTags(string source)
    {
        int lineStart = 0;
        while (lineStart < source.Length)
        {
            _cancellationToken.ThrowIfCancellationRequested();
            int lineEnd = source.IndexOfAny(['\r', '\n'], lineStart);
            if (lineEnd < 0)
                break;

            int nextLine = lineEnd + 1;
            if (source[lineEnd] == '\r' && nextLine < source.Length && source[nextLine] == '\n')
                nextLine++;

            if (lineStart > 0 &&
                source.AsSpan(lineStart, lineEnd - lineStart).Trim().IsEmpty &&
                nextLine < source.Length)
            {
                if (!SafeHtmlParser.TryParseTagSequence(
                        source[..lineStart],
                        SafeHtmlParseLimits.Default,
                        _cancellationToken,
                        out IReadOnlyList<SafeHtmlTag> tags,
                        out _) || tags.Count == 0)
                {
                    return false;
                }

                foreach (SafeHtmlTag tag in tags)
                {
                    if (tag.Kind != SafeHtmlTagKind.SelfClosing)
                        return false;
                }

                return true;
            }

            lineStart = nextLine;
        }

        return false;
    }

    private void VisitSafeHtmlNodes(IReadOnlyList<SafeHtmlNode> nodes)
    {
        foreach (SafeHtmlNode node in nodes)
        {
            _cancellationToken.ThrowIfCancellationRequested();
            if (node is not SafeHtmlElement element ||
                SafeHtmlParser.IsSuppressedElement(element.Name) ||
                element.Name is "form" or "button")
            {
                continue;
            }

            if (!AddElementDemand(element))
                continue;

            // Unknown elements are either rendered literally or unwrapped
            // by SafeHtml. Walking descendants is a safe superset for both
            // policies and catches recognized formatting nested inside an
            // unsupported wrapper.
            VisitSafeHtmlNodes(element.Children);
        }
    }

    /// <summary>
    /// Records the roles one rendered element reads. Returns false for elements
    /// whose descendants SafeHtml does not style independently.
    /// </summary>
    private bool AddElementDemand(SafeHtmlElement element)
    {
        switch (element.Name)
        {
            case "h1": _demand.Add(MarkdownStyleRoleDemand.Heading1); break;
            case "h2": _demand.Add(MarkdownStyleRoleDemand.Heading2); break;
            case "h3": _demand.Add(MarkdownStyleRoleDemand.Heading3); break;
            case "h4": _demand.Add(MarkdownStyleRoleDemand.Heading4); break;
            case "h5": _demand.Add(MarkdownStyleRoleDemand.Heading5); break;
            case "h6": _demand.Add(MarkdownStyleRoleDemand.Heading6); break;
            case "a": _demand.Add(MarkdownStyleRoleDemand.Link); break;
            case "b":
            case "strong": _demand.Add(MarkdownStyleRoleDemand.Strong); break;
            case "i":
            case "em":
            case "cite":
            case "var": _demand.Add(MarkdownStyleRoleDemand.Emphasis); break;
            case "s":
            case "strike":
            case "del": _demand.Add(MarkdownStyleRoleDemand.Strikethrough); break;
            case "sub": _demand.Add(MarkdownStyleRoleDemand.Subscript); break;
            case "sup": _demand.Add(MarkdownStyleRoleDemand.Superscript); break;
            case "ins":
            case "u": _demand.Add(MarkdownStyleRoleDemand.Inserted); break;
            case "mark": _demand.Add(MarkdownStyleRoleDemand.Marked); break;
            case "code":
            case "kbd":
            case "samp": _demand.Add(MarkdownStyleRoleDemand.CodeInline); break;
            case "blockquote": _demand.Add(MarkdownStyleRoleDemand.Quote); break;
            case "ul":
            case "ol": _demand.Add(MarkdownStyleRoleDemand.ListMarker); break;
            case "hr": _demand.Add(MarkdownStyleRoleDemand.ThematicBreak); break;
            case "table": _demand.AddTableDemand(); break;
            case "pre":
                _demand.AddCodeBlockDemand();
                // SafeHtml flattens preformatted descendants to text;
                // nested inline markup does not acquire its own role.
                return false;
            case "details":
                // The disclosure control always reads Body. Its
                // summary defaults to Strong unless a nested heading
                // supplies the semantic style. The renderer also
                // searches the summary for headings/pre blocks even
                // below inline wrappers that normal HTML traversal
                // prunes, so collect those special reads explicitly.
                SafeHtmlElement? summary = FindDirectChild(element, "summary");
                MarkdownStyleRoleDemand summaryHeading = FindDescendantHeadingDemand(summary);
                _demand.Add(MarkdownStyleRoleDemand.Body |
                    (summaryHeading == MarkdownStyleRoleDemand.None
                        ? MarkdownStyleRoleDemand.Strong
                        : summaryHeading));
                if (ContainsDescendantPreformatted(summary))
                    _demand.AddCodeBlockDemand();
                break;
            case "img":
            case "picture":
            case "svg":
            case "video":
            case "audio":
                _demand.Add(MarkdownStyleRoleDemand.ImageCaption);
                // These are lowered to one atomic image run; their
                // parsed HTML descendants are metadata/source inputs,
                // not independently styled document content.
                return false;
            case "input":
                if (element.TryGetAttribute("type", out string inputType) &&
                    inputType.Equals("checkbox", StringComparison.OrdinalIgnoreCase))
                {
                    _demand.Add(MarkdownStyleRoleDemand.ListMarker);
                }
                break;
        }

        return true;
    }

    private static SafeHtmlElement? FindDirectChild(SafeHtmlElement parent, string name)
    {
        foreach (SafeHtmlNode node in parent.Children)
        {
            if (node is SafeHtmlElement element && element.Name == name)
                return element;
        }

        return null;
    }

    private static MarkdownStyleRoleDemand FindDescendantHeadingDemand(SafeHtmlElement? parent)
    {
        if (parent is null)
            return MarkdownStyleRoleDemand.None;

        // Match HtmlElementQueries.FindDescendantHeadingKey: first heading
        // in document order wins, including a heading nested in pre,
        // suppressed wrappers, or otherwise non-rendered inline wrappers.
        foreach (SafeHtmlNode node in parent.Children)
        {
            if (node is not SafeHtmlElement element)
                continue;

            MarkdownStyleRoleDemand heading = element.Name switch
            {
                "h1" => MarkdownStyleRoleDemand.Heading1,
                "h2" => MarkdownStyleRoleDemand.Heading2,
                "h3" => MarkdownStyleRoleDemand.Heading3,
                "h4" => MarkdownStyleRoleDemand.Heading4,
                "h5" => MarkdownStyleRoleDemand.Heading5,
                "h6" => MarkdownStyleRoleDemand.Heading6,
                _ => MarkdownStyleRoleDemand.None,
            };
            if (heading != MarkdownStyleRoleDemand.None)
                return heading;

            heading = FindDescendantHeadingDemand(element);
            if (heading != MarkdownStyleRoleDemand.None)
                return heading;
        }

        return MarkdownStyleRoleDemand.None;
    }

    private static bool ContainsDescendantPreformatted(SafeHtmlElement? parent)
    {
        if (parent is null)
            return false;

        // Match HtmlElementQueries.DescendantPreformatted, which searches
        // through form/button wrappers but skips active/raw-content trees.
        foreach (SafeHtmlNode node in parent.Children)
        {
            if (node is not SafeHtmlElement element ||
                SafeHtmlParser.IsSuppressedElement(element.Name))
            {
                continue;
            }

            if (element.Name == "pre" || ContainsDescendantPreformatted(element))
                return true;
        }

        return false;
    }

    internal static bool IsRoleNeutralUnsupportedHtml(HtmlInline html)
    {
        // An unparsable tag remains conservative: a future policy could
        // interpret it differently. Parsed comments, declarations, raw
        // suppressed elements, and unsupported custom elements are either
        // literal text or omitted and never acquire a style role.
        if (!SafeHtmlParser.TryParseSingleTag(html.Tag, out SafeHtmlTag tag))
            return false;
        return tag.Kind is SafeHtmlTagKind.Comment or SafeHtmlTagKind.Declaration ||
            SafeHtmlParser.IsSuppressedElement(tag.Name) ||
            !SafeHtmlInlineElementNames.IsSupportedElement(tag.Name);
    }
}
