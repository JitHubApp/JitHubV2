using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using MarkdownRenderer.Extensions;

namespace MarkdownRenderer.Document;

/// <summary>
/// Runs the registered extension renderers over a parsed document and
/// collects their declarative content by source range and by exact node.
/// </summary>
internal static class MarkdownExtensionContentCollector
{
    internal static async ValueTask<MarkdownExtensionContentSnapshot> BuildAsync(
        string source,
        Markdig.Syntax.MarkdownDocument document,
        MarkdownExtensionSet extensions,
        CancellationToken cancellationToken,
        bool deferOffscreenScenes)
    {
        if (extensions.BlockSyntaxKinds.Count == 0 && extensions.InlineSyntaxKinds.Count == 0)
            return MarkdownExtensionContentSnapshot.Empty;

        MarkdownExtensionSet? deferredSceneMarkers = deferOffscreenScenes ? extensions : null;

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
        return new MarkdownExtensionContentSnapshot(
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
                            new MarkdownExtensionContext(
                                node,
                                cancellationToken,
                                extensionDiagnostics.Add,
                                deferredSceneMarkers),
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
                            new MarkdownExtensionContext(
                                node,
                                cancellationToken,
                                extensionDiagnostics.Add,
                                deferredSceneMarkers),
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
}
