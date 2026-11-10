using System;
using MarkdownRenderer.Diagnostics.Internal;
using MarkdownRenderer.Layout.Internal.LazyLayout;
using MarkdownRenderer.Layout.Internal.Snapshots;
using MarkdownRenderer.Layout.Internal.Vectors.Drawing;
using MarkdownRenderer.Performance;
using Microsoft.UI.Dispatching;

namespace MarkdownRenderer.Controls.LazyLayout;

/// <summary>
/// Retries visible vector scenes that were deferred for lack of aggregate
/// native capacity. While the published snapshot has such scenes, one weak
/// ledger subscription wakes lazy layout when capacity is refunded, and the
/// last completed attempt decides whether a viewport change still needs one.
/// </summary>
internal sealed class VectorCapacityRetry
{
    private readonly IVectorCapacityRetryHost _host;
    private readonly MarkdownViewLifetime _lifetime;
    private readonly PublishedLayout _published;
    private readonly MarkdownViewport _viewport;

    private VectorCapacityWakeObserver? _observer;
    private IDisposable? _registration;
    private VectorNativeResourceAdmission? _registrationAdmission;
    private LayoutSnapshot? _observerSnapshot;
    private LazyLayoutBand _lastAttemptVisibleBand;
    private long _lastAttemptGeneration = -1;
    private bool _hasAttempt;

    internal VectorCapacityRetry(
        IVectorCapacityRetryHost host,
        MarkdownViewLifetime lifetime,
        PublishedLayout published,
        MarkdownViewport viewport)
    {
        _host = host;
        _lifetime = lifetime;
        _published = published;
        _viewport = viewport;
    }


    /// <summary>
    /// True while the performance session defers offscreen scenes, which is
    /// when vector scenes are admitted visible-first against native capacity.
    /// </summary>
    internal bool IsAdmissionEnabled => Admission is not null;

    /// <summary>
    /// The borrowed session's native capacity ledger while admission is
    /// enabled; null otherwise. The session, not this view, owns the ledger.
    /// </summary>
    internal VectorNativeResourceAdmission? Admission =>
        _host.PerformanceSession is IMarkdownPerformanceSessionInternal
        {
            IsDisposed: false,
            Options.DeferOffscreenScenes: true,
        } session
            ? session.VectorNativeAdmission
            : null;

    private static bool SameVisibleVectorBand(LazyLayoutBand left, LazyLayoutBand right)
        => left.Top == right.Top && left.Bottom == right.Bottom;

    /// <summary>
    /// True when <paramref name="snapshot"/> has capacity-deferred scenes and
    /// the visible band or the capacity generation moved since the last
    /// completed attempt.
    /// </summary>
    internal bool IsRetryDue(
        LayoutSnapshot snapshot,
        LazyLayoutBand visibleVectorBand,
        bool forceCapacityRetry)
    {
        if (Admission is not { } admission || !snapshot.HasCapacityDeferredScenes)
            return false;

        return forceCapacityRetry ||
            !_hasAttempt ||
            !SameVisibleVectorBand(visibleVectorBand, _lastAttemptVisibleBand) ||
            snapshot.ActiveCapacityGeneration > _lastAttemptGeneration ||
            admission.CapacityGeneration > _lastAttemptGeneration;
    }

    /// <summary>
    /// Keeps one weak ledger subscription only while this current, opted-in
    /// snapshot has scenes deferred specifically by aggregate native capacity.
    /// The generation check brackets registration so a refund cannot be lost
    /// between observing fallback and subscribing.
    /// </summary>
    internal void Synchronize(LayoutSnapshot? snapshot)
    {
        VectorNativeResourceAdmission? admission = Admission;
        if (_lifetime.IsDisposed || _lifetime.IsUnloaded || snapshot is null ||
            !ReferenceEquals(snapshot, _published.Snapshot) ||
            admission is null ||
            !snapshot.HasCapacityDeferredScenes)
        {
            Reset();
            return;
        }

        // A subscription belongs to one session's ledger; a different
        // borrowed session starts over against its own ledger.
        if (!ReferenceEquals(snapshot, _observerSnapshot) ||
            (_registration is not null && !ReferenceEquals(admission, _registrationAdmission)))
        {
            Reset();
            _observerSnapshot = snapshot;
        }

        long generationBeforeRegistration = snapshot.ActiveCapacityGeneration;
        if (_registration is null)
        {
            DispatcherQueue? dispatcher = _host.DispatcherQueue;
            if (dispatcher is null)
                return;

            var observer = new VectorCapacityWakeObserver(this, dispatcher);
            IDisposable registration;
            try
            {
                registration = admission.RegisterCapacityObserver(observer);
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
            // whose owner edge back to this owner is weak.
            _observer = observer;
            _registration = registration;
            _registrationAdmission = admission;
        }

        long generationAfterRegistration = admission.CapacityGeneration;
        if (generationAfterRegistration > generationBeforeRegistration)
            _observer?.OnCapacityAvailable(generationAfterRegistration);
    }

    /// <summary>
    /// Takes a wake that a viewport or paint path observed before its
    /// dispatcher callback ran. True when it is newer than the last attempt.
    /// </summary>
    internal bool ConsumePendingWake(LayoutSnapshot snapshot)
    {
        if (!ReferenceEquals(snapshot, _published.Snapshot) ||
            !ReferenceEquals(snapshot, _observerSnapshot) ||
            _observer is not { } observer)
        {
            return false;
        }

        long generation = observer.ConsumePendingGeneration();
        return generation >= 0 &&
            generation > _lastAttemptGeneration &&
            snapshot.HasCapacityDeferredScenes;
    }

    internal void ProcessWake(VectorCapacityWakeObserver observer)
    {
        if (!ReferenceEquals(observer, _observer))
            return;

        long generation = observer.ConsumePendingGeneration();
        LayoutSnapshot? snapshot = _published.Snapshot;
        if (generation < 0 || _lifetime.IsDisposed || _lifetime.IsUnloaded ||
            snapshot is null || !ReferenceEquals(snapshot, _observerSnapshot) ||
            _host.RequestGeometryRecoveryIfRequired(snapshot))
        {
            return;
        }

        if (!snapshot.HasCapacityDeferredScenes || !IsAdmissionEnabled)
        {
            Reset();
            return;
        }

        if (generation <= _lastAttemptGeneration &&
            SameVisibleVectorBand(_lastAttemptVisibleBand, _viewport.GetVisibleBand()))
        {
            return;
        }

        // Re-read current scroll/ancestor viewport and the current snapshot on
        // the UI thread. Never reuse the snapshot or band that existed when the
        // worker refunded a native credit.
        _host.QueueLazyLayoutForViewport(forceCapacityRetry: true);
    }

    /// <summary>Drops the subscription and forgets the last attempt.</summary>
    internal void Reset()
    {
        VectorCapacityWakeObserver? observer = _observer;
        IDisposable? registration = _registration;
        _observer = null;
        _registration = null;
        _registrationAdmission = null;
        _observerSnapshot = null;
        _hasAttempt = false;
        _lastAttemptGeneration = -1;
        _lastAttemptVisibleBand = default;

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

    /// <summary>
    /// Records a completed lazy layout attempt. Canceled or superseded work
    /// must not consume the one retry a capacity generation allows.
    /// </summary>
    internal void RecordAttempt(LazyLayoutBand visibleBand, long capacityGeneration)
    {
        _lastAttemptVisibleBand = visibleBand;
        _lastAttemptGeneration = capacityGeneration;
        _hasAttempt = true;
    }
}
