using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using MarkdownRenderer.Document.Internal;
using MarkdownRenderer.Engine.Internal;
using MarkdownRenderer.Extensions;

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
    private readonly IReadOnlyCollection<string> _extensionStyleRoleNames;

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
        _headings = DocumentSnapshot.Freeze(headings);
        _links = DocumentSnapshot.Freeze(links);
        _codeBlocks = DocumentSnapshot.Freeze(codeBlocks);
        _images = DocumentSnapshot.Freeze(images);
        _footnotes = DocumentSnapshot.Freeze(footnotes);
        _definitionItems = DocumentSnapshot.Freeze(definitionItems);
        _abbreviations = DocumentSnapshot.Freeze(abbreviations);
        _fragments = DocumentSnapshot.Freeze(fragments);
        _diagnostics = DocumentSnapshot.Freeze(diagnostics);
        _extensionBlocks = DocumentSnapshot.FreezeExtensionContent(extensionBlocks);
        _extensionInlines = DocumentSnapshot.FreezeExtensionContent(extensionInlines);
        _extensionBlockNodes = DocumentSnapshot.FreezeExtensionNodeContent(extensionBlockNodes);
        _extensionInlineNodes = DocumentSnapshot.FreezeExtensionNodeContent(extensionInlineNodes);
        _extensionStyleRoleNames = CollectExtensionStyleRoleNames(
            _extensionBlockNodes,
            _extensionInlineNodes);
        PresentationConfiguration = presentationConfiguration;
        _sourceMap = MarkdownDocumentSourceMapBuilder.Build(
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

    /// <summary>
    /// Returns the style roles emitted by declarative extensions in this
    /// document. The UI adapter uses this finite set to resolve resource-backed
    /// custom roles without scanning the application's complete XAML resource
    /// graph.
    /// </summary>
    internal IReadOnlyCollection<string> GetExtensionStyleRoleNames()
        => _extensionStyleRoleNames;

    private static IReadOnlyCollection<string> CollectExtensionStyleRoleNames(
        IReadOnlyDictionary<Block, MarkdownContentFragment> blockNodes,
        IReadOnlyDictionary<Inline, MarkdownContentFragment> inlineNodes)
    {
        if (blockNodes.Count == 0 && inlineNodes.Count == 0)
            return Array.Empty<string>();

        var roles = new HashSet<string>(StringComparer.Ordinal);
        foreach (MarkdownContentFragment fragment in blockNodes.Values)
            CollectStyleRoleNames(fragment.Items, roles);
        foreach (MarkdownContentFragment fragment in inlineNodes.Values)
            CollectStyleRoleNames(fragment.Items, roles);
        return roles.Count == 0 ? Array.Empty<string>() : [.. roles];
    }

    private static void CollectStyleRoleNames(
        IReadOnlyList<MarkdownContent> content,
        HashSet<string> roles)
    {
        foreach (MarkdownContent item in content)
        {
            if (!item.StyleRole.IsEmpty)
                roles.Add(item.StyleRole.Name);

            if (item.Children.Count > 0)
                CollectStyleRoleNames(item.Children, roles);
        }
    }

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
        IMarkdownPresentationConfiguration? presentationConfiguration = null,
        bool deferOffscreenScenes = false)
    {
        if (document is null)
            return Empty;

        cancellationToken.ThrowIfCancellationRequested();
        var builder = new MarkdownDocumentQueryBuilder(sourceText ?? string.Empty, cancellationToken);
        builder.VisitContainer(document);
        cancellationToken.ThrowIfCancellationRequested();
        MarkdownExtensionContentSnapshot extensionContent = await MarkdownExtensionContentCollector.BuildAsync(
                builder.SourceText,
                document,
                extensions ?? MarkdownExtensionSet.Empty,
                cancellationToken,
                deferOffscreenScenes)
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
}
