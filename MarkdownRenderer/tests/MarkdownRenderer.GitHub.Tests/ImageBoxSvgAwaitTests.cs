using System;
using System.Threading;
using System.Threading.Tasks;
using MarkdownRenderer.Layout.Boxes;
using Xunit;

namespace MarkdownRenderer.GitHub.Tests;

public sealed class ImageBoxSvgAwaitTests
{
    [Fact]
    public async Task SuccessfulWaitTransfersResultOwnershipToCaller()
    {
        var resource = new DisposableProbe();

        DisposableProbe result = await ImageBox.AwaitSvgResultAsync(
            new ValueTask<DisposableProbe>(resource),
            CancellationToken.None);

        Assert.Same(resource, result);
        Assert.Equal(0, resource.DisposeCount);

        result.Dispose();
        Assert.Equal(1, resource.DisposeCount);
    }

    [Fact]
    public async Task CanceledWaitDisposesLateSuccessfulResultExactlyOnce()
    {
        var completion = new TaskCompletionSource<DisposableProbe>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation = new CancellationTokenSource();
        Task<DisposableProbe> wait = ImageBox.AwaitSvgResultAsync(
            new ValueTask<DisposableProbe>(completion.Task),
            cancellation.Token);

        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await wait);

        var resource = new DisposableProbe();
        completion.SetResult(resource);
        await resource.Disposed.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(1, resource.DisposeCount);
    }

    [Fact]
    public async Task AbandonedResultIsDisposedWhenItCompletedBeforeCleanupWasAttached()
    {
        var resource = new DisposableProbe();
        Task<DisposableProbe> completed = Task.FromResult(resource);

        Task retirement = ImageBox.RetireUnclaimedSvgResult(completed);

        await retirement.WaitAsync(TimeSpan.FromSeconds(5));
        await resource.Disposed.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, resource.DisposeCount);
    }

    [Fact]
    public async Task AbandonedFaultIsObservedWithoutFaultingRetirement()
    {
        var completion = new TaskCompletionSource<DisposableProbe>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        Task retirement = ImageBox.RetireUnclaimedSvgResult(completion.Task);

        completion.SetException(new InvalidOperationException("provider failed after cancellation"));

        await retirement.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(retirement.IsCompletedSuccessfully);
    }

    private sealed class DisposableProbe : IDisposable
    {
        private readonly TaskCompletionSource<bool> _disposed = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private int _disposeCount;

        internal Task Disposed => _disposed.Task;
        internal int DisposeCount => Volatile.Read(ref _disposeCount);

        public void Dispose()
        {
            Interlocked.Increment(ref _disposeCount);
            _disposed.TrySetResult(true);
        }
    }
}
