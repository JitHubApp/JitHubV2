using System;
using System.Threading;
using MarkdownRenderer.Diagnostics;
using MarkdownRenderer.Layout;
using MarkdownRenderer.Performance;
using Microsoft.UI.Dispatching;

namespace MarkdownRenderer.Controls;

public partial class MarkdownRendererControl
{
    private VectorCapacityWakeObserver? _vectorCapacityWakeObserver;
    private IDisposable? _vectorCapacityObserverRegistration;
    private LayoutSnapshot? _vectorCapacityObserverSnapshot;
    private LazyLayoutBand _lastVectorCapacityAttemptVisibleBand;
    private long _lastVectorCapacityAttemptGeneration = -1;
    private bool _hasVectorCapacityAttempt;

    private bool IsVisibleFirstVectorAdmissionEnabled =>
        PerformanceSession is IMarkdownPerformanceSessionInternal
        {
            IsDisposed: false,
            Options.DeferOffscreenScenes: true,
        };

    private static LazyLayoutBand IncludeBand(LazyLayoutBand realizationBand, LazyLayoutBand visibleVectorBand)
        => new(
            Math.Min(realizationBand.Top, visibleVectorBand.Top),
            Math.Max(realizationBand.Bottom, visibleVectorBand.Bottom));

    private static bool SameVisibleVectorBand(LazyLayoutBand left, LazyLayoutBand right)
        => left.Top == right.Top && left.Bottom == right.Bottom;

    private bool IsVectorCapacityRetryDue(
        LayoutSnapshot snapshot,
        LazyLayoutBand visibleVectorBand,
        bool forceCapacityRetry)
    {
        if (!IsVisibleFirstVectorAdmissionEnabled || !snapshot.HasCapacityDeferredScenes)
            return false;

        return forceCapacityRetry ||
            !_hasVectorCapacityAttempt ||
            !SameVisibleVectorBand(visibleVectorBand, _lastVectorCapacityAttemptVisibleBand) ||
            snapshot.ActiveCapacityGeneration > _lastVectorCapacityAttemptGeneration ||
            VectorNativeResourceAdmission.Shared.CapacityGeneration > _lastVectorCapacityAttemptGeneration;
    }

    /// <summary>
    /// Keeps one weak ledger subscription only while this current, opted-in
    /// snapshot has scenes deferred specifically by aggregate native capacity.
    /// The generation check brackets registration so a refund cannot be lost
    /// between observing fallback and subscribing.
    /// </summary>
    private void SynchronizeVectorCapacityObserver(LayoutSnapshot? snapshot)
    {
        if (_isDisposed || _isUnloaded || snapshot is null ||
            !ReferenceEquals(snapshot, _snapshot) ||
            !IsVisibleFirstVectorAdmissionEnabled ||
            !snapshot.HasCapacityDeferredScenes)
        {
            ResetVectorCapacityObserver();
            return;
        }

        if (!ReferenceEquals(snapshot, _vectorCapacityObserverSnapshot))
        {
            ResetVectorCapacityObserver();
            _vectorCapacityObserverSnapshot = snapshot;
        }

        long generationBeforeRegistration = snapshot.ActiveCapacityGeneration;
        if (_vectorCapacityObserverRegistration is null)
        {
            DispatcherQueue? dispatcher = DispatcherQueue;
            if (dispatcher is null)
                return;

            var observer = new VectorCapacityWakeObserver(this, dispatcher);
            IDisposable registration;
            try
            {
                registration = VectorNativeResourceAdmission.Shared.RegisterCapacityObserver(observer);
            }
            catch (Exception exception)
            {
                observer.Deactivate();
                MarkdownDiagnostics.WriteLine(
                    $"[MarkdownRendererControl] vector capacity observer registration failed: " +
                    $"{exception.GetType().Name} (0x{exception.HResult:X8}).");
                return;
            }

            // Publish the strong owner fields only after the weak ledger entry
            // exists. A racing callback can already enqueue through observer,
            // whose owner edge back to this control is weak.
            _vectorCapacityWakeObserver = observer;
            _vectorCapacityObserverRegistration = registration;
        }

        long generationAfterRegistration = VectorNativeResourceAdmission.Shared.CapacityGeneration;
        if (generationAfterRegistration > generationBeforeRegistration)
            _vectorCapacityWakeObserver?.OnCapacityAvailable(generationAfterRegistration);
    }

    private bool ConsumePendingVectorCapacityWake(LayoutSnapshot snapshot)
    {
        if (!ReferenceEquals(snapshot, _snapshot) ||
            !ReferenceEquals(snapshot, _vectorCapacityObserverSnapshot) ||
            _vectorCapacityWakeObserver is not { } observer)
        {
            return false;
        }

        long generation = observer.ConsumePendingGeneration();
        return generation >= 0 &&
            generation > _lastVectorCapacityAttemptGeneration &&
            snapshot.HasCapacityDeferredScenes;
    }

    private void ProcessVectorCapacityWake(VectorCapacityWakeObserver observer)
    {
        if (!ReferenceEquals(observer, _vectorCapacityWakeObserver))
            return;

        long generation = observer.ConsumePendingGeneration();
        LayoutSnapshot? snapshot = _snapshot;
        if (generation < 0 || _isDisposed || _isUnloaded ||
            snapshot is null || !ReferenceEquals(snapshot, _vectorCapacityObserverSnapshot) ||
            RequestGeometryRecoveryIfRequired(snapshot))
        {
            return;
        }

        if (!snapshot.HasCapacityDeferredScenes || !IsVisibleFirstVectorAdmissionEnabled)
        {
            ResetVectorCapacityObserver();
            return;
        }

        if (generation <= _lastVectorCapacityAttemptGeneration &&
            SameVisibleVectorBand(_lastVectorCapacityAttemptVisibleBand, GetCurrentVisibleVectorBand()))
        {
            return;
        }

        // Re-read current scroll/ancestor viewport and the current snapshot on
        // the UI thread. Never reuse the snapshot or band that existed when the
        // worker refunded a native credit.
        QueueLazyLayoutForViewport(forceCapacityRetry: true);
    }

    private LazyLayoutBand GetCurrentVisibleVectorBand()
    {
        TryGetViewport(out double top, out double height, out _);
        return LazyLayoutBand.FromVisibleViewport(top, height);
    }

    private void ResetVectorCapacityObserver()
    {
        VectorCapacityWakeObserver? observer = _vectorCapacityWakeObserver;
        IDisposable? registration = _vectorCapacityObserverRegistration;
        _vectorCapacityWakeObserver = null;
        _vectorCapacityObserverRegistration = null;
        _vectorCapacityObserverSnapshot = null;
        _hasVectorCapacityAttempt = false;
        _lastVectorCapacityAttemptGeneration = -1;
        _lastVectorCapacityAttemptVisibleBand = default;

        // Close the weak target before unsubscribing so an already copied
        // ledger callback cannot schedule stale work for a replacement tree.
        observer?.Deactivate();
        try { registration?.Dispose(); }
        catch (Exception exception)
        {
            MarkdownDiagnostics.WriteLine(
                $"[MarkdownRendererControl] vector capacity observer disposal failed: " +
                $"{exception.GetType().Name} (0x{exception.HResult:X8}).");
        }
    }

    private sealed class VectorCapacityWakeObserver : IVectorNativeCapacityObserver
    {
        private readonly WeakReference<MarkdownRendererControl> _owner;
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
            MarkdownRendererControl owner,
            DispatcherQueue dispatcher)
        {
            _owner = new WeakReference<MarkdownRendererControl>(owner);
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
                            owner.TryGetTarget(out MarkdownRendererControl? control) &&
                            observer.TryGetTarget(out currentObserver))
                        {
                            control.ProcessVectorCapacityWake(currentObserver);
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
            // If the owning control has gone away without a normal Unloaded
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
}
