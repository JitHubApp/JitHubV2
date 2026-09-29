using System.Windows.Input;
using MarkdownRenderer.Controls;
using MarkdownRenderer.Hosting;
using Xunit;

namespace MarkdownRenderer.GitHub.Tests;

public sealed class MarkdownInputAndContextCommandTests
{
    [Fact]
    public void PointerPolicy_DefersTouchButStartsMouseAndPenSelection()
    {
        Assert.False(MarkdownPointerGesturePolicy.BeginsImmediateSelection(
            MarkdownPointerModality.Touch));
        Assert.True(MarkdownPointerGesturePolicy.BeginsImmediateSelection(
            MarkdownPointerModality.Mouse));
        Assert.True(MarkdownPointerGesturePolicy.BeginsImmediateSelection(
            MarkdownPointerModality.Pen));

        Assert.True(MarkdownPointerGesturePolicy.IsPrimarySelectionPress(
            MarkdownPointerModality.Pen,
            isInContact: true,
            isLeftButtonPressed: false,
            isBarrelButtonPressed: false));
        Assert.False(MarkdownPointerGesturePolicy.IsPrimarySelectionPress(
            MarkdownPointerModality.Pen,
            isInContact: true,
            isLeftButtonPressed: false,
            isBarrelButtonPressed: true));
    }

    [Theory]
    [InlineData(4, 4, 0)]
    [InlineData(4, 12, 1)]
    [InlineData(20, 4, 2)]
    [InlineData(-20, 4, 2)]
    public void PointerPolicy_ArbitratesBlockPanWithoutStealingAncestorScroll(
        double deltaX,
        double deltaY,
        int expectedValue)
    {
        Assert.Equal(
            (MarkdownPanGestureDecision)expectedValue,
            MarkdownPointerGesturePolicy.ClassifyHorizontalPan(deltaX, deltaY));
    }

    [Fact]
    public void TargetCommandDispatcher_PassesStableContextToProviderAndCommand()
    {
        var command = new RecordingCommand(canExecute: true);
        var provider = new RecordingProvider(command);
        var target = new MarkdownTargetCommand(
            MarkdownCommandKind.CopyCode,
            new SourceSpan(7, 13),
            Target: null,
            Language: "csharp");

        Assert.True(MarkdownTargetCommandDispatcher.CanExecute(
            provider,
            target,
            out bool provided));
        Assert.True(provided);

        MarkdownTargetCommandDispatchResult result =
            MarkdownTargetCommandDispatcher.Execute(provider, target);

        Assert.Equal(MarkdownTargetCommandDispatchResult.Executed, result);
        Assert.Equal(2, provider.Contexts.Count);
        Assert.All(provider.Contexts, context =>
        {
            Assert.Equal(MarkdownCommandKind.CopyCode, context.Kind);
            Assert.Equal(new SourceSpan(7, 13), context.SourceRange);
            Assert.Equal("csharp", context.Language);
            Assert.Null(context.Target);
        });
        Assert.Same(provider.Contexts[0], command.CanExecuteContexts[0]);
        Assert.Same(provider.Contexts[1], command.CanExecuteContexts[1]);
        Assert.Same(provider.Contexts[1], command.ExecutedContext);
    }

    [Fact]
    public void TargetCommandDispatcher_DoesNotFallThroughWhenHostDisablesCommand()
    {
        var command = new RecordingCommand(canExecute: false);
        var provider = new RecordingProvider(command);
        var target = new MarkdownTargetCommand(
            MarkdownCommandKind.CopyLink,
            new SourceSpan(2, 5),
            "https://example.test/");

        Assert.False(MarkdownTargetCommandDispatcher.CanExecute(
            provider,
            target,
            out bool provided));
        Assert.True(provided);
        Assert.Equal(
            MarkdownTargetCommandDispatchResult.Disabled,
            MarkdownTargetCommandDispatcher.Execute(provider, target));
        Assert.Null(command.ExecutedContext);
    }

    [Fact]
    public void TargetCommandDispatcher_UsesLibraryFallbackOnlyWhenProviderReturnsNull()
    {
        var target = new MarkdownTargetCommand(
            MarkdownCommandKind.CopyImage,
            new SourceSpan(3, 9),
            "https://example.test/image.png");

        Assert.True(MarkdownTargetCommandDispatcher.CanExecute(
            provider: null,
            target,
            out bool provided));
        Assert.False(provided);
        Assert.Equal(
            MarkdownTargetCommandDispatchResult.NotProvided,
            MarkdownTargetCommandDispatcher.Execute(provider: null, target));
    }

    [Fact]
    public void LinkActivationPolicy_PreservesFragmentActionDisabledAndFallbackAcrossModalities()
    {
        MarkdownLinkInputKind[] modalities =
        [
            MarkdownLinkInputKind.Mouse,
            MarkdownLinkInputKind.Touch,
            MarkdownLinkInputKind.Pen,
            MarkdownLinkInputKind.Keyboard,
            MarkdownLinkInputKind.Automation,
        ];

        foreach (MarkdownLinkInputKind modality in modalities)
        {
            var fragmentProvider = new RecordingProvider(
                new RecordingCommand(canExecute: true));
            MarkdownLinkActivationDecision fragment = MarkdownLinkActivationPolicy.Evaluate(
                modality,
                internalTargetHandled: true,
                fragmentProvider,
                new SourceSpan(1, 2),
                "#target",
                action: null);
            Assert.Equal(MarkdownLinkActivationPolicyResult.InternalTarget, fragment.Result);
            Assert.True(fragment.IsHandled);
            Assert.Equal(modality, fragment.InputKind);
            Assert.Empty(fragmentProvider.Contexts);

            var actionCommand = new RecordingCommand(canExecute: true);
            var actionProvider = new RecordingProvider(actionCommand);
            MarkdownLinkActivationDecision action = MarkdownLinkActivationPolicy.Evaluate(
                modality,
                internalTargetHandled: false,
                actionProvider,
                new SourceSpan(3, 4),
                "https://example.test/fallback-url",
                "host-action");
            Assert.Equal(MarkdownLinkActivationPolicyResult.ExecutedCommand, action.Result);
            Assert.True(action.IsHandled);
            Assert.Equal(modality, action.InputKind);
            Assert.Equal("host-action", Assert.Single(actionProvider.Contexts).Target);
            Assert.NotNull(actionCommand.ExecutedContext);

            var disabledCommand = new RecordingCommand(canExecute: false);
            var disabledProvider = new RecordingProvider(disabledCommand);
            MarkdownLinkActivationDecision disabled = MarkdownLinkActivationPolicy.Evaluate(
                modality,
                internalTargetHandled: false,
                disabledProvider,
                new SourceSpan(5, 6),
                "https://example.test/disabled",
                action: null);
            Assert.Equal(MarkdownLinkActivationPolicyResult.DisabledCommand, disabled.Result);
            Assert.True(disabled.IsHandled);
            Assert.Equal(modality, disabled.InputKind);
            Assert.Null(disabledCommand.ExecutedContext);

            MarkdownLinkActivationDecision fallback = MarkdownLinkActivationPolicy.Evaluate(
                modality,
                internalTargetHandled: false,
                provider: null,
                new SourceSpan(7, 8),
                "https://example.test/fallback",
                action: null);
            Assert.Equal(MarkdownLinkActivationPolicyResult.RaiseHostEvent, fallback.Result);
            Assert.False(fallback.IsHandled);
            Assert.Equal(modality, fallback.InputKind);
        }
    }

    [Fact]
    public void TableClipboardFormatter_ProducesTsvAndSafeRichHtml()
    {
        IReadOnlyList<IReadOnlyList<string>> rows =
        [
            new[] { "Name", "Value" },
            new[] { "<unsafe>", "line 1\nline 2\tend" },
        ];

        MarkdownTableClipboardPayload payload =
            MarkdownTableClipboardFormatter.Format(rows, headerRowCount: 1);

        Assert.Equal("Name\tValue" + Environment.NewLine + "<unsafe>\tline 1 line 2 end", payload.Text);
        Assert.Equal(
            "<table><thead><tr><th>Name</th><th>Value</th></tr></thead>" +
            "<tbody><tr><td>&lt;unsafe&gt;</td><td>line 1 line 2 end</td></tr></tbody></table>",
            payload.Html);
    }

    private sealed class RecordingProvider(ICommand? command) : IMarkdownCommandProvider
    {
        public List<MarkdownCommandContext> Contexts { get; } = [];

        public ICommand? GetCommand(MarkdownCommandContext context)
        {
            Contexts.Add(context);
            return command;
        }
    }

    private sealed class RecordingCommand(bool canExecute) : ICommand
    {
        public List<object?> CanExecuteContexts { get; } = [];
        public object? ExecutedContext { get; private set; }

        public event EventHandler? CanExecuteChanged
        {
            add { }
            remove { }
        }

        public bool CanExecute(object? parameter)
        {
            CanExecuteContexts.Add(parameter);
            return canExecute;
        }

        public void Execute(object? parameter) => ExecutedContext = parameter;
    }
}
