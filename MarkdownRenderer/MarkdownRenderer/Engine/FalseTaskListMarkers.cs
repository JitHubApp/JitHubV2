using System;
using System.Linq;
using Markdig;
using Markdig.Extensions.TaskLists;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace MarkdownRenderer;

/// <summary>
/// Markdig's task-list inline parser accepts <c>[X](</c>, <c>[x](</c> and
/// <c>[ ](</c> as a task marker, but GFM requires whitespace after the marker:
/// <c>- [X](https://x.com/…)</c> is a link labelled "X", as GitHub renders it.
/// Both the rendering parser and the public engine apply this repair so the
/// document they expose matches the view (P3-005).
/// </summary>
internal static class FalseTaskListMarkers
{
    internal static void Normalize(MarkdownDocument document, string source)
    {
        foreach (ParagraphBlock paragraph in document.Descendants<ParagraphBlock>())
        {
            if (paragraph.Inline?.FirstChild is not TaskList ||
                paragraph.Span.Start < 0 ||
                paragraph.Span.End < paragraph.Span.Start ||
                paragraph.Span.End >= source.Length)
            {
                continue;
            }

            int searchLength = Math.Min(16, paragraph.Span.End - paragraph.Span.Start + 1);
            int markerStart = source.IndexOf('[', paragraph.Span.Start, searchLength);
            if (markerStart < 0 ||
                markerStart + 2 >= source.Length ||
                source[markerStart + 2] != ']' ||
                source[markerStart + 1] is not (' ' or 'x' or 'X'))
            {
                continue;
            }

            int afterMarker = markerStart + 3;
            if (afterMarker >= source.Length || char.IsWhiteSpace(source[afterMarker]))
            {
                continue;
            }

            // Reparse only the affected paragraph without the task extension
            // and retain absolute source spans for selection, commands, and
            // diagnostics.
            int paragraphLength = paragraph.Span.End - paragraph.Span.Start + 1;
            string paragraphSource = source.Substring(paragraph.Span.Start, paragraphLength);
            MarkdownDocument reparsed = Markdown.Parse(paragraphSource);
            ParagraphBlock? repaired = reparsed.OfType<ParagraphBlock>().FirstOrDefault();
            if (repaired?.Inline is null)
            {
                continue;
            }

            ContainerInline repairedInline = repaired.Inline;
            OffsetInlineSpans(repairedInline, paragraph.Span.Start);
            repaired.Inline = null;
            paragraph.Inline = repairedInline;
        }
    }

    private static void OffsetInlineSpans(ContainerInline container, int offset)
    {
        foreach (Inline inline in container)
        {
            if (!inline.Span.IsEmpty)
            {
                inline.Span = new Markdig.Syntax.SourceSpan(
                    inline.Span.Start + offset,
                    inline.Span.End + offset);
            }

            if (inline is LinkInline link)
            {
                link.LabelSpan = OffsetSpan(link.LabelSpan, offset);
                link.UrlSpan = OffsetSpan(link.UrlSpan, offset);
                link.TitleSpan = OffsetSpan(link.TitleSpan, offset);
            }

            if (inline is ContainerInline nested)
            {
                OffsetInlineSpans(nested, offset);
            }
        }
    }

    private static Markdig.Syntax.SourceSpan OffsetSpan(
        Markdig.Syntax.SourceSpan span,
        int offset) => span.IsEmpty
        ? span
        : new Markdig.Syntax.SourceSpan(span.Start + offset, span.End + offset);
}
