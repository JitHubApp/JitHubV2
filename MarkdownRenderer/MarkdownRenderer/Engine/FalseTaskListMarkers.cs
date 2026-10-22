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
    internal static void Normalize(MarkdownDocument document, string source, MarkdownPipeline? pipeline = null)
    {
        foreach (ParagraphBlock paragraph in document.Descendants<ParagraphBlock>().ToArray())
        {
            if (paragraph.Inline is null ||
                paragraph.Span.Start < 0 ||
                paragraph.Span.End < paragraph.Span.Start ||
                paragraph.Span.End >= source.Length ||
                !HasFalseMarker(paragraph, source, out TaskList? realMarker))
            {
                continue;
            }

            // Reparse only the affected text as a standalone paragraph, where
            // no task marker can form (Markdig only creates one inside a list
            // item), and retain absolute source spans for selection, commands
            // and diagnostics. A real leading marker is kept as it is.
            int reparseStart = realMarker is null ? paragraph.Span.Start : realMarker.Span.End + 1;
            int reparseLength = paragraph.Span.End - reparseStart + 1;
            if (reparseLength <= 0)
                continue;

            string paragraphSource = source.Substring(reparseStart, reparseLength);
            MarkdownDocument reparsed = pipeline is null
                ? Markdown.Parse(paragraphSource)
                : Markdown.Parse(paragraphSource, pipeline);
            // Accept only a single paragraph: a container prefix on a later
            // line (a quote's "> ") would otherwise split the text and lose it.
            if (reparsed.Count != 1 || reparsed[0] is not ParagraphBlock { Inline: { } repairedInline } repaired)
            {
                continue;
            }

            OffsetInlineSpans(repairedInline, reparseStart);
            repaired.Inline = null;
            if (realMarker is not null)
            {
                realMarker.Remove();
                if (repairedInline.FirstChild is { } firstRepaired)
                    firstRepaired.InsertBefore(realMarker);
                else
                    repairedInline.AppendChild(realMarker);
            }

            paragraph.Inline = repairedInline;
        }
    }

    /// <summary>
    /// A GFM task marker is only the paragraph's first inline and must be
    /// followed by whitespace. Markdig also creates one mid-paragraph
    /// ("Follow us on [X](url)"), which is always false.
    /// </summary>
    private static bool HasFalseMarker(ParagraphBlock paragraph, string source, out TaskList? realMarker)
    {
        realMarker = null;
        bool leadingIsFalse = paragraph.Inline!.FirstChild is TaskList && IsLeadingMarkerFalse(paragraph, source);
        bool laterMarker = false;
        bool first = true;
        foreach (Inline inline in paragraph.Inline!)
        {
            if (inline is TaskList && !first)
            {
                laterMarker = true;
                break;
            }
            first = false;
        }

        if (laterMarker && paragraph.Inline!.FirstChild is TaskList leading && !leadingIsFalse)
            realMarker = leading;
        return leadingIsFalse || laterMarker;
    }

    private static bool IsLeadingMarkerFalse(ParagraphBlock paragraph, string source)
    {
        int searchLength = Math.Min(16, paragraph.Span.End - paragraph.Span.Start + 1);
        int markerStart = source.IndexOf('[', paragraph.Span.Start, searchLength);
        if (markerStart < 0 ||
            markerStart + 2 >= source.Length ||
            source[markerStart + 2] != ']' ||
            source[markerStart + 1] is not (' ' or 'x' or 'X'))
        {
            return false;
        }

        int afterMarker = markerStart + 3;
        return afterMarker < source.Length && !char.IsWhiteSpace(source[afterMarker]);
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
