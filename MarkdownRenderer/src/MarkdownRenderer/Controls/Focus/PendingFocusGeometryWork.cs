using System.Collections.Generic;
using MarkdownRenderer.Controls.Input;
using MarkdownRenderer.Layout;
using Windows.Foundation;
using Windows.System;

namespace MarkdownRenderer.Controls.Focus;

/// <summary>
/// Focus work that waits for readable geometry: queued navigation keys, the
/// pointer point Tab resumes from, and a ring or reveal refresh. The
/// recovery clock calls <see cref="Process"/> until nothing is pending; a
/// snapshot that needs geometry recovery drops the work instead.
/// </summary>
internal sealed class PendingFocusGeometryWork
{
    private readonly IFocusGeometryRecoveryHost _host;
    private readonly MarkdownViewLifetime _lifetime;
    private readonly PublishedLayout _published;
    private readonly KeyboardFocusModel _focus;
    private readonly FocusGeometryReader _geometry;
    private readonly FocusRingPresenter _focusRing;
    private readonly FocusedItemReveal _reveal;
    private readonly HorizontalOverflowScrollVisuals _overflowScroll;
    private readonly SpatialFocusKeyQueue _queuedKeys;
    private readonly KeyboardFocusNavigator _navigator;

    private bool _processing;
    private bool _refreshPending;
    private (Point Point, long Generation)? _resumePoint;

    internal PendingFocusGeometryWork(
        IFocusGeometryRecoveryHost host,
        MarkdownViewLifetime lifetime,
        PublishedLayout published,
        KeyboardFocusModel focus,
        FocusGeometryReader geometry,
        FocusRingPresenter focusRing,
        FocusedItemReveal reveal,
        HorizontalOverflowScrollVisuals overflowScroll,
        SpatialFocusKeyQueue queuedKeys,
        KeyboardFocusNavigator navigator)
    {
        _host = host;
        _lifetime = lifetime;
        _published = published;
        _focus = focus;
        _geometry = geometry;
        _focusRing = focusRing;
        _reveal = reveal;
        _overflowScroll = overflowScroll;
        _queuedKeys = queuedKeys;
        _navigator = navigator;
    }

    /// <summary>True while any focus work waits for geometry.</summary>
    internal bool HasPendingWork =>
        _queuedKeys.HasPending || _refreshPending || _resumePoint is not null;

    /// <summary>
    /// Asks for the focus ring and reveal to be refreshed once geometry is
    /// readable. Without a recovery clock, rebuilds instead.
    /// </summary>
    internal void RequestRetry()
    {
        if (_lifetime.IsDisposed || _lifetime.IsUnloaded)
            return;

        if (_host.RequestGeometryRecoveryIfRequired(_published.Snapshot))
            return;

        _refreshPending = true;
        _host.StartRecoveryClock();
        if (!_host.HasRecoveryClock)
        {
            _refreshPending = false;
            _resumePoint = null;
            _focus.ClearResumeIndex();
            _host.RequestFullRebuild();
        }
    }

    /// <summary>
    /// Makes the focusable item nearest <paramref name="documentPoint"/> the
    /// one Tab resumes from, now or once geometry is readable.
    /// </summary>
    internal void RememberResumePoint(Point documentPoint)
    {
        if (_published.Snapshot is not { } snapshot)
        {
            _focus.ClearResumeIndex();
            _resumePoint = null;
            return;
        }

        long generation = _published.Generation;
        FocusGeometryReadStatus status = _geometry.TryCaptureRectMap(
            snapshot, generation, snapshot.LayoutRevision, _focus.Items,
            out List<(int Index, Rect Rect)> map);
        if (status != FocusGeometryReadStatus.Ready)
        {
            _resumePoint = (documentPoint, generation);
            RequestRetry();
            return;
        }

        _resumePoint = null;
        _focus.SetResumeIndex(FocusableItemQueries.FindNearestFocusableItem(map, documentPoint));
    }

    /// <summary>Retries the pending work against the published layout.</summary>
    internal void Process()
    {
        if (_processing || _lifetime.IsDisposed || _lifetime.IsUnloaded || _published.Snapshot is not { } snapshot)
            return;

        if (_host.RequestGeometryRecoveryIfRequired(snapshot))
        {
            Clear();
            return;
        }

        _processing = true;
        try
        {
            ReplayQueuedKey();
            ResolveResumePoint(snapshot);

            if (_refreshPending && !_queuedKeys.HasPending)
            {
                _refreshPending = false;
                // A reveal waiting for settled geometry must not
                // override a scroll the user requested since focus moved.
                if (_reveal.IsCurrent())
                    _ = _reveal.ScrollIntoView();
                _focusRing.Update();
            }
        }
        finally
        {
            _processing = false;
        }
    }

    /// <summary>Drops all pending focus work and queued ring placements.</summary>
    internal void Clear()
    {
        _queuedKeys.Clear();
        _refreshPending = false;
        _resumePoint = null;
        _overflowScroll.ClearPendingRetry();
        _focusRing.InvalidatePublication();
    }

    private void ReplayQueuedKey()
    {
        if (!_queuedKeys.HasPending)
            return;

        if (!_queuedKeys.IsOriginCurrent(_focus.Current, _published.Generation))
        {
            _queuedKeys.Clear();
            return;
        }

        // A clear or a newer key during the replay must not dequeue it.
        VirtualKey key = _queuedKeys.Head;
        long epoch = _queuedKeys.Epoch;
        if (_navigator.TryReplay(key) == FocusGeometryReadStatus.Ready)
            _queuedKeys.CompleteHead(key, epoch, _focus.Current);
    }

    private void ResolveResumePoint(LayoutSnapshot snapshot)
    {
        if (_resumePoint is not { } pending)
            return;

        long generation = _published.Generation;
        if (pending.Generation != generation)
        {
            _resumePoint = null;
            _focus.ClearResumeIndex();
            return;
        }

        FocusGeometryReadStatus status = _geometry.TryCaptureRectMap(
            snapshot,
            generation,
            snapshot.LayoutRevision,
            _focus.Items,
            out List<(int Index, Rect Rect)> map);
        if (status == FocusGeometryReadStatus.Ready)
        {
            _resumePoint = null;
            _focus.SetResumeIndex(FocusableItemQueries.FindNearestFocusableItem(map, pending.Point));
        }
        else if (status == FocusGeometryReadStatus.Stale &&
                 generation != _published.Generation)
        {
            _resumePoint = null;
            _focus.ClearResumeIndex();
        }
    }
}
