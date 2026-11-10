using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace MarkdownRenderer.Html.Internal.Renderers;

/// <summary>
/// Shifts the spans of a reparsed Markdown fragment into document coordinates.
/// </summary>
internal static class HtmlMarkdownSpanOffsets
{
    internal static void OffsetMarkdownSpans(ContainerBlock container, int offset)
    {
        OffsetMarkdownSpan(container, offset);
        foreach (Block block in container)
        {
            if (block is ContainerBlock childContainer)
            {
                OffsetMarkdownSpans(childContainer, offset);
            }
            else
            {
                OffsetMarkdownSpan(block, offset);
                if (block is LeafBlock { Inline: not null } leaf)
                {
                    OffsetInlineSpans(leaf.Inline, offset);
                }
            }
        }
    }

    private static void OffsetInlineSpans(ContainerInline container, int offset)
    {
        OffsetMarkdownSpan(container, offset);
        foreach (Inline inline in container)
        {
            if (inline is ContainerInline child)
            {
                OffsetInlineSpans(child, offset);
            }
            else
            {
                OffsetMarkdownSpan(inline, offset);
            }
        }
    }

    private static void OffsetMarkdownSpan(MarkdownObject node, int offset)
    {
        if (node.Span.Start < 0)
        {
            return;
        }

        node.Span = new Markdig.Syntax.SourceSpan(
            checked(node.Span.Start + offset),
            checked(node.Span.End + offset));
    }
}
