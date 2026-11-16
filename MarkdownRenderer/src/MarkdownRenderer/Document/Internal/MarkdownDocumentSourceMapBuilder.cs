using System;
using System.Collections.Generic;

namespace MarkdownRenderer.Document.Internal;

/// <summary>
/// Builds the parse-time semantic-to-source entries of a
/// <see cref="MarkdownDocument"/> in source order.
/// </summary>
internal static class MarkdownDocumentSourceMapBuilder
{
    internal static IReadOnlyList<MarkdownSourceMapEntry> Build(
        IReadOnlyList<MarkdownHeading> headings,
        IReadOnlyList<MarkdownLink> links,
        IReadOnlyList<MarkdownCodeBlock> codeBlocks,
        IReadOnlyList<MarkdownImage> images,
        IReadOnlyList<MarkdownFootnote> footnotes,
        IReadOnlyList<MarkdownDefinitionItem> definitionItems,
        IReadOnlyList<MarkdownAbbreviation> abbreviations,
        IReadOnlyList<MarkdownFragment> fragments)
    {
        var entries = new List<MarkdownSourceMapEntry>(
            headings.Count +
            links.Count +
            codeBlocks.Count +
            images.Count +
            footnotes.Count +
            definitionItems.Count +
            abbreviations.Count +
            fragments.Count);

        AddEntries(entries, headings, MarkdownSourceElementKind.Heading, static value => value.SourceSpan, static value => value.BlockIndex);
        AddEntries(entries, links, MarkdownSourceElementKind.Link, static value => value.SourceSpan, static value => value.BlockIndex);
        AddEntries(entries, codeBlocks, MarkdownSourceElementKind.CodeBlock, static value => value.SourceSpan, static value => value.BlockIndex);
        AddEntries(entries, images, MarkdownSourceElementKind.Image, static value => value.SourceSpan, static value => value.BlockIndex);
        AddEntries(entries, footnotes, MarkdownSourceElementKind.Footnote, static value => value.SourceSpan, static value => value.BlockIndex);
        AddEntries(entries, definitionItems, MarkdownSourceElementKind.DefinitionItem, static value => value.SourceSpan, static value => value.BlockIndex);
        AddEntries(entries, abbreviations, MarkdownSourceElementKind.Abbreviation, static value => value.SourceSpan, static value => value.BlockIndex);
        AddEntries(entries, fragments, MarkdownSourceElementKind.Fragment, static value => value.SourceSpan, static value => value.BlockIndex);

        entries.Sort(static (left, right) =>
        {
            int comparison = left.SourceSpan.Start.CompareTo(right.SourceSpan.Start);
            if (comparison != 0)
                return comparison;

            comparison = right.SourceSpan.Length.CompareTo(left.SourceSpan.Length);
            if (comparison != 0)
                return comparison;

            comparison = left.BlockIndex.CompareTo(right.BlockIndex);
            return comparison != 0
                ? comparison
                : left.Kind.CompareTo(right.Kind);
        });
        return DocumentSnapshot.Freeze(entries);
    }

    private static void AddEntries<T>(
        List<MarkdownSourceMapEntry> target,
        IReadOnlyList<T> source,
        MarkdownSourceElementKind kind,
        Func<T, SourceSpan> getSpan,
        Func<T, int> getBlockIndex)
    {
        foreach (var value in source)
        {
            var span = getSpan(value);
            if (!span.IsEmpty)
                target.Add(new MarkdownSourceMapEntry(kind, span, getBlockIndex(value)));
        }
    }
}
