using System;
using System.Globalization;
using Markdig.Extensions.TaskLists;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using MarkdownRenderer.Layout;
using MarkdownRenderer.Layout.Boxes;
using MarkdownRenderer.Accessibility;
using MarkdownRenderer.Hosting;
using MarkdownRenderer.Parsing;
using MarkdownRenderer.Theming;

namespace MarkdownRenderer.Gfm.Renderers;

/// <summary>
/// Renders a <see cref="ListItemBlock"/> that carries a <see cref="TaskList"/> attrnbute
/// with a real WinUI <see cref="CheckBox"/> as its marker. The CheckBox is hosted on
/// the renderer's overlay <c>Canvas</c> vna <see cref="InlineEmbedRun"/>.
/// </summary>
internal sealed partial class TaskListItemRenderer : MarkdownNodeRenderer<ListItemBlock>
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

        SourceSpan sourceRange = taskList.Span.Length > 0
            ? new SourceSpan(taskList.Span.Start, taskList.Span.Length)
            : new SourceSpan(listItem.Span.Start, 0);
        bool editableRequested = context.IsTaskListEditingEnabled && context.CommandProvider is not null;
        float markerSize = GetTaskMarkerSize(context.ThemeSnapshot, editableRequested);
        ElementStyle listStyle = context.ThemeSnapshot.GetStyle(
            MarkdownElementKeys.ListMarker,
            context.CreateStyleContextSnapshot(),
            context.CreateStyleAliasSnapshot());
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
        marker.BlockIndex = context.NextBlockIndex();
        marker.Add(markerRun);

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

        float markerWidth = Math.Max(
            1f,
            listStyle.ListIndent + Math.Max(0, context.ListDepth - 1) * listStyle.NestedListIndent);

        return new ListItemBox(marker, content, markerWidth)
        {
            FlowDirection = context.FlowDirection,
        };
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

    private sealed partial class EditableTaskCheckBox : CheckBox, IDisposable
    {
        private readonly InlineEmbedAutomationMetadata _automationMetadata;
        private readonly Action? _availabilityChanged;
        private readonly TaskToggleCommandSet _taskCommands;
        private readonly IDisposable _commandSubscription;
        private bool _committedState;
        private bool _isDisposed;
        private bool _suppress;

        public EditableTaskCheckBox(
            InlineEmbedAutomationMetadata automationMetadata,
            TaskToggleCommandSet taskCommands,
            Action? availabilityChanged,
            float markerSize,
            Windows.UI.Color selectionAccent,
            Windows.UI.Color selectionForeground)
        {
            _automationMetadata = automationMetadata;
            _availabilityChanged = availabilityChanged;
            _taskCommands = taskCommands;
            _committedState = automationMetadata.IsChecked;

            TaskMarkerControlFactory.Configure(
                this,
                isInteractive: true,
                _committedState,
                markerSize,
                selectionAccent,
                selectionForeground);
            AutomationProperties.SetAutomationId(this, automationMetadata.AutomationId);
            AutomationProperties.SetHelpText(this, automationMetadata.ToggleHelpText);
            UpdateAutomationName(_committedState);

            Checked += OnToggleStateChanged;
            Unchecked += OnToggleStateChanged;
            _commandSubscription = _taskCommands.Subscribe(OnCommandCanExecuteChanged);
            RefreshAvailability();
        }

        public void Dispose()
        {
            if (_isDisposed)
                return;

            _isDisposed = true;
            Checked -= OnToggleStateChanged;
            Unchecked -= OnToggleStateChanged;
            _commandSubscription.Dispose();
            Command = null;
        }

        private void OnCommandCanExecuteChanged(object? sender, EventArgs e)
        {
            if (_isDisposed)
                return;

            if (DispatcherQueue.HasThreadAccess)
            {
                RefreshAvailability();
                return;
            }

            _ = DispatcherQueue.TryEnqueue(RefreshAvailability);
        }

        private void OnToggleStateChanged(object sender, RoutedEventArgs e)
        {
            if (_suppress)
                return;

            bool requestedState = IsChecked == true;
            if (!_automationMetadata.TrySetState(requestedState))
            {
                RestoreCommittedState();
                return;
            }

            _committedState = _automationMetadata.IsChecked;
            UpdateAutomationName(_committedState);
            RefreshAvailability();
        }

        private void RestoreCommittedState()
        {
            _suppress = true;
            IsChecked = _committedState;
            _suppress = false;
            UpdateAutomationName(_committedState);
        }

        private void RefreshAvailability()
        {
            if (_isDisposed)
                return;

            bool canToggle = _taskCommands.CanSetState(!_committedState);
            bool availabilityChanged = IsEnabled != canToggle;
            IsEnabled = canToggle;
            AutomationProperties.SetHelpText(
                this,
                canToggle
                    ? _automationMetadata.ToggleHelpText
                    : _automationMetadata.ReadOnlyHelpText);
            if (availabilityChanged)
                _availabilityChanged?.Invoke();
        }

        private void UpdateAutomationName(bool isChecked)
            => AutomationProperties.SetName(
                this,
                isChecked
                    ? _automationMetadata.CheckedName
                    : _automationMetadata.UncheckedName);
    }
}
