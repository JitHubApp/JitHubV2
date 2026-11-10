using System;
using System.Threading;
using System.Threading.Tasks;
using MarkdownRenderer.Images;
using MarkdownRenderer.Internal.Concurrency;

namespace MarkdownRenderer.Performance;

/// <summary>
/// One document-scoped request for an image source, shared by that
/// document's waiters for the same source until the last one leaves.
/// </summary>
internal sealed class DocumentImageRequest(
    RetirableCancellationTokenSource cancellation,
    bool background,
    MarkdownPerformanceSessionLifetime lifetime,
    IDisposable lifetimeWork)
{
    private readonly TaskCompletionSource<bool> _completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly ResolutionLeaseSlot _result = new();
    private readonly CancellationRetirementHandshake _cancellationRetirement = new(cancellation);
    private IDisposable? _lifetimeWork = lifetimeWork;
    internal RetirableCancellationTokenSource Cancellation { get; } = cancellation;
    internal bool Background { get; } = background;
    internal Task<bool> Task => _completion.Task;
    internal int WaiterCount { get; set; }

    internal void Start(
        Func<Task<IMarkdownImageResolutionLease>> operation,
        Action<IMarkdownImageResolutionLease, Task> onSuccess,
        Action<Exception, Task> onFailure)
    {
        Task completion = CompleteAsync(operation, onSuccess, onFailure);
        MarkdownRenderer.Internal.Concurrency.TaskFaultObservation.ObserveFault(completion);
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
            "document image request");
    }

    internal bool SetResult(IMarkdownImageResolutionLease result)
    {
        _result.SetRoot(result);
        return _completion.TrySetResult(true);
    }

    internal IMarkdownImageResolutionLease RetainResult() =>
        _result.TryRetain(out IMarkdownImageResolutionLease? retained)
            ? retained
            : throw new InvalidOperationException("The document image result was released before its waiter claimed it.");

    internal void ReleaseRootSafely() => _result.ReleaseRootSafely();

    internal void SetException(Exception exception)
    {
        if (exception is OperationCanceledException canceled)
            _completion.TrySetCanceled(canceled.CancellationToken);
        else
            _completion.TrySetException(exception);
    }

}
