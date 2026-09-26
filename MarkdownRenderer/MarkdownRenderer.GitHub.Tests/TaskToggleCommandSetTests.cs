using System.Windows.Input;
using MarkdownRenderer.Gfm.Renderers;
using MarkdownRenderer.Hosting;
using MarkdownRenderer.Layout;
using Xunit;

namespace MarkdownRenderer.GitHub.Tests;

public sealed class TaskToggleCommandSetTests
{
    [Fact]
    public void CreateReturnsNullWhenProviderHasNoTaskCommands()
    {
        var provider = new RecordingProvider(static _ => null);

        TaskToggleCommandSet? commands = TaskToggleCommandSet.Create(
            provider,
            new SourceSpan(4, 3));

        Assert.Null(commands);
        Assert.Equal(["checked", "unchecked"], provider.Targets);
    }

    [Fact]
    public void AvailabilityChangesAreObservedOnceAndDetachedOnDispose()
    {
        var command = new MutableCommand(canExecute: false);
        var provider = new RecordingProvider(_ => command);
        TaskToggleCommandSet commands = Assert.IsType<TaskToggleCommandSet>(
            TaskToggleCommandSet.Create(provider, new SourceSpan(4, 3)));
        int notifications = 0;

        IDisposable subscription = commands.Subscribe((_, _) => notifications++);
        Assert.Equal(1, command.SubscriberCount);
        Assert.False(commands.CanSetState(true));

        command.SetCanExecute(true);

        Assert.Equal(1, notifications);
        Assert.True(commands.CanSetState(true));
        Assert.True(commands.TrySetState(true));
        Assert.Equal("checked", Assert.IsType<MarkdownCommandContext>(command.LastParameter).Target);

        subscription.Dispose();
        subscription.Dispose();
        Assert.Equal(0, command.SubscriberCount);
        command.SetCanExecute(false);
        Assert.Equal(1, notifications);
    }

    [Fact]
    public void StateSpecificCommandsReceiveTheirMatchingContexts()
    {
        var check = new MutableCommand(canExecute: true);
        var uncheck = new MutableCommand(canExecute: true);
        var provider = new RecordingProvider(context =>
            context.Target == "checked" ? check : uncheck);
        TaskToggleCommandSet commands = Assert.IsType<TaskToggleCommandSet>(
            TaskToggleCommandSet.Create(provider, new SourceSpan(10, 3)));

        Assert.True(commands.TrySetState(true));
        Assert.True(commands.TrySetState(false));

        MarkdownCommandContext checkContext = Assert.IsType<MarkdownCommandContext>(check.LastParameter);
        MarkdownCommandContext uncheckContext = Assert.IsType<MarkdownCommandContext>(uncheck.LastParameter);
        Assert.Equal(MarkdownCommandKind.ToggleTask, checkContext.Kind);
        Assert.Equal(new SourceSpan(10, 3), checkContext.SourceRange);
        Assert.Equal("checked", checkContext.Target);
        Assert.Equal("unchecked", uncheckContext.Target);
    }

    [Fact]
    public void InlineFocusabilityTracksLiveCommandAvailability()
    {
        var command = new MutableCommand(canExecute: false);
        TaskToggleCommandSet commands = Assert.IsType<TaskToggleCommandSet>(
            TaskToggleCommandSet.Create(
                new RecordingProvider(_ => command),
                new SourceSpan(4, 3)));
        var metadata = new InlineEmbedAutomationMetadata(
            "Completed task",
            "Incomplete task",
            "Read-only task state",
            "Toggle task state",
            "task-1",
            isChecked: false,
            commands.CanSetState,
            commands.TrySetState);
        var embed = new InlineEmbedRun(
            20,
            20,
            static () => throw new InvalidOperationException())
        {
            AutomationMetadata = metadata,
        };

        Assert.False(LayoutSnapshot.IsInlineEmbedKeyboardFocusable(embed));

        command.SetCanExecute(true);

        Assert.True(LayoutSnapshot.IsInlineEmbedKeyboardFocusable(embed));
    }

    private sealed class RecordingProvider(
        Func<MarkdownCommandContext, ICommand?> resolver) : IMarkdownCommandProvider
    {
        internal List<string?> Targets { get; } = [];

        public ICommand? GetCommand(MarkdownCommandContext context)
        {
            Targets.Add(context.Target);
            return resolver(context);
        }
    }

    private sealed class MutableCommand(bool canExecute) : ICommand
    {
        private EventHandler? _canExecuteChanged;
        private bool _canExecute = canExecute;

        internal int SubscriberCount { get; private set; }
        internal object? LastParameter { get; private set; }

        public event EventHandler? CanExecuteChanged
        {
            add
            {
                _canExecuteChanged += value;
                SubscriberCount++;
            }
            remove
            {
                _canExecuteChanged -= value;
                SubscriberCount--;
            }
        }

        public bool CanExecute(object? parameter) => _canExecute;

        public void Execute(object? parameter) => LastParameter = parameter;

        internal void SetCanExecute(bool value)
        {
            _canExecute = value;
            _canExecuteChanged?.Invoke(this, EventArgs.Empty);
        }
    }
}
