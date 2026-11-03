using System;
using System.Collections.Generic;
using System.Text;
using Markdig.Syntax;
using MarkdownRenderer.Layout;

namespace MarkdownRenderer.Html.Renderers;

/// <summary>
/// Builds the Markdown source for a nested fragment: the fragment text plus
/// the root document's link reference definitions it may use, with an
/// optional projection back to authored source.
/// </summary>
internal static class HtmlNestedMarkdownSource
{
    internal static string BuildNestedMarkdownSource(
        string rawText,
        int absoluteTextStart,
        MarkdownLayoutContext context,
        MarkdownDocument? rootDocument,
        out int bodyOffset,
        out MarkdownSourceProjection? nestedProjection)
    {
        bodyOffset = 0;
        nestedProjection = null;
        MarkdownSourceProjection? safeHtmlProjection = context.SafeHtmlSourceProjection;
        MarkdownSourceProjectionBuilder? projectionBuilder = safeHtmlProjection is null
            ? null
            : new MarkdownSourceProjectionBuilder();

        void AppendRawText(StringBuilder destination)
        {
            destination.Append(rawText);
            projectionBuilder?.AppendProjection(
                rawText,
                safeHtmlProjection!,
                absoluteTextStart,
                context.CancellationToken);
        }

        if (rawText.IndexOf('[') < 0)
        {
            if (projectionBuilder is null)
                return rawText;

            var rawOnly = new StringBuilder(rawText.Length);
            AppendRawText(rawOnly);
            nestedProjection = projectionBuilder.Build();
            return rawOnly.ToString();
        }

        string sourceText = context.SourceMap.SourceText;
        LinkReferenceDefinitionGroup? definitions = rootDocument is { } root
            ? LinkReferenceDefinitionExtensions.GetLinkReferenceDefinitions(root, false)
            : null;
        SourceSpan sourceRange = safeHtmlProjection is null
            ? new SourceSpan(absoluteTextStart, rawText.Length)
            : safeHtmlProjection.MapRangeOrEmpty(absoluteTextStart, rawText.Length);
        if (definitions is null ||
            sourceRange.Start < 0 ||
            sourceRange.Start > sourceText.Length ||
            sourceRange.Length > sourceText.Length - sourceRange.Start)
        {
            if (projectionBuilder is not null)
            {
                var rawOnly = new StringBuilder(rawText.Length);
                AppendRawText(rawOnly);
                nestedProjection = projectionBuilder.Build();
                return rawOnly.ToString();
            }

            return rawText;
        }

        int absoluteTextStartInDocument = sourceRange.Start;
        int absoluteTextEnd = sourceRange.End;
        bool literalReferenceMatchIsSafe = HasSimpleAsciiReferenceLabels(rawText);
        var precedingDefinitions = new List<(int Start, int Length)>();
        var followingDefinitions = new List<(int Start, int Length)>();
        foreach (LinkReferenceDefinition definition in definitions.Links.Values)
        {
            // Generated heading and footnote entries have no source definition
            // to replay into nested Markdown.
            if (definition.GetType() != typeof(LinkReferenceDefinition))
                continue;
            // The root document can have many definitions while one HTML
            // fragment references only a few. Replaying every definition into
            // every fragment makes parsing quadratic. A literal ASCII label
            // can be excluded only when neither side needs CommonMark's
            // whitespace, escape, entity, or Unicode normalization; keep the
            // conservative full replay for those less common cases.
            if (!MayUseReferenceLabel(rawText, definition.Label, literalReferenceMatchIsSafe))
                continue;
            if (!TryGetDefinitionSourceRange(definition, sourceText, out int start, out int length))
            {
                continue;
            }

            int end = start + length;
            if (end <= absoluteTextStartInDocument)
            {
                precedingDefinitions.Add((start, length));
            }
            else if (start >= absoluteTextEnd)
            {
                followingDefinitions.Add((start, length));
            }
            // A definition inside this Markdown fragment is already present in
            // rawText. Overlapping/synthetic spans are intentionally not replayed.
        }

        var combined = new StringBuilder();
        if (precedingDefinitions.Count > 0)
        {
            AppendDefinitionSources(combined, precedingDefinitions, sourceText, projectionBuilder);
            AppendSyntheticLineBreak(combined, projectionBuilder, new SourceSpan(sourceRange.Start, 0));
        }

        bodyOffset = combined.Length;
        AppendRawText(combined);
        foreach ((int start, int length) in followingDefinitions)
        {
            combined.Append("\n\n");
            if (projectionBuilder is not null)
            {
                SourceSpan boundary = new(sourceRange.End, 0);
                projectionBuilder.AppendMapped("\n", boundary);
                projectionBuilder.AppendMapped("\n", boundary);
            }
            combined.Append(sourceText, start, length);
            projectionBuilder?.AppendMapped(
                sourceText.Substring(start, length),
                new SourceSpan(start, length));
            AppendDefinitionLineBreak(combined, projectionBuilder, sourceText, start + length);
        }

        if (projectionBuilder is not null)
            nestedProjection = projectionBuilder.Build();

        return combined.ToString();
    }

    internal static bool HasSimpleAsciiReferenceLabels(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return false;

        char previous = '\0';
        foreach (char current in text)
        {
            if (current > 0x7F || current is '\\' or '&' or '\r' or '\n' or '\t' ||
                (current == ' ' && previous == ' '))
                return false;
            previous = current;
        }

        return true;
    }

    internal static bool MayUseReferenceLabel(
        string fragment,
        string? label,
        bool literalReferenceMatchIsSafe)
        => !literalReferenceMatchIsSafe ||
           !HasSimpleAsciiReferenceLabels(label) ||
           fragment.IndexOf(label!, StringComparison.OrdinalIgnoreCase) >= 0;

    private static void AppendDefinitionSources(
        StringBuilder destination,
        IReadOnlyList<(int Start, int Length)> definitions,
        string sourceText,
        MarkdownSourceProjectionBuilder? projectionBuilder)
    {
        foreach ((int start, int length) in definitions)
        {
            destination.Append(sourceText, start, length);
            projectionBuilder?.AppendMapped(
                sourceText.Substring(start, length),
                new SourceSpan(start, length));
            AppendDefinitionLineBreak(destination, projectionBuilder, sourceText, start + length);
        }
    }

    private static void AppendDefinitionLineBreak(
        StringBuilder destination,
        MarkdownSourceProjectionBuilder? projectionBuilder,
        string sourceText,
        int afterDefinition)
    {
        destination.Append('\n');
        if (projectionBuilder is null)
            return;

        SourceSpan lineBreak = afterDefinition < sourceText.Length && sourceText[afterDefinition] == '\r'
            ? new SourceSpan(afterDefinition, afterDefinition + 1 < sourceText.Length && sourceText[afterDefinition + 1] == '\n' ? 2 : 1)
            : afterDefinition < sourceText.Length && sourceText[afterDefinition] == '\n'
                ? new SourceSpan(afterDefinition, 1)
                : new SourceSpan(afterDefinition, 0);
        projectionBuilder.AppendMapped("\n", lineBreak);
    }

    private static void AppendSyntheticLineBreak(
        StringBuilder destination,
        MarkdownSourceProjectionBuilder? projectionBuilder,
        SourceSpan sourceBoundary)
    {
        destination.Append('\n');
        projectionBuilder?.AppendMapped("\n", sourceBoundary);
    }

    private static bool TryGetDefinitionSourceRange(
        LinkReferenceDefinition definition,
        string sourceText,
        out int start,
        out int length)
    {
        start = -1;
        int end = -1;
        IncludeSpan(definition.Span, ref start, ref end);
        IncludeSpan(definition.LabelSpan, ref start, ref end);
        IncludeSpan(definition.UrlSpan, ref start, ref end);
        IncludeSpan(definition.TitleSpan, ref start, ref end);
        if (start < 0 || end < start || start >= sourceText.Length)
        {
            length = 0;
            return false;
        }

        int lineEnd = end + 1;
        while (lineEnd < sourceText.Length && sourceText[lineEnd] is not ('\r' or '\n'))
        {
            lineEnd++;
        }

        length = lineEnd - start;
        return length > 0;
    }

    private static void IncludeSpan(Markdig.Syntax.SourceSpan span, ref int start, ref int end)
    {
        if (span.IsEmpty || span.Start < 0 || span.End < span.Start)
        {
            return;
        }

        start = start < 0 ? span.Start : Math.Min(start, span.Start);
        end = Math.Max(end, span.End);
    }
}
