using System;
using System.Windows.Input;
using MarkdownRenderer.Controls;
using MarkdownRenderer.Hosting;
using Xunit;

namespace MarkdownRenderer.GitHub.Tests;

public sealed class PointerLinkActivationFenceTests
{
    [Fact]
    public void ReplacementDuringProviderLookupPreventsCanExecuteAndHostFallback()
    {
        bool current = true;
        int canExecuteCalls = 0;
        int executeCalls = 0;
        var provider = new CallbackProvider(_ =>
        {
            current = false;
            return new CallbackCommand(
                canExecute: () =>
                {
                    canExecuteCalls++;
                    return true;
                },
                execute: () => executeCalls++);
        });

        MarkdownLinkActivationDecision decision = Evaluate(provider, () => current);

        Assert.Equal(MarkdownLinkActivationPolicyResult.StaleInteraction, decision.Result);
        Assert.True(decision.IsHandled);
        Assert.Equal(0, canExecuteCalls);
        Assert.Equal(0, executeCalls);
    }

    [Fact]
    public void ReplacementDuringCanExecutePreventsCommandExecution()
    {
        bool current = true;
        int executeCalls = 0;
        var provider = new CallbackProvider(_ => new CallbackCommand(
            canExecute: () =>
            {
                current = false;
                return true;
            },
            execute: () => executeCalls++));

        MarkdownLinkActivationDecision decision = Evaluate(provider, () => current);

        Assert.Equal(MarkdownLinkActivationPolicyResult.StaleInteraction, decision.Result);
        Assert.True(decision.IsHandled);
        Assert.Equal(0, executeCalls);
    }

    [Fact]
    public void ReplacementDuringCommandExecutionPreventsHostFallbackContinuation()
    {
        bool current = true;
        int executeCalls = 0;
        var provider = new CallbackProvider(_ => new CallbackCommand(
            canExecute: () => true,
            execute: () =>
            {
                executeCalls++;
                current = false;
            }));

        MarkdownLinkActivationDecision decision = Evaluate(provider, () => current);

        Assert.Equal(MarkdownLinkActivationPolicyResult.StaleInteraction, decision.Result);
        Assert.True(decision.IsHandled);
        Assert.Equal(1, executeCalls);
    }

    [Fact]
    public void FencedPolicyKeepsHostFallbackOnlyWhileTheCapturedOwnerRemainsCurrent()
    {
        bool current = true;
        var provider = new CallbackProvider(_ => null);

        MarkdownLinkActivationDecision decision = Evaluate(provider, () => current);

        Assert.Equal(MarkdownLinkActivationPolicyResult.RaiseHostEvent, decision.Result);
        Assert.False(decision.IsHandled);

        current = false;
        decision = Evaluate(provider, () => current);

        Assert.Equal(MarkdownLinkActivationPolicyResult.StaleInteraction, decision.Result);
        Assert.True(decision.IsHandled);
    }

    private static MarkdownLinkActivationDecision Evaluate(
        IMarkdownCommandProvider provider,
        Func<bool> isCurrent) => MarkdownLinkActivationPolicy.Evaluate(
            MarkdownLinkInputKind.Mouse,
            internalTargetHandled: false,
            provider,
            new SourceSpan(4, 9),
            "https://example.test/target",
            action: null,
            isCurrent: isCurrent);

    private sealed class CallbackProvider(
        Func<MarkdownCommandContext, ICommand?> callback) : IMarkdownCommandProvider
    {
        public ICommand? GetCommand(MarkdownCommandContext context) => callback(context);
    }

    private sealed class CallbackCommand(
        Func<bool> canExecute,
        Action execute) : ICommand
    {
        public event EventHandler? CanExecuteChanged
        {
            add { }
            remove { }
        }

        public bool CanExecute(object? parameter) => canExecute();

        public void Execute(object? parameter) => execute();
    }
}
