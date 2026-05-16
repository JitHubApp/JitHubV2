using Markdig.Extensions.TaskLists;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
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
        // GFM TaskList is a LeafInline injected as the first inline of the
        // first ParagraphBlock child — NOT data on the ListItemBlock itself.
        TaskList? taskList = null;
        ParagraphBlock? firstParagraph = null;
        foreach (var child in listItem)
        {
            if (child is ParagraphBlock pb && pb.Inline is not null)
            {
                firstParagraph = pb;
                foreach (var inl in pb.Inline)
                {
                    if (inl is TaskList tl) { taskList = tl; break; }
                    break; // only the first inline can be TaskList
                }
                break;
            }
        }
        if (taskList is null || firstParagraph is null) return null;

        bool isChecked = taskList.Checked;

        var marker = new InlineContainerBox(context, MarkdownElementKeys.ListMarker);
        marker.BlockIndex = context.NextBlockIndex();
        marker.Add(new InlineEmbedRun(20f, 20f, () => CreateTaskCheckBox(isChecked))
        {
            ElementKey = MarkdownElementKeys.ListMarker,
            SourceSpan = new MarkdownRenderer.SourceSpan(listItem.Span.Start, 0)
        });

        var content = new StackBox
        {
            FlowDirection = context.FlowDirection,
        };
        content.BlockIndex = context.NextBlockIndex();

        foreach (var child in listItem)
        {
            if (child is ParagraphBlock p && p.Inline is not null)
            {
                var contentBox = new InlineContainerBox(context, MarkdownElementKeys.Body);
                contentBox.BlockIndex = context.NextBlockIndex();
                // Skip the TaskList inline — it's the marker, not body content.
                GfmChildBuilder.AddInlines(contentBox, p.Inline, skipFirstIf: i => i is TaskList);
                content.Add(contentBox);
            }
            else
            {
                var box = GfmChildBuilder.TryBuildBlock(child, context);
                if (box is not null) content.Add(box);
            }
        }

        return new ListItemBox(marker, content, markerWidth: 28f)
        {
            FlowDirection = context.FlowDirection,
        };
    }

    private static CheckBox CreateTaskCheckBox(bool isChecked)
    {
        var checkBox = new CheckBox
        {
            IsChecked = isChecked,
            IsEnabled = true,
            IsTabStop = true,
            MinWidth = 20,
            MinHeight = 20,
            Padding = new Thickness(0),
            Margin = new Thickness(0),
            HorizontalContentAlignment = HorizontalAlignment.Left,
            VerticalContentAlignment = VerticalAlignment.Center,
        };

        AutomationProperties.SetName(checkBox, isChecked ? "Completed task" : "Incomplete task");
        AutomationProperties.SetHelpText(checkBox, "Read-only task checkbox");

        void RestoreCheckedState(object sender, RoutedEventArgs e)
        {
            if (checkBox.IsChecked != isChecked)
                checkBox.IsChecked = isChecked;
        }

        checkBox.Checked += RestoreCheckedState;
        checkBox.Unchecked += RestoreCheckedState;
        return checkBox;
    }
}
