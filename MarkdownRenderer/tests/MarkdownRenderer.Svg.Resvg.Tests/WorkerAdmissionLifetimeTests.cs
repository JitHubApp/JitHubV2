using MarkdownRenderer.Svg.Resvg.Internal;
using Xunit;

namespace MarkdownRenderer.Svg.Resvg.Tests;

public sealed class WorkerAdmissionLifetimeTests
{
    [Fact]
    public async Task FailedAdmissionDetachesRegistrationFromLiveCallerToken()
    {
        using var cancellation = new CancellationTokenSource();
        int callbacks = 0;
        CancellationTokenRegistration registration = cancellation.Token.Register(
            () => Interlocked.Increment(ref callbacks));
        var failure = new ObjectDisposedException("WorkerPool");

        ObjectDisposedException thrown = await Assert.ThrowsAsync<ObjectDisposedException>(() =>
            WorkerPool.AwaitAdmissionAsync(Task.FromException<object>(failure), registration));

        Assert.Same(failure, thrown);
        cancellation.Cancel();
        Assert.Equal(0, Volatile.Read(ref callbacks));
    }

    [Fact]
    public async Task CanceledAdmissionDetachesRegistrationFromLiveCallerToken()
    {
        using var caller = new CancellationTokenSource();
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        int callbacks = 0;
        CancellationTokenRegistration registration = caller.Token.Register(
            () => Interlocked.Increment(ref callbacks));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            WorkerPool.AwaitAdmissionAsync(Task.FromCanceled<object>(canceled.Token), registration));

        caller.Cancel();
        Assert.Equal(0, Volatile.Read(ref callbacks));
    }

    [Fact]
    public async Task SuccessfulAdmissionPreservesRegistrationUntilActiveLeaseDisposesIt()
    {
        using var caller = new CancellationTokenSource();
        int callbacks = 0;
        using CancellationTokenRegistration registration = caller.Token.Register(
            () => Interlocked.Increment(ref callbacks));
        var lease = new object();

        Assert.Same(lease, await WorkerPool.AwaitAdmissionAsync(Task.FromResult(lease), registration));

        caller.Cancel();
        Assert.Equal(1, Volatile.Read(ref callbacks));
    }

    [Fact]
    public async Task FailedAdmissionWaitsForConcurrentCallbackWithoutHoldingQueueLock()
    {
        using var caller = new CancellationTokenSource();
        using var callbackEntered = new ManualResetEventSlim();
        using var releaseCallback = new ManualResetEventSlim();
        var completion = new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationTokenRegistration registration = caller.Token.Register(() =>
        {
            callbackEntered.Set();
            Assert.True(releaseCallback.Wait(TimeSpan.FromSeconds(5)));
        });
        Task<object> admission = WorkerPool.AwaitAdmissionAsync(completion.Task, registration);
        Task cancellation = Task.Run(caller.Cancel);
        try
        {
            Assert.True(callbackEntered.Wait(TimeSpan.FromSeconds(5)));
            completion.SetException(new ObjectDisposedException("WorkerPool"));
            Assert.False(admission.IsCompleted);
        }
        finally
        {
            releaseCallback.Set();
        }

        await cancellation.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => admission.WaitAsync(TimeSpan.FromSeconds(5)));
    }
}
