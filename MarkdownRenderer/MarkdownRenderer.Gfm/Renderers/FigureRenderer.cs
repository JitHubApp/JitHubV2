using Markdig.Extensions.Figures;
using Markdig.Syntax;
using MarkdownRenderer.Layout;
using MarkdownRenderer.Layout.Boxes;
using MarkdownRenderer.Parsing;
using MarkdownRenderer.Theming;

namespace MarkdownRenderer.Gfm.Renderers;

/// <summary>
/// Renders Markdig figure containers and captions using native layout boxes.
/// </summary>
public sealed class FigureRenderer : MarkdownNodeRenderer<Figure>
{
    /// <inheritdoc />
    public override BlockBox? BuildBlock(Figure figure, MarkdownLayoutContext context)
    {
        var style = context.ThemeSnapshot.GetStyle(
            MarkdownElementKeys.Figure,
            context.CreateStyleContextSnapshot(),
            context.CreateStyleAliasSnapshot());

        var stack = new StackBox
        {
            FlowDirection = context.FlowDirection,
            Margin = style.Margin,
            ContentPadding = style.Padding,
            Background = style.Background,
            BorderBrush = style.BorderBrush,
            BorderThickness = style.BorderThickness,
            CornerRadius = style.CornerRadius,
        };
        stack.BlockIndex = context.NextBlockIndex();

        using var figureScope = context.PushStyleContext(MarkdownElementKeys.Figure);
        foreach (var child in figure)
        {
            context.CancellationToken.ThrowIfCancellationRequested();
            BlockBox? box = child switch
            {
                FigureCaption caption => BuildCaption(caption, context),
                _ => GfmChildBuilder.TryBuildBlock(child, context),
            };

            if (box is not null)
                stack.Add(box);
        }

        return stack;
    }

    private static InlineContainerBox BuildCaption(FigureCaption caption, MarkdownLayoutContext context)
    {
        using var attrScope = context.PushMarkdownAttributes(caption);
        var box = new InlineContainerBox(context, MarkdownElementKeys.FigureCaption);
        box.BlockIndex = context.NextBlockIndex();
        if (caption.Inline is not null)
            GfmChildBuilder.AddInlines(box, caption.Inline);
        context.RegisterMarkdownAttributes(caption, box.BlockIndex);
        return box;
    }
}
