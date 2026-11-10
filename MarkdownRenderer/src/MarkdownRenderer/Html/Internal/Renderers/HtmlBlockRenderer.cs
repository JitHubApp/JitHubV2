using System.Collections.Generic;
using System.Linq;
using Markdig;
using Markdig.Syntax;
using MarkdownRenderer.Layout;
using MarkdownRenderer.Layout.Boxes;
using MarkdownRenderer.Theming;
using MarkdownRenderer.Hosting;
using MarkdownRenderer.Hosting.Internal;
using MarkdownRenderer.Parsing.Internal;
using MarkdownRenderer.Parsing.Internal.SafeHtml;

namespace MarkdownRenderer.Html.Internal.Renderers;

/// <summary>
/// Renders the safe HTML subset GitHub READMEs commonly use for presentation.
/// Active content and unsafe URL schemes are never materialized.
/// </summary>
internal sealed class HtmlBlockRenderer : MarkdownNodeRenderer<HtmlBlock>
{
    private readonly SafeHtmlOptions _options;
    private readonly HtmlNestedMarkdown _nestedMarkdown = new();
    private readonly HtmlBlockBuilder _blocks;

    internal HtmlBlockRenderer(SafeHtmlOptions? options = null)
    {
        _options = options ?? SafeHtmlOptions.Default;
        _blocks = new HtmlBlockBuilder(_options, new HtmlInlineBuilder(_options), _nestedMarkdown);
    }

    /// <inheritdoc />
    public override BlockBox? BuildBlock(HtmlBlock htmlBlock, MarkdownLayoutContext context)
    {
        HtmlNestedMarkdownPass pass = _nestedMarkdown.GetPass(context);
        MarkdownDocument? previousRoot = pass.RootDocument;
        Block rootNode = htmlBlock;
        while (rootNode.Parent is { } parent)
            rootNode = parent;
        pass.RootDocument = rootNode as MarkdownDocument;
        try
        {
            // A configured safe-HTML renderer owns every HtmlBlock, including blocks
            // that intentionally produce no visual content (comments, declarations,
            // and suppressed active elements). Returning null would invoke the core
            // literal fallback and leak that hidden markup into the document.
            return BuildRoot(htmlBlock, context, pass);
        }
        finally
        {
            pass.RootDocument = previousRoot;
        }
    }

    private StackBox BuildRoot(
        HtmlBlock htmlBlock,
        MarkdownLayoutContext context,
        HtmlNestedMarkdownPass pass)
    {
        SafeHtmlParseLimits limits = CreateParseLimits(_options.Budgets);
        string htmlSource = htmlBlock.Lines.ToString();
        MarkdownSourceProjection? htmlSourceProjection = ResolveSourceProjection(
            htmlBlock,
            htmlSource,
            context,
            out int sourceOffset);
        // Every HtmlBlock owns a separate coordinate space. Explicitly
        // clear a parent's mapper for an ordinary/global-source block so
        // nested parsing cannot accidentally apply the previous block's
        // projection a second time.
        using var sourceProjectionScope = context.SourceProjections.PushSafeHtml(htmlSourceProjection);
        // Markdig can retain Markdown after a whitespace-only blank line
        // inside a void-element HTML block (for example <hr> followed by
        // a heading). Split only a pure, closed tag prefix; reparse the
        // tail under the same GFM pipeline and safe-HTML policy.
        int markdownTailStart = HtmlNestedMarkdown.FindMarkdownTailAfterVoidTags(
            htmlSource,
            limits,
            pass,
            context.CancellationToken);
        SafeHtmlDocument document = SafeHtmlParser.Parse(
            markdownTailStart < 0 ? htmlSource : htmlSource[..markdownTailStart],
            limits,
            context.CancellationToken);
        var root = HtmlBoxes.CreateStack(context, collapsePositiveAdjacentMargins: true);
        root.HasResolvedSafeHtmlAlignment = true;
        SafeHtmlAlignment inheritedAlignment = context.SafeHtmlBlock.Alignment;
        _blocks.AppendBlocks(root, document.Root.Children, context, sourceOffset, inheritedAlignment);

        if (markdownTailStart >= 0)
        {
            HtmlNestedMarkdown.AppendMarkdownTail(
                root,
                htmlSource[markdownTailStart..],
                sourceOffset + markdownTailStart,
                context,
                pass);
        }

        if (document.IsTruncated)
            AppendBudgetNotice(root, context, inheritedAlignment);

        ApplyInlineFlowScope(root, document, context);
        return root;
    }

    private static SafeHtmlParseLimits CreateParseLimits(SafeHtmlBudgets budgets) =>
        new(
            budgets.MaxInputLength,
            budgets.MaxNodeCount,
            budgets.MaxNestingDepth,
            budgets.MaxAttributeCount,
            budgets.MaxAttributeValueLength,
            budgets.MaxTagLength);

    private static MarkdownSourceProjection? ResolveSourceProjection(
        HtmlBlock htmlBlock,
        string htmlSource,
        MarkdownLayoutContext context,
        out int sourceOffset)
    {
        sourceOffset = htmlBlock.Span.Start;
        if (context.SourceProjection is { } markdownProjection &&
            markdownProjection.TryGetHtmlBlockProjection(
                htmlBlock,
                htmlSource,
                context.CancellationToken,
                out MarkdownSourceProjection mappedProjection))
        {
            sourceOffset = 0;
            return mappedProjection;
        }

        if (context.SourceProjection is null &&
            HtmlBlockSourceProjection.TryCreateDirectHtmlBlockProjection(
                htmlBlock,
                htmlSource,
                context.SourceMap.SourceText,
                context.CancellationToken,
                out MarkdownSourceProjection directProjection))
        {
            // In ordinary Markdown, Markdig's HtmlBlock line slices still
            // point at their exact authored ranges even when a container
            // (for example, a block quote) removed a prefix from the text
            // passed to SafeHtmlParser. Build that line-derived map rather
            // than treating HtmlBlock.Span.Start as a contiguous offset.
            sourceOffset = 0;
            return directProjection;
        }

        return null;
    }

    private static void AppendBudgetNotice(
        StackBox root,
        MarkdownLayoutContext context,
        SafeHtmlAlignment alignment)
    {
        var notice = HtmlBoxes.CreateInlineBox(context, MarkdownElementKeys.Body, alignment);
        notice.Add(new TextRun(context.ResolveString(
            MarkdownStringKeys.HtmlBudgetExceeded,
            MarkdownLocalizedStrings.HtmlBudgetExceeded))
        {
            SourceSpan = SourceSpan.Empty,
        });
        HtmlBoxes.AddChild(root, notice, context);
    }

    private static void ApplyInlineFlowScope(
        StackBox root,
        SafeHtmlDocument document,
        MarkdownLayoutContext context)
    {
        if (context.SafeHtmlInlineFlowScopeId is int inlineFlowScopeId &&
            IsInlineOnlyHtmlFragment(document.Root.Children) &&
            root.Children.Count == 1 &&
            root.Children[0] is InlineContainerBox inlineFragment)
        {
            root.SafeHtmlInlineFlowScopeId = inlineFlowScopeId;
            // Raw inline content in a persistent HTML div is not a
            // Markdown paragraph, so the theme's Body paragraph margin
            // must not separate source fragments of that same flow.
            inlineFragment.UseContainerMargin = false;
            inlineFragment.Margin = default;
        }
    }

    private static bool IsInlineOnlyHtmlFragment(IReadOnlyList<SafeHtmlNode> nodes)
    {
        if (nodes.Count == 0)
        {
            return false;
        }

        if (nodes.Count == 1 &&
            nodes[0] is SafeHtmlElement { Name: "div" or "center" } flowContainer)
        {
            return !flowContainer.Children.Any(
                static child => child is SafeHtmlElement element && HtmlElementNames.IsBlockElement(element.Name));
        }

        foreach (SafeHtmlNode node in nodes)
        {
            if (node is SafeHtmlElement element && HtmlElementNames.IsBlockElement(element.Name))
                return false;
        }

        return true;
    }
}
