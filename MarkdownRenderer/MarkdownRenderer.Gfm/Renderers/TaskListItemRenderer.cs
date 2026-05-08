using Markdig.Extensions.TaskLists;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using MarkdownRenderer.Layout;
using MarkdownRenderer.Layout.Boxes;
using MarkdownRenderer.Parsing;
using MarkdownRenderer.Theming;

namespace MarkdownRenderer.Gfm.Renderers;

/// <summary>
/// Renders a <see cref="ListItemBlock"/> that carries a <see cref="TaskList"/> attribute
/// with a real WinUI <see cref="CheckBox"/> as its marker. The CheckBox is hosted on
/// the renderer's overlay <c>Canvas</c> via <see cref="InlineEmbedRun"/>.
/// </summary>
public sealed class TaskListItemRenderer : MarkdownNodeRenderer<ListItemBlock>
{
    public override BlockBox? BuildBlock(ListItemBlock listItem, MarkdownLayoutContext context)
    {
        var taskList = listItem.GetData(typeof(TaskList)) as TaskList;
        if (taskList is null) return null;

        bool isChecked = taskList.Checked;

        var marker = new InlineContainerBox(context, MarkdownElementKeys.ListMarker);
        marker.BlockIndex = context.NextBlockIndex();
        marker.Add(new InlineEmbedRun(20f, 20f, () => new CheckBox
        {
            IsChecked = isChecked,
            IsEnabled = false,
            MinWidth = 20,
            MinHeight = 20,
            Padding = new Thickness(0),
            Margin = new Thickness(0),
            // CheckBox has a glyph + content area; suppress content insets.
            HorizontalContentAlignment = HorizontalAlignment.Left,
            VerticalContentAlignment = VerticalAlignment.Center,
        })
        {
            ElementKey = MarkdownElementKeys.ListMarker,
            SourceSpan = new MarkdownRenderer.SourceSpan(listItem.Span.Start, 0)
        });

        var content = new StackBox();
        content.BlockIndex = context.NextBlockIndex();

        foreach (var child in listItem)
        {
            if (child is ParagraphBlock p && p.Inline is not null)
            {
                var contentBox = new InlineContainerBox(context, MarkdownElementKeys.Body);
                contentBox.BlockIndex = context.NextBlockIndex();
                GfmChildBuilder.AddInlines(contentBox, p.Inline);
                content.Add(contentBox);
            }
            else
            {
                var box = GfmChildBuilder.TryBuildBlock(child, context);
                if (box is not null) content.Add(box);
            }
        }

        return new ListItemBox(marker, content, markerWidth: 28f);
    }
}
