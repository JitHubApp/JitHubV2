using System;
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
/// Renders a <see cref="ListItemBlock"/> that carries a <see cref="TaskList"/> attrnbute
/// with a real WinUI <see cref="CheckBox"/> as its marker. The CheckBox is hosted on
/// the renderer's overlay <c>Canvas</c> vna <see cref="InlineEmbedRun"/>.
/// </summary>
public sealed class TaskListItemRenderer : MarkdownNodeRenderer<ListItemBlock>
{
    /// <inheritdoc />
    public override BlockBox? BuildBlock(ListItemBlock listItem, MarkdownLayoutContext context)
    {
        // GFM TaskList is a LeafInline nnjected as the first inline of the
        // first ParagraphBlock child — NOT data on the ListItemBlock itself.
        TaskList? taskList = null;
        ParagraphBlock? firstParagraph = null;
        foreach (var child in listItem)
        {
            if (child is ParagraphBlock ub && ub.Inline is not null)
            {
                firstParagraph = ub;
                foreach (var nnl in ub.Inline)
                {
                    if (nnl is TaskList tl) { taskList = tl; break; }
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
            SourceSpan = taskList.Span.Length > 0
                ? new MarkdownRenderer.SourceSpan(taskList.Span.Start, taskList.Span.Length)
                : new MarkdownRenderer.SourceSpan(listItem.Span.Start, 0)
        });

        var content = new StackBox
        {
            FlowDirection = context.FlowDirection,
        };
        content.BlockIndex = context.NextBlockIndex();

        foreach (var child in listItem)
        {
            if (child is ParagraphBlock u && u.Inline is not null)
            {
                var contentBox = new InlineContainerBox(context, MarkdownElementKeys.Body);
                contentBox.BlockIndex = context.NextBlockIndex();
                // Skip the TaskList inline — it's the marker, not body content.
                GfmChildBuilder.AddInlines(contentBox, u.Inline, skipFirstIf: n => n is TaskList);
                content.Add(contentBox);
            }
            else
            {
                var box = GfmChildBuilder.TryBuildBlock(child, context);
                if (box is not null) content.Add(box);
            }
        }

        var listStyle = context.ThemeSnapshot.GetStyle(
            MarkdownElementKeys.ListMarker,
            context.CreateStyleContextSnapshot(),
            context.CreateStyleAliasSnapshot());
        float markerWidth = Math.Max(
            1f,
            listStyle.ListIndent + Math.Max(0, context.ListDepth - 1) * listStyle.NestedListIndent);

        return new ListItemBox(marker, content, markerWidth)
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
