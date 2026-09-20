using System;
using System.Collections.Generic;
using System.Threading;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using MarkdownRenderer.Parsing;

namespace MarkdownRenderer.Images;

internal static class MarkdownImagePrefetchSourceCollector
{
    // A renderer must never turn an adversarial document into an unbounded network queue.
    // This is intentionally above ordinary README image counts while remaining finite.
    private const int MaximumSourceCount = 2048;

    internal static IReadOnlyList<string> Collect(
        MarkdownDocument document,
        SafeHtmlRenderPolicy? safeHtmlPolicy,
        CancellationToken cancellationToken)
    {
        HashSet<string> seen = new(StringComparer.Ordinal);
        List<string> sources = [];
        VisitBlocks(document, safeHtmlPolicy, seen, sources, cancellationToken);
        return sources;
    }

    private static void VisitBlocks(
        ContainerBlock container,
        SafeHtmlRenderPolicy? safeHtmlPolicy,
        HashSet<string> seen,
        List<string> sources,
        CancellationToken cancellationToken)
    {
        foreach (Block block in container)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (sources.Count >= MaximumSourceCount)
            {
                return;
            }

            if (block is HtmlBlock htmlBlock && safeHtmlPolicy is { EnableImages: true })
            {
                CollectHtmlSources(
                    htmlBlock.Lines.ToString(),
                    safeHtmlPolicy.Limits,
                    seen,
                    sources,
                    cancellationToken);
            }

            if (block is LeafBlock { Inline: { } inline })
            {
                VisitInlines(inline, safeHtmlPolicy, seen, sources, cancellationToken);
            }

            if (block is ContainerBlock nested)
            {
                VisitBlocks(nested, safeHtmlPolicy, seen, sources, cancellationToken);
            }
        }
    }

    private static void VisitInlines(
        ContainerInline container,
        SafeHtmlRenderPolicy? safeHtmlPolicy,
        HashSet<string> seen,
        List<string> sources,
        CancellationToken cancellationToken)
    {
        List<string> suppressedElements = [];
        foreach (Inline inline in container)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (sources.Count >= MaximumSourceCount)
            {
                return;
            }

            if (inline is LinkInline { IsImage: true } image && suppressedElements.Count == 0)
            {
                Add(image.Url, seen, sources);
            }
            else if (inline is HtmlInline html && safeHtmlPolicy is { EnableImages: true })
            {
                CollectHtmlSources(
                    html.Tag,
                    safeHtmlPolicy.Limits,
                    seen,
                    sources,
                    cancellationToken,
                    suppressedElements);
            }

            if (inline is ContainerInline nested && suppressedElements.Count == 0)
            {
                VisitInlines(nested, safeHtmlPolicy, seen, sources, cancellationToken);
            }
        }
    }

    private static void CollectHtmlSources(
        string html,
        SafeHtmlParseLimits limits,
        HashSet<string> seen,
        List<string> sources,
        CancellationToken cancellationToken,
        List<string>? inheritedSuppressedElements = null)
    {
        List<string> suppressedElements = inheritedSuppressedElements ?? [];
        foreach (SafeHtmlTag tag in SafeHtmlParser.ParseTags(
                     html,
                     limits,
                     cancellationToken,
                     out _))
        {
            if (sources.Count >= MaximumSourceCount)
            {
                return;
            }

            if (tag.Kind == SafeHtmlTagKind.Closing)
            {
                for (int index = suppressedElements.Count - 1; index >= 0; index--)
                {
                    if (!string.Equals(suppressedElements[index], tag.Name, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    suppressedElements.RemoveRange(index, suppressedElements.Count - index);
                    break;
                }

                continue;
            }

            bool suppressedElement = SafeHtmlParser.IsSuppressedElement(tag.Name);
            if (tag.Kind == SafeHtmlTagKind.Opening && suppressedElement)
            {
                suppressedElements.Add(tag.Name);
                continue;
            }

            if (suppressedElements.Count > 0 || suppressedElement)
            {
                continue;
            }

            if (tag.Name == "img" &&
                tag.Kind is SafeHtmlTagKind.Opening or SafeHtmlTagKind.SelfClosing &&
                SafeHtmlParser.TryGetSafeImageSource(tag, out string source))
            {
                Add(source, seen, sources);
            }
        }
    }

    private static void Add(string? source, HashSet<string> seen, List<string> sources)
    {
        if (!string.IsNullOrWhiteSpace(source) && seen.Add(source))
        {
            sources.Add(source);
        }
    }
}
