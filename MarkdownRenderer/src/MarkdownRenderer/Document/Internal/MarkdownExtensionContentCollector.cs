using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using MarkdownRenderer.Extensions;

namespace MarkdownRenderer.Document.Internal;

/// <summary>
/// Runs the registered extension renderers over a parsed document and
/// collects their declarative content by source range and by exact node.
/// One instance serves one <see cref="BuildAsync"/> call.
/// </summary>
internal sealed class MarkdownExtensionContentCollector
{
    private readonly string _source;
    private readonly MarkdownExtensionSet _extensions;
    private readonly CancellationToken _cancellationToken;
    private readonly MarkdownExtensionSet? _deferredSceneMarkers;
    private readonly Dictionary<SourceSpan, List<MarkdownContentFragment>> _blocks = new();
    private readonly Dictionary<SourceSpan, List<MarkdownContentFragment>> _inlines = new();
    private readonly Dictionary<Block, MarkdownContentFragment> _blockNodes = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<Inline, MarkdownContentFragment> _inlineNodes = new(ReferenceEqualityComparer.Instance);
    private readonly List<MarkdownDiagnostic> _diagnostics = new();

    private MarkdownExtensionContentCollector(
        string source,
        MarkdownExtensionSet extensions,
        CancellationToken cancellationToken,
        MarkdownExtensionSet? deferredSceneMarkers)
    {
        _source = source;
        _extensions = extensions;
        _cancellationToken = cancellationToken;
        _deferredSceneMarkers = deferredSceneMarkers;
    }

    internal static async ValueTask<MarkdownExtensionContentSnapshot> BuildAsync(
        string source,
        Markdig.Syntax.MarkdownDocument document,
        MarkdownExtensionSet extensions,
        CancellationToken cancellationToken,
        bool deferOffscreenScenes)
    {
        if (extensions.BlockSyntaxKinds.Count == 0 && extensions.InlineSyntaxKinds.Count == 0)
            return MarkdownExtensionContentSnapshot.Empty;

        var collector = new MarkdownExtensionContentCollector(
            source,
            extensions,
            cancellationToken,
            deferOffscreenScenes ? extensions : null);
        await collector.VisitAsync(document).ConfigureAwait(false);
        return collector.ToSnapshot();
    }

    private async ValueTask VisitAsync(ContainerBlock container)
    {
        foreach (Block block in container)
        {
            _cancellationToken.ThrowIfCancellationRequested();
            if (MarkdownSyntaxAdapter.TryGetBlockKind(block, out string kind) &&
                _extensions.TryGetBlockRendererInvoker(kind, out var renderer) &&
                renderer is not null)
            {
                var node = MarkdownSyntaxAdapter.CreateBlockNode(block, _source);
                var fragment = await RenderAsync(renderer, node).ConfigureAwait(false);
                SourceSpan sourceRange = block.Span.Start >= 0 && block.Span.Length > 0
                    ? new SourceSpan(block.Span.Start, block.Span.Length)
                    : SourceSpan.Empty;
                if (fragment.Items.Count > 0 && !sourceRange.IsEmpty)
                {
                    _blockNodes.Add(block, fragment);
                    AddBySourceRange(_blocks, sourceRange, fragment);
                }
            }

            if (block is LeafBlock { Inline: { } inlineContainer })
                await VisitInlinesAsync(inlineContainer).ConfigureAwait(false);
            if (block is ContainerBlock nested)
                await VisitAsync(nested).ConfigureAwait(false);
        }
    }

    private async ValueTask VisitInlinesAsync(ContainerInline container)
    {
        foreach (Inline inline in container)
        {
            _cancellationToken.ThrowIfCancellationRequested();
            if (MarkdownSyntaxAdapter.TryGetInlineKind(inline, out string kind) &&
                _extensions.TryGetInlineRendererInvoker(kind, out var renderer) &&
                renderer is not null)
            {
                var node = MarkdownSyntaxAdapter.CreateInlineNode(inline, _source);
                var fragment = await RenderAsync(renderer, node).ConfigureAwait(false);
                if (fragment.Items.Count > 0 && !node.SourceSpan.IsEmpty)
                {
                    _inlineNodes.Add(inline, fragment);
                    AddBySourceRange(_inlines, node.SourceSpan, fragment);
                }
            }

            if (inline is ContainerInline nested)
                await VisitInlinesAsync(nested).ConfigureAwait(false);
        }
    }

    private async ValueTask<MarkdownContentFragment> RenderAsync(
        MarkdownAsyncNodeRenderer renderer,
        MarkdownSyntaxNode node)
    {
        var content = new MarkdownContentBuilder();
        await renderer(
                new MarkdownExtensionContext(
                    node,
                    _cancellationToken,
                    _diagnostics.Add,
                    _deferredSceneMarkers),
                content)
            .ConfigureAwait(false);
        _cancellationToken.ThrowIfCancellationRequested();
        return content.Build();
    }

    private MarkdownExtensionContentSnapshot ToSnapshot()
    {
        _diagnostics.Sort(static (left, right) =>
        {
            int comparison = left.SourceSpan.Start.CompareTo(right.SourceSpan.Start);
            return comparison != 0
                ? comparison
                : string.CompareOrdinal(left.Code, right.Code);
        });
        return new MarkdownExtensionContentSnapshot(
            _blocks,
            _inlines,
            _blockNodes,
            _inlineNodes,
            _diagnostics);
    }

    private static void AddBySourceRange(
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
