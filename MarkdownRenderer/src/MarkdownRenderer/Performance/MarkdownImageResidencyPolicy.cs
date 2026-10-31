using System;
using System.Collections.Generic;
using System.Threading;

namespace MarkdownRenderer.Performance;

/// <summary>
/// Coordinates live ImageBox resources without retaining the document's image
/// plan. The control supplies the current indexed viewport set on scroll and
/// supplies the full published plan only during an existing plan rebuild pass.
/// </summary>
internal sealed class MarkdownImageResidencyPolicy : IMarkdownImageResidencyPolicy
{
    private readonly EventHandler _residencyWorkDrainedHandler;
    private readonly HashSet<IMarkdownImageResidencyTarget> _active =
        new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<IMarkdownImageResidencyTarget> _pending =
        new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<IMarkdownImageResidencyTarget> _subscribed =
        new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<IMarkdownImageResidencyTarget> _requested =
        new(ReferenceEqualityComparer.Instance);
    private readonly List<IMarkdownImageResidencyTarget> _retireScratch = [];
    private readonly List<IMarkdownImageResidencyTarget> _pendingScratch = [];
    private readonly List<IMarkdownImageResidencyTarget> _resetScratch = [];
    private Action? _requestReconcile;
    private int _disposed;

    internal MarkdownImageResidencyPolicy(Action requestReconcile)
    {
        _requestReconcile = requestReconcile ?? throw new ArgumentNullException(nameof(requestReconcile));
        _residencyWorkDrainedHandler = OnResidencyWorkDrained;
    }

    /// <remarks>
    /// Callers serialize Reconcile, Reset, and Dispose with the owning snapshot
    /// read lease. The asynchronous drain callback only requests a dispatcher
    /// reconciliation; it never reads or mutates a target on the drain thread.
    /// </remarks>
    public void Reconcile(
        IReadOnlyList<IMarkdownImageResidencyTarget> activeImages,
        IReadOnlyList<IMarkdownImageResidencyTarget>? publishedImages = null)
    {
        ArgumentNullException.ThrowIfNull(activeImages);
        ThrowIfDisposed();

        _requested.Clear();
        for (int index = 0; index < activeImages.Count; index++)
        {
            IMarkdownImageResidencyTarget target = activeImages[index]
                ?? throw new ArgumentException("The active image list contains a null target.", nameof(activeImages));
            _requested.Add(target);
        }

        // Remove outgoing active references first. A pending drain is retained
        // only until its notification lets the control reconcile it again.
        _retireScratch.Clear();
        foreach (IMarkdownImageResidencyTarget target in _active)
        {
            if (!_requested.Contains(target))
                _retireScratch.Add(target);
        }

        foreach (IMarkdownImageResidencyTarget target in _retireScratch)
        {
            _active.Remove(target);
            BeginSuspension(target);
        }
        _retireScratch.Clear();

        // Keep reference identity across layout-plan rebuilds. Stable active
        // boxes need no repeated lifecycle calls; newly active or still-pending
        // boxes retry resume only under the snapshot lease.
        foreach (IMarkdownImageResidencyTarget target in _requested)
        {
            bool newlyActive = _active.Add(target);
            if (!newlyActive && !_pending.Contains(target))
                continue;

            // Subscribe before the transition attempt: a cancellation drain can
            // complete between TryResumeResources returning false and AddEvent.
            Subscribe(target);
            if (target.TryResumeResources())
            {
                RemovePending(target);
                continue;
            }

            // A false resume can mean that terminal disposal already won. A
            // suspended live target that releases immediately gets one final
            // resume attempt; a disposed target is dropped instead of retained.
            bool released = target.TryReleaseSuspendedResources();
            if (released && target.TryResumeResources())
            {
                RemovePending(target);
                continue;
            }

            if (released)
            {
                _active.Remove(target);
                RemovePending(target);
                continue;
            }

            _pending.Add(target);
        }

        if (publishedImages is not null)
        {
            // This full-plan pass is allowed only when rebuilding plans, where
            // the control already walks every image. It is never retained and
            // is omitted by ordinary scroll reconciliation.
            for (int index = 0; index < publishedImages.Count; index++)
            {
                IMarkdownImageResidencyTarget target = publishedImages[index]
                    ?? throw new ArgumentException("The published image list contains a null target.", nameof(publishedImages));
                if (_requested.Contains(target) || _active.Contains(target) || _pending.Contains(target))
                    continue;

                BeginSuspension(target);
            }
        }

        // Retry inactive pending targets as a bounded safety net for an event
        // that raced subscription teardown. Pending targets currently wanted
        // by the viewport wait for their drain event and are not released here.
        _pendingScratch.Clear();
        foreach (IMarkdownImageResidencyTarget target in _pending)
        {
            if (!_requested.Contains(target))
                _pendingScratch.Add(target);
        }

        foreach (IMarkdownImageResidencyTarget target in _pendingScratch)
        {
            if (target.TryReleaseSuspendedResources())
                RemovePending(target);
        }
        _pendingScratch.Clear();

        // A plan rebuild may briefly track every cold image. Removing entries
        // does not reset HashSet's high-water enumeration slots, and Clear on a
        // List does not release its backing array. Compact sparse storage only
        // after a large band contraction, never on every stable scroll frame.
        int storageFloor = System.Math.Max(256, _active.Count);
        CompactSparseTargets(_active, storageFloor);
        CompactSparseTargets(_pending, storageFloor);
        CompactSparseTargets(_subscribed, storageFloor);
        CompactSparseTargets(_requested, storageFloor);
        if (_retireScratch.Capacity / 2 > storageFloor)
            _retireScratch.Capacity = storageFloor;
        if (_pendingScratch.Capacity / 2 > storageFloor)
            _pendingScratch.Capacity = storageFloor;
    }

    /// <summary>
    /// Drops borrowed box references on snapshot replacement. The snapshot's
    /// normal retirement path remains the sole owner of terminal disposal.
    /// </summary>
    public void Reset()
    {
        _resetScratch.Clear();
        foreach (IMarkdownImageResidencyTarget target in _subscribed)
            _resetScratch.Add(target);
        foreach (IMarkdownImageResidencyTarget target in _resetScratch)
            target.ResidencyWorkDrained -= _residencyWorkDrainedHandler;

        _active.Clear();
        _pending.Clear();
        _subscribed.Clear();
        _requested.Clear();
        _retireScratch.Clear();
        _pendingScratch.Clear();
        _resetScratch.Clear();
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        Reset();
        Interlocked.Exchange(ref _requestReconcile, null);
    }

    private void BeginSuspension(IMarkdownImageResidencyTarget target)
    {
        _pending.Add(target);
        Subscribe(target);
        target.SuspendResources();
    }

    private void Subscribe(IMarkdownImageResidencyTarget target)
    {
        if (_subscribed.Add(target))
            target.ResidencyWorkDrained += _residencyWorkDrainedHandler;
    }

    private void RemovePending(IMarkdownImageResidencyTarget target)
    {
        _pending.Remove(target);
        if (_subscribed.Remove(target))
            target.ResidencyWorkDrained -= _residencyWorkDrainedHandler;
    }

    private void OnResidencyWorkDrained(object? sender, EventArgs eventArgs)
    {
        if (Volatile.Read(ref _disposed) != 0)
            return;

        Action? requestReconcile = Volatile.Read(ref _requestReconcile);
        if (requestReconcile is null)
            return;

        try
        {
            requestReconcile();
        }
        catch
        {
            // This is a wake-up from a background drain. Never let a dispatcher
            // scheduling failure fault image cleanup or mutate box state here.
        }
    }

    private void ThrowIfDisposed() =>
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

    private static void CompactSparseTargets(
        HashSet<IMarkdownImageResidencyTarget> targets, int storageFloor)
    {
        if (targets.Count == 0)
            targets.Clear();
        int capacity = targets.EnsureCapacity(0);
        if (capacity / 2 > storageFloor && targets.Count < capacity / 4)
            targets.TrimExcess(System.Math.Max(storageFloor, targets.Count * 2));
    }
}
