using System;
using System.Collections.Generic;
using System.Windows.Input;
using MarkdownRenderer.Diagnostics;
using MarkdownRenderer.Hosting;

namespace MarkdownRenderer.Gfm.Renderers;

/// <summary>
/// Captures the state-specific task commands for one layout snapshot and
/// provides a bounded subscription to their availability notifications.
/// </summary>
internal sealed class TaskToggleCommandSet
{
    private readonly Endpoint _check;
    private readonly Endpoint _uncheck;

    private TaskToggleCommandSet(Endpoint check, Endpoint uncheck)
    {
        _check = check;
        _uncheck = uncheck;
    }

    internal static TaskToggleCommandSet? Create(
        IMarkdownCommandProvider provider,
        SourceSpan sourceRange)
    {
        ArgumentNullException.ThrowIfNull(provider);

        Endpoint check = Resolve(provider, CreateContext(sourceRange, isChecked: true));
        Endpoint uncheck = Resolve(provider, CreateContext(sourceRange, isChecked: false));
        return check.Command is null && uncheck.Command is null
            ? null
            : new TaskToggleCommandSet(check, uncheck);
    }

    internal bool CanSetState(bool requestedState)
    {
        Endpoint endpoint = GetEndpoint(requestedState);
        if (endpoint.Command is null)
            return false;

        try
        {
            return endpoint.Command.CanExecute(endpoint.Context);
        }
        catch (Exception ex)
        {
            MarkdownDiagnostics.WriteLine(
                $"[TaskListItemRenderer] Toggle command availability failed: {ex.Message}");
            return false;
        }
    }

    internal bool TrySetState(bool requestedState)
    {
        Endpoint endpoint = GetEndpoint(requestedState);
        if (endpoint.Command is null)
            return false;

        try
        {
            if (!endpoint.Command.CanExecute(endpoint.Context))
                return false;

            endpoint.Command.Execute(endpoint.Context);
            return true;
        }
        catch (Exception ex)
        {
            MarkdownDiagnostics.WriteLine($"[TaskListItemRenderer] Toggle command failed: {ex.Message}");
            return false;
        }
    }

    internal IDisposable Subscribe(EventHandler handler)
    {
        ArgumentNullException.ThrowIfNull(handler);

        var commands = new List<ICommand>(2);
        Subscribe(_check.Command, handler, commands);
        if (!ReferenceEquals(_uncheck.Command, _check.Command))
            Subscribe(_uncheck.Command, handler, commands);
        return new CommandSubscription(commands, handler);
    }

    private Endpoint GetEndpoint(bool requestedState) => requestedState ? _check : _uncheck;

    private static MarkdownCommandContext CreateContext(SourceSpan sourceRange, bool isChecked) =>
        new(
            MarkdownCommandKind.ToggleTask,
            sourceRange,
            target: isChecked ? "checked" : "unchecked");

    private static Endpoint Resolve(
        IMarkdownCommandProvider provider,
        MarkdownCommandContext context)
    {
        try
        {
            return new Endpoint(provider.GetCommand(context), context);
        }
        catch (Exception ex)
        {
            MarkdownDiagnostics.WriteLine(
                $"[TaskListItemRenderer] Toggle command resolution failed: {ex.Message}");
            return new Endpoint(null, context);
        }
    }

    private static void Subscribe(
        ICommand? command,
        EventHandler handler,
        List<ICommand> subscriptions)
    {
        if (command is null)
            return;

        try
        {
            command.CanExecuteChanged += handler;
            subscriptions.Add(command);
        }
        catch (Exception ex)
        {
            MarkdownDiagnostics.WriteLine(
                $"[TaskListItemRenderer] Toggle command subscription failed: {ex.Message}");
        }
    }

    private readonly record struct Endpoint(ICommand? Command, MarkdownCommandContext Context);

    private sealed class CommandSubscription(
        IReadOnlyList<ICommand> commands,
        EventHandler handler) : IDisposable
    {
        private bool _isDisposed;

        public void Dispose()
        {
            if (_isDisposed)
                return;

            _isDisposed = true;
            foreach (ICommand command in commands)
            {
                try
                {
                    command.CanExecuteChanged -= handler;
                }
                catch (Exception ex)
                {
                    MarkdownDiagnostics.WriteLine(
                        $"[TaskListItemRenderer] Toggle command unsubscription failed: {ex.Message}");
                }
            }
        }
    }
}
