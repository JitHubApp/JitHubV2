using MarkdownRenderer.Accessibility;
using MarkdownRenderer.Hosting;
using MarkdownRenderer.Layout;
using Xunit;

namespace MarkdownRenderer.Tests;

public sealed class HostedElementLifecycleContractTests
{
    [Fact]
    public void CancellationDetachesAttemptAndStaleCompletionCannotDisplaceRetry()
    {
        var gate = new HostedElementRealizationGate<string>();

        Assert.True(gate.TryBegin("first request", CancellationToken.None, out var first));
        Assert.True(gate.HasActive);

        gate.CancelActive();

        Assert.True(first.CancellationToken.IsCancellationRequested);
        Assert.False(gate.HasActive);
        Assert.True(gate.TryBegin("retry request", CancellationToken.None, out var retry));

        Assert.False(gate.TryClaimCompletion(first));
        Assert.True(gate.HasActive);
        Assert.True(gate.TryClaimCompletion(retry));
        Assert.False(gate.HasActive);
        Assert.Equal("first request", first.Request);
        Assert.Equal("retry request", retry.Request);

        first.Dispose();
        retry.Dispose();
    }

    [Fact]
    public void ParentCancellationRejectsCurrentCompletionAndAllowsNextAttempt()
    {
        using var lifetime = new CancellationTokenSource();
        var gate = new HostedElementRealizationGate<string>();
        Assert.True(gate.TryBegin("cancelled", lifetime.Token, out var cancelled));

        lifetime.Cancel();

        Assert.True(gate.TryBegin("next", CancellationToken.None, out var next));
        Assert.False(gate.TryClaimCompletion(cancelled));
        Assert.True(gate.HasActive);
        Assert.True(gate.TryClaimCompletion(next));

        cancelled.Dispose();
        next.Dispose();
    }

    [Fact]
    public void DuplicateBeginIsRejectedWithoutReplacingActiveRequest()
    {
        var gate = new HostedElementRealizationGate<string>();
        Assert.True(gate.TryBegin("active", CancellationToken.None, out var active));

        Assert.False(gate.TryBegin("duplicate", CancellationToken.None, out _));
        Assert.True(gate.TryClaimCompletion(active));

        active.Dispose();
    }

    [Fact]
    public async Task ThrowingCancellationCallbackCannotEscapeCleanup()
    {
        var gate = new HostedElementRealizationGate<string>();
        Assert.True(gate.TryBegin("throwing callback", CancellationToken.None, out var operation));
        using var registration = operation.CancellationToken.Register(
            static () => throw new InvalidOperationException("host callback failure"));

        Exception? failure = Record.Exception(gate.CancelActive);

        Assert.Null(failure);
        Assert.True(operation.CancellationToken.IsCancellationRequested);
        Assert.False(gate.HasActive);
        operation.Dispose();
        await operation.Retirement.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task CancelActiveReturnsWhileFactoryCancellationCallbackIsBlocked()
    {
        var gate = new HostedElementRealizationGate<string>();
        Assert.True(gate.TryBegin("blocking callback", CancellationToken.None, out var operation));
        var callbackEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var callbackRelease = new ManualResetEventSlim();
        using CancellationTokenRegistration registration = operation.CancellationToken.Register(() =>
        {
            callbackEntered.TrySetResult();
            if (!callbackRelease.Wait(TimeSpan.FromSeconds(5)))
                throw new TimeoutException("Cancellation callback was not released.");
        });

        Task cancel = Task.Run(gate.CancelActive);
        try
        {
            await cancel.WaitAsync(TimeSpan.FromSeconds(1));
            Assert.True(operation.CancellationToken.IsCancellationRequested);
            await callbackEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));

            operation.Dispose();
            Assert.False(operation.Retirement.IsCompleted);
        }
        finally
        {
            callbackRelease.Set();
        }

        await operation.Retirement.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task ParentCancellationIsNonBlockingAndOperationDisposalWaitsForChildCallbacks()
    {
        using var lifetime = new CancellationTokenSource();
        var gate = new HostedElementRealizationGate<string>();
        Assert.True(gate.TryBegin("parent cancellation", lifetime.Token, out var operation));
        var callbackEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var callbackRelease = new ManualResetEventSlim();
        using CancellationTokenRegistration registration = operation.CancellationToken.Register(() =>
        {
            callbackEntered.TrySetResult();
            if (!callbackRelease.Wait(TimeSpan.FromSeconds(5)))
                throw new TimeoutException("Child cancellation callback was not released.");
        });

        Task cancelParent = Task.Run(lifetime.Cancel);
        try
        {
            await cancelParent.WaitAsync(TimeSpan.FromSeconds(1));
            Assert.True(operation.CancellationToken.IsCancellationRequested);
            await callbackEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.False(gate.TryClaimCompletion(operation));

            operation.Dispose();
            Assert.False(operation.Retirement.IsCompleted);
        }
        finally
        {
            callbackRelease.Set();
        }

        await operation.Retirement.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public void HostedFocusKindIsDistinctFromLegacyBlockEmbed()
    {
        var hosted = new FocusableItem(7, 0, FocusableItemKind.DeclarativeHostedElement);

        Assert.True(hosted.IsDeclarativeHostedElement);
        Assert.False(hosted.IsBlockEmbed);
        Assert.False(hosted.IsInlineEmbed);
        Assert.False(hosted.IsLink);
    }

    [Fact]
    public void AutomationIdentityIsDeterministicAndDistinguishesLogicalElements()
    {
        var range = new SourceSpan(12, 8);
        string first = HostedElementAutomationIdentity.Create("tests.card", range, blockIndex: 3);

        Assert.Equal(first, HostedElementAutomationIdentity.Create("tests.card", range, blockIndex: 3));
        Assert.NotEqual(first, HostedElementAutomationIdentity.Create("tests.card", range, blockIndex: 4));
        Assert.NotEqual(first, HostedElementAutomationIdentity.Create("tests.other", range, blockIndex: 3));
        Assert.NotEqual(first, HostedElementAutomationIdentity.Create("tests.card", new SourceSpan(13, 8), blockIndex: 3));
    }
}
