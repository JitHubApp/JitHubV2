using System;
using System.Collections.Generic;
using MarkdownRenderer.Controls.Embeds;
using MarkdownRenderer.Controls.Input;
using MarkdownRenderer.Layout;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;
using VirtualKey = Windows.System.VirtualKey;

namespace MarkdownRenderer.Controls.Focus;

/// <summary>
/// Moves keyboard focus for Tab, the arrow keys, Home and End. Tab walks the
/// focusable items in order; arrows move to the nearest item in that
/// direction or scroll the focused horizontal overflow region. A key that
/// arrives while focus geometry cannot be read is queued and replayed by
/// <see cref="PendingFocusGeometryWork"/>.
/// </summary>
internal sealed class KeyboardFocusNavigator
{
    // Logical pixels one arrow key scrolls a horizontal overflow region.
    private const double OverflowScrollStep = 48;

    private readonly IFocusGeometryRecoveryHost _host;
    private readonly MarkdownViewLifetime _lifetime;
    private readonly PublishedLayout _published;
    private readonly KeyboardFocusModel _focus;
    private readonly FocusGeometryReader _geometry;
    private readonly FocusRingPresenter _focusRing;
    private readonly HorizontalOverflowScrollVisuals _overflowScroll;
    private readonly EmbedRealization _embeds;
    private readonly FocusedItemCommit _commit;
    private readonly SpatialFocusKeyQueue _queuedKeys;
    private readonly DispatcherQueue _dispatcher;

    internal KeyboardFocusNavigator(
        IFocusGeometryRecoveryHost host,
        MarkdownViewLifetime lifetime,
        PublishedLayout published,
        KeyboardFocusModel focus,
        FocusGeometryReader geometry,
        FocusRingPresenter focusRing,
        HorizontalOverflowScrollVisuals overflowScroll,
        EmbedRealization embeds,
        FocusedItemCommit commit,
        SpatialFocusKeyQueue queuedKeys,
        DispatcherQueue dispatcher)
    {
        _host = host;
        _lifetime = lifetime;
        _published = published;
        _focus = focus;
        _geometry = geometry;
        _focusRing = focusRing;
        _overflowScroll = overflowScroll;
        _embeds = embeds;
        _commit = commit;
        _queuedKeys = queuedKeys;
        _dispatcher = dispatcher;
    }

    /// <summary>
    /// Advances (or reverses) keyboard focus through the focusable items list.
    /// Returns true when focus is successfully moved (prevents Tab from leaving the control).
    /// </summary>
    internal bool MoveTab(bool reverse)
    {
        _queuedKeys.Clear();
        var items = _focus.Items;
        if (items is null || items.Count == 0) return false;

        int nextIndex = FocusNavigationHelper.MoveTab(items.Count, _focus.FocusedIndex, _focus.ResumeIndex, reverse);
        if (nextIndex < 0)
        {
            // Let Tab/Shift+Tab leave the control to the next focusable element.
            _focus.ClearFocus();
            _focus.ClearResumeIndex();
            _focusRing.Update();
            SuppressHostedTabStopsForNativeTab();
            return false;
        }

        _focus.SetFocusedIndex(nextIndex);
        _focus.ClearResumeIndex();
        return _commit.Commit(reverse);
    }

    /// <summary>
    /// Handles an arrow, Home or End key from the renderer. Left and Right
    /// either scroll the focused overflow region or move focus, in the order
    /// the focused item's kind asks for. Returns true when the key is consumed.
    /// </summary>
    internal bool Navigate(VirtualKey key)
    {
        if (_queuedKeys.HasPending)
            return QueueKey(key);

        FocusGeometryReadStatus status = TryNavigate(key, out bool handled);
        if (status == FocusGeometryReadStatus.Ready)
            return handled;
        return QueueKey(key);
    }

    /// <summary>
    /// Moves focus to the nearest item in the arrow key's direction, without
    /// scrolling an overflow region. Used from inside hosted elements.
    /// </summary>
    internal bool MoveSpatial(VirtualKey key)
    {
        if (_queuedKeys.HasPending)
            return QueueKey(key);

        FocusGeometryReadStatus status = TryMoveSpatial(key, out bool handled);
        if (status == FocusGeometryReadStatus.Ready)
            return handled;
        return QueueKey(key);
    }

    /// <summary>Replays a queued key once focus geometry may be readable.</summary>
    internal FocusGeometryReadStatus TryReplay(VirtualKey key) => TryNavigate(key, out _);

    /// <summary>Drops the keys waiting for geometry.</summary>
    internal void ClearQueuedKeys() => _queuedKeys.Clear();

    private FocusGeometryReadStatus TryNavigate(VirtualKey key, out bool handled)
    {
        if (key is VirtualKey.Home or VirtualKey.End)
            return TryScrollOverflow(key, out handled);

        if (key is VirtualKey.Left or VirtualKey.Right)
        {
            HorizontalArrowHandlingOrder order =
                MarkdownKeyboardInputPolicy.GetHorizontalArrowHandlingOrder(
                    _focus.Current?.Kind);
            FocusGeometryReadStatus first = order == HorizontalArrowHandlingOrder.OverflowThenSpatial
                ? TryScrollOverflow(key, out handled)
                : TryMoveSpatial(key, out handled);
            if (first != FocusGeometryReadStatus.Ready || handled)
                return first;
            return order == HorizontalArrowHandlingOrder.OverflowThenSpatial
                ? TryMoveSpatial(key, out handled)
                : TryScrollOverflow(key, out handled);
        }

        return TryMoveSpatial(key, out handled);
    }

    private FocusGeometryReadStatus TryMoveSpatial(VirtualKey key, out bool handled)
    {
        handled = false;
        var items = _focus.Items;
        if (items is null || items.Count == 0)
            return FocusGeometryReadStatus.Ready;

        if (_focus.FocusedIndex < 0)
        {
            if (_focus.ResumeIndex < 0)
                return FocusGeometryReadStatus.Ready;
            _focus.SetFocusedIndex(Math.Clamp(_focus.ResumeIndex, 0, items.Count - 1));
            _focus.ClearResumeIndex();
            handled = _commit.Commit(reverse: false);
            return FocusGeometryReadStatus.Ready;
        }

        LayoutSnapshot? snapshot = _published.Snapshot;
        if (snapshot is null)
            return FocusGeometryReadStatus.Stale;
        long generation = _published.Generation;
        long revision = snapshot.LayoutRevision;
        FocusGeometryReadStatus captureStatus = _geometry.TryCaptureRectMap(
            snapshot, generation, revision, items,
            out List<(int Index, Rect Rect)> map);
        if (captureStatus != FocusGeometryReadStatus.Ready)
            return captureStatus;

        int currentLocalIndex = -1;
        for (int i = 0; i < map.Count; i++)
        {
            if (map[i].Index == _focus.FocusedIndex)
            {
                currentLocalIndex = i;
                break;
            }
        }

        if (currentLocalIndex < 0)
            return FocusGeometryReadStatus.Ready;

        var rects = new List<Rect>(map.Count);
        foreach (var entry in map) rects.Add(entry.Rect);

        var direction = key switch
        {
            VirtualKey.Left => FocusNavigationDirection.Left,
            VirtualKey.Right => FocusNavigationDirection.Right,
            VirtualKey.Up => FocusNavigationDirection.Up,
            VirtualKey.Down => FocusNavigationDirection.Down,
            _ => FocusNavigationDirection.Down,
        };

        int nextLocalIndex = FocusNavigationHelper.MoveSpatial(rects, currentLocalIndex, direction);
        if (nextLocalIndex < 0)
            return FocusGeometryReadStatus.Ready;

        if (!IsCurrentTarget(snapshot, generation, revision))
            return FocusGeometryReadStatus.Stale;

        _focus.SetFocusedIndex(map[nextLocalIndex].Index);
        _focus.ClearResumeIndex();
        handled = _commit.Commit(reverse: false);
        return FocusGeometryReadStatus.Ready;
    }

    /// <summary>
    /// Scrolls the focused item's horizontal overflow region one step for
    /// Left or Right, or to its start or end for Home or End.
    /// </summary>
    private FocusGeometryReadStatus TryScrollOverflow(VirtualKey key, out bool handled)
    {
        handled = false;
        if (_published.Snapshot is not { } snapshot ||
            _focus.Items is not { } items ||
            _focus.FocusedIndex < 0 ||
            _focus.FocusedIndex >= items.Count)
        {
            return FocusGeometryReadStatus.Ready;
        }

        long generation = _published.Generation;
        long revision = snapshot.LayoutRevision;
        if (!snapshot.TryBeginInteraction())
            return FocusGeometryReadStatus.Busy;

        HorizontalOverflowVisualState visualState = default;
        bool changed = false;
        bool current;
        double oldOffset = 0;
        int blockIndex = items[_focus.FocusedIndex].BlockIndex;
        try
        {
            current = IsCurrentTarget(snapshot, generation, revision);
            if (current && snapshot.TryGetHorizontalOverflowForBlockIndex(
                    blockIndex,
                    out IHorizontalOverflowBox? overflow) &&
                overflow is not null)
            {
                oldOffset = overflow.HorizontalOffset;
                if (key is VirtualKey.Home or VirtualKey.End)
                {
                    double target = key == VirtualKey.End
                        ? Math.Max(0, overflow.HorizontalExtent - overflow.HorizontalViewport)
                        : 0;
                    changed = snapshot.TrySetHorizontalOffset(overflow, target);
                }
                else
                {
                    bool towardPhysicalRight = key == VirtualKey.Right;
                    double logicalDelta = overflow.IsRightToLeft
                        ? (towardPhysicalRight ? -OverflowScrollStep : OverflowScrollStep)
                        : (towardPhysicalRight ? OverflowScrollStep : -OverflowScrollStep);
                    changed = snapshot.TryScrollHorizontal(overflow, logicalDelta);
                }
                if (changed)
                    _ = _overflowScroll.TryCaptureUnderLease(
                        snapshot, generation, revision, overflow, oldOffset, out visualState);
            }
        }
        finally
        {
            snapshot.EndInteraction();
        }

        if (!current)
            return FocusGeometryReadStatus.Stale;
        if (!changed)
            return IsCurrentTarget(snapshot, generation, revision)
                ? FocusGeometryReadStatus.Ready
                : FocusGeometryReadStatus.Stale;

        handled = true;
        if (visualState.Snapshot is not null)
            _overflowScroll.Publish(visualState);
        else
            _overflowScroll.QueueRetry(snapshot, generation, blockIndex, oldOffset);
        return FocusGeometryReadStatus.Ready;
    }

    private bool QueueKey(VirtualKey key)
    {
        if (_host.RequestGeometryRecoveryIfRequired(_published.Snapshot))
            return false;
        if (_lifetime.IsDisposed || _lifetime.IsUnloaded || _published.Snapshot is null ||
            _focus.Items is not { } items || items.Count == 0)
        {
            return false;
        }

        long generation = _published.Generation;
        if (_queuedKeys.HasPending && !_queuedKeys.IsOriginCurrent(_focus.Current, generation))
            _queuedKeys.Clear();

        if (_queuedKeys.IsFull)
            return false; // Leave this repeated key unhandled so its parent can act on it.

        if (!_queuedKeys.HasOrigin)
        {
            if (_focus.Current is not { } origin)
                return false;
            _queuedKeys.SetOrigin(origin, generation);
        }

        _queuedKeys.Enqueue(key);
        _host.StartRecoveryClock();
        if (!_host.HasRecoveryClock)
        {
            _queuedKeys.Clear();
            return false;
        }

        return true;
    }

    private void SuppressHostedTabStopsForNativeTab()
    {
        var suppressed = new List<(Control Control, bool IsTabStop)>();
        var seen = new HashSet<Control>();
        foreach (var plan in _embeds.EmbedPlans)
        {
            if (plan.Realized is { } realized)
                HostedFocusNavigation.SuppressHostedTabStops(realized, suppressed, seen);
        }
        foreach (var plan in _embeds.CodeBlockActionPlans)
        {
            if (plan.Realized is { } realized)
                HostedFocusNavigation.SuppressHostedTabStops(realized, suppressed, seen);
        }

        if (suppressed.Count == 0)
            return;

        _dispatcher.TryEnqueue(() =>
        {
            foreach (var (control, isTabStop) in suppressed)
                control.IsTabStop = isTabStop;
        });
    }

    private bool IsCurrentTarget(LayoutSnapshot snapshot, long snapshotGeneration, long layoutRevision) =>
        !_lifetime.IsDisposed && !_lifetime.IsUnloaded &&
        _published.IsCurrent(snapshot, snapshotGeneration, layoutRevision);
}
