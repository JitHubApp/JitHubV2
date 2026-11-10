using System;
using Markdig.Extensions.TaskLists;
using Markdig.Syntax;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using MarkdownRenderer.Layout;
using MarkdownRenderer.Layout.Boxes;
using MarkdownRenderer.Accessibility;
using MarkdownRenderer.Hosting;
using MarkdownRenderer.Theming;
using MarkdownRenderer.Hosting.Internal;
using MarkdownRenderer.Parsing.Internal;

namespace MarkdownRenderer.Gfm.Internal.Renderers;

/// <summary>
/// Renders a <see cref="ListItemBlock"/> that carries a <see cref="TaskList"/> attribute
/// with a real WinUI <see cref="CheckBox"/> as its marker. The CheckBox is hosted on
/// the renderer's overlay <c>Canvas</c> via <see cref="InlineEmbedRun"/>.
/// </summary>
internal sealed class TaskListItemRenderer : MarkdownNodeRenderer<ListItemBlock>
{
    /// <inheritdoc />
    public override BlockBox? BuildBlock(ListItemBlock listItem, MarkdownLayoutContext context)
    {
        TaskList? taskList = FindTaskList(listItem);
        if (taskList is null) return null;

        bool isChecked = taskList.Checked;

        SourceSpan sourceRange = taskList.Span.Length > 0
            ? new SourceSpan(taskList.Span.Start, taskList.Span.Length)
            : new SourceSpan(listItem.Span.Start, 0);
        bool editableRequested = context.IsTaskListEditingEnabled && context.CommandProvider is not null;
        float markerSize = GetTaskMarkerSize(context.ThemeSnapshot, editableRequested);
        ElementStyle listStyle = context.ThemeSnapshot.GetStyle(
            MarkdownElementKeys.ListMarker,
            context.CreateStyleContextSnapshot(),
            context.CreateStyleAliasSnapshot());
        InlineContainerBox marker = BuildMarker(context, sourceRange, isChecked, editableRequested, markerSize);
        StackBox content = BuildContent(listItem, context);

        float markerWidth = Math.Max(
            1f,
            listStyle.ListIndent + Math.Max(0, context.Styles.ListDepth - 1) * listStyle.NestedListIndent);

        var result = new ListItemBox(marker, content, markerWidth)
        {
            FlowDirection = context.FlowDirection,
        };
        context.RegisterConstructionBlock(result);
        context.AdoptConstructionBlock(marker);
        context.AdoptConstructionBlock(content);
        return result;
    }

    /// <summary>
    /// Finds the task marker. GFM TaskList is a LeafInline injected as the
    /// first inline of the first ParagraphBlock child, not data on the
    /// ListItemBlock itself.
    /// </summary>
    private static TaskList? FindTaskList(ListItemBlock listItem)
    {
        foreach (var child in listItem)
        {
            // Only the first inline can be TaskList.
            if (child is ParagraphBlock pb && pb.Inline is not null)
                return pb.Inline.FirstChild as TaskList;
        }
        return null;
    }

    /// <summary>
    /// Builds the marker box that hosts the task's check box, editable when
    /// <paramref name="editableRequested"/> and a command set is available.
    /// </summary>
    private static InlineContainerBox BuildMarker(
        MarkdownLayoutContext context,
        SourceSpan sourceRange,
        bool isChecked,
        bool editableRequested,
        float markerSize)
    {
        (Windows.UI.Color selectionAccent, Windows.UI.Color selectionForeground) =
            GetTaskMarkerColors(context.ThemeSnapshot);
        string completed = ResolveString(
            context,
            MarkdownStringKeys.TaskCompleted,
            MarkdownLocalizedStrings.TaskCompleted);
        string incomplete = ResolveString(
            context,
            MarkdownStringKeys.TaskIncomplete,
            MarkdownLocalizedStrings.TaskIncomplete);
        string readOnlyHelp = ResolveString(
            context,
            MarkdownStringKeys.TaskReadOnly,
            MarkdownLocalizedStrings.TaskReadOnly);
        string toggleHelp = ResolveString(
            context,
            MarkdownStringKeys.TaskToggle,
            MarkdownLocalizedStrings.TaskToggle);
        TaskToggleCommandSet? taskCommands = null;
        if (editableRequested && context.CommandProvider is { } taskCommandProvider)
        {
            taskCommands = TaskToggleCommandSet.Create(taskCommandProvider, sourceRange);
        }
        Func<bool, bool>? canSetState = taskCommands is null ? null : taskCommands.CanSetState;
        Func<bool, bool>? trySetState = taskCommands is null ? null : taskCommands.TrySetState;
        var automationMetadata = new InlineEmbedAutomationMetadata(
            completed,
            incomplete,
            readOnlyHelp,
            toggleHelp,
            CreateTaskMarkerAutomationId(sourceRange),
            isChecked,
            canSetState,
            trySetState);
        var markerRun = new InlineEmbedRun(
            markerSize,
            markerSize,
            () => CreateTaskMarker(
                automationMetadata,
                taskCommands,
                context.TaskCommandAvailabilityChanged,
                markerSize,
                selectionAccent,
                selectionForeground))
        {
            ElementKey = MarkdownElementKeys.ListMarker,
            SourceSpan = sourceRange,
            AutomationMetadata = automationMetadata,
            Recycle = static element =>
            {
                if (element is EditableTaskCheckBox editable)
                    editable.Dispose();
            },
        };
        var marker = new InlineContainerBox(context, MarkdownElementKeys.ListMarker)
        {
            StyleState = isChecked ? "checked" : "unchecked",
        };
        context.RegisterConstructionBlock(marker);
        marker.BlockIndex = context.NextBlockIndex();
        marker.Add(markerRun);
        return marker;
    }

    /// <summary>
    /// Builds the task's content stack: its paragraphs without the task
    /// marker inline, then its other children.
    /// </summary>
    private static StackBox BuildContent(ListItemBlock listItem, MarkdownLayoutContext context)
    {
        var content = new StackBox
        {
            FlowDirection = context.FlowDirection,
        };
        context.RegisterConstructionBlock(content);
        content.BlockIndex = context.NextBlockIndex();

        bool isTightList = listItem.Parent is ListBlock { IsLoose: false };
        foreach (var child in listItem)
        {
            if (child is ParagraphBlock p && p.Inline is not null)
            {
                var contentBox = new InlineContainerBox(context, MarkdownElementKeys.Body);
                context.RegisterConstructionBlock(contentBox);
                contentBox.BlockIndex = context.NextBlockIndex();
                if (isTightList)
                {
                    // Tight list paragraphs have no separate paragraph box in
                    // GFM. Keep their horizontal styling, but match ordinary
                    // list items by removing only the vertical paragraph gap.
                    Thickness margin = contentBox.Margin;
                    contentBox.Margin = new Thickness(margin.Left, 0, margin.Right, 0);
                    contentBox.UseContainerMargin = false;
                }
                // Skip the TaskList inline — it's the marker, not body content.
                GfmChildBuilder.AddInlines(contentBox, p.Inline, skipFirstIf: i => i is TaskList);
                content.Add(contentBox);
                context.AdoptConstructionBlock(contentBox);
            }
            else
            {
                var box = GfmChildBuilder.TryBuildBlock(child, context);
                if (box is not null)
                {
                    content.Add(box);
                    context.AdoptConstructionBlock(box);
                }
            }
        }
        return content;
    }

    private static FrameworkElement CreateTaskMarker(
        InlineEmbedAutomationMetadata automationMetadata,
        TaskToggleCommandSet? taskCommands,
        Action? availabilityChanged,
        float markerSize,
        Windows.UI.Color selectionAccent,
        Windows.UI.Color selectionForeground)
    {
        if (taskCommands is not null)
        {
            return new EditableTaskCheckBox(
                automationMetadata,
                taskCommands,
                availabilityChanged,
                markerSize,
                selectionAccent,
                selectionForeground);
        }

        return TaskMarkerControlFactory.CreateReadOnly(
            automationMetadata,
            markerSize,
            selectionAccent,
            selectionForeground);
    }

    internal static float GetTaskMarkerSize(ThemeSnapshot snapshot, bool editableRequested)
    {
        return TaskMarkerControlFactory.GetMarkerSize(snapshot, editableRequested);
    }

    internal static (Windows.UI.Color Accent, Windows.UI.Color Foreground) GetTaskMarkerColors(
        ThemeSnapshot snapshot)
        => TaskMarkerControlFactory.GetColors(snapshot);

    private static MarkdownCommandContext CreateCommandContext(SourceSpan sourceRange, bool isChecked)
        => new(
            MarkdownCommandKind.ToggleTask,
            sourceRange,
            target: isChecked ? "checked" : "unchecked");

    internal static string CreateTaskMarkerAutomationId(SourceSpan sourceRange) =>
        TaskMarkerControlFactory.CreateAutomationId(sourceRange);

    private static string ResolveString(
        MarkdownLayoutContext context,
        string key,
        string fallback) => context.ResolveString(key, fallback);
}
