using Xunit;
using MarkdownRenderer.Internal.Concurrency;

namespace MarkdownRenderer.Tests;

public sealed class RetirableCancellationTokenSourceTests
{
    [Fact]
    public async Task RepeatedRequestsShareTheFirstRequestsCallbackTask()
    {
        using var source = new RetirableCancellationTokenSource();
        using var releaseCallback = new ManualResetEventSlim();
        using CancellationTokenRegistration registration = source.Token.Register(() =>
            releaseCallback.Wait(TimeSpan.FromSeconds(5)));

        Task first = source.RequestCancellation();
        Task second = CancellationTokenSourceRetirement.RequestCancellation(source);
        try
        {
            // A later request against an already-canceling source must not
            // report completed callbacks while the first callbacks still run.
            Assert.Same(first, second);
            Assert.False(second.IsCompleted);
        }
        finally
        {
            releaseCallback.Set();
        }

        await first.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public void RequestStateIsOwnedByEachSource()
    {
        using var firstSource = new RetirableCancellationTokenSource();
        using var secondSource = new RetirableCancellationTokenSource();

        _ = firstSource.RequestCancellation();

        Assert.True(firstSource.IsCancellationRequested);
        Assert.False(secondSource.IsCancellationRequested);
        _ = secondSource.RequestCancellation();
        Assert.True(secondSource.IsCancellationRequested);
    }

    [Fact]
    public void LinkedSourceIsCanceledByEitherParent()
    {
        using var single = new CancellationTokenSource();
        using var first = new CancellationTokenSource();
        using var second = new CancellationTokenSource();
        using RetirableCancellationTokenSource singleLinked =
            RetirableCancellationTokenSource.CreateLinked(single.Token);
        using RetirableCancellationTokenSource firstLinked =
            RetirableCancellationTokenSource.CreateLinked(first.Token, second.Token);
        using RetirableCancellationTokenSource secondLinked =
            RetirableCancellationTokenSource.CreateLinked(first.Token, second.Token);

        single.Cancel();
        Assert.True(singleLinked.IsCancellationRequested);

        Assert.False(firstLinked.IsCancellationRequested);
        second.Cancel();
        Assert.True(firstLinked.IsCancellationRequested);
        Assert.True(secondLinked.IsCancellationRequested);
    }

    [Fact]
    public void LinkedSourceOfCanceledParentStartsCanceled()
    {
        using RetirableCancellationTokenSource linked =
            RetirableCancellationTokenSource.CreateLinked(new CancellationToken(canceled: true));

        Assert.True(linked.IsCancellationRequested);
        Assert.True(linked.RequestCancellation().IsCompletedSuccessfully);
    }

    [Fact]
    public void DisposedLinkedSourceNoLongerListensToItsParent()
    {
        using var parent = new CancellationTokenSource();
        RetirableCancellationTokenSource linked =
            RetirableCancellationTokenSource.CreateLinked(parent.Token);
        CancellationToken linkedToken = linked.Token;
        linked.Dispose();

        parent.Cancel();

        Assert.False(linkedToken.IsCancellationRequested);
    }
}
