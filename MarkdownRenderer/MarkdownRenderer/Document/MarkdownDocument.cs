using System;
using System.Collections.Generic;
using System.Linq;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using Markdig.Extensions.Abbreviations;
using Markdig.Extensions.DefinitionLists;
using Markdig.Extensions.Footnotes;
using Markdig.Extensions.Tables;
using Markdig.Renderers.Html;

namespace MarkdownRenderer.Document;

/// <summary>
/// Immutable public snapshot of a parsed markdown document.
/// </summary>
public sealed class MarkdownDocument
{
    private static readonly MarkdownDocument _empty = new(string.Empty, [], [], [], [], [], [], [], []);

    private readonly IReadOnlyList<MarkdownHeading> _headings;
    private readonly IReadOnlyList<MarkdownLink> _links;
    private readonly IReadOnlyList<MarkdownCodeBlock> _codeBlocks;
    private readonly IReadOnlyList<MarkdownImage> _images;
    private readonly IReadOnlyList<MarkdownFootnote> _footnotes;
    private readonly IReadOnlyList<MarkdownDefinitionItem> _definitionItems;
    private readonly IReadOnlyList<MarkdownAbbreviation> _abbreviations;
    private readonly IReadOnlyList<MarkdownFragment> _fragments;

    private MarkdownDocument(
        string sourceText,
        IReadOnlyList<MarkdownHeading> headings,
        IReadOnlyList<MarkdownLink> links,
        IReadOnlyList<MarkdownCodeBlock> codeBlocks,
        IReadOnlyList<MarkdownImage> images,
        IReadOnlyList<MarkdownFootnote> footnotes,
        IReadOnlyList<MarkdownDefinitionItem> definitionItems,
        IReadOnlyList<MarkdownAbbreviation> abbreviations,
        IReadOnlyList<MarkdownFragment> fragments)
    {
        SourceText = sourceText;
        _headings = headings;
        _links = links;
        _codeBlocks = codeBlocks;
        _images = images;
        _footnotes = footnotes;
        _definitionItems = definitionItems;
        _abbreviations = abbreviations;
        _fragments = fragments;
    }

    /// <summary>Gets an empty document snapshot.</summary>
    public static MarkdownDocument Empty => _empty;

    /// <summary>Gets the normalized markdown source text used to create this snapshot.</summary>
    public string SourceText { get; }

    /// <summary>Returns all headings in document order.</summary>
    public IReadOnlyList<MarkdownHeading> GetHeadings() => _headings;

    /// <summary>Returns all non-image links in document order.</summary>
    public IReadOnlyList<MarkdownLink> GetLinks() => _links;

    /// <summary>Returns all fenced and indented code blocks in document order.</summary>
    public IReadOnlyList<MarkdownCodeBlock> GetCodeBlocks() => _codeBlocks;

    /// <summary>Returns all inline and block images in document order.</summary>
    public IReadOnlyList<MarkdownImage> GetImages() => _images;

    /// <summary>Returns all footnote definitions in document order.</summary>
    public IReadOnlyList<MarkdownFootnote> GetFootnotes() => _footnotes;

    /// <summary>Returns all definition-list items in document order.</summary>
    public IReadOnlyList<MarkdownDefinitionItem> GetDefinitionItems() => _definitionItems;

    /// <summary>Returns all abbreviation occurrences in document order.</summary>
    public IReadOnlyList<MarkdownAbbreviation> GetAbbreviations() => _abbreviations;

    /// <summary>Returns generic-attribute fragment targets in document order.</summary>
    public IReadOnlyList<MarkdownFragment> GetFragments() => _fragments;

    internal static MarkdownDocument FromParsed(string sourceText, Markdig.Syntax.MarkdownDocument document)
    {
        if (document is null)
            return Empty;

        var builder = new QueryBuilder(sourceText ?? string.Empty);
        builder.VisitContainer(document);
        return new MarkdownDocument(
            builder.SourceText,
            builder.Headings.ToArray(),
            builder.Links.ToArray(),
            builder.CodeBlocks.ToArray(),
            builder.Images.ToArray(),
            builder.Footnotes.ToArray(),
            builder.DefinitionItems.ToArray(),
            builder.Abbreviations.ToArray(),
            builder.Fragments.ToArray());
    }

    private sealed class QueryBuilder
    {
        private int _blockIndex;

        internal QueryBuilder(string sourceText)
        {
            SourceText = sourceText;
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
                VisitBlock(block);
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
                    if (leaf is FencedCodeBlock fenced)
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
                    continue;

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
}

/// <summary>Summary information for a heading in a markdown document.</summary>
/// <param name="DisplayText">Text displayed for the heading.</param>
/// <param name="SourceSpan">Span of the heading in the markdown source.</param>
/// <param name="BlockIndex">One-based block index assigned during document traversal.</param>
/// <param name="Level">Heading level, from 1 through 6.</param>
public sealed record MarkdownHeading(string DisplayText, SourceSpan SourceSpan, int BlockIndex, int Level);

/// <summary>Summary information for a non-image link in a markdown document.</summary>
/// <param name="DisplayText">Text displayed for the link.</param>
/// <param name="SourceSpan">Span of the link in the markdown source.</param>
/// <param name="BlockIndex">One-based block index assigned during document traversal.</param>
/// <param name="Url">Resolved link URL text from the markdown source.</param>
/// <param name="Title">Optional link title.</param>
public sealed record MarkdownLink(string DisplayText, SourceSpan SourceSpan, int BlockIndex, string Url, string? Title);

/// <summary>Summary information for a code block in a markdown document.</summary>
/// <param name="DisplayText">Code text displayed for the block.</param>
/// <param name="SourceSpan">Span of the code block in the markdown source.</param>
/// <param name="BlockIndex">One-based block index assigned during document traversal.</param>
/// <param name="Language">Optional fenced-code language identifier.</param>
public sealed record MarkdownCodeBlock(string DisplayText, SourceSpan SourceSpan, int BlockIndex, string? Language);

/// <summary>Summary information for an image in a markdown document.</summary>
/// <param name="DisplayText">Display text associated with the image, usually its alt text.</param>
/// <param name="SourceSpan">Span of the image in the markdown source.</param>
/// <param name="BlockIndex">One-based block index assigned during document traversal.</param>
/// <param name="Source">Image URL or data URI text from the markdown source.</param>
/// <param name="AltText">Image alt text.</param>
/// <param name="Title">Optional image title.</param>
/// <param name="IsInline">True when the image came from an inline image run.</param>
public sealed record MarkdownImage(
    string DisplayText,
    SourceSpan SourceSpan,
    int BlockIndex,
    string Source,
    string AltText,
    string? Title,
    bool IsInline);

/// <summary>Summary information for a footnote definition.</summary>
/// <param name="Label">Footnote label from the markdown source.</param>
/// <param name="DisplayText">Rendered footnote definition text.</param>
/// <param name="SourceSpan">Span of the footnote definition in the markdown source.</param>
/// <param name="BlockIndex">One-based block index assigned during document traversal.</param>
/// <param name="Order">Markdig display order when available.</param>
public sealed record MarkdownFootnote(string Label, string DisplayText, SourceSpan SourceSpan, int BlockIndex, int Order);

/// <summary>Summary information for a definition-list item.</summary>
/// <param name="Term">Definition term text.</param>
/// <param name="Definition">Definition description text.</param>
/// <param name="SourceSpan">Span of the definition item in the markdown source.</param>
/// <param name="BlockIndex">One-based block index assigned during document traversal.</param>
/// <param name="Marker">Definition marker character, usually ':' or '~'.</param>
public sealed record MarkdownDefinitionItem(string Term, string Definition, SourceSpan SourceSpan, int BlockIndex, char Marker);

/// <summary>Summary information for an abbreviation occurrence.</summary>
/// <param name="DisplayText">Abbreviation text shown in the document.</param>
/// <param name="SourceSpan">Span of the abbreviation occurrence in the markdown source.</param>
/// <param name="BlockIndex">One-based block index assigned during document traversal.</param>
/// <param name="Expansion">Expanded abbreviation text.</param>
public sealed record MarkdownAbbreviation(string DisplayText, SourceSpan SourceSpan, int BlockIndex, string Expansion);

/// <summary>Summary information for a generic-attribute fragment target.</summary>
/// <param name="Id">Fragment id without the leading '#'.</param>
/// <param name="SourceSpan">Span of the attributed element in the markdown source.</param>
/// <param name="BlockIndex">One-based block index assigned during document traversal.</param>
public sealed record MarkdownFragment(string Id, SourceSpan SourceSpan, int BlockIndex);
