using System.Collections.Generic;
using System.Threading;
using Markdig.Extensions.Abbreviations;
using Markdig.Extensions.DefinitionLists;
using Markdig.Extensions.Footnotes;
using Markdig.Extensions.Mathematics;
using Markdig.Extensions.Tables;
using Markdig.Renderers.Html;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace MarkdownRenderer.Document.Internal;

/// <summary>
/// Walks a parsed Markdig document once and collects the semantic summaries
/// (headings, links, code blocks, images, footnotes, definition items,
/// abbreviations and fragment targets) a <see cref="MarkdownDocument"/> exposes.
/// </summary>
internal sealed class MarkdownDocumentQueryBuilder
{
    private readonly CancellationToken _cancellationToken;
    private int _blockIndex;

    internal MarkdownDocumentQueryBuilder(string sourceText, CancellationToken cancellationToken)
    {
        SourceText = sourceText;
        _cancellationToken = cancellationToken;
    }

    internal string SourceText { get; }
    internal List<MarkdownHeading> Headings { get; } = new();
    internal List<MarkdownLink> Links { get; } = new();
    internal List<MarkdownCodeBlock> CodeBlocks { get; } = new();
    internal List<MarkdownImage> Images { get; } = new();
    internal List<MarkdownFootnote> Footnotes { get; } = new();
    internal List<MarkdownDefinitionItem> DefinitionItems { get; } = new();
    internal List<MarkdownAbbreviation> Abbreviations { get; } = new();
    internal List<MarkdownFragment> Fragments { get; } = new();

    internal void VisitContainer(ContainerBlock container)
    {
        foreach (var block in container)
        {
            _cancellationToken.ThrowIfCancellationRequested();
            VisitBlock(block);
        }
    }

    private void VisitBlock(Block block)
    {
        int blockIndex = ++_blockIndex;
        RegisterFragment(block, blockIndex);
        switch (block)
        {
            case HeadingBlock heading:
                Headings.Add(new MarkdownHeading(
                    FlattenInline(heading.Inline),
                    ToSourceSpan(heading.Span),
                    blockIndex,
                    heading.Level));
                VisitInlines(heading.Inline, blockIndex);
                break;
            case LeafBlock leaf:
                if (leaf is MathBlock)
                {
                    // Math feature packs contribute atomic semantic content;
                    // display formulas are not code blocks.
                }
                else if (leaf is FencedCodeBlock fenced)
                {
                    CodeBlocks.Add(new MarkdownCodeBlock(
                        fenced.Lines.ToString(),
                        ToSourceSpan(fenced.Span),
                        blockIndex,
                        NormalizeCodeLanguage(fenced.Info)));
                }
                else if (leaf is CodeBlock code)
                {
                    CodeBlocks.Add(new MarkdownCodeBlock(
                        code.Lines.ToString(),
                        ToSourceSpan(code.Span),
                        blockIndex,
                        null));
                }

                VisitInlines(leaf.Inline, blockIndex);
                break;
            case Table table:
                foreach (var row in table)
                {
                    if (row is ContainerBlock rowContainer)
                        VisitContainer(rowContainer);
                }
                break;
            case Footnote footnote:
                Footnotes.Add(new MarkdownFootnote(
                    NormalizeFootnoteLabel(footnote.Label),
                    FlattenBlock(footnote),
                    ToSourceSpan(footnote.Span),
                    blockIndex,
                    footnote.Order));
                VisitContainer(footnote);
                break;
            case DefinitionList definitionList:
                VisitDefinitionList(definitionList, blockIndex);
                break;
            case ContainerBlock childContainer:
                VisitContainer(childContainer);
                break;
        }
    }

    private void VisitDefinitionList(DefinitionList list, int blockIndex)
    {
        foreach (var child in list)
        {
            if (child is not DefinitionItem item)
            {
                continue;
            }

            RegisterFragment(item, blockIndex);
            var terms = new List<string>();
            var definitions = new List<string>();
            foreach (var entry in item)
            {
                RegisterFragment(entry, blockIndex);
                if (entry is DefinitionTerm term)
                {
                    var text = FlattenInline(term.Inline);
                    if (text.Length > 0)
                        terms.Add(text);
                    VisitInlines(term.Inline, blockIndex);
                }
                else
                {
                    var text = FlattenBlock(entry);
                    if (text.Length > 0)
                        definitions.Add(text);
                    if (entry is LeafBlock leaf)
                        VisitInlines(leaf.Inline, blockIndex);
                    else if (entry is ContainerBlock nested)
                        VisitContainer(nested);
                }
            }

            DefinitionItems.Add(new MarkdownDefinitionItem(
                string.Join(", ", terms),
                string.Join("\n", definitions),
                ToSourceSpan(item.Span),
                blockIndex,
                GetDefinitionMarker(item)));
        }
    }

    private char GetDefinitionMarker(DefinitionItem item)
    {
        if (item.Span.Start >= 0 && item.Span.Start < SourceText.Length)
        {
            char marker = SourceText[item.Span.Start];
            if (marker is ':' or '~')
                return marker;
        }

        return item.OpeningCharacter;
    }

    private static string NormalizeFootnoteLabel(string? label)
        => label?.TrimStart('^') ?? string.Empty;

    private void VisitInlines(ContainerInline? container, int blockIndex)
    {
        if (container is null)
            return;

        foreach (var inline in container)
        {
            _cancellationToken.ThrowIfCancellationRequested();
            RegisterFragment(inline, blockIndex);
            if (inline is LinkInline link)
            {
                var text = FlattenInline(link);
                var span = ToSourceSpan(link.Span);
                if (link.IsImage)
                {
                    Images.Add(new MarkdownImage(
                        text,
                        span,
                        blockIndex,
                        link.Url ?? string.Empty,
                        text,
                        link.Title,
                        true));
                }
                else
                {
                    Links.Add(new MarkdownLink(
                        text,
                        span,
                        blockIndex,
                        link.Url ?? string.Empty,
                        link.Title));
                }
            }
            else if (inline is AbbreviationInline abbreviation)
            {
                Abbreviations.Add(new MarkdownAbbreviation(
                    abbreviation.Abbreviation?.Label ?? string.Empty,
                    ToSourceSpan(abbreviation.Span),
                    blockIndex,
                    abbreviation.Abbreviation?.Text.ToString() ?? string.Empty));
            }

            if (inline is ContainerInline nested)
                VisitInlines(nested, blockIndex);
        }
    }

    private void RegisterFragment(IMarkdownObject markdownObject, int blockIndex)
    {
        var id = HtmlAttributesExtensions.TryGetAttributes(markdownObject)?.Id;
        if (string.IsNullOrWhiteSpace(id))
            return;

        Fragments.Add(new MarkdownFragment(
            id.TrimStart('#'),
            ToSourceSpan(GetSpan(markdownObject)),
            blockIndex));
    }

    private static SourceSpan ToSourceSpan(Markdig.Syntax.SourceSpan span)
        => span.Start >= 0 && span.Length > 0
            ? new SourceSpan(span.Start, span.Length)
            : SourceSpan.Empty;

    private static Markdig.Syntax.SourceSpan GetSpan(IMarkdownObject markdownObject)
        => markdownObject switch
        {
            Block block => block.Span,
            Inline inline => inline.Span,
            _ => default,
        };

    private static string? NormalizeCodeLanguage(string? info)
    {
        var text = info?.Trim();
        if (string.IsNullOrEmpty(text))
            return null;

        int firstWhitespace = text.IndexOfAny([' ', '\t', '\r', '\n']);
        return firstWhitespace > 0 ? text[..firstWhitespace] : text;
    }

    private static string FlattenInline(ContainerInline? container)
    {
        if (container is null)
            return string.Empty;

        var parts = new List<string>();
        FlattenInline(container, parts);
        return string.Concat(parts);
    }

    private static void FlattenInline(ContainerInline container, List<string> parts)
    {
        foreach (var child in container)
        {
            switch (child)
            {
                case LiteralInline literal:
                    parts.Add(literal.Content.ToString());
                    break;
                case CodeInline code:
                    parts.Add(code.Content);
                    break;
                case LineBreakInline:
                    parts.Add("\n");
                    break;
                case AbbreviationInline abbreviation:
                    parts.Add(abbreviation.Abbreviation?.Label ?? string.Empty);
                    break;
                case ContainerInline nested:
                    FlattenInline(nested, parts);
                    break;
            }
        }
    }

    private static string FlattenBlock(Block block)
    {
        if (block is LeafBlock leaf)
            return FlattenInline(leaf.Inline);
        if (block is ContainerBlock container)
        {
            var parts = new List<string>();
            foreach (var child in container)
            {
                var text = FlattenBlock(child);
                if (!string.IsNullOrWhiteSpace(text))
                    parts.Add(text);
            }
            return string.Join("\n", parts);
        }
        return string.Empty;
    }
}
