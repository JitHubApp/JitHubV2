using System;
using System.Threading;
using Microsoft.UI.Dispatching;
using MarkdownRenderer.Layout.Internal.Vectors.Drawing;

namespace MarkdownRenderer.Controls.Internal.LazyLayout;

/// <summary>
/// The weak ledger subscription that wakes a renderer when aggregate vector
/// native capacity is refunded. It hands the newest refunded generation to
/// the UI thread, and retries with a dormant backoff clock only when the
/// dispatcher rejects that handoff.
/// </summary>
internal sealed class VectorCapacityWakeObserver : IVectorNativeCapacityObserver
{
    private readonly WeakReference<VectorCapacityRetry> _owner;
    private readonly WeakReference<VectorCapacityWakeObserver> _self;
    private readonly DispatcherQueue _dispatcher;
    private readonly Timer _retryTimer;
    private long _pendingGeneration = -1;
    private long _nextDispatchTicket;
    private long _activeDispatchTicket;
    private int _retryAttempt;
    private int _dispatchQueued;
    private int _deactivated;

    internal VectorCapacityWakeObserver(
        VectorCapacityRetry owner,
        DispatcherQueue dispatcher)
    {
        _owner = new WeakReference<VectorCapacityRetry>(owner);
        _self = new WeakReference<VectorCapacityWakeObserver>(this);
        _dispatcher = dispatcher;
        // Allocate the sole retry clock on the UI registration path, but
        // leave it dormant unless the dispatcher rejects a capacity wake.
        _retryTimer = new Timer(
            static state => ((VectorCapacityWakeObserver)state!).OnRetryClock(),
            this,
            Timeout.Infinite,
            Timeout.Infinite);
    }

    public void OnCapacityAvailable(long generation)
    {
        if (Volatile.Read(ref _deactivated) != 0)
            return;

        while (true)
        {
            long current = Interlocked.Read(ref _pendingGeneration);
            if (current >= generation ||
                Interlocked.CompareExchange(ref _pendingGeneration, generation, current) == current)
            {
                break;
            }
        }

        TryEnqueueWake();
    }

    internal long ConsumePendingGeneration()
    {
        // A viewport/paint path may consume a wake before its queued
        // DispatcherQueue callback runs. Invalidate that callback ticket
        // so it cannot clear or steal a newer wake.
        AcknowledgeWakeDispatch(Interlocked.Read(ref _activeDispatchTicket));
        return Interlocked.Exchange(ref _pendingGeneration, -1);
    }

    internal void Deactivate()
    {
        Interlocked.Exchange(ref _deactivated, 1);
        Interlocked.Exchange(ref _pendingGeneration, -1);
        Interlocked.Exchange(ref _activeDispatchTicket, 0);
        Interlocked.Exchange(ref _dispatchQueued, 0);
        try { _retryTimer.Dispose(); }
        catch (Exception) { }
    }

    private void TryEnqueueWake()
    {
        if (Volatile.Read(ref _deactivated) != 0 ||
            Interlocked.Exchange(ref _dispatchQueued, 1) != 0)
        {
            return;
        }

        long ticket = Interlocked.Increment(ref _nextDispatchTicket);
        Volatile.Write(ref _activeDispatchTicket, ticket);
        var owner = _owner;
        var observer = _self;
        try
        {
            if (!_dispatcher.TryEnqueue(DispatcherQueuePriority.Low, () =>
                {
                    if (observer.TryGetTarget(out VectorCapacityWakeObserver? currentObserver) &&
                        currentObserver.AcknowledgeWakeDispatch(ticket) &&
                        owner.TryGetTarget(out VectorCapacityRetry? retry) &&
                        observer.TryGetTarget(out currentObserver))
                    {
                        retry.ProcessWake(currentObserver);
                    }
                }))
            {
                // Keep the generation pending. The next viewport/paint
                // scheduler call or this dormant one-shot clock can retry.
                if (AcknowledgeWakeDispatch(ticket, resetRetryAttempt: false))
                    ArmRetryClock();
            }
        }
        catch (Exception)
        {
            if (AcknowledgeWakeDispatch(ticket, resetRetryAttempt: false))
                ArmRetryClock();
        }
    }

    private bool AcknowledgeWakeDispatch(long ticket, bool resetRetryAttempt = true)
    {
        if (ticket == 0 || Interlocked.CompareExchange(ref _activeDispatchTicket, 0, ticket) != ticket)
            return false;

        Volatile.Write(ref _dispatchQueued, 0);
        if (resetRetryAttempt)
        {
            Interlocked.Exchange(ref _retryAttempt, 0);
            StopRetryClock();
        }
        return true;
    }

    private void OnRetryClock()
    {
        if (Volatile.Read(ref _deactivated) != 0)
            return;

        // A rejected-dispatch timer temporarily roots its callback state.
        // If the owning renderer has gone away without a normal Unloaded
        // transition, break that root rather than retrying forever.
        if (!_owner.TryGetTarget(out _))
        {
            Deactivate();
            return;
        }

        if (Interlocked.Read(ref _pendingGeneration) < 0)
            return;

        TryEnqueueWake();
    }

    private void ArmRetryClock()
    {
        if (Volatile.Read(ref _deactivated) != 0)
            return;

        int attempt = Math.Min(Interlocked.Increment(ref _retryAttempt), 5);
        int delayMilliseconds = Math.Min(1_600, 100 << (attempt - 1));
        try { _retryTimer.Change(delayMilliseconds, Timeout.Infinite); }
        catch (Exception) { /* The pending generation remains available to later viewport work. */ }
    }

    private void StopRetryClock()
    {
        try { _retryTimer.Change(Timeout.Infinite, Timeout.Infinite); }
        catch (Exception) { }
    }
}
