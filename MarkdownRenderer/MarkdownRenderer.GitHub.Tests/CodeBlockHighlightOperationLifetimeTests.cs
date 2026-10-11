using System.Threading;
using System.Threading.Tasks;
using MarkdownRenderer.CodeBlocks;
using MarkdownRenderer.Hosting;
using Xunit;

namespace MarkdownRenderer.GitHub.Tests;

public sealed class CodeBlockHighlightOperationLifetimeTests
{
    [Fact]
    public async Task ReservationIsVisibleToReentrantRetirementAndWaitsForOperation()
    {
        var tracked = new List<Task>();
        var operation = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task? reentrantRetirement = null;

        CodeBlockHighlightOperationLifetime.StartAndTrack(
            tracked.Add,
            () =>
            {
                // Model a synchronous provider callback that unloads/replaces
                // the owner before returning its operation Task.
                Assert.Single(tracked);
                reentrantRetirement = Task.WhenAll(tracked.ToArray());
                return operation.Task;
            });

        Assert.NotNull(reentrantRetirement);
        Assert.False(reentrantRetirement!.IsCompleted);

        operation.SetResult();
        await reentrantRetirement;
        Assert.True(tracked[0].IsCompletedSuccessfully);
    }

    [Fact]
    public async Task SynchronousStartFailureCompletesTheReservedLifetimeAndPropagates()
    {
        var tracked = new List<Task>();

        Assert.Throws<InvalidOperationException>(() =>
            CodeBlockHighlightOperationLifetime.StartAndTrack(
                tracked.Add,
                () => throw new InvalidOperationException("provider entry failed")));

        Task reservation = Assert.Single(tracked);
        await reservation;
        Assert.True(reservation.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task SynchronousRegistrationFailureDoesNotStartOrStrandAnOperation()
    {
        Task? reservation = null;
        bool startInvoked = false;

        Assert.Throws<InvalidOperationException>(() =>
            CodeBlockHighlightOperationLifetime.StartAndTrack(
                task =>
                {
                    reservation = task;
                    throw new InvalidOperationException("registration failed");
                },
                () =>
                {
                    startInvoked = true;
                    return Task.CompletedTask;
                }));

        Assert.False(startInvoked);
        Assert.NotNull(reservation);
        await reservation!;
        Assert.True(reservation!.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task FaultedAndCanceledOperationsDoNotStrandRetirement()
    {
        var faultedOperation = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var faultTracked = new List<Task>();
        CodeBlockHighlightOperationLifetime.StartAndTrack(faultTracked.Add, () => faultedOperation.Task);

        faultedOperation.SetException(new InvalidOperationException("provider failed"));
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await faultedOperation.Task);
        await Assert.Single(faultTracked);

        var canceledOperation = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var canceledTracked = new List<Task>();
        CodeBlockHighlightOperationLifetime.StartAndTrack(canceledTracked.Add, () => canceledOperation.Task);

        canceledOperation.SetCanceled();
        await Assert.Single(canceledTracked);
        Assert.True(canceledOperation.Task.IsCanceled);
    }

    [Fact]
    public void CapturedTokenRemainsSafeAfterCancellationSourceRetirement()
    {
        var source = new CancellationTokenSource();
        Assert.True(CodeBlockHighlightOperationLifetime.TryCaptureToken(source, out CancellationToken token));

        source.Cancel();
        source.Dispose();

        Assert.True(token.IsCancellationRequested);

        var alreadyDisposed = new CancellationTokenSource();
        alreadyDisposed.Dispose();
        Assert.False(CodeBlockHighlightOperationLifetime.TryCaptureToken(alreadyDisposed, out _));
    }

    [Fact]
    public void RevisionCallbackMustPassTheGenerationFenceWithoutASecondRead()
    {
        int generation = 7;
        var original = new TestHighlighter(9, () => generation++);
        using var lifetime = new CancellationTokenSource();
        Assert.True(CodeBlockHighlightOperationLifetime.TryCaptureToken(lifetime, out CancellationToken token));
        int requestGeneration = generation;

        int observedRevision = original.Revision;

        Assert.False(CodeBlockHighlightOperationLifetime.IsCurrentAfterRevisionCallback(
            original,
            original,
            generation,
            requestGeneration,
            isDisposed: false,
            isUnloaded: false,
            operationToken: token,
            observedRevision: observedRevision,
            requestRevision: 9));
        Assert.Equal(1, original.RevisionReadCount);
    }

    [Fact]
    public void RevisionCallbackMustPassTheProviderIdentityFence()
    {
        ICodeHighlighter? currentProvider = null;
        var replacement = new TestHighlighter(9);
        var original = new TestHighlighter(9, () => currentProvider = replacement);
        currentProvider = original;
        using var lifetime = new CancellationTokenSource();
        Assert.True(CodeBlockHighlightOperationLifetime.TryCaptureToken(lifetime, out CancellationToken token));

        int observedRevision = original.Revision;

        Assert.False(CodeBlockHighlightOperationLifetime.IsCurrentAfterRevisionCallback(
            currentProvider,
            original,
            currentGeneration: 7,
            requestGeneration: 7,
            isDisposed: false,
            isUnloaded: false,
            operationToken: token,
            observedRevision: observedRevision,
            requestRevision: 9));
        Assert.Equal(1, original.RevisionReadCount);
    }

#pragma warning disable CS0618 // The stable provider interface inherits this compatibility contract.
    private sealed class TestHighlighter : ICodeHighlighter
    {
        private readonly int _revision;
        private readonly Action? _onRevisionRead;
        private int _revisionReadCount;

        internal TestHighlighter(int revision, Action? onRevisionRead = null)
        {
            _revision = revision;
            _onRevisionRead = onRevisionRead;
        }

        public int Revision
        {
            get
            {
                Interlocked.Increment(ref _revisionReadCount);
                _onRevisionRead?.Invoke();
                return _revision;
            }
        }

        internal int RevisionReadCount => Volatile.Read(ref _revisionReadCount);

        public ValueTask<CodeBlockHighlightResult?> HighlightAsync(
            CodeBlockHighlightRequest request,
            CancellationToken cancellationToken)
            => ValueTask.FromResult<CodeBlockHighlightResult?>(CodeBlockHighlightResult.Empty);

        ValueTask<CodeBlockHighlightResult?> ICodeBlockSyntaxHighlighter.HighlightAsync(
            CodeBlockHighlightRequest request)
            => HighlightAsync(request, request.CancellationToken);
    }
#pragma warning restore CS0618
}
