using MarkdownRenderer.Layout;
using MarkdownRenderer.Layout.Boxes;
using MarkdownRenderer.Parsing.Internal.SafeHtml;

namespace MarkdownRenderer.Html.Internal.Renderers;

/// <summary>
/// Creates safe-HTML layout boxes and moves them through the layout
/// context's construction ownership.
/// </summary>
internal static class HtmlBoxes
{
    internal static StackBox CreateStack(
        MarkdownLayoutContext context,
        bool collapsePositiveAdjacentMargins = false)
    {
        var stack = new StackBox
        {
            BlockIndex = context.NextBlockIndex(),
            FlowDirection = context.FlowDirection,
            CollapsePositiveAdjacentMargins = collapsePositiveAdjacentMargins,
        };
        context.RegisterConstructionBlock(stack);
        return stack;
    }

    internal static InlineContainerBox CreateInlineBox(
        MarkdownLayoutContext context,
        string elementKey,
        SafeHtmlAlignment alignment)
    {
        var box = new InlineContainerBox(context, elementKey)
        {
            BlockIndex = context.NextBlockIndex(),
            TextAlignment = HtmlAlignments.ToCanvasAlignment(alignment),
        };
        context.RegisterConstructionBlock(box);
        return box;
    }

    internal static T TrackBlock<T>(MarkdownLayoutContext context, T block) where T : BlockBox
    {
        context.RegisterConstructionBlock(block);
        return block;
    }

    internal static void AddChild(StackBox parent, BlockBox child, MarkdownLayoutContext context)
    {
        parent.Add(child);
        context.AdoptConstructionBlock(child);
    }

    internal static void AddRun(InlineContainerBox parent, InlineRun run)
    {
        if (run is InlineImageRun image)
            parent.Context.RegisterConstructionBlock(image.Image);
        parent.Add(run);
        if (run is InlineImageRun addedImage)
            parent.Context.AdoptConstructionBlock(addedImage.Image);
    }

    internal static BlockBox? ReturnIfNonEmpty(BlockBox block, MarkdownLayoutContext context)
    {
        bool hasContent = block switch
        {
            StackBox stack => stack.Children.Count > 0,
            InlineContainerBox inline => inline.Runs.Count > 0,
            _ => true,
        };
        if (hasContent)
            return block;

        context.DiscardConstructionBlock(block);
        return null;
    }
}
