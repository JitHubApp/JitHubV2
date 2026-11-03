using System;
using System.Threading;
using System.Threading.Tasks;
using MarkdownRenderer.Images;

namespace MarkdownRenderer.Performance;

/// <summary>
/// One session-wide fetch of an image source, shared by every document
/// request for the same key until its last waiter leaves.
/// </summary>
internal sealed class SharedImageFetch(
    CancellationTokenSource cancellation,
    bool background,
    MarkdownPerformanceSessionLifetime lifetime,
    IDisposable lifetimeWork)
{
    private readonly TaskCompletionSource<bool> _completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly ResolutionLeaseSlot _result = new();
    private readonly CancellationRetirementHandshake _cancellationRetirement = new(cancellation);
    private int _resolutionStarted;
    private IDisposable? _lifetimeWork = lifetimeWork;
    internal CancellationTokenSource Cancellation { get; } = cancellation;
    internal bool Background { get; } = background;
    internal Task<bool> Task => _completion.Task;
    internal int WaiterCount { get; set; }
    internal bool HasStartedResolution => Volatile.Read(ref _resolutionStarted) != 0;
    internal void MarkResolutionStarted() => Volatile.Write(ref _resolutionStarted, 1);

    internal void Start(
        Func<Task<IMarkdownImageResolutionLease>> operation,
        Action<IMarkdownImageResolutionLease, Task> onSuccess,
        Action<Exception, Task> onFailure)
    {
        Task completion = CompleteAsync(operation, onSuccess, onFailure);
        MarkdownRenderer.TaskFaultObservation.ObserveFault(completion);
    }

    private static async Task CompleteAsync(
        Func<Task<IMarkdownImageResolutionLease>> operation,
        Action<IMarkdownImageResolutionLease, Task> onSuccess,
        Action<Exception, Task> onFailure)
    {
        Task<IMarkdownImageResolutionLease>? operationCompletion = null;
        try
        {
            operationCompletion = operation();
            onSuccess(
                await operationCompletion.ConfigureAwait(false),
                operationCompletion);
        }
        catch (Exception exception)
        {
            onFailure(exception, operationCompletion ?? System.Threading.Tasks.Task.CompletedTask);
        }
    }

    internal void RequestCancellation()
        => _cancellationRetirement.RequestCancellation();

    internal void RetireCancellation(Task operationCompletion)
    {
        Task? callbacks = _cancellationRetirement.BeginRetirement();
        IDisposable? lifetimeWorkLease = Interlocked.Exchange(ref _lifetimeWork, null);
        if (lifetimeWorkLease is null)
            return;

        lifetime.RetireCancellationSource(
            Cancellation,
            callbacks,
            operationCompletion,
            lifetimeWorkLease,
            "shared image fetch");
    }

    internal bool SetResult(IMarkdownImageResolutionLease result)
    {
        _result.SetRoot(result);
        return _completion.TrySetResult(true);
    }

    internal IMarkdownImageResolutionLease RetainResult() =>
        _result.TryRetain(out IMarkdownImageResolutionLease? retained)
            ? retained
            : throw new InvalidOperationException("The shared image result was released before its waiter claimed it.");

    internal void ReleaseRootSafely() => _result.ReleaseRootSafely();

    internal void SetException(Exception exception)
    {
        if (exception is OperationCanceledException canceled)
            _completion.TrySetCanceled(canceled.CancellationToken);
        else
            _completion.TrySetException(exception);
    }
}
