using System;
using System.Net;
using System.Text;
using System.Windows.Input;
using MarkdownRenderer.Hosting;

namespace MarkdownRenderer.Controls;

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
