using Xunit;

namespace MarkdownRenderer.Tests;

public sealed class CancellationTokenSourceRetirementTests
{
    [Fact]
    public async Task CancellationIsImmediateButCallbacksAndLifetimeRetireBeforeSourceDisposal()
    {
        var source = new CancellationTokenSource();
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
                if (!releaseCallback.Wait(TimeSpan.FromSeconds(5)))
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
            await callbackEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
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

        await callbacks.WaitAsync(TimeSpan.FromSeconds(2));
        await retirement.WaitAsync(TimeSpan.FromSeconds(2));
        await callbackExited.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Throws<ObjectDisposedException>(() =>
        {
            _ = source.Token;
        });
    }

    [Fact]
    public async Task ThrowingCancellationRegistrationIsObservedAndCannotPreventRetirement()
    {
        var source = new CancellationTokenSource();
        using CancellationTokenRegistration registration = source.Token.Register(
            static () => throw new InvalidOperationException("expected callback failure"));

        Task callbacks = CancellationTokenSourceRetirement.RequestCancellation(source);
        Task retirement = CancellationTokenSourceRetirement.DisposeAfter(
            source,
            callbacks);

        // CancelAsync schedules registered callbacks on the thread pool. This
        // is a lifetime-ordering assertion, not a latency gate: a busy CI host
        // may delay that callback beyond two seconds without losing it.
        await retirement.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(callbacks.IsFaulted);
        Assert.NotNull(callbacks.Exception);
        Assert.Throws<ObjectDisposedException>(() =>
        {
            _ = source.Token;
        });
    }
}
