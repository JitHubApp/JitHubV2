using System;
using System.Threading;

namespace MarkdownRenderer.Controls;

/// <summary>
/// Periodically offers a coalesced recovery callback to an owning dispatcher
/// without keeping the dispatcher target alive. The enqueue delegate should
/// capture only the dispatcher, and the recovery delegate should be static or
/// otherwise not capture <typeparamref name="T"/>.
/// </summary>
internal sealed class WeakRecoveryTimer<T> : IDisposable where T : class
{
    private readonly object _sync = new();
    private readonly WeakReference<T> _target;
    private readonly Action _queuedCallback;
    private Timer? _timer;
    private Func<Action, bool>? _enqueue;
    private Action<T>? _recover;
    private bool _dispatchPending;
    private bool _disposed;

    internal WeakRecoveryTimer(
        T target,
        Func<Action, bool> enqueue,
        Action<T> recover,
        TimeSpan interval)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(enqueue);
        ArgumentNullException.ThrowIfNull(recover);
        if (interval <= TimeSpan.Zero || interval == Timeout.InfiniteTimeSpan)
            throw new ArgumentOutOfRangeException(nameof(interval));

        _target = new WeakReference<T>(target);
        _enqueue = enqueue;
        _recover = recover;
        _queuedCallback = RunQueuedCallback;
        _timer = new Timer(static state => ((WeakRecoveryTimer<T>)state!).Pulse(), this, interval, interval);
    }

    internal bool IsDisposed
    {
        get { lock (_sync) return _disposed; }
    }

    /// <summary>Runs one timer tick synchronously. Intended for deterministic tests.</summary>
    internal void Pulse()
    {
        Func<Action, bool>? enqueue;
        Timer? timerToDispose = null;
        lock (_sync)
        {
            if (_disposed)
                return;

            // Check liveness even while a dispatcher callback is outstanding,
            // so a withheld callback cannot keep a dead owner registered forever.
            if (!TryGetLiveTarget(out _))
            {
                timerToDispose = MarkDisposedUnderLock();
                enqueue = null;
            }
            else if (_dispatchPending)
            {
                return;
            }
            else
            {
                _dispatchPending = true;
                enqueue = _enqueue;
            }
        }

        timerToDispose?.Dispose();
        if (enqueue is null)
            return;

        bool accepted;
        try
        {
            // Dispatcher enqueue APIs are asynchronous: they retain this
            // cached action but do not invoke it inline.
            accepted = enqueue(_queuedCallback);
        }
        catch
        {
            ResetPending();
            return;
        }

        if (!accepted)
            ResetPending();
    }

    public void Dispose()
    {
        Timer? timerToDispose;
        lock (_sync)
        {
            if (_disposed)
                return;
            timerToDispose = MarkDisposedUnderLock();
        }

        timerToDispose?.Dispose();
    }

    private void RunQueuedCallback()
    {
        T? target;
        Action<T>? recover;
        Timer? timerToDispose = null;
        lock (_sync)
        {
            if (_disposed || !_dispatchPending)
                return;

            if (!TryGetLiveTarget(out target))
            {
                timerToDispose = MarkDisposedUnderLock();
                recover = null;
            }
            else
            {
                recover = _recover;
            }
        }

        timerToDispose?.Dispose();
        if (target is null || recover is null)
            return;

        try
        {
            recover(target);
        }
        catch
        {
            // Recovery is best-effort. A failed callback must not escape onto
            // the dispatcher thread or prevent a later timer tick from retrying.
        }
        finally
        {
            ResetPending();
        }
    }

    private bool TryGetLiveTarget(out T? target) => _target.TryGetTarget(out target);

    private void ResetPending()
    {
        lock (_sync)
        {
            if (!_disposed)
                _dispatchPending = false;
        }
    }

    // Caller must hold _sync.
    private Timer? MarkDisposedUnderLock()
    {
        _disposed = true;
        _dispatchPending = false;
        _enqueue = null;
        _recover = null;
        Timer? timer = _timer;
        _timer = null;
        return timer;
    }
}
