using Markdig.Extensions.DefinitionLists;
using Markdig.Syntax;
using MarkdownRenderer.Layout;
using MarkdownRenderer.Layout.Boxes;
using MarkdownRenderer.Parsing;
using MarkdownRenderer.Theming;
using Microsoft.UI.Xaml;

namespace MarkdownRenderer.Gfm.Renderers;

/// <summary>
/// Renders Markdown Extra definition lists as native term/description blocks.
/// </summary>
public sealed class DefinitionListRenderer : MarkdownNodeRenderer<DefinitionList>
{
    /// <inheritdoc />
    public override BlockBox? BuildBlock(DefinitionList list, MarkdownLayoutContext context)
    {
        var stack = new StackBox
        {
            FlowDirection = context.FlowDirection,
            Margin = new Thickness(0, 4, 0, 8),
        };
        stack.BlockIndex = context.NextBlockIndex();

        foreach (var child in list)
        {
            context.CancellationToken.ThrowIfCancellationRequested();
            if (child is not DefinitionItem item)
                continue;

            using var itemScope = context.PushMarkdownAttributes(item);
            var itemStack = new StackBox
            {
                FlowDirection = context.FlowDirection,
            };
            itemStack.BlockIndex = context.NextBlockIndex();
            context.RegisterMarkdownAttributes(item, itemStack.BlockIndex);

            foreach (var entry in item)
            {
                context.CancellationToken.ThrowIfCancellationRequested();
                BlockBox? box = entry switch
                {
                    DefinitionTerm term => BuildTerm(term, context),
                    ParagraphBlock paragraph => BuildDescriptionParagraph(paragraph, context),
                    _ => BuildDescriptionBlock(entry, context),
                };

                if (box is not null)
                    itemStack.Add(box);
            }

            stack.Add(itemStack);
        }

        return stack;
    }

    private static InlineContainerBox BuildTerm(DefinitionTerm term, MarkdownLayoutContext context)
    {
        using var attrScope = context.PushMarkdownAttributes(term);
        var box = new InlineContainerBox(context, MarkdownElementKeys.DefinitionTerm);
        box.BlockIndex = context.NextBlockIndex();
        if (term.Inline is not null)
            GfmChildBuilder.AddInlines(box, term.Inline);
        context.RegisterMarkdownAttributes(term, box.BlockIndex);
        return box;
    }

    private static InlineContainerBox BuildDescriptionParagraph(ParagraphBlock paragraph, MarkdownLayoutContext context)
    {
        using var attrScope = context.PushMarkdownAttributes(paragraph);
        var box = new InlineContainerBox(context, MarkdownElementKeys.DefinitionDescription);
        box.BlockIndex = context.NextBlockIndex();
        if (paragraph.Inline is not null)
            GfmChildBuilder.AddInlines(box, paragraph.Inline);
        context.RegisterMarkdownAttributes(paragraph, box.BlockIndex);
        return box;
    }

    private static BlockBox? BuildDescriptionBlock(Block block, MarkdownLayoutContext context)
    {
        using var styleScope = context.PushStyleContext(MarkdownElementKeys.DefinitionDescription);
        return GfmChildBuilder.TryBuildBlock(block, context);
    }
}
