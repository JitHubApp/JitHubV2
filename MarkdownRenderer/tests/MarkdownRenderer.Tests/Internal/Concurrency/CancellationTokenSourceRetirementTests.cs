using Xunit;
using MarkdownRenderer.Internal.Concurrency;

namespace MarkdownRenderer.Tests.Internal.Concurrency;

public sealed class CancellationTokenSourceRetirementTests
{
    // CancelAsync runs registered callbacks on the thread pool. These waits only
    // turn a lost callback or a hang into a failure; the ordering assertions
    // decide the result. A busy CI host can delay a callback by seconds.
    private static readonly TimeSpan HangGuard = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task CancellationIsImmediateButCallbacksAndLifetimeRetireBeforeSourceDisposal()
    {
        var source = new RetirableCancellationTokenSource();
        CancellationToken token = source.Token;
        var callbackEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var callbackExited = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var lifetime = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var releaseCallback = new ManualResetEventSlim();
        using CancellationTokenRegistration registration = token.Register(() =>
        {
            callbackEntered.TrySetResult();
            try
            {
                if (!releaseCallback.Wait(HangGuard))
                    throw new TimeoutException("Cancellation callback was not released.");
            }
            finally
            {
                callbackExited.TrySetResult();
            }
        });

        Task callbacks = CancellationTokenSourceRetirement.RequestCancellation(source);
        Task retirement = Task.CompletedTask;
        try
        {
            Assert.True(token.IsCancellationRequested);
            await callbackEntered.Task.WaitAsync(HangGuard);
            Assert.False(callbacks.IsCompleted);

            retirement = CancellationTokenSourceRetirement.DisposeAfter(
                source,
                callbacks,
                lifetime.Task);
            lifetime.TrySetResult();
            Assert.False(retirement.IsCompleted);
            _ = source.Token;
        }
        finally
        {
            releaseCallback.Set();
        }

        await callbacks.WaitAsync(HangGuard);
        await retirement.WaitAsync(HangGuard);
        await callbackExited.Task.WaitAsync(HangGuard);
        Assert.Throws<ObjectDisposedException>(() =>
        {
            _ = source.Token;
        });
    }

    [Fact]
    public async Task ThrowingCancellationRegistrationIsObservedAndCannotPreventRetirement()
    {
        var source = new RetirableCancellationTokenSource();
        using CancellationTokenRegistration registration = source.Token.Register(
            static () => throw new InvalidOperationException("expected callback failure"));

        Task callbacks = CancellationTokenSourceRetirement.RequestCancellation(source);
        Task retirement = CancellationTokenSourceRetirement.DisposeAfter(
            source,
            callbacks);

        await retirement.WaitAsync(HangGuard);
        Assert.True(callbacks.IsFaulted);
        Assert.NotNull(callbacks.Exception);
        Assert.Throws<ObjectDisposedException>(() =>
        {
            _ = source.Token;
        });
    }
}
