using System;
using Markdig.Extensions.Tables;
using Markdig.Helpers;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace MarkdownRenderer;

/// <summary>
/// Post-parse pass that maps a prepared-source syntax tree back to authored
/// coordinates and applies the 0.29-gfm inline differences: same-delimiter
/// strong flattening, escaped pipes in table code spans, and the extended
/// autolink repairs.
/// </summary>
internal static class Gfm029SyntaxTreeNormalizer
{
    internal static void Normalize(
        Markdig.Syntax.MarkdownDocument document,
        string source,
        bool enableAutoLinks,
        Gfm029Compatibility.PreparedSource prepared)
    {
        if (prepared.HasChanges)
        {
            RestoreMaskedLiterals(document, prepared);
            RemapBlock(document, prepared, source);
        }
        VisitBlocks(document, source, enableAutoLinks, insideTable: false);
    }

    private static void RemapBlock(Block block, Gfm029Compatibility.PreparedSource prepared, string source)
    {
        if (block is Gfm029Compatibility.IPreparedSourceRemappable sourceRemappable)
            sourceRemappable.RemapPreparedSource(prepared, source);

        block.Span = prepared.MapSpan(block.Span);
        if (block is LeafBlock leaf && block is (CodeBlock or HtmlBlock))
            prepared.TryRemapLines(leaf.Lines, source);
        if (block is LeafBlock { Inline: { } inline })
            RemapInline(inline, prepared);
        if (block is ContainerBlock container)
        {
            foreach (Block child in container)
                RemapBlock(child, prepared, source);
        }
    }

    private static void RemapInline(Inline inline, Gfm029Compatibility.PreparedSource prepared)
    {
        inline.Span = prepared.MapSpan(inline.Span);
        if (inline is LinkInline link && !link.UrlSpan.IsEmpty)
            link.UrlSpan = prepared.MapSpan(link.UrlSpan);
        if (inline is ContainerInline container)
        {
            for (Inline? child = container.FirstChild; child is not null; child = child.NextSibling)
                RemapInline(child, prepared);
        }
    }

    private static void RestoreMaskedLiterals(ContainerBlock container, Gfm029Compatibility.PreparedSource prepared)
    {
        foreach (Block block in container)
        {
            if (block is LeafBlock { Inline: { } inline })
                RestoreMaskedLiterals(inline, prepared);
            if (block is ContainerBlock nested)
                RestoreMaskedLiterals(nested, prepared);
        }
    }

    private static void RestoreMaskedLiterals(ContainerInline container, Gfm029Compatibility.PreparedSource prepared)
    {
        for (Inline? child = container.FirstChild; child is not null; child = child.NextSibling)
        {
            if (child is LiteralInline literal &&
                prepared.TryRestoreMaskedLiteral(literal.Span, literal.Content, out StringSlice restoredContent))
                literal.Content = restoredContent;
            if (child is ContainerInline nested)
                RestoreMaskedLiterals(nested, prepared);
        }
    }

    private static void VisitBlocks(
        ContainerBlock container,
        string source,
        bool enableAutoLinks,
        bool insideTable)
    {
        foreach (Block block in container)
        {
            bool childInsideTable = insideTable || block is Table;
            if (block is LeafBlock { Inline: { } inline })
                VisitInlines(inline, source, enableAutoLinks, childInsideTable);
            if (block is ContainerBlock nested)
                VisitBlocks(nested, source, enableAutoLinks, childInsideTable);
        }
    }

    private static void VisitInlines(
        ContainerInline container,
        string source,
        bool enableAutoLinks,
        bool insideTable)
    {
        Inline? child = container.FirstChild;
        while (child is not null)
        {
            Inline? next = child.NextSibling;
            if (child is ContainerInline nested)
            {
                VisitInlines(
                    nested,
                    source,
                    enableAutoLinks && child is not LinkInline,
                    insideTable);
                if (container is EmphasisInline outer &&
                    nested is EmphasisInline inner &&
                    outer.DelimiterChar == inner.DelimiterChar &&
                    outer.DelimiterCount == 2 &&
                    inner.DelimiterCount == 2)
                {
                    // CommonMark 0.30 made same-delimiter strong nesting
                    // recursive. 0.29-gfm treats the inner delimiters as part
                    // of the already-open strong span. Flatten only that exact
                    // case; same-delimiter nested emphasis remains meaningful.
                    nested.MoveChildrenAfter(nested);
                    nested.Remove();
                }
            }
            else if (insideTable && child is CodeInline code && code.Content.Contains("\\|", StringComparison.Ordinal))
            {
                // GFM resolves an escaped table pipe before parsing cell
                // inlines, including inside code spans.
                code.Content = code.Content.Replace("\\|", "|", StringComparison.Ordinal);
            }

            child = next;
        }

        if (enableAutoLinks)
        {
            Gfm029AutolinkRepair.RepairUrlSuffixes(container, source);
            Gfm029AutolinkRepair.LinkEmailLiterals(container, source);
        }
    }
}
