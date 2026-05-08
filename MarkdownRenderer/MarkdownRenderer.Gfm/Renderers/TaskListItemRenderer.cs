using Markdig.Extensions.TaskLists;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using MarkdownRenderer.Layout;
using MarkdownRenderer.Layout.Boxes;
using MarkdownRenderer.Parsing;
using MarkdownRenderer.Theming;
using Microsoft.UI.Xaml;

namespace MarkdownRenderer.Gfm.Renderers;

/// <summary>
/// Renders a <see cref="ListItemBlock"/> that carries a <see cref="TaskList"/> attribute
/// with a checkbox character marker (☑/☐). Returns <c>null</c> for plain list items so
/// the core renderer handles them unchanged.
/// </summary>
public sealed class TaskListItemRenderer : MarkdownNodeRenderer<ListItemBlock>
{
    public override BlockBox? BuildBlock(ListItemBlock listItem, MarkdownLayoutContext context)
    {
        var taskList = listItem.GetData(typeof(TaskList)) as TaskList;
        if (taskList is null) return null;

        string checkboxChar = taskList.Checked ? "\u2611 " : "\u2610 ";

        var itemBox = new StackBox
        {
            ContentPadding = new Thickness(20, 0, 0, 0),
        };

        var marker = new InlineContainerBox(context, MarkdownElementKeys.ListMarker);
        marker.BlockIndex = context.NextBlockIndex();
        marker.Add(new TextRun(checkboxChar)
        {
            ElementKey = MarkdownElementKeys.ListMarker,
            SourceSpan = new MarkdownRenderer.SourceSpan(listItem.Span.Start, 0)
        });
        itemBox.Add(marker);

        foreach (var child in listItem)
        {
            if (child is ParagraphBlock p && p.Inline is not null)
            {
                var contentBox = new InlineContainerBox(context, MarkdownElementKeys.Body);
                contentBox.BlockIndex = context.NextBlockIndex();
                GfmChildBuilder.AddInlines(contentBox, p.Inline);
                itemBox.Add(contentBox);
            }
            else
            {
                var box = GfmChildBuilder.TryBuildBlock(child, context);
                if (box is not null) itemBox.Add(box);
            }
        }

        return itemBox;
    }
}
