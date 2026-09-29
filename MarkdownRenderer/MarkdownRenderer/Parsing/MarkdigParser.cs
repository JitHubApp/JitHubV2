using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Markdig;
using Markdig.Extensions.TaskLists;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace MarkdownRenderer.Parsing;

internal sealed class MarkdigParser
{
    private readonly MarkdownPipeline _pipeline;

    public MarkdigParser(MarkdownPipeline pipeline)
    {
        _pipeline = pipeline ?? throw new ArgumentNullException(nameof(pipeline));
    }

    public Task<ParsedMarkdown?> ParseAsync(string source, CancellationToken ct = default)
    {
        // Fast-path early cancellation without producing a canceled Task. The
        // renderer treats null as cooperative cancellation, avoiding noisy
        // first-chance OperationCanceledException/TaskCanceledException output.
        if (ct.IsCancellationRequested)
            return Task.FromResult<ParsedMarkdown?>(null);

        return Task.Run(() =>
        {
            // Markdown.Parse is synchronous and not cancellation-aware; we just
            // observe the token cooperatively before paying the parse cost.
            // Use IsCancellationRequested + return rather than ThrowIfCancellationRequested
            // so we don't generate an exception object on the cancelled path.
            if (ct.IsCancellationRequested)
                return null;
            var fixedSource = ForgivingDataUriFixer.Fix(source ?? string.Empty);
            var document = Markdown.Parse(fixedSource, _pipeline);
            NormalizeFalseTaskListMarkers(document, fixedSource);
            return (ParsedMarkdown?)new ParsedMarkdown(fixedSource, document);
        }, CancellationToken.None);
    }

    public ParsedMarkdown Parse(string source)
    {
        var fixedSource = ForgivingDataUriFixer.Fix(source ?? string.Empty);
        var document = Markdown.Parse(fixedSource, _pipeline);
        NormalizeFalseTaskListMarkers(document, fixedSource);
        return new ParsedMarkdown(fixedSource, document);
    }

    private static void NormalizeFalseTaskListMarkers(MarkdownDocument document, string source)
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

            // Markdig's task-list inline parser accepts `[X](` even though this
            // is a normal Markdown link label, not a GFM task marker. Reparse
            // only the affected paragraph without the task extension and retain
            // absolute source spans for selection, commands, and diagnostics.
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

internal sealed record ParsedMarkdown(string SourceText, MarkdownDocument Document);
