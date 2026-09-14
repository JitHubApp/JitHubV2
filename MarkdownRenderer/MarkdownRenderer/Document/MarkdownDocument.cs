using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using Markdig.Extensions.Abbreviations;
using Markdig.Extensions.DefinitionLists;
using Markdig.Extensions.Footnotes;
using Markdig.Extensions.Tables;
using Markdig.Extensions.Mathematics;
using Markdig.Renderers.Html;
using MarkdownRenderer.Extensions;
using System.Collections.ObjectModel;

namespace MarkdownRenderer.Document;

/// <summary>
/// Immutable public snapshot of a parsed markdown document.
/// </summary>
public sealed class MarkdownDocument
{
    private static readonly MarkdownDocument _empty = new(
        string.Empty,
        parsedDocument: null,
        headings: [],
        links: [],
        codeBlocks: [],
        images: [],
        footnotes: [],
        definitionItems: [],
        abbreviations: [],
        fragments: [],
        diagnostics: [],
        extensionBlocks: null,
        extensionInlines: null,
        extensionBlockNodes: null,
        extensionInlineNodes: null,
        presentationConfiguration: null);

    private readonly IReadOnlyList<MarkdownHeading> _headings;
    private readonly IReadOnlyList<MarkdownLink> _links;
    private readonly IReadOnlyList<MarkdownCodeBlock> _codeBlocks;
    private readonly IReadOnlyList<MarkdownImage> _images;
    private readonly IReadOnlyList<MarkdownFootnote> _footnotes;
    private readonly IReadOnlyList<MarkdownDefinitionItem> _definitionItems;
    private readonly IReadOnlyList<MarkdownAbbreviation> _abbreviations;
    private readonly IReadOnlyList<MarkdownFragment> _fragments;
    private readonly IReadOnlyList<MarkdownDiagnostic> _diagnostics;
    private readonly IReadOnlyList<MarkdownSourceMapEntry> _sourceMap;
    private readonly IReadOnlyDictionary<SourceSpan, IReadOnlyList<MarkdownContentFragment>> _extensionBlocks;
    private readonly IReadOnlyDictionary<SourceSpan, IReadOnlyList<MarkdownContentFragment>> _extensionInlines;
    private readonly IReadOnlyDictionary<Block, MarkdownContentFragment> _extensionBlockNodes;
    private readonly IReadOnlyDictionary<Inline, MarkdownContentFragment> _extensionInlineNodes;

    private MarkdownDocument(
        string sourceText,
        Markdig.Syntax.MarkdownDocument? parsedDocument,
        IReadOnlyList<MarkdownHeading> headings,
        IReadOnlyList<MarkdownLink> links,
        IReadOnlyList<MarkdownCodeBlock> codeBlocks,
        IReadOnlyList<MarkdownImage> images,
        IReadOnlyList<MarkdownFootnote> footnotes,
        IReadOnlyList<MarkdownDefinitionItem> definitionItems,
        IReadOnlyList<MarkdownAbbreviation> abbreviations,
        IReadOnlyList<MarkdownFragment> fragments,
        IReadOnlyList<MarkdownDiagnostic> diagnostics,
        IReadOnlyDictionary<SourceSpan, List<MarkdownContentFragment>>? extensionBlocks,
        IReadOnlyDictionary<SourceSpan, List<MarkdownContentFragment>>? extensionInlines,
        IReadOnlyDictionary<Block, MarkdownContentFragment>? extensionBlockNodes,
        IReadOnlyDictionary<Inline, MarkdownContentFragment>? extensionInlineNodes,
        IMarkdownPresentationConfiguration? presentationConfiguration)
    {
        SourceText = sourceText;
        ParsedDocument = parsedDocument;
        _headings = Freeze(headings);
        _links = Freeze(links);
        _codeBlocks = Freeze(codeBlocks);
        _images = Freeze(images);
        _footnotes = Freeze(footnotes);
        _definitionItems = Freeze(definitionItems);
        _abbreviations = Freeze(abbreviations);
        _fragments = Freeze(fragments);
        _diagnostics = Freeze(diagnostics);
        _extensionBlocks = FreezeExtensionContent(extensionBlocks);
        _extensionInlines = FreezeExtensionContent(extensionInlines);
        _extensionBlockNodes = FreezeExtensionNodeContent(extensionBlockNodes);
        _extensionInlineNodes = FreezeExtensionNodeContent(extensionInlineNodes);
        PresentationConfiguration = presentationConfiguration;
        _sourceMap = BuildSourceMap(
            _headings,
            _links,
            _codeBlocks,
            _images,
            _footnotes,
            _definitionItems,
            _abbreviations,
            _fragments);
    }

    /// <summary>Gets an empty document snapshot.</summary>
    public static MarkdownDocument Empty => _empty;

    /// <summary>Gets the markdown source text represented by this snapshot.</summary>
    public string SourceText { get; }

    /// <summary>
    /// Gets the markdown source represented by this document. Offsets in all
    /// exposed source spans index this string as UTF-16 code units.
    /// </summary>
    public string Source => SourceText;

    /// <summary>Gets parser diagnostics in source order.</summary>
    public IReadOnlyList<MarkdownDiagnostic> Diagnostics => _diagnostics;

    /// <summary>
    /// Gets immutable parse-time semantic-to-source entries in source order.
    /// This is distinct from the rendered-position map produced during layout.
    /// Every span is half-open and uses UTF-16 offsets into <see cref="Source"/>.
    /// </summary>
    public IReadOnlyList<MarkdownSourceMapEntry> SourceMap => _sourceMap;

    /// <summary>Gets whether parsing produced one or more error diagnostics.</summary>
    public bool HasErrors => _diagnostics.Any(static diagnostic => diagnostic.Severity == MarkdownDiagnosticSeverity.Error);

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

    internal Markdig.Syntax.MarkdownDocument? ParsedDocument { get; }

    /// <summary>
    /// Carries the engine's immutable presentation snapshot so a reusable
    /// document renders identically when assigned to a bare view.
    /// </summary>
    internal IMarkdownPresentationConfiguration? PresentationConfiguration { get; }

    internal IReadOnlyDictionary<SourceSpan, IReadOnlyList<MarkdownContentFragment>> ExtensionBlockContent
        => _extensionBlocks;

    internal IReadOnlyDictionary<SourceSpan, IReadOnlyList<MarkdownContentFragment>> ExtensionInlineContent
        => _extensionInlines;

    internal IReadOnlyDictionary<Block, MarkdownContentFragment> ExtensionBlockNodeContent
        => _extensionBlockNodes;

    internal IReadOnlyDictionary<Inline, MarkdownContentFragment> ExtensionInlineNodeContent
        => _extensionInlineNodes;

    internal static MarkdownDocument FromParsed(string sourceText, Markdig.Syntax.MarkdownDocument document)
        => FromParsed(sourceText, document, [], CancellationToken.None);

    internal static MarkdownDocument FromParsed(
        string sourceText,
        Markdig.Syntax.MarkdownDocument document,
        IReadOnlyList<MarkdownDiagnostic> diagnostics,
        CancellationToken cancellationToken)
        => FromParsed(
            sourceText,
            document,
            diagnostics,
            MarkdownExtensionSet.Empty,
            cancellationToken);

    internal static MarkdownDocument FromParsed(
        string sourceText,
        Markdig.Syntax.MarkdownDocument document,
        IReadOnlyList<MarkdownDiagnostic> diagnostics,
        MarkdownExtensionSet extensions,
        CancellationToken cancellationToken) =>
        FromParsedAsync(sourceText, document, diagnostics, extensions, cancellationToken)
            .AsTask()
            .GetAwaiter()
            .GetResult();

    internal static async ValueTask<MarkdownDocument> FromParsedAsync(
        string sourceText,
        Markdig.Syntax.MarkdownDocument document,
        IReadOnlyList<MarkdownDiagnostic> diagnostics,
        MarkdownExtensionSet extensions,
        CancellationToken cancellationToken,
        IMarkdownPresentationConfiguration? presentationConfiguration = null)
    {
        if (document is null)
            return Empty;

        cancellationToken.ThrowIfCancellationRequested();
        var builder = new QueryBuilder(sourceText ?? string.Empty, cancellationToken);
        builder.VisitContainer(document);
        cancellationToken.ThrowIfCancellationRequested();
        ExtensionContentSnapshot extensionContent = await BuildExtensionContentAsync(
                builder.SourceText,
                document,
                extensions ?? MarkdownExtensionSet.Empty,
                cancellationToken)
            .ConfigureAwait(false);
        return new MarkdownDocument(
            builder.SourceText,
            document,
            builder.Headings,
            builder.Links,
            builder.CodeBlocks,
            builder.Images,
            builder.Footnotes,
            builder.DefinitionItems,
            builder.Abbreviations,
            builder.Fragments,
            CombineDiagnostics(diagnostics, extensionContent.Diagnostics),
            extensionContent.Blocks,
            extensionContent.Inlines,
            extensionContent.BlockNodes,
            extensionContent.InlineNodes,
            presentationConfiguration);
    }

    /// <summary>
    /// Gets declarative content emitted for a parsed block or inline with the
    /// supplied UTF-16 source range. Block output takes precedence if a block
    /// and inline happen to share the same range.
    /// </summary>
    /// <remarks>
    /// Source ranges identify source text, not individual syntax nodes. When
    /// multiple nodes of the same category have an identical range, this method
    /// returns the first emitted fragment. Rendering preserves every fragment by
    /// dispatching against the exact parsed node.
    /// </remarks>
    public bool TryGetExtensionContent(
        SourceSpan sourceRange,
        out MarkdownContentFragment? fragment)
    {
        if (_extensionBlocks.TryGetValue(sourceRange, out var values) && values.Count > 0)
        {
            fragment = values[0];
            return true;
        }

        return TryGetInlineExtensionContent(sourceRange, out fragment);
    }

    /// <summary>Gets declarative block content for an exact UTF-16 source range.</summary>
    /// <remarks>
    /// If multiple block nodes share the range, this source-oriented lookup
    /// returns the first emitted fragment. The renderer uses exact-node dispatch
    /// so colliding ranges do not discard nested block output.
    /// </remarks>
    public bool TryGetBlockExtensionContent(
        SourceSpan sourceRange,
        out MarkdownContentFragment? fragment)
    {
        if (_extensionBlocks.TryGetValue(sourceRange, out var values) && values.Count > 0)
        {
            fragment = values[0];
            return true;
        }

        fragment = null;
        return false;
    }

    /// <summary>
    /// Gets every declarative block fragment emitted for an exact UTF-16 source
    /// range, in parsed-node order.
    /// </summary>
    /// <remarks>
    /// A source range can identify more than one nested syntax node, such as a
    /// one-item list and its item. Use this method when every colliding fragment
    /// is required; <see cref="TryGetBlockExtensionContent(SourceSpan, out MarkdownContentFragment?)"/>
    /// returns only the first fragment for convenience.
    /// </remarks>
    public IReadOnlyList<MarkdownContentFragment> GetBlockExtensionContent(SourceSpan sourceRange) =>
        _extensionBlocks.TryGetValue(sourceRange, out var fragments)
            ? fragments
            : Array.Empty<MarkdownContentFragment>();

    internal bool TryGetBlockExtensionContent(
        Block block,
        out MarkdownContentFragment? fragment)
    {
        ArgumentNullException.ThrowIfNull(block);
        if (_extensionBlockNodes.TryGetValue(block, out var value))
        {
            fragment = value;
            return true;
        }

        fragment = null;
        return false;
    }

    /// <summary>Gets declarative inline content for an exact UTF-16 source range.</summary>
    /// <remarks>
    /// If multiple inline nodes share the range, this source-oriented lookup
    /// returns the first emitted fragment. The renderer uses exact-node dispatch
    /// so colliding ranges do not discard nested inline output.
    /// </remarks>
    public bool TryGetInlineExtensionContent(
        SourceSpan sourceRange,
        out MarkdownContentFragment? fragment)
    {
        if (_extensionInlines.TryGetValue(sourceRange, out var values) && values.Count > 0)
        {
            fragment = values[0];
            return true;
        }

        fragment = null;
        return false;
    }

    /// <summary>
    /// Gets every declarative inline fragment emitted for an exact UTF-16 source
    /// range, in parsed-node order.
    /// </summary>
    /// <remarks>
    /// Use this method when every colliding fragment is required;
    /// <see cref="TryGetInlineExtensionContent(SourceSpan, out MarkdownContentFragment?)"/>
    /// returns only the first fragment for convenience.
    /// </remarks>
    public IReadOnlyList<MarkdownContentFragment> GetInlineExtensionContent(SourceSpan sourceRange) =>
        _extensionInlines.TryGetValue(sourceRange, out var fragments)
            ? fragments
            : Array.Empty<MarkdownContentFragment>();

    internal bool TryGetInlineExtensionContent(
        Inline inline,
        out MarkdownContentFragment? fragment)
    {
        ArgumentNullException.ThrowIfNull(inline);
        if (_extensionInlineNodes.TryGetValue(inline, out var value))
        {
            fragment = value;
            return true;
        }

        fragment = null;
        return false;
    }

    private static async ValueTask<ExtensionContentSnapshot> BuildExtensionContentAsync(
        string source,
        Markdig.Syntax.MarkdownDocument document,
        MarkdownExtensionSet extensions,
        CancellationToken cancellationToken)
    {
        if (extensions.BlockSyntaxKinds.Count == 0 && extensions.InlineSyntaxKinds.Count == 0)
            return ExtensionContentSnapshot.Empty;

        var blocks = new Dictionary<SourceSpan, List<MarkdownContentFragment>>();
        var inlines = new Dictionary<SourceSpan, List<MarkdownContentFragment>>();
        var blockNodes = new Dictionary<Block, MarkdownContentFragment>(ReferenceEqualityComparer.Instance);
        var inlineNodes = new Dictionary<Inline, MarkdownContentFragment>(ReferenceEqualityComparer.Instance);
        var extensionDiagnostics = new List<MarkdownDiagnostic>();
        await VisitAsync(document).ConfigureAwait(false);
        extensionDiagnostics.Sort(static (left, right) =>
        {
            int comparison = left.SourceSpan.Start.CompareTo(right.SourceSpan.Start);
            return comparison != 0
                ? comparison
                : string.CompareOrdinal(left.Code, right.Code);
        });
        return new ExtensionContentSnapshot(
            blocks,
            inlines,
            blockNodes,
            inlineNodes,
            extensionDiagnostics);

        async ValueTask VisitAsync(ContainerBlock container)
        {
            foreach (Block block in container)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (MarkdownSyntaxAdapter.TryGetBlockKind(block, out string kind) &&
                    extensions.TryGetBlockRendererInvoker(kind, out var renderer) &&
                    renderer is not null)
                {
                    var node = MarkdownSyntaxAdapter.CreateBlockNode(block, source);
                    var content = new MarkdownContentBuilder();
                    await renderer(
                            new MarkdownExtensionContext(node, cancellationToken, extensionDiagnostics.Add),
                            content)
                        .ConfigureAwait(false);
                    cancellationToken.ThrowIfCancellationRequested();
                    var fragment = content.Build();
                    SourceSpan sourceRange = block.Span.Start >= 0 && block.Span.Length > 0
                        ? new SourceSpan(block.Span.Start, block.Span.Length)
                        : SourceSpan.Empty;
                    if (fragment.Items.Count > 0 && !sourceRange.IsEmpty)
                    {
                        blockNodes.Add(block, fragment);
                        AddBySourceRange(blocks, sourceRange, fragment);
                    }
                }

                if (block is LeafBlock { Inline: { } inlineContainer })
                    await VisitInlinesAsync(inlineContainer).ConfigureAwait(false);
                if (block is ContainerBlock nested)
                    await VisitAsync(nested).ConfigureAwait(false);
            }
        }

        async ValueTask VisitInlinesAsync(ContainerInline container)
        {
            foreach (Inline inline in container)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (MarkdownSyntaxAdapter.TryGetInlineKind(inline, out string kind) &&
                    extensions.TryGetInlineRendererInvoker(kind, out var renderer) &&
                    renderer is not null)
                {
                    var node = MarkdownSyntaxAdapter.CreateInlineNode(inline, source);
                    var content = new MarkdownContentBuilder();
                    await renderer(
                            new MarkdownExtensionContext(node, cancellationToken, extensionDiagnostics.Add),
                            content)
                        .ConfigureAwait(false);
                    cancellationToken.ThrowIfCancellationRequested();
                    var fragment = content.Build();
                    if (fragment.Items.Count > 0 && !node.SourceSpan.IsEmpty)
                    {
                        inlineNodes.Add(inline, fragment);
                        AddBySourceRange(inlines, node.SourceSpan, fragment);
                    }
                }

                if (inline is ContainerInline nested)
                    await VisitInlinesAsync(nested).ConfigureAwait(false);
            }
        }

        static void AddBySourceRange(
            Dictionary<SourceSpan, List<MarkdownContentFragment>> content,
            SourceSpan sourceRange,
            MarkdownContentFragment fragment)
        {
            if (!content.TryGetValue(sourceRange, out var fragments))
            {
                fragments = new List<MarkdownContentFragment>();
                content.Add(sourceRange, fragments);
            }

            fragments.Add(fragment);
        }
    }

    private static IReadOnlyDictionary<SourceSpan, IReadOnlyList<MarkdownContentFragment>> FreezeExtensionContent(
        IReadOnlyDictionary<SourceSpan, List<MarkdownContentFragment>>? content)
    {
        var snapshot = new Dictionary<SourceSpan, IReadOnlyList<MarkdownContentFragment>>();
        if (content is not null)
        {
            foreach (var entry in content)
                snapshot.Add(entry.Key, Freeze(entry.Value));
        }

        return new ReadOnlyDictionary<SourceSpan, IReadOnlyList<MarkdownContentFragment>>(snapshot);
    }

    private static IReadOnlyDictionary<TNode, MarkdownContentFragment> FreezeExtensionNodeContent<TNode>(
        IReadOnlyDictionary<TNode, MarkdownContentFragment>? content)
        where TNode : class
        => new ReadOnlyDictionary<TNode, MarkdownContentFragment>(
            content is null || content.Count == 0
                ? new Dictionary<TNode, MarkdownContentFragment>(ReferenceEqualityComparer.Instance)
                : new Dictionary<TNode, MarkdownContentFragment>(content, ReferenceEqualityComparer.Instance));

    private sealed record ExtensionContentSnapshot(
        IReadOnlyDictionary<SourceSpan, List<MarkdownContentFragment>> Blocks,
        IReadOnlyDictionary<SourceSpan, List<MarkdownContentFragment>> Inlines,
        IReadOnlyDictionary<Block, MarkdownContentFragment> BlockNodes,
        IReadOnlyDictionary<Inline, MarkdownContentFragment> InlineNodes,
        IReadOnlyList<MarkdownDiagnostic> Diagnostics)
    {
        internal static ExtensionContentSnapshot Empty { get; } = new(
            new Dictionary<SourceSpan, List<MarkdownContentFragment>>(),
            new Dictionary<SourceSpan, List<MarkdownContentFragment>>(),
            new Dictionary<Block, MarkdownContentFragment>(ReferenceEqualityComparer.Instance),
            new Dictionary<Inline, MarkdownContentFragment>(ReferenceEqualityComparer.Instance),
            Array.Empty<MarkdownDiagnostic>());
    }

    private static IReadOnlyList<MarkdownDiagnostic> CombineDiagnostics(
        IReadOnlyList<MarkdownDiagnostic>? parserDiagnostics,
        IReadOnlyList<MarkdownDiagnostic> extensionDiagnostics)
    {
        int parserCount = parserDiagnostics?.Count ?? 0;
        if (parserCount == 0)
            return extensionDiagnostics;
        if (extensionDiagnostics.Count == 0)
            return parserDiagnostics!;

        var combined = new List<MarkdownDiagnostic>(parserCount + extensionDiagnostics.Count);
        for (int i = 0; i < parserCount; i++)
            combined.Add(parserDiagnostics![i]);
        for (int i = 0; i < extensionDiagnostics.Count; i++)
            combined.Add(extensionDiagnostics[i]);
        combined.Sort(static (left, right) =>
        {
            int comparison = left.SourceSpan.Start.CompareTo(right.SourceSpan.Start);
            return comparison != 0
                ? comparison
                : string.CompareOrdinal(left.Code, right.Code);
        });
        return combined;
    }

    private static IReadOnlyList<T> Freeze<T>(IReadOnlyList<T> values)
    {
        if (values.Count == 0)
            return Array.AsReadOnly(Array.Empty<T>());

        var snapshot = new T[values.Count];
        for (int i = 0; i < values.Count; i++)
            snapshot[i] = values[i];
        return Array.AsReadOnly(snapshot);
    }

    private static IReadOnlyList<MarkdownSourceMapEntry> BuildSourceMap(
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
        return Freeze(entries);
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

    private sealed class QueryBuilder
    {
        private readonly CancellationToken _cancellationToken;
        private int _blockIndex;

        internal QueryBuilder(string sourceText, CancellationToken cancellationToken)
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

/// <summary>Classifies an immutable entry in a document's semantic source map.</summary>
public enum MarkdownSourceElementKind
{
    /// <summary>A heading block.</summary>
    Heading,

    /// <summary>A non-image link.</summary>
    Link,

    /// <summary>A fenced or indented code block.</summary>
    CodeBlock,

    /// <summary>An image.</summary>
    Image,

    /// <summary>A footnote definition.</summary>
    Footnote,

    /// <summary>A definition-list item.</summary>
    DefinitionItem,

    /// <summary>An abbreviation occurrence.</summary>
    Abbreviation,

    /// <summary>A named fragment target.</summary>
    Fragment,
}

/// <summary>
/// Maps a semantic document element to a half-open UTF-16 source span.
/// </summary>
/// <param name="Kind">Semantic element kind.</param>
/// <param name="SourceSpan">Half-open UTF-16 span in the document source.</param>
/// <param name="BlockIndex">One-based block index assigned during document traversal.</param>
public sealed record MarkdownSourceMapEntry(
    MarkdownSourceElementKind Kind,
    SourceSpan SourceSpan,
    int BlockIndex);

/// <summary>Severity assigned to a markdown parse or validation diagnostic.</summary>
public enum MarkdownDiagnosticSeverity
{
    /// <summary>Informational guidance that does not affect rendering.</summary>
    Information,

    /// <summary>A recoverable issue that may change rendered output.</summary>
    Warning,

    /// <summary>An error that prevented part of the source from being interpreted.</summary>
    Error,
}

/// <summary>
/// Describes a parser or profile validation result without exposing parser-specific types.
/// </summary>
/// <param name="Code">Stable machine-readable diagnostic code.</param>
/// <param name="Severity">Diagnostic severity.</param>
/// <param name="Message">Human-readable diagnostic message.</param>
/// <param name="SourceSpan">Half-open UTF-16 source span associated with the diagnostic.</param>
public sealed record MarkdownDiagnostic(
    string Code,
    MarkdownDiagnosticSeverity Severity,
    string Message,
    SourceSpan SourceSpan);
