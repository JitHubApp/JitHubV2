using System;
using System.Threading;
using System.Threading.Tasks;
using MarkdownRenderer.Diagnostics.Internal;
using MarkdownRenderer.Internal.Concurrency;

namespace MarkdownRenderer.Performance.Internal.Session;

/// <summary>
/// Owns a performance session's disposal flag, admitted-work drain, lifetime
/// cancellation source and cancellation-source retirement. Every session
/// owner registers its work here so shutdown can wait for all of it.
/// </summary>
internal sealed class MarkdownPerformanceSessionLifetime
{
    private readonly object _gate = new();
    private readonly RetirableCancellationTokenSource _lifetime = new();
    private TaskCompletionSource<bool>? _workDrained;
    private TaskCompletionSource<bool>? _shutdownCompleted;
    private EventHandler? _disposedHandlers;
    private int _activeWork;
    private int _disposed;
    private long _cancellationRetirementFailures;

    internal MarkdownPerformanceSessionLifetime()
    {
        Token = _lifetime.Token;
    }

    internal CancellationToken Token { get; }

    internal bool IsDisposed => Volatile.Read(ref _disposed) != 0;

    /// <summary>Admitted work that shutdown still waits for.</summary>
    internal int ActiveWork
    {
        get
        {
            lock (_gate)
                return _activeWork;
        }
    }

    // Internal evidence for cancellation callback failures. The callbacks are
    // always observed during CTS retirement; this counter lets focused tests
    // verify that failures were not abandoned with a discarded CancelAsync task.
    internal long CancellationRetirementFailures =>
        Interlocked.Read(ref _cancellationRetirementFailures);

    /// <summary>
    /// Adds a disposal observer. Returns false when the session is already
    /// disposed; the caller then notifies the observer itself.
    /// </summary>
    internal bool TryAddDisposedHandler(EventHandler? value)
    {
        lock (_gate)
        {
            if (_disposed != 0)
                return false;
            _disposedHandlers += value;
            return true;
        }
    }

    internal void RemoveDisposedHandler(EventHandler? value)
    {
        lock (_gate)
            _disposedHandlers -= value;
    }

    /// <summary>
    /// Runs <paramref name="create"/> while holding the disposal gate, so no
    /// object is created after shutdown has started.
    /// </summary>
    internal TResult CreateWhileActive<TState, TResult>(
        object owner,
        TState state,
        Func<TState, TResult> create)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed != 0, owner);
            return create(state);
        }
    }

    /// <summary>
    /// Marks the session disposed and arms the work drain. Returns false when
    /// shutdown already started.
    /// </summary>
    internal bool TryBeginShutdown(
        out Task drain,
        out TaskCompletionSource<bool> completed,
        out EventHandler? disposedHandlers)
    {
        lock (_gate)
        {
            if (_disposed != 0)
            {
                drain = Task.CompletedTask;
                completed = null!;
                disposedHandlers = null;
                return false;
            }
            _disposed = 1;
            disposedHandlers = _disposedHandlers;
            _disposedHandlers = null;
            _workDrained = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (_activeWork == 0)
                _workDrained.SetResult(true);
            drain = _workDrained.Task;
            completed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _shutdownCompleted = completed;
            return true;
        }
    }

    internal Task? ShutdownCompletion
    {
        get
        {
            lock (_gate)
                return _shutdownCompleted?.Task;
        }
    }

    internal Task CancelAsync() => _lifetime.CancelAsync();

    internal void DisposeCancellation() => _lifetime.Dispose();

    internal void RegisterWork()
    {
        if (!TryRegisterWork())
            throw new OperationCanceledException(Token);
    }

    internal SessionWorkLease? TryBeginWorkLease()
    {
        // Allocate first so even an allocation failure cannot strand the
        // active-work count. The returned lease is idempotent.
        SessionWorkLease lease = new(this);
        return TryRegisterWork() ? lease : null;
    }

    internal bool TryRegisterWork()
    {
        lock (_gate)
        {
            if (_disposed != 0)
                return false;
            _activeWork++;
            return true;
        }
    }

    internal void RetireWork()
    {
        lock (_gate)
        {
            if (--_activeWork == 0)
                _workDrained?.TrySetResult(true);
        }
    }

    internal void RetireCancellationSource(
        RetirableCancellationTokenSource cancellation,
        Task? cancellationCallbacks,
        Task operationCompletion,
        IDisposable lifetimeWork,
        string owner)
    {
        Task callbacks = cancellationCallbacks ??
            (cancellation.IsCancellationRequested
                ? CancellationTokenSourceRetirement.RequestCancellation(cancellation)
                : Task.CompletedTask);
        Task retirement;
        try
        {
            // The caller invokes this only after the operation task has
            // completed. Pass that task explicitly as an additional retirement
            // guard so disposal remains tied to both resolver and token work.
            retirement = CancellationTokenSourceRetirement.DisposeAfter(
                cancellation, callbacks, operationCompletion);
        }
        catch (Exception exception)
        {
            RecordCancellationRetirementFailure(owner, exception);
            try { cancellation.Dispose(); }
            catch (Exception disposeException)
            {
                RecordCancellationRetirementFailure(owner, disposeException);
            }
            finally
            {
                try { lifetimeWork.Dispose(); }
                catch (Exception releaseException)
                {
                    RecordCancellationRetirementFailure(owner, releaseException);
                }
            }
            return;
        }

        _ = FinishCancellationRetirementAsync(callbacks, retirement, lifetimeWork, owner);
    }

    private async Task FinishCancellationRetirementAsync(
        Task callbacks,
        Task retirement,
        IDisposable lifetimeWork,
        string owner)
    {
        try
        {
            // DisposeAfter observes these inputs too, but awaiting the callback
            // task here preserves its exception for the session's diagnostics.
            await callbacks.ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            RecordCancellationRetirementFailure(owner, exception);
        }

        try
        {
            await retirement.ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            RecordCancellationRetirementFailure(owner, exception);
        }
        finally
        {
            try { lifetimeWork.Dispose(); }
            catch (Exception exception)
            {
                RecordCancellationRetirementFailure(owner, exception);
            }
        }
    }

    private void RecordCancellationRetirementFailure(string owner, Exception exception)
    {
        Interlocked.Increment(ref _cancellationRetirementFailures);
        try
        {
            MarkdownDiagnostics.WriteLine(
                $"[MarkdownPerformanceSession] {owner} cancellation retirement failed: {exception.GetType().Name}");
        }
        catch
        {
        }
    }
}
