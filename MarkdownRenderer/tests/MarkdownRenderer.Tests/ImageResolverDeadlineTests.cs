using System.Diagnostics;
using MarkdownRenderer.Images;
using Xunit;

namespace MarkdownRenderer.Tests;

public sealed class ImageResolverDeadlineTests
{
    // Upper bound for waits on events the test expects to happen. Callbacks run
    // on the thread pool, which a loaded CI runner can delay by seconds. These
    // bounds only stop a hung test; no assertion depends on them.
    private static readonly TimeSpan LivenessTimeout = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task TimeoutCancelsResolverTokenAndStopsWaitingForIgnoringResolver()
    {
        var never = new TaskCompletionSource<int>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken resolverToken = default;
        var stopwatch = Stopwatch.StartNew();

        await Assert.ThrowsAsync<TimeoutException>(async () =>
            await ImageResolverDeadline.RunAsync(
                token =>
                {
                    resolverToken = token;
                    return new ValueTask<int>(never.Task);
                },
                TimeSpan.FromMilliseconds(25),
                CancellationToken.None));

        stopwatch.Stop();
        Assert.True(resolverToken.IsCancellationRequested);
        Assert.InRange(stopwatch.Elapsed, TimeSpan.Zero, TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task DocumentCancellationRemainsCancellationRatherThanTimeout()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await ImageResolverDeadline.RunAsync(
                _ => ValueTask.FromResult(42),
                TimeSpan.FromSeconds(1),
                cancellation.Token));
    }

    [Fact]
    public async Task CompletedResolutionIsReturnedBeforeDeadline()
    {
        int result = await ImageResolverDeadline.RunAsync(
            _ => ValueTask.FromResult(42),
            TimeSpan.FromSeconds(1),
            CancellationToken.None);

        Assert.Equal(42, result);
    }

    [Fact]
    public async Task TimedOutResolverDisposesLateSuccessfulResultExactlyOnce()
    {
        var pending = new TaskCompletionSource<TrackedResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var disposed = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var result = new TrackedResult();

        await Assert.ThrowsAsync<TimeoutException>(async () =>
            await ImageResolverDeadline.RunAsync(
                _ => new ValueTask<TrackedResult>(pending.Task),
                TimeSpan.FromMilliseconds(25),
                CancellationToken.None,
                abandoned =>
                {
                    abandoned.Dispose();
                    disposed.TrySetResult();
                }));

        Assert.Equal(0, result.DisposeCount);
        Assert.True(pending.TrySetResult(result));
        await disposed.Task.WaitAsync(LivenessTimeout);
        Assert.Equal(1, result.DisposeCount);
    }

    [Fact]
    public async Task DocumentCancellationDisposesLateSuccessfulResultExactlyOnce()
    {
        using var cancellation = new CancellationTokenSource();
        var pending = new TaskCompletionSource<TrackedResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var disposed = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var result = new TrackedResult();
        ValueTask<TrackedResult> work = ImageResolverDeadline.RunAsync(
            _ => new ValueTask<TrackedResult>(pending.Task),
            TimeSpan.FromSeconds(5),
            cancellation.Token,
            abandoned =>
            {
                abandoned.Dispose();
                disposed.TrySetResult();
            });

        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await work);

        Assert.True(pending.TrySetResult(result));
        await disposed.Task.WaitAsync(LivenessTimeout);
        Assert.Equal(1, result.DisposeCount);
    }

    [Fact]
    public async Task SuccessfullyReturnedResultIsNotDisposed()
    {
        var result = new TrackedResult();

        TrackedResult returned = await ImageResolverDeadline.RunAsync(
            _ => ValueTask.FromResult(result),
            TimeSpan.FromSeconds(1),
            CancellationToken.None,
            static abandoned => abandoned.Dispose());

        Assert.Same(result, returned);
        Assert.Equal(0, result.DisposeCount);
    }

    [Fact]
    public async Task SynchronousSuccessDuringDocumentCancellationIsDisposedInsteadOfPublished()
    {
        using var cancellation = new CancellationTokenSource();
        var result = new TrackedResult();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await ImageResolverDeadline.RunAsync(
                _ =>
                {
                    cancellation.Cancel();
                    return ValueTask.FromResult(result);
                },
                TimeSpan.FromSeconds(5),
                cancellation.Token,
                abandoned => abandoned.Dispose()));
        Assert.Equal(1, result.DisposeCount);
    }

    [Fact]
    public async Task LateDisposerFailureIsObservedWithoutReplacingTimeout()
    {
        var pending = new TaskCompletionSource<TrackedResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var cleanupObserved = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var observedCompletion = new TaskCompletionSource<Task>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        int disposeAttempts = 0;

        await Assert.ThrowsAsync<TimeoutException>(async () =>
            await ImageResolverDeadline.RunAsync(
                _ => new ValueTask<TrackedResult>(pending.Task),
                TimeSpan.FromMilliseconds(25),
                CancellationToken.None,
                _ =>
                {
                    disposeAttempts++;
                    cleanupObserved.TrySetResult();
                    throw new InvalidOperationException("private resolver details");
                },
                completion => observedCompletion.TrySetResult(completion)));

        Assert.True(pending.TrySetResult(new TrackedResult()));
        await cleanupObserved.Task.WaitAsync(LivenessTimeout);
        Task completionTask = await observedCompletion.Task.WaitAsync(LivenessTimeout);
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await completionTask.WaitAsync(LivenessTimeout));
        Assert.Equal(1, disposeAttempts);
    }

    [Fact]
    public async Task ImmediateAbandonedDisposerFailureIsObservedWithoutReplacingCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        var pending = new TaskCompletionSource<TrackedResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var observedCompletion = new TaskCompletionSource<Task>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var result = new TrackedResult();
        int disposeAttempts = 0;
        ValueTask<TrackedResult> work = ImageResolverDeadline.RunAsync(
            _ => new ValueTask<TrackedResult>(pending.Task),
            TimeSpan.FromSeconds(5),
            cancellation.Token,
            abandoned =>
            {
                disposeAttempts++;
                abandoned.Dispose();
                throw new InvalidOperationException("private resolver details");
            },
            completion => observedCompletion.TrySetResult(completion));

        // CancellationTokenSource invokes callbacks in reverse registration order.
        // Complete the operation after RunAsync has installed its cancellation
        // waiter, making the successful task visible to cleanup in the catch path.
        using CancellationTokenRegistration completeBeforeWaiter = cancellation.Token.Register(
            static state =>
            {
                var completion = ((TaskCompletionSource<TrackedResult> Pending, TrackedResult Result))state!;
                completion.Pending.TrySetResult(completion.Result);
            },
            (pending, result));
        Task<TrackedResult> workTask = work.AsTask();

        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await workTask);

        Task cleanupTask = await observedCompletion.Task.WaitAsync(LivenessTimeout);
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await cleanupTask.WaitAsync(LivenessTimeout));
        Assert.Equal(1, disposeAttempts);
        Assert.Equal(1, result.DisposeCount);
    }

    [Fact]
    public async Task PreCanceledDocumentDoesNotStartResolverOrDisposeAnything()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        int starts = 0;
        int disposeAttempts = 0;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await ImageResolverDeadline.RunAsync(
                _ =>
                {
                    starts++;
                    return ValueTask.FromResult(new TrackedResult());
                },
                TimeSpan.FromSeconds(1),
                cancellation.Token,
                _ => disposeAttempts++));

        Assert.Equal(0, starts);
        Assert.Equal(0, disposeAttempts);
    }

    [Fact]
    public async Task SynchronousResolverFailureDoesNotInvokeAbandonedResultDisposer()
    {
        int disposeAttempts = 0;
        var resolverFailure = new InvalidOperationException("private resolver details");

        InvalidOperationException observed = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await ImageResolverDeadline.RunAsync<TrackedResult>(
                _ => throw resolverFailure,
                TimeSpan.FromSeconds(1),
                CancellationToken.None,
                _ => disposeAttempts++));

        Assert.Same(resolverFailure, observed);
        Assert.Equal(0, disposeAttempts);
    }

    [Fact]
    public async Task LateResolverFaultDoesNotInvokeAbandonedResultDisposer()
    {
        using var cancellation = new CancellationTokenSource();
        var pending = new TaskCompletionSource<TrackedResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var resolverSettled = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        int disposeAttempts = 0;
        ValueTask<TrackedResult> work = ImageResolverDeadline.RunAsync(
            _ => new ValueTask<TrackedResult>(pending.Task),
            TimeSpan.FromSeconds(5),
            cancellation.Token,
            _ => disposeAttempts++);

        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await work);

        _ = pending.Task.ContinueWith(
            static (_, state) => ((TaskCompletionSource)state!).TrySetResult(),
            resolverSettled,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
        Assert.True(pending.TrySetException(new InvalidOperationException("private resolver details")));
        await resolverSettled.Task.WaitAsync(LivenessTimeout);

        Assert.Equal(0, disposeAttempts);
    }

    [Fact]
    public async Task AbandonedCompletionObserverWaitsForLateResultDisposal()
    {
        using var cancellation = new CancellationTokenSource();
        using var releaseDisposer = new ManualResetEventSlim();
        var pending = new TaskCompletionSource<TrackedResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var disposerEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var observedCompletion = new TaskCompletionSource<Task>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var result = new TrackedResult();
        ValueTask<TrackedResult> work = ImageResolverDeadline.RunAsync(
            _ => new ValueTask<TrackedResult>(pending.Task),
            TimeSpan.FromSeconds(5),
            cancellation.Token,
            abandoned =>
            {
                disposerEntered.TrySetResult();
                if (!releaseDisposer.Wait(LivenessTimeout))
                    throw new TimeoutException("The test did not release the disposer.");
                abandoned.Dispose();
            },
            completion => observedCompletion.TrySetResult(completion));

        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await work);
        Task completionTask = await observedCompletion.Task.WaitAsync(LivenessTimeout);
        Assert.False(completionTask.IsCompleted);

        try
        {
            Task completeResult = Task.Run(() => pending.TrySetResult(result));
            await disposerEntered.Task.WaitAsync(LivenessTimeout);
            Assert.False(completionTask.IsCompleted);
            releaseDisposer.Set();
            await completeResult.WaitAsync(LivenessTimeout);
            await completionTask.WaitAsync(LivenessTimeout);
        }
        finally
        {
            releaseDisposer.Set();
        }

        Assert.Equal(1, result.DisposeCount);
    }

    [Fact]
    public async Task AbandonedCompletionObserverWaitsForImmediateRaceDisposal()
    {
        using var cancellation = new CancellationTokenSource();
        using var releaseDisposer = new ManualResetEventSlim();
        var pending = new TaskCompletionSource<TrackedResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var allowLateRegistration = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var registrationReady = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var disposerEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var observedCompletion = new TaskCompletionSource<Task>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var result = new TrackedResult();
        CancellationTokenRegistration registration = default;
        ValueTask<TrackedResult> work = ImageResolverDeadline.RunAsync(
            token =>
            {
                _ = Task.Run(async () =>
                {
                    await allowLateRegistration.Task.ConfigureAwait(false);
                    registration = token.Register(
                        static state =>
                        {
                            var completion = ((TaskCompletionSource<TrackedResult> Pending, TrackedResult Result))state!;
                            completion.Pending.TrySetResult(completion.Result);
                        },
                        (pending, result));
                    registrationReady.TrySetResult();
                });
                return new ValueTask<TrackedResult>(pending.Task);
            },
            TimeSpan.FromSeconds(5),
            cancellation.Token,
            abandoned =>
            {
                disposerEntered.TrySetResult();
                if (!releaseDisposer.Wait(LivenessTimeout))
                    throw new TimeoutException("The test did not release the disposer.");
                abandoned.Dispose();
            },
            completion => observedCompletion.TrySetResult(completion));
        Task<TrackedResult> workTask = work.AsTask();
        allowLateRegistration.TrySetResult();
        await registrationReady.Task.WaitAsync(LivenessTimeout);

        Task cancelTask = Task.Run(cancellation.Cancel);
        Task firstCompletion = await Task.WhenAny(
                disposerEntered.Task,
                workTask,
                Task.Delay(LivenessTimeout));

        try
        {
            if (ReferenceEquals(firstCompletion, disposerEntered.Task))
            {
                Assert.False(observedCompletion.Task.IsCompleted);
                releaseDisposer.Set();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await workTask);
                Task completionTask = await observedCompletion.Task.WaitAsync(LivenessTimeout);
                Assert.Same(Task.CompletedTask, completionTask);
                Assert.Equal(1, result.DisposeCount);
            }
            else
            {
                Assert.Same(workTask, firstCompletion);
                TrackedResult returned = await workTask;
                Assert.Same(result, returned);
                Assert.False(observedCompletion.Task.IsCompleted);
                Assert.Equal(0, result.DisposeCount);
            }

            await cancelTask.WaitAsync(LivenessTimeout);
        }
        finally
        {
            releaseDisposer.Set();
            registration.Dispose();
        }
    }

    [Fact]
    public async Task AbandonedCompletionObserverFailureDoesNotReplaceCancellationOrSkipCleanup()
    {
        using var cancellation = new CancellationTokenSource();
        var pending = new TaskCompletionSource<TrackedResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var disposed = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var result = new TrackedResult();
        ValueTask<TrackedResult> work = ImageResolverDeadline.RunAsync(
            _ => new ValueTask<TrackedResult>(pending.Task),
            TimeSpan.FromSeconds(5),
            cancellation.Token,
            abandoned =>
            {
                abandoned.Dispose();
                disposed.TrySetResult();
            },
            _ => throw new InvalidOperationException("private observer details"));

        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await work);
        Assert.True(pending.TrySetResult(result));
        await disposed.Task.WaitAsync(LivenessTimeout);

        Assert.Equal(1, result.DisposeCount);
    }

    [Fact]
    public async Task ConcurrentSuccessAndDocumentCancellationEitherReturnsOrDisposesExactlyOnce()
    {
        for (int iteration = 0; iteration < 64; iteration++)
        {
            using var cancellation = new CancellationTokenSource();
            var pending = new TaskCompletionSource<TrackedResult>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseStarted = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var disposed = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var result = new TrackedResult();
            ValueTask<TrackedResult> work = ImageResolverDeadline.RunAsync(
                _ => new ValueTask<TrackedResult>(pending.Task),
                TimeSpan.FromSeconds(5),
                cancellation.Token,
                abandoned =>
                {
                    abandoned.Dispose();
                    disposed.TrySetResult();
                });

            Task complete = Task.Run(async () =>
            {
                await releaseStarted.Task.ConfigureAwait(false);
                pending.TrySetResult(result);
            });
            Task cancel = Task.Run(async () =>
            {
                await releaseStarted.Task.ConfigureAwait(false);
                cancellation.Cancel();
            });
            releaseStarted.TrySetResult();
            await Task.WhenAll(complete, cancel).WaitAsync(LivenessTimeout);

            bool returnedToCaller = false;
            try
            {
                TrackedResult returned = await work.AsTask().WaitAsync(LivenessTimeout);
                Assert.Same(result, returned);
                returnedToCaller = true;
            }
            catch (OperationCanceledException)
            {
                await disposed.Task.WaitAsync(LivenessTimeout);
            }

            Assert.Equal(returnedToCaller ? 0 : 1, result.DisposeCount);
            Assert.Equal(returnedToCaller, !disposed.Task.IsCompleted);
        }
    }

    private sealed class TrackedResult : IDisposable
    {
        private int _disposeCount;

        public int DisposeCount => Volatile.Read(ref _disposeCount);

        public void Dispose() => Interlocked.Increment(ref _disposeCount);
    }
}
