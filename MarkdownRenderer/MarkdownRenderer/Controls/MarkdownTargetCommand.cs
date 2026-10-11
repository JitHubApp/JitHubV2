using System;
using System.Collections.Generic;
using System.Net;
using System.Text;
using System.Windows.Input;
using MarkdownRenderer.Hosting;

namespace MarkdownRenderer.Controls;

/// <summary>
/// Immutable description of a context action. The descriptor contains only
/// stable public command data; live layout objects remain private to the
/// control that owns them.
/// </summary>
internal readonly struct MarkdownTargetCommand
{
    public MarkdownTargetCommand(
        MarkdownCommandKind Kind,
        SourceSpan SourceRange,
        string? Target = null,
        string? Language = null)
    {
        this.Kind = Kind;
        this.SourceRange = SourceRange;
        this.Target = Target;
        this.Language = Language;
    }

    public readonly MarkdownCommandKind Kind;

    public readonly SourceSpan SourceRange;

    public readonly string? Target;

    public readonly string? Language;

    internal MarkdownCommandContext CreateContext()
        => new(Kind, SourceRange, Target, Language);
}

internal enum MarkdownTargetCommandDispatchResult
{
    NotProvided,
    Disabled,
    Executed,
    StaleInteraction,
}

internal enum MarkdownLinkActivationPolicyResult
{
    InternalTarget,
    DisabledCommand,
    ExecutedCommand,
    RaiseHostEvent,
    StaleInteraction,
}

internal readonly struct MarkdownLinkActivationDecision
{
    public MarkdownLinkActivationDecision(
        MarkdownLinkInputKind InputKind,
        MarkdownLinkActivationPolicyResult Result)
    {
        this.InputKind = InputKind;
        this.Result = Result;
    }

    public readonly MarkdownLinkInputKind InputKind;

    public readonly MarkdownLinkActivationPolicyResult Result;

    internal bool IsHandled => Result != MarkdownLinkActivationPolicyResult.RaiseHostEvent;
}

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

/// <summary>
/// Centralizes modality-neutral link routing after disclosure handling. The
/// caller performs any fragment scroll and supplies whether it succeeded; all
/// modalities then share the same action, disabled-command, and fallback rules.
/// </summary>
internal static class MarkdownLinkActivationPolicy
{
    internal static MarkdownLinkActivationDecision Evaluate(
        MarkdownLinkInputKind inputKind,
        bool internalTargetHandled,
        IMarkdownCommandProvider? provider,
        SourceSpan sourceRange,
        string url,
        string? action)
        => EvaluateCore(
            inputKind,
            internalTargetHandled,
            provider,
            sourceRange,
            url,
            action,
            isCurrent: null);

    internal static MarkdownLinkActivationDecision Evaluate(
        MarkdownLinkInputKind inputKind,
        bool internalTargetHandled,
        IMarkdownCommandProvider? provider,
        SourceSpan sourceRange,
        string url,
        string? action,
        Func<bool> isCurrent)
        => EvaluateCore(
            inputKind,
            internalTargetHandled,
            provider,
            sourceRange,
            url,
            action,
            isCurrent);

    private static MarkdownLinkActivationDecision EvaluateCore(
        MarkdownLinkInputKind inputKind,
        bool internalTargetHandled,
        IMarkdownCommandProvider? provider,
        SourceSpan sourceRange,
        string url,
        string? action,
        Func<bool>? isCurrent)
    {
        if (isCurrent is not null && !isCurrent())
        {
            return new MarkdownLinkActivationDecision(
                inputKind,
                MarkdownLinkActivationPolicyResult.StaleInteraction);
        }

        if (internalTargetHandled)
        {
            return new MarkdownLinkActivationDecision(
                inputKind,
                MarkdownLinkActivationPolicyResult.InternalTarget);
        }

        string commandTarget = string.IsNullOrWhiteSpace(action) ? url : action;
        MarkdownTargetCommand target = new(
            MarkdownCommandKind.Activate,
            sourceRange,
            commandTarget);
        MarkdownTargetCommandDispatchResult dispatch = isCurrent is null
            ? MarkdownTargetCommandDispatcher.Execute(provider, target)
            : MarkdownTargetCommandDispatcher.Execute(provider, target, isCurrent);
        if (isCurrent is not null && !isCurrent())
        {
            return new MarkdownLinkActivationDecision(
                inputKind,
                MarkdownLinkActivationPolicyResult.StaleInteraction);
        }

        MarkdownLinkActivationPolicyResult result = dispatch switch
        {
            MarkdownTargetCommandDispatchResult.Executed =>
                MarkdownLinkActivationPolicyResult.ExecutedCommand,
            MarkdownTargetCommandDispatchResult.Disabled =>
                MarkdownLinkActivationPolicyResult.DisabledCommand,
            MarkdownTargetCommandDispatchResult.StaleInteraction =>
                MarkdownLinkActivationPolicyResult.StaleInteraction,
            _ => MarkdownLinkActivationPolicyResult.RaiseHostEvent,
        };
        return new MarkdownLinkActivationDecision(inputKind, result);
    }

    internal static MarkdownLinkActivationDecision CreateHostFallback(
        MarkdownLinkInputKind inputKind) =>
        new(inputKind, MarkdownLinkActivationPolicyResult.RaiseHostEvent);
}

internal readonly struct MarkdownTableClipboardPayload
{
    public MarkdownTableClipboardPayload(string Text, string Html)
    {
        this.Text = Text;
        this.Html = Html;
    }

    public readonly string Text;

    public readonly string Html;
}

/// <summary>Builds a conservative rendered table payload for clipboard fallbacks.</summary>
internal static class MarkdownTableClipboardFormatter
{
    internal static MarkdownTableClipboardPayload Format(
        string[][] rows,
        int headerRowCount)
    {
        var text = new StringBuilder();
        var html = new StringBuilder("<table>");
        if (rows.Length == 0)
            return new MarkdownTableClipboardPayload(string.Empty, "<table></table>");

        int boundedHeaderRows = System.Math.Clamp(headerRowCount, 0, rows.Length);

        for (int rowIndex = 0; rowIndex < rows.Length; rowIndex++)
        {
            if (rowIndex == 0 && boundedHeaderRows > 0)
                html.Append("<thead>");
            if (rowIndex == boundedHeaderRows && boundedHeaderRows > 0)
                html.Append("</thead><tbody>");
            else if (rowIndex == 0 && boundedHeaderRows == 0)
                html.Append("<tbody>");

            if (rowIndex > 0)
                text.AppendLine();
            html.Append("<tr>");

            string[] row = rows[rowIndex];
            for (int columnIndex = 0; columnIndex < row.Length; columnIndex++)
            {
                if (columnIndex > 0)
                    text.Append('\t');

                string value = NormalizeCellText(row[columnIndex]);
                text.Append(value);
                string tag = rowIndex < boundedHeaderRows ? "th" : "td";
                html.Append('<').Append(tag).Append('>')
                    .Append(WebUtility.HtmlEncode(value))
                    .Append("</").Append(tag).Append('>');
            }

            html.Append("</tr>");
        }

        if (boundedHeaderRows > 0 && boundedHeaderRows == rows.Length)
            html.Append("</thead>");
        else
            html.Append("</tbody>");
        html.Append("</table>");
        return new MarkdownTableClipboardPayload(text.ToString(), html.ToString());
    }

    private static string NormalizeCellText(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return string.Empty;

        return value
            .Replace("\r\n", " ", System.StringComparison.Ordinal)
            .Replace('\r', ' ')
            .Replace('\n', ' ')
            .Replace('\t', ' ');
    }
}
