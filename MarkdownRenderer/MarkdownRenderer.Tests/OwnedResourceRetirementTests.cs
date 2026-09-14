using MarkdownRenderer.Controls;
using Xunit;

namespace MarkdownRenderer.Tests;

public sealed class OwnedResourceRetirementTests
{
    [Fact]
    public async Task CombineLifetimes_PreservesWorkDetachedByEarlierRetirement()
    {
        var earlier = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var current = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);

        Task firstRetirement = OwnedResourceRetirement.CombineLifetimes(
            Task.CompletedTask,
            earlier.Task);
        Task repeatedRetirement = OwnedResourceRetirement.CombineLifetimes(
            firstRetirement,
            current.Task);

        current.TrySetResult();
        Assert.False(repeatedRetirement.IsCompleted);

        earlier.TrySetResult();
        await repeatedRetirement.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task ResourceIsDisposedOnlyAfterItsLifetimeCompletes()
    {
        var lifetime = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var resource = new TrackingDisposable();

        Task retirement = OwnedResourceRetirement.DisposeAfter(resource, lifetime.Task);

        Assert.False(resource.IsDisposed);
        Assert.False(retirement.IsCompleted);
        lifetime.TrySetResult();
        await retirement.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(resource.IsDisposed);
    }

    [Fact]
    public async Task FaultedLifetimeCannotStrandOwnedResource()
    {
        var expected = new InvalidOperationException("worker failed");
        Exception? observed = null;
        var resource = new TrackingDisposable();

        Task retirement = OwnedResourceRetirement.DisposeAfter(
            resource,
            Task.FromException(expected),
            exception => observed = exception);

        await retirement.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(resource.IsDisposed);
        Assert.Same(expected, observed);
    }

    [Fact]
    public async Task OwnerWaitsForBlockingCancellationCallbackAfterWorkReturns()
    {
        var source = new CancellationTokenSource();
        CancellationToken token = source.Token;
        var owner = new TrackingDisposable();
        var callbackEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var workReturned = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var releaseCallback = new ManualResetEventSlim();
        using CancellationTokenRegistration registration = token.Register(() =>
        {
            owner.ThrowIfDisposed();
            callbackEntered.TrySetResult();
            if (!releaseCallback.Wait(TimeSpan.FromSeconds(5)))
                throw new TimeoutException("Cancellation callback was not released.");
            owner.ThrowIfDisposed();
        });

        Task work = Task.Run(async () =>
        {
            while (!token.IsCancellationRequested)
                await Task.Yield();
            workReturned.TrySetResult();
        });

        Task generationRetirement =
            CancellationTokenSourceRetirement.CancelAndDisposeAfter(source, work);
        Task ownerRetirement = OwnedResourceRetirement.DisposeAfter(
            owner,
            generationRetirement);

        try
        {
            await callbackEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            await workReturned.Task.WaitAsync(TimeSpan.FromSeconds(2));
            await work.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.False(owner.IsDisposed);
            Assert.False(generationRetirement.IsCompleted);
            Assert.False(ownerRetirement.IsCompleted);
        }
        finally
        {
            releaseCallback.Set();
        }

        await generationRetirement.WaitAsync(TimeSpan.FromSeconds(2));
        await ownerRetirement.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(owner.IsDisposed);
    }

    private sealed class TrackingDisposable : IDisposable
    {
        private int _disposeCount;

        internal bool IsDisposed => Volatile.Read(ref _disposeCount) != 0;

        public void Dispose() => Interlocked.Increment(ref _disposeCount);

        internal void ThrowIfDisposed()
        {
            if (IsDisposed)
                throw new ObjectDisposedException(nameof(TrackingDisposable));
        }
    }
}
