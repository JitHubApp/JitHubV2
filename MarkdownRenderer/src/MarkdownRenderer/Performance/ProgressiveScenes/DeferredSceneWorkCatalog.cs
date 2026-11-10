using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using MarkdownRenderer.Document;
using MarkdownRenderer.Document.Internal;
using MarkdownRenderer.Extensions;
using RendererDocument = MarkdownRenderer.Document.MarkdownDocument;

namespace MarkdownRenderer.Performance.ProgressiveScenes;

/// <summary>
/// Reads the private deferred-scene markers that built-in scene feature packs
/// attach to their readable fallback content, and builds a plan's immutable
/// producer table from them.
/// </summary>
internal static class DeferredSceneWorkCatalog
{
    // Built-in scene feature packs use this private, serializable content
    // attribute to mark a readable fallback. The marker value is
    // "<exact syntax kind>|<document-unique token>". It is metadata only; the
    // source text and source span remain the fallback and diagnostic authority.

    private const char MarkerSeparator = '|';

    /// <summary>
    /// Builds the immutable marker-to-producer table from parser output. Returns
    /// null when canceled or when the document has no deferred scene work.
    /// </summary>
    internal static IReadOnlyDictionary<string, DeferredSceneWork>? TryBuild(
        RendererDocument document,
        CancellationToken cancellationToken)
    {
        var works = new Dictionary<string, DeferredSceneWork>(StringComparer.Ordinal);
        foreach (KeyValuePair<Block, MarkdownContentFragment> entry in document.ExtensionBlockNodeContent)
        {
            if (cancellationToken.IsCancellationRequested)
                return null;

            Block block = entry.Key;
            if (!MarkdownSyntaxAdapter.TryGetBlockKind(block, out string syntaxKind))
            {
                continue;
            }

            AddBlockWorks(works, document, block, syntaxKind, entry.Value);
        }

        foreach (KeyValuePair<Inline, MarkdownContentFragment> entry in document.ExtensionInlineNodeContent)
        {
            if (cancellationToken.IsCancellationRequested)
                return null;

            Inline inline = entry.Key;
            if (!MarkdownSyntaxAdapter.TryGetInlineKind(inline, out string syntaxKind))
                continue;

            AddInlineWorks(works, document, inline, syntaxKind, entry.Value);
        }

        if (cancellationToken.IsCancellationRequested || works.Count == 0)
            return null;

        return new ReadOnlyDictionary<string, DeferredSceneWork>(works);
    }

    private static void AddBlockWorks(
        Dictionary<string, DeferredSceneWork> works,
        RendererDocument document,
        Block block,
        string syntaxKind,
        MarkdownContentFragment fragment)
    {
        MarkdownSyntaxNode? node = null;
        Collect(fragment.Items, content =>
        {
            if (!content.Attributes.TryGetValue(MarkdownContentAttributes.DeferredScene, out string? marker) ||
                marker is null ||
                !TryReadMarker(marker, syntaxKind, out _) ||
                fragment.DeferredSceneRenderer is not { } renderer)
            {
                return;
            }

            MarkdownSyntaxNode blockNode = node ??= MarkdownSyntaxAdapter.CreateBlockNode(block, document.Source);
            if (!content.SourceSpan.Equals(blockNode.SourceSpan))
                return;

            var work = new DeferredSceneWork(
                blockNode,
                renderer,
                fragment.DeferredSceneRendererLifetime,
                content.Text ?? blockNode.Literal ?? string.Empty,
                content.Language,
                IsInline: false);
            // A malformed producer must not cause two visible boxes to
            // invoke the same callback/result slot. Independent markers
            // remain distinct even when their SourceSpan values coincide.
            works.TryAdd(marker, work);
        });
    }

    private static void AddInlineWorks(
        Dictionary<string, DeferredSceneWork> works,
        RendererDocument document,
        Inline inline,
        string syntaxKind,
        MarkdownContentFragment fragment)
    {
        MarkdownSyntaxNode? node = null;
        Collect(fragment.Items, content =>
        {
            if (!content.Attributes.TryGetValue(MarkdownContentAttributes.DeferredScene, out string? marker) ||
                marker is null ||
                !TryReadMarker(marker, syntaxKind, out _) ||
                fragment.DeferredSceneRenderer is not { } renderer)
            {
                return;
            }

            MarkdownSyntaxNode inlineNode = node ??= MarkdownSyntaxAdapter.CreateInlineNode(inline, document.Source);
            if (!content.SourceSpan.Equals(inlineNode.SourceSpan))
                return;

            var work = new DeferredSceneWork(
                inlineNode,
                renderer,
                fragment.DeferredSceneRendererLifetime,
                content.SemanticText ?? inlineNode.Literal ?? string.Empty,
                content.Language,
                IsInline: true);
            works.TryAdd(marker, work);
        });
    }

    internal static bool ContainsDeferredSceneFallback(RendererDocument document)
    {
        foreach (MarkdownContentFragment fragment in document.ExtensionBlockNodeContent.Values)
        {
            bool found = false;
            Collect(fragment.Items, content => found = true);
            if (found)
                return true;
        }

        foreach (MarkdownContentFragment fragment in document.ExtensionInlineNodeContent.Values)
        {
            bool found = false;
            Collect(fragment.Items, content => found = true);
            if (found)
                return true;
        }

        return false;
    }

    internal static void Collect(
        IReadOnlyList<MarkdownContent> content,
        Action<MarkdownContent> onCandidate)
    {
        foreach (MarkdownContent item in content)
        {
            if (item.Attributes.ContainsKey(MarkdownContentAttributes.DeferredScene))
            {
                onCandidate(item);
            }

            if (item.Children.Count > 0)
                Collect(item.Children, onCandidate);
        }
    }

    internal static bool TryReadMarker(string marker, string syntaxKind, out string token)
    {
        token = string.Empty;
        int separator = marker.IndexOf(MarkerSeparator);
        if (separator <= 0 || separator == marker.Length - 1 ||
            !string.Equals(marker[..separator], syntaxKind, StringComparison.Ordinal))
        {
            return false;
        }

        token = marker[(separator + 1)..];
        return true;
    }
}
