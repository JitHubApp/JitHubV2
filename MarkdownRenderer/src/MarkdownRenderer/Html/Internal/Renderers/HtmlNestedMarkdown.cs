using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Markdig;
using Markdig.Syntax;
using MarkdownRenderer.Layout.Internal.Boxes;
using MarkdownRenderer.Layout.Internal.Building;
using MarkdownRenderer.Layout.Internal.Context;
using MarkdownRenderer.Layout.Internal.SourceProjection;
using MarkdownRenderer.Parsing.Internal.SafeHtml;

namespace MarkdownRenderer.Html.Internal.Renderers;

/// <summary>
/// Reparses Markdown that GitHub permits inside safe-HTML blocks: the tail
/// after a pure void-tag prefix and the text of inert presentation
/// containers. Owns the per-layout-pass recursion ceiling.
/// </summary>
internal sealed class HtmlNestedMarkdown
{
    private const int MaxNestedMarkdownDepth = 4;

    private const string GitHubMarkdownContainersFeature =
        "MarkdownRenderer.GitHub.MarkdownInHtmlContainers";

    // Nested-Markdown recursion depth and the root document whose reference
    // definitions nested fragments replay. Both belong to one layout pass; a
    // layout context builds one pass at a time, so it keys this state.
    private readonly ConditionalWeakTable<MarkdownLayoutContext, HtmlNestedMarkdownPass> _passes = new();

    internal HtmlNestedMarkdownPass GetPass(MarkdownLayoutContext context) =>
        _passes.GetOrCreateValue(context);

    internal static int FindMarkdownTailAfterVoidTags(
        string source,
        SafeHtmlParseLimits limits,
        HtmlNestedMarkdownPass pass,
        System.Threading.CancellationToken cancellationToken)
    {
        if (pass.Depth >= MaxNestedMarkdownDepth)
            return -1;

        int lineStart = 0;
        while (lineStart < source.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int lineEnd = source.IndexOfAny(['\r', '\n'], lineStart);
            if (lineEnd < 0)
                break;
            int nextLine = lineEnd + 1;
            if (source[lineEnd] == '\r' && nextLine < source.Length && source[nextLine] == '\n')
                nextLine++;

            if (lineStart > 0 &&
                source.AsSpan(lineStart, lineEnd - lineStart).Trim().IsEmpty &&
                nextLine < source.Length)
            {
                // Later blank lines cannot turn a non-tag prefix into a pure
                // void-tag prefix, so inspect only the first boundary.
                if (!SafeHtmlParser.TryParseTagSequence(
                        source[..lineStart],
                        limits,
                        cancellationToken,
                        out IReadOnlyList<SafeHtmlTag> tags,
                        out _) || tags.Count == 0)
                    return -1;

                foreach (SafeHtmlTag tag in tags)
                {
                    if (tag.Kind != SafeHtmlTagKind.SelfClosing)
                        return -1;
                }

                return nextLine;
            }

            lineStart = nextLine;
        }

        return -1;
    }

    /// <summary>
    /// Reparses the Markdown that follows a pure void-tag prefix and appends
    /// its blocks to <paramref name="root"/>, one nesting level deeper.
    /// </summary>
    internal static void AppendMarkdownTail(
        StackBox root,
        string tailSource,
        int absoluteTextStart,
        MarkdownLayoutContext context,
        HtmlNestedMarkdownPass pass)
    {
        pass.Depth++;
        try
        {
            string nestedSource = HtmlNestedMarkdownSource.BuildNestedMarkdownSource(
                tailSource,
                absoluteTextStart,
                context,
                pass.RootDocument,
                out int bodyOffset,
                out MarkdownSourceProjection? nestedProjection);
            MarkdownDocument tail = Markdown.Parse(nestedSource, context.Registry.BuildPipeline());
            if (nestedProjection is null)
            {
                HtmlMarkdownSpanOffsets.OffsetMarkdownSpans(tail, absoluteTextStart - bodyOffset);
                GfmChildBuilder.PopulateChildren(root, tail, context);
            }
            else
            {
                nestedProjection.ApplyTo(tail, context.CancellationToken);
                using (context.SourceProjections.PushMarkdown(nestedProjection))
                    GfmChildBuilder.PopulateChildren(root, tail, context);
            }
        }
        finally
        {
            pass.Depth--;
        }
    }

    internal BlockBox? TryBuildMarkdownContainer(
        SafeHtmlElement element,
        MarkdownLayoutContext context,
        int sourceOffset,
        SafeHtmlAlignment alignment)
    {
        HtmlNestedMarkdownPass pass = _passes.GetOrCreateValue(context);
        if (!context.Registry.HasPresentationFeature(GitHubMarkdownContainersFeature) ||
            element.Name is not ("article" or "aside" or "center" or "div" or "main" or "section") ||
            element.Children.Count != 1 ||
            element.Children[0] is not SafeHtmlText text ||
            string.IsNullOrWhiteSpace(text.RawText) ||
            pass.Depth >= MaxNestedMarkdownDepth)
        {
            return null;
        }

        // CommonMark deliberately treats the contents of a type-6 HTML block as
        // raw text. GitHub README rendering permits Markdown in these inert
        // presentation containers, which is heavily used for centered badge and
        // sponsor groups. Reparse only the text child, under the same immutable
        // pipeline and a strict recursion ceiling; active HTML remains governed
        // by the safe-HTML policy on every nested pass.
        pass.Depth++;
        try
        {
            int absoluteTextStart = sourceOffset + text.SourceStart;
            string nestedMarkdownSource = HtmlNestedMarkdownSource.BuildNestedMarkdownSource(
                text.RawText,
                absoluteTextStart,
                context,
                pass.RootDocument,
                out int nestedMarkdownBodyOffset,
                out MarkdownSourceProjection? nestedProjection);
            MarkdownDocument fragment = Markdown.Parse(
                nestedMarkdownSource,
                context.Registry.BuildPipeline());
            if (nestedProjection is null)
            {
                HtmlMarkdownSpanOffsets.OffsetMarkdownSpans(fragment, absoluteTextStart - nestedMarkdownBodyOffset);
            }
            else
            {
                nestedProjection.ApplyTo(fragment, context.CancellationToken);
            }

            var stack = HtmlBoxes.CreateStack(context);
            if (nestedProjection is null)
            {
                GfmChildBuilder.PopulateChildren(stack, fragment, context, alignment);
            }
            else
            {
                using (context.SourceProjections.PushMarkdown(nestedProjection))
                    GfmChildBuilder.PopulateChildren(stack, fragment, context, alignment);
            }
            return HtmlBoxes.ReturnIfNonEmpty(stack, context);
        }
        finally
        {
            pass.Depth--;
        }
    }
}
