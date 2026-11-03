using System;
using System.Threading;
using System.Threading.Tasks;

namespace MarkdownRenderer.Performance.ProgressiveScenes;

/// <summary>
/// One in-flight deferred-scene producer: its generation, linked cancellation
/// source and task. Disposal retires the source only after the task and any
/// cancellation callbacks have unwound.
/// </summary>
internal sealed class ActiveSceneWork(
    long generation,
    CancellationTokenSource cancellation)
{
    private readonly object _gate = new();
    private CancellationTokenSource? _cancellation = cancellation;
    private int _cancelRequested;
    private Task? _retirementTask;
    internal long Generation { get; } = generation;
    internal CancellationToken Token { get; } = cancellation.Token;
    internal Task Task { get; set; } = Task.CompletedTask;

    internal void Cancel()
    {
        CancellationTokenSource? source;
        lock (_gate)
            source = _cancellation;
        if (source is not null && Interlocked.Exchange(ref _cancelRequested, 1) == 0)
            _ = CancellationTokenSourceRetirement.RequestCancellation(source);
    }

    internal Task Dispose(Task? lifetime = null)
    {
        CancellationTokenSource? source;
        TaskCompletionSource? completion = null;
        lock (_gate)
        {
            if (_retirementTask is not null)
                return _retirementTask;

            source = _cancellation;
            _cancellation = null;
            if (source is null)
                return _retirementTask = Task.CompletedTask;

            completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _retirementTask = completion.Task;
        }

        Task? callbacks = Volatile.Read(ref _cancelRequested) != 0 || Token.IsCancellationRequested
            ? CancellationTokenSourceRetirement.RequestCancellation(source)
            : Task.CompletedTask;
        Task retirement = CancellationTokenSourceRetirement.DisposeAfter(source, callbacks, lifetime);
        _ = CompleteRetirementAsync(retirement, completion!);
        return completion!.Task;
    }

    private static async Task CompleteRetirementAsync(
        Task retirement,
        TaskCompletionSource completion)
    {
        try
        {
            await retirement.ConfigureAwait(false);
            completion.TrySetResult();
        }
        catch (Exception exception)
        {
            completion.TrySetException(exception);
        }
    }
}
