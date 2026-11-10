using System.Text;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using MarkdownRenderer.Accessibility;
using MarkdownRenderer.Hosting;
using MarkdownRenderer.Hosting.Internal;
using MarkdownRenderer.Layout.Boxes;
using MarkdownRenderer.Parsing.Internal.SafeHtml;
using MarkdownRenderer.Theming;

namespace MarkdownRenderer.Layout;

/// <summary>
/// Lightweight block/inline builder for GFM extension renderers. Handles common
/// Markdig block types without requiring access to the internal LayoutBuilder.
/// The GFM, safe-HTML and GitHub packs share this one copy.
/// </summary>
internal static class GfmChildBuilder
{
    /// <summary>Builds child blocks from <paramref name="container"/> and adds them to <paramref name="stack"/>.</summary>
    internal static void PopulateChildren(
        StackBox stack,
        ContainerBlock container,
        MarkdownLayoutContext context,
        SafeHtmlAlignment inheritedAlignment = SafeHtmlAlignment.Inherit)
    {
        // The renderer that owns this stack may throw after child construction
        // begins but before it returns the stack to its caller. Keep that
        // partial owner in the build ledger so already-attached children are
        // still retired on that failure path.
        context.RegisterConstructionBlock(stack);
        SafeHtmlBlockScopeTracker? htmlScopes = context.Registry.SafeHtmlPolicy is null
            ? null
            : context.SafeHtmlBlock.NestedScopes?.CloneForNestedContent() ??
              new SafeHtmlBlockScopeTracker(context.Registry.SafeHtmlPolicy.Limits);
        bool htmlBudgetNoticeAdded = htmlScopes?.BudgetExceeded == true;
        foreach (Block child in container)
        {
            context.CancellationToken.ThrowIfCancellationRequested();
            bool suppressedBeforeBlock = htmlScopes?.IsContentSuppressed == true;
            SafeHtmlAlignment alignmentAtBlockEntry = htmlScopes?.CurrentAlignment ?? SafeHtmlAlignment.Inherit;
            if (alignmentAtBlockEntry == SafeHtmlAlignment.Inherit)
                alignmentAtBlockEntry = inheritedAlignment;
            int? inlineFlowScopeIdAtBlockEntry = htmlScopes?.CurrentInlineFlowScopeId;
            bool scopeOnly = child is HtmlBlock htmlBlock && htmlScopes?.Process(
                    htmlBlock.Lines.ToString(),
                    htmlBlock.Span.Start,
                    context.DisclosureStates,
                    context.CancellationToken) == true;
            if (suppressedBeforeBlock && child is not HtmlBlock)
            {
                htmlScopes?.ObserveBlockTags(
                    child,
                    context.DisclosureStates,
                    context.CancellationToken);
            }

            if (htmlScopes?.BudgetExceeded == true)
            {
                if (!htmlBudgetNoticeAdded)
                {
                    AddHtmlBudgetNotice(stack, context);
                    htmlBudgetNoticeAdded = true;
                }

                if (child is HtmlBlock || suppressedBeforeBlock)
                    continue;
            }

            if (scopeOnly || suppressedBeforeBlock)
                continue;

            if (!TryAddChild(stack, child, context, htmlScopes, alignmentAtBlockEntry, inlineFlowScopeIdAtBlockEntry))
                continue;

            if (!suppressedBeforeBlock && child is not HtmlBlock)
            {
                htmlScopes?.ObserveBlockTags(
                    child,
                    context.DisclosureStates,
                    context.CancellationToken);
                if (htmlScopes?.BudgetExceeded == true && !htmlBudgetNoticeAdded)
                {
                    AddHtmlBudgetNotice(stack, context);
                    htmlBudgetNoticeAdded = true;
                }
            }
        }
    }

    // Builds one child under the safe-HTML context captured at its entry and
    // adopts it into the stack. Returns false when the child produced no box.
    private static bool TryAddChild(
        StackBox stack,
        Block child,
        MarkdownLayoutContext context,
        SafeHtmlBlockScopeTracker? htmlScopes,
        SafeHtmlAlignment alignmentAtBlockEntry,
        int? inlineFlowScopeIdAtBlockEntry)
    {
        SafeHtmlBlockScopeTracker? nestedScopes = child is ContainerBlock or HtmlBlock
            ? htmlScopes?.CloneForNestedContent()
            : null;
        BlockBox? box;
        using (context.SafeHtmlBlock.Push(
                   alignmentAtBlockEntry,
                   nestedScopes,
                   inlineFlowScopeIdAtBlockEntry))
            box = TryBuildBlock(child, context);
        if (box is null)
            return false;

        if (child is not HtmlBlock)
            ApplyHtmlAlignment(box, alignmentAtBlockEntry);
        stack.Add(box);
        context.AdoptConstructionBlock(box);
        return true;
    }

    private static void AddHtmlBudgetNotice(StackBox stack, MarkdownLayoutContext context)
    {
        var notice = new InlineContainerBox(context, MarkdownElementKeys.Body)
        {
            BlockIndex = context.NextBlockIndex(),
        };
        context.RegisterConstructionBlock(notice);
        notice.Add(new TextRun(context.ResolveString(
            MarkdownStringKeys.HtmlBudgetExceeded,
            MarkdownLocalizedStrings.HtmlBudgetExceeded))
        {
            SourceSpan = MarkdownRenderer.SourceSpan.Empty,
        });
        stack.Add(notice);
        context.AdoptConstructionBlock(notice);
    }

    private static void ApplyHtmlAlignment(BlockBox box, SafeHtmlAlignment alignment)
    {
        if (box is StackBox { HasResolvedSafeHtmlAlignment: true })
            return;

        if (alignment == SafeHtmlAlignment.Inherit)
            return;

        var canvasAlignment = alignment switch
        {
            SafeHtmlAlignment.Center => Microsoft.Graphics.Canvas.Text.CanvasHorizontalAlignment.Center,
            SafeHtmlAlignment.Right => Microsoft.Graphics.Canvas.Text.CanvasHorizontalAlignment.Right,
            _ => Microsoft.Graphics.Canvas.Text.CanvasHorizontalAlignment.Left,
        };
        switch (box)
        {
            case InlineContainerBox inline:
                inline.TextAlignment = canvasAlignment;
                break;
            case ImageBox image:
                image.ContentAlignment = canvasAlignment;
                break;
            case StackBox nested:
                foreach (BlockBox child in nested.Children)
                    ApplyHtmlAlignment(child, alignment);
                break;
        }
    }

    /// <summary>Attempts to build a <see cref="BlockBox"/> for a Markdig block node.</summary>
    internal static BlockBox? TryBuildBlock(Block block, MarkdownLayoutContext context)
    {
        // A nested list is not a generic stack: it owns marker numbering,
        // tight/loose spacing and list-depth indentation. Use the same builder
        // as top-level lists when called inside a normal layout transaction.
        if (block is ListBlock && context.ChildBuilders.TryBuildBlock(block, out BlockBox? nestedList))
            return nestedList;

        // Code blocks need the owning layout's source projection and chunk
        // planner. Details summaries and other extension-owned containers can
        // contain Markdig code nodes too, so use the same builder as top-level
        // code whenever that owner is available. Keep a shared-layout fallback
        // for internal callers that construct a child outside BuildBlocks.
        if (block is CodeBlock code)
        {
            if (context.ChildBuilders.TryBuildBlock(block, out BlockBox? ownerBuiltCode))
                return ownerBuiltCode;

            return LayoutBuilder.BuildCodeBlockForExtension(code, context);
        }

        using var attrScope = context.PushMarkdownAttributes(block);
        BlockBox? box = null;

        // Check registry first so registered custom renderers are honoured.
        if (context.Registry.TryGetRenderer(block.GetType(), out var renderer) && renderer is not null)
        {
            var custom = renderer.BuildBlock(block, context);
            if (custom is not null)
            {
                context.RegisterConstructionBlock(custom);
                box = custom;
            }
        }

        box ??= block switch
        {
            ParagraphBlock p => BuildLeaf(p, context, MarkdownElementKeys.Body),
            HeadingBlock h => BuildLeaf(h, context, h.Level switch
            {
                1 => MarkdownElementKeys.Heading1,
                2 => MarkdownElementKeys.Heading2,
                3 => MarkdownElementKeys.Heading3,
                4 => MarkdownElementKeys.Heading4,
                5 => MarkdownElementKeys.Heading5,
                _ => MarkdownElementKeys.Heading6
            }),
            ContainerBlock cb => BuildContainer(cb, context),
            _ => null
        };

        if (box is not null)
        {
            context.RegisterConstructionBlock(box);
            if (box.BlockIndex == 0)
                box.BlockIndex = context.NextBlockIndex();
            context.RegisterMarkdownAttributes(block, box.BlockIndex);
        }

        return box;
    }

    private static InlineContainerBox BuildLeaf(LeafBlock leaf, MarkdownLayoutContext context, string elementKey)
    {
        var box = new InlineContainerBox(context, elementKey);
        context.RegisterConstructionBlock(box);
        box.BlockIndex = context.NextBlockIndex();
        if (leaf.Inline is not null)
            AddInlines(box, leaf.Inline);
        return box;
    }

    private static StackBox BuildContainer(ContainerBlock cb, MarkdownLayoutContext context)
    {
        var stack = new StackBox
        {
            FlowDirection = context.FlowDirection,
        };
        context.RegisterConstructionBlock(stack);
        stack.BlockIndex = context.NextBlockIndex();
        PopulateChildren(stack, cb, context);
        return stack;
    }

    internal static void AddInlines(
        InlineContainerBox box,
        ContainerInline inlines,
        System.Func<Inline, bool>? skipFirstIf = null,
        int inheritedAliasStart = -1)
        => GfmChildInlineBuilder.AddInlines(box, inlines, skipFirstIf, inheritedAliasStart);

    /// <inheritdoc cref="GfmChildInlineBuilder.AddInlinesFromSource"/>
    internal static void AddInlinesFromSource(
        InlineContainerBox box,
        ContainerInline inlines,
        int bodySourceStart)
        => GfmChildInlineBuilder.AddInlinesFromSource(box, inlines, bodySourceStart);

    internal static void FlattenInlines(ContainerInline container, StringBuilder sb)
        => GfmChildInlineBuilder.FlattenInlines(container, sb);
}
