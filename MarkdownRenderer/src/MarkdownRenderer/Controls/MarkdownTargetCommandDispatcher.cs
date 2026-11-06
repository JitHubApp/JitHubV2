using System;
using System.Net;
using System.Text;
using System.Windows.Input;
using MarkdownRenderer.Hosting;

namespace MarkdownRenderer.Controls;

/// <summary>Centralizes the provider/null-fallback contract for target commands.</summary>
internal static class MarkdownTargetCommandDispatcher
{
    internal static bool CanExecute(
        IMarkdownCommandProvider? provider,
        MarkdownTargetCommand target,
        out bool isProvided)
    {
        MarkdownCommandContext context = target.CreateContext();
        ICommand? command = provider?.GetCommand(context);
        isProvided = command is not null;
        return command?.CanExecute(context) ?? true;
    }

    internal static MarkdownTargetCommandDispatchResult Execute(
        IMarkdownCommandProvider? provider,
        MarkdownTargetCommand target)
        => ExecuteCore(provider, target, isCurrent: null);

    internal static MarkdownTargetCommandDispatchResult Execute(
        IMarkdownCommandProvider? provider,
        MarkdownTargetCommand target,
        Func<bool> isCurrent)
        => ExecuteCore(provider, target, isCurrent);

    private static MarkdownTargetCommandDispatchResult ExecuteCore(
        IMarkdownCommandProvider? provider,
        MarkdownTargetCommand target,
        Func<bool>? isCurrent)
    {
        MarkdownCommandContext context = target.CreateContext();
        ICommand? command = provider?.GetCommand(context);
        if (isCurrent is not null && !isCurrent())
            return MarkdownTargetCommandDispatchResult.StaleInteraction;
        if (command is null)
            return MarkdownTargetCommandDispatchResult.NotProvided;

        bool canExecute = command.CanExecute(context);
        if (isCurrent is not null && !isCurrent())
            return MarkdownTargetCommandDispatchResult.StaleInteraction;
        if (!canExecute)
            return MarkdownTargetCommandDispatchResult.Disabled;

        command.Execute(context);
        return MarkdownTargetCommandDispatchResult.Executed;
    }
}
