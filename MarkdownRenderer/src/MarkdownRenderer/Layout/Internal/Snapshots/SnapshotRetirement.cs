using System;
using System.Threading;
using System.Threading.Tasks;

namespace MarkdownRenderer.Layout.Internal.Snapshots;

/// <summary>
/// Retirement and disposal lifecycle of a snapshot. Retirement rejects new
/// read and paint leases immediately and disposes native resources on the
/// thread pool under the layout gate, so it never waits behind an in-flight
/// background measure. The completion task surfaces disposal failures.
/// </summary>
internal sealed class SnapshotRetirement
{
    private readonly object _gate;
    private readonly ILayoutSnapshotRetirementHost _host;
    private TaskCompletionSource? _retirementCompletion;
    private int _retirementRequested;
    private int _disposed;

    internal SnapshotRetirement(object gate, ILayoutSnapshotRetirementHost host)
    {
        _gate = gate;
        _host = host;
    }

    internal bool IsRetirementRequested => Volatile.Read(ref _retirementRequested) != 0;

    internal bool IsDisposed => Volatile.Read(ref _disposed) != 0;

    internal bool IsRetiringOrDisposed => IsRetirementRequested || IsDisposed;

    internal void Dispose()
    {
        Interlocked.Exchange(ref _retirementRequested, 1);
        TaskCompletionSource completion = GetRetirementCompletion();
        bool ownsDisposal = false;
        Exception? failure = null;
        Task[] pendingRetirementTasks = Array.Empty<Task>();
        try
        {
            lock (_gate)
            {
                DisposeCoreNoLock(ref ownsDisposal, out pendingRetirementTasks);
            }
        }
        catch (Exception exception)
        {
            failure = exception;
            throw;
        }
        finally
        {
            if (ownsDisposal)
            {
                CompleteRetirementAfterDrain(
                    pendingRetirementTasks,
                    failure,
                    completion);
            }
        }
    }

    /// <summary>
    /// Retires a snapshot without waiting for an in-flight background measure
    /// to release the layout lock. Snapshot replacement and control teardown
    /// call this from the UI thread; final native-resource disposal always runs
    /// on the pool and waits for any active measure there.
    /// </summary>
    internal Task Retire()
    {
        TaskCompletionSource completion = GetRetirementCompletion();
        if (Interlocked.Exchange(ref _retirementRequested, 1) != 0)
        {
            return completion.Task;
        }

        ThreadPool.UnsafeQueueUserWorkItem(
            static retirement => retirement.DisposeRetiredSnapshot(),
            this,
            preferLocal: false);
        return completion.Task;
    }

    private void DisposeRetiredSnapshot()
    {
        TaskCompletionSource completion = GetRetirementCompletion();
        bool ownsDisposal = false;
        Exception? failure = null;
        Task[] pendingRetirementTasks = Array.Empty<Task>();
        try
        {
            lock (_gate)
            {
                DisposeCoreNoLock(ref ownsDisposal, out pendingRetirementTasks);
            }
        }
        catch (Exception exception)
        {
            // Retirement runs on the pool, where rethrowing would terminate the
            // process. Preserve the failure on the completion task so internal
            // drainers can surface it deterministically.
            failure = exception;
        }
        finally
        {
            if (ownsDisposal)
            {
                CompleteRetirementAfterDrain(
                    pendingRetirementTasks,
                    failure,
                    completion);
            }
        }
    }

    private TaskCompletionSource GetRetirementCompletion()
    {
        TaskCompletionSource? completion = Volatile.Read(ref _retirementCompletion);
        if (completion is not null)
            return completion;

        var candidate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        return Interlocked.CompareExchange(ref _retirementCompletion, candidate, null) ?? candidate;
    }

    private static void CompleteRetirementAfterDrain(
        Task[] pendingRetirementTasks,
        Exception? failure,
        TaskCompletionSource completion)
    {
        if (pendingRetirementTasks.Length == 0)
        {
            CompleteRetirement(completion, failure);
            return;
        }

        _ = CompleteRetirementAfterDrainAsync(
            pendingRetirementTasks,
            failure,
            completion);
    }

    private static async Task CompleteRetirementAfterDrainAsync(
        Task[] pendingRetirementTasks,
        Exception? failure,
        TaskCompletionSource completion)
    {
        try
        {
            await Task.WhenAll(pendingRetirementTasks).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            failure = failure is null
                ? exception
                : new AggregateException(
                    "Snapshot disposal and native-work retirement both failed.",
                    failure,
                    exception);
        }

        CompleteRetirement(completion, failure);
    }

    private static void CompleteRetirement(
        TaskCompletionSource completion,
        Exception? failure)
    {
        if (failure is null)
            completion.TrySetResult();
        else
            completion.TrySetException(failure);
    }

    private void DisposeCoreNoLock(
        ref bool ownsDisposal,
        out Task[] pendingRetirementTasks)
    {
        pendingRetirementTasks = Array.Empty<Task>();
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        ownsDisposal = true;
        _host.DisposeResourcesNoLock(out pendingRetirementTasks);
    }
}
