using JitHub.Services.Markdown;
using Xunit;

namespace JitHub.WinUI.Tests.Services;

public sealed class MarkdownImageFallbackOwnershipTests
{
    [Fact]
    public async Task SequentialSuccessRacingCancellationIsDisposedWithoutStartingFallback()
    {
        using var cancellation = new CancellationTokenSource();
        var source = new OwnedResult();
        bool fallbackStarted = false;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            MarkdownImageFallbackPipeline.FirstSuccessfulAsync<OwnedResult>(
                _ => { cancellation.Cancel(); return Task.FromResult<OwnedResult?>(source); },
                _ => { fallbackStarted = true; return Task.FromResult<OwnedResult?>(null); },
                (_, _) => { }, cancellation.Token, static result => result.Dispose()));
        await source.Disposed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, source.DisposeCount);
        Assert.False(fallbackStarted);
    }

    [Fact]
    public async Task SequentialCanceledWaitDisposesLateSuccessfulProducer()
    {
        using var cancellation = new CancellationTokenSource();
        var producer = NewProducer();
        var source = new OwnedResult();
        Task<OwnedResult?> result = MarkdownImageFallbackPipeline.FirstSuccessfulAsync(
            _ => producer.Task, _ => Task.FromResult<OwnedResult?>(null),
            (_, _) => { }, cancellation.Token, static value => value.Dispose());
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => result);
        Assert.Equal(0, source.DisposeCount);
        producer.SetResult(source);
        await source.Disposed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, source.DisposeCount);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task HedgeTransfersWinnerAndDisposesLateSuccessfulLoser(bool primaryWins)
    {
        var primary = NewProducer();
        var secondary = NewProducer();
        var secondaryStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var winner = new OwnedResult();
        var loser = new OwnedResult();
        Task<OwnedResult?> result = MarkdownImageFallbackPipeline.FirstSuccessfulHedgedAsync(
            _ => primary.Task,
            _ => { secondaryStarted.SetResult(); return secondary.Task; },
            (_, _) => { }, TimeSpan.Zero, CancellationToken.None,
            static value => value.Dispose());
        await secondaryStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        (primaryWins ? primary : secondary).SetResult(winner);
        Assert.Same(winner, await result.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(0, winner.DisposeCount);
        (primaryWins ? secondary : primary).SetResult(loser);
        await loser.Disposed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, loser.DisposeCount);
        Assert.Equal(0, winner.DisposeCount);
        winner.Dispose();
        Assert.Equal(1, winner.DisposeCount);
    }

    [Fact]
    public async Task BothCompletedHedgesDisposeOnlyTheUnselectedResult()
    {
        var primary = NewProducer();
        var secondary = NewProducer();
        var secondaryStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = new OwnedResult();
        var second = new OwnedResult();
        Task<OwnedResult?> operation = MarkdownImageFallbackPipeline.FirstSuccessfulHedgedAsync(
            _ => primary.Task,
            _ => { secondaryStarted.SetResult(); return secondary.Task; },
            (_, _) => { }, TimeSpan.Zero, CancellationToken.None,
            static value => value.Dispose());
        await secondaryStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        primary.SetResult(first);
        secondary.SetResult(second);
        OwnedResult? winner = await operation.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.NotNull(winner);
        OwnedResult loser = ReferenceEquals(winner, first) ? second : first;
        await loser.Disposed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, winner.DisposeCount);
        Assert.Equal(1, loser.DisposeCount);
        winner.Dispose();
    }

    [Fact]
    public async Task HedgeCancellationDisposesLateSuccessOnBothBranches()
    {
        using var cancellation = new CancellationTokenSource();
        var primary = NewProducer();
        var secondary = NewProducer();
        var secondaryStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = new OwnedResult();
        var second = new OwnedResult();
        Task<OwnedResult?> operation = MarkdownImageFallbackPipeline.FirstSuccessfulHedgedAsync(
            _ => primary.Task,
            _ => { secondaryStarted.SetResult(); return secondary.Task; },
            (_, _) => { }, TimeSpan.Zero, cancellation.Token,
            static value => value.Dispose());
        await secondaryStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation);
        primary.SetResult(first);
        secondary.SetResult(second);
        await Task.WhenAll(first.Disposed.Task, second.Disposed.Task).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, first.DisposeCount);
        Assert.Equal(1, second.DisposeCount);
    }

    [Fact]
    public async Task ThrowingLoserDisposerAndReporterCannotReplaceWinner()
    {
        var primary = NewProducer();
        var winner = new OwnedResult();
        var loser = new OwnedResult();
        var reported = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<OwnedResult?> operation = MarkdownImageFallbackPipeline.FirstSuccessfulHedgedAsync(
            _ => primary.Task, _ => Task.FromResult<OwnedResult?>(winner),
            (_, _) => { reported.SetResult(); throw new InvalidOperationException("Reporter failure."); },
            TimeSpan.Zero, CancellationToken.None,
            result => { result.Dispose(); throw new InvalidOperationException("Cleanup failure."); });
        Assert.Same(winner, await operation.WaitAsync(TimeSpan.FromSeconds(5)));
        primary.SetResult(loser);
        await reported.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, loser.DisposeCount);
        Assert.Equal(0, winner.DisposeCount);
        winner.Dispose();
    }

    [Fact]
    public async Task CancellationCallbacksDoNotBlockWinnerAndRetireBeforeLoserDisposal()
    {
        var primary = NewProducer();
        var callbackEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var releaseCallback = new ManualResetEventSlim();
        var winner = new OwnedResult();
        var loser = new OwnedResult();
        CancellationTokenRegistration registration = default;
        Task<OwnedResult?> operation = MarkdownImageFallbackPipeline.FirstSuccessfulHedgedAsync(
            token =>
            {
                registration = token.Register(() =>
                {
                    callbackEntered.SetResult();
                    if (!releaseCallback.Wait(TimeSpan.FromSeconds(5)))
                        throw new TimeoutException("Test callback was not released.");
                });
                return primary.Task;
            },
            _ => Task.FromResult<OwnedResult?>(winner), (_, _) => { },
            TimeSpan.Zero, CancellationToken.None, static result => result.Dispose());
        try
        {
            Assert.Same(winner, await operation.WaitAsync(TimeSpan.FromSeconds(5)));
            await callbackEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            primary.SetResult(loser);
            Assert.Equal(0, loser.DisposeCount);
            releaseCallback.Set();
            await loser.Disposed.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(1, loser.DisposeCount);
            Assert.Equal(0, winner.DisposeCount);
        }
        finally
        {
            releaseCallback.Set();
            primary.TrySetResult(loser);
            registration.Dispose();
            winner.Dispose();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CallerCancellationDoesNotWaitForCandidateCallbackAndLateOwnerWaitsForCallback(
        bool useHedge)
    {
        using var callerCancellation = new CancellationTokenSource();
        using var releaseCallback = new ManualResetEventSlim();
        var producer = NewProducer();
        var callbackEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var producerObserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondaryStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var source = new OwnedResult();
        bool sequentialFallbackStarted = false;
        CancellationTokenRegistration providerRegistration = default;
        Func<CancellationToken, Task<OwnedResult?>> primary = token =>
        {
            // Retain the registration independently of the provider operation.
            // This lets its operation complete while the token callback remains
            // blocked, so retirement must await callback completion itself.
            providerRegistration = token.Register(() =>
            {
                callbackEntered.TrySetResult();
                // This deliberately stalls an uncooperative provider callback.
                // Caller cancellation must only queue it, never run it inline.
                if (!releaseCallback.Wait(TimeSpan.FromSeconds(5)))
                    throw new TimeoutException("Test callback was not released.");
            });
            return ObserveOwnerAsync(producer.Task, producerObserved);
        };

        Task<OwnedResult?> operation = useHedge
            ? MarkdownImageFallbackPipeline.FirstSuccessfulHedgedAsync(
                primary,
                _ =>
                {
                    secondaryStarted.TrySetResult();
                    return Task.FromResult<OwnedResult?>(null);
                },
                static (_, _) => { },
                TimeSpan.Zero,
                callerCancellation.Token,
                static result => result.Dispose())
            : MarkdownImageFallbackPipeline.FirstSuccessfulAsync(
                primary,
                _ =>
                {
                    sequentialFallbackStarted = true;
                    return Task.FromResult<OwnedResult?>(null);
                },
                static (_, _) => { },
                callerCancellation.Token,
                static result => result.Dispose());

        if (useHedge)
            await secondaryStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Task cancellationTask = Task.Run(callerCancellation.Cancel);
        try
        {
            await callbackEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await cancellationTask.WaitAsync(TimeSpan.FromSeconds(2));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation);

            producer.SetResult(source);
            await producerObserved.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Task.Delay(TimeSpan.FromMilliseconds(100));
            Assert.Equal(0, source.DisposeCount);
        }
        finally
        {
            releaseCallback.Set();
            providerRegistration.Dispose();
        }

        await cancellationTask.WaitAsync(TimeSpan.FromSeconds(5));
        await source.Disposed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, source.DisposeCount);
        Assert.False(sequentialFallbackStarted);
    }

    [Fact]
    public async Task ThrowingTransportDiagnosticReporterDoesNotSuppressSequentialFallback()
    {
        var source = new OwnedResult();
        int attempts = 0;
        OwnedResult? result = await MarkdownImageFallbackPipeline.FirstSuccessfulAsync(
            _ =>
            {
                Interlocked.Increment(ref attempts);
                return Task.FromException<OwnedResult?>(new IOException("Injected transport failure."));
            },
            _ =>
            {
                Interlocked.Increment(ref attempts);
                return Task.FromResult<OwnedResult?>(source);
            },
            static (_, _) => throw new InvalidOperationException("Injected diagnostic sink failure."),
            CancellationToken.None,
            static candidate => candidate.Dispose());

        Assert.Same(source, result);
        Assert.Equal(2, attempts);
        Assert.Equal(0, source.DisposeCount);
        source.Dispose();
    }

    private static TaskCompletionSource<OwnedResult?> NewProducer() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    [Fact]
    public async Task SourceFreeRetirementIsRegisteredBeforeTransportAndWaitsForLoserCleanup()
    {
        var primary = NewProducer();
        var winner = new OwnedResult();
        var loser = new OwnedResult();
        Task? retirement = null;
        int registrations = 0;
        OwnedResult? result = await MarkdownImageFallbackPipeline.FirstSuccessfulHedgedAsync(
            _ => { Assert.NotNull(retirement); return primary.Task; },
            _ => Task.FromResult<OwnedResult?>(winner), (_, _) => { },
            TimeSpan.Zero, CancellationToken.None, static value => value.Dispose(),
            completion => { registrations++; retirement = completion; });
        Assert.Same(winner, result);
        Assert.Equal(1, registrations);
        Assert.NotNull(retirement);
        Assert.False(retirement.IsCompleted);
        primary.SetResult(loser);
        await retirement.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, loser.DisposeCount);
        Assert.Equal(0, winner.DisposeCount);
        winner.Dispose();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RetirementRegistrationFailureDoesNotStartUntrackedTransport(bool useHedge)
    {
        bool producerStarted = false;
        Task? retirement = null;
        Func<CancellationToken, Task<OwnedResult?>> producer = _ =>
        {
            producerStarted = true;
            return Task.FromResult<OwnedResult?>(null);
        };
        Action<Task> registration = completion =>
        {
            retirement = completion;
            throw new InvalidOperationException("Injected lifetime registration failure.");
        };
        await Assert.ThrowsAsync<InvalidOperationException>(() => useHedge
            ? MarkdownImageFallbackPipeline.FirstSuccessfulHedgedAsync(
                producer, producer, (_, _) => { }, TimeSpan.Zero, CancellationToken.None,
                retirementObserver: registration)
            : MarkdownImageFallbackPipeline.FirstSuccessfulAsync(
                producer, producer, (_, _) => { }, CancellationToken.None,
                retirementObserver: registration));
        Assert.False(producerStarted);
        Assert.NotNull(retirement);
        Assert.True(retirement.IsCompletedSuccessfully);
    }

    private static async Task<OwnedResult?> ObserveOwnerAsync(
        Task<OwnedResult?> producer,
        TaskCompletionSource observed)
    {
        OwnedResult? result = await producer.ConfigureAwait(false);
        observed.TrySetResult();
        return result;
    }

    private sealed class OwnedResult : IDisposable
    {
        private int _disposeCount;
        internal int DisposeCount => Volatile.Read(ref _disposeCount);
        internal TaskCompletionSource Disposed { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Dispose()
        {
            Interlocked.Increment(ref _disposeCount);
            Disposed.TrySetResult();
        }
    }
}
