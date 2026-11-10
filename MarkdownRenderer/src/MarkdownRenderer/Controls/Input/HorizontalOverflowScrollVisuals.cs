using System;
using System.Collections.Generic;
using MarkdownRenderer.Accessibility;
using MarkdownRenderer.Controls.Embeds;
using MarkdownRenderer.Controls.Focus;
using MarkdownRenderer.Layout;
using MarkdownRenderer.Layout.Boxes;
using MarkdownRenderer.Selection;
using MarkdownRenderer.Selection.Internal;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Automation;
using Windows.Foundation;

namespace MarkdownRenderer.Controls.Input;

/// <summary>
/// Publishes what a horizontal overflow region's scroll changed: inline
/// hosted elements inside it move, the selection overlay and focus ring
/// follow, automation hears the new scroll percent, and the region is
/// repainted. The change is captured under the snapshot lease and applied
/// on the dispatcher; a capture that cannot run now is retried by the
/// recovery clock.
/// </summary>
internal sealed class HorizontalOverflowScrollVisuals
{
    private readonly IHorizontalOverflowScrollHost _host;
    private readonly MarkdownViewSurface _surface;
    private readonly MarkdownViewLifetime _lifetime;
    private readonly PublishedLayout _published;
    private readonly SelectionController _selection;
    private readonly KeyboardFocusModel _focus;
    private readonly EmbedRealization _embeds;
    private readonly DispatcherQueue? _dispatcher;

    private bool _hasPendingRetry;
    private long _pendingRetryGeneration;
    private int _pendingRetryBlockIndex;
    private double _pendingRetryOldOffset;

    private readonly HorizontalOverflowScrollAutomationEvents _automationEvents;

    internal HorizontalOverflowScrollVisuals(
        IHorizontalOverflowScrollHost host,
        MarkdownViewSurface surface,
        MarkdownViewLifetime lifetime,
        PublishedLayout published,
        SelectionController selection,
        KeyboardFocusModel focus,
        EmbedRealization embeds,
        DispatcherQueue? dispatcher)
    {
        _host = host;
        _surface = surface;
        _lifetime = lifetime;
        _published = published;
        _selection = selection;
        _focus = focus;
        _embeds = embeds;
        _dispatcher = dispatcher;
        _automationEvents = new HorizontalOverflowScrollAutomationEvents(host, lifetime, published, dispatcher);
    }

    /// <summary>True while a capture waits for the recovery clock.</summary>
    internal bool HasPendingRetry => _hasPendingRetry;

    /// <summary>Drops a capture waiting for the recovery clock.</summary>
    internal void ClearPendingRetry()
    {
        _hasPendingRetry = false;
        _pendingRetryGeneration = 0;
    }

    /// <summary>Drops queued automation notifications.</summary>
    internal void ClearAutomationNotifications() => _automationEvents.Clear();

    /// <summary>Publishes the scroll of <paramref name="target"/> from <paramref name="oldOffset"/>.</summary>
    internal void Refresh(IHorizontalOverflowBox target, double oldOffset)
    {
        if (_lifetime.IsDisposed || _lifetime.IsUnloaded || _published.Snapshot is not { } snapshot ||
            target is not BlockBox targetBlock)
        {
            return;
        }

        long generation = _published.Generation;
        long revision = snapshot.LayoutRevision;
        if (!snapshot.TryBeginInteraction())
        {
            QueueRetry(
                snapshot, generation, targetBlock.BlockIndex, oldOffset);
            return;
        }

        HorizontalOverflowVisualState state = default;
        bool captured = false;
        try
        {
            if (IsCurrent(snapshot, generation, revision))
            {
                captured = snapshot.TryGetHorizontalOverflowForBlockIndex(
                    targetBlock.BlockIndex,
                    out IHorizontalOverflowBox? currentTarget) &&
                    ReferenceEquals(currentTarget, target) &&
                    TryCaptureUnderLease(
                        snapshot, generation, revision, target, oldOffset, out state);
            }
        }
        finally
        {
            snapshot.EndInteraction();
        }

        if (captured && IsCurrent(snapshot, generation, revision))
            Publish(state);
        else if (ReferenceEquals(snapshot, _published.Snapshot) &&
                 generation == _published.Generation)
            QueueRetry(
                snapshot, generation, targetBlock.BlockIndex, oldOffset);
    }

    /// <summary>
    /// Captures what the scroll of <paramref name="target"/> changed. The
    /// caller holds the snapshot's interaction lease.
    /// </summary>
    internal bool TryCaptureUnderLease(
        LayoutSnapshot snapshot,
        long snapshotGeneration,
        long layoutRevision,
        IHorizontalOverflowBox target,
        double oldOffset,
        out HorizontalOverflowVisualState state)
    {
        state = default;
        if (!IsCurrent(snapshot, snapshotGeneration, layoutRevision) ||
            target is not BlockBox targetBlock ||
            !snapshot.TryGetHorizontalOverflowForBlockIndex(
                targetBlock.BlockIndex,
                out IHorizontalOverflowBox? currentTarget) ||
            !ReferenceEquals(currentTarget, target))
        {
            return false;
        }

        double newOffset = target.HorizontalOffset;
        double logicalDelta = newOffset - oldOffset;
        if (Math.Abs(logicalDelta) <= 0.1)
            return false;

        double physicalDelta = target.IsRightToLeft ? logicalDelta : -logicalDelta;
        List<InlineEmbedPlacementUpdate>? placements = null;
        foreach (EmbedPlan plan in _embeds.EmbedPlans)
        {
            if (plan is not InlineEmbedPlan inline ||
                !Contains(target, inline.Icb))
            {
                continue;
            }

            Rect rect = inline.Rect;
            placements ??= new List<InlineEmbedPlacementUpdate>();
            placements.Add(new(
                new WeakReference<InlineEmbedPlan>(inline),
                new Rect(rect.X + physicalDelta, rect.Y, rect.Width, rect.Height)));
        }

        state = new(
            new WeakReference<LayoutSnapshot>(snapshot),
            snapshotGeneration,
            layoutRevision,
            targetBlock.BlockIndex,
            new WeakReference<IHorizontalOverflowBox>(target),
            oldOffset,
            newOffset,
            target.HorizontalExtent,
            target.HorizontalViewport,
            target.IsRightToLeft,
            snapshot.ToLogicalRectForBlockIndexUnderPaint(
                targetBlock.BlockIndex, target.HorizontalViewportBounds),
            snapshot.ToLogicalRectForBlockIndexUnderPaint(
                targetBlock.BlockIndex, target.HorizontalScrollTrackBounds),
            placements?.ToArray() ?? Array.Empty<InlineEmbedPlacementUpdate>());
        return true;
    }

    /// <summary>Queues <paramref name="state"/> to be applied on the dispatcher.</summary>
    internal void Publish(HorizontalOverflowVisualState state)
    {
        bool queued = false;
        try
        {
            queued = _dispatcher is { } dispatcher &&
                dispatcher.TryEnqueue(() => Apply(state));
        }
        catch (Exception)
        {
        }
        if (!queued)
        {
            QueueRetry(
                state.Snapshot.TryGetTarget(out LayoutSnapshot? snapshot)
                    ? snapshot
                    : null,
                state.SnapshotGeneration,
                state.BlockIndex,
                state.OldOffset);
        }
    }

    /// <summary>
    /// Asks the recovery clock to capture the scroll of the overflow region
    /// in block <paramref name="blockIndex"/> again. Rebuilds when no clock
    /// can run.
    /// </summary>
    internal void QueueRetry(
        LayoutSnapshot? snapshot,
        long snapshotGeneration,
        int blockIndex,
        double oldOffset)
    {
        if (snapshot is null || !ReferenceEquals(snapshot, _published.Snapshot) ||
            snapshotGeneration != _published.Generation)
        {
            return;
        }

        _hasPendingRetry = true;
        _pendingRetryGeneration = snapshotGeneration;
        _pendingRetryBlockIndex = blockIndex;
        _pendingRetryOldOffset = oldOffset;
        _host.StartRecoveryClock();
        if (!_host.HasRecoveryClock)
        {
            _hasPendingRetry = false;
            _host.RequestFullRebuild();
        }
    }

    /// <summary>Retries a waiting capture. Called by the recovery clock.</summary>
    internal void ProcessPendingRetry()
    {
        if (!_hasPendingRetry)
            return;
        if (_host.RequestGeometryRecoveryIfRequired(_published.Snapshot))
        {
            _hasPendingRetry = false;
            return;
        }
        if (_lifetime.IsDisposed || _lifetime.IsUnloaded || _published.Snapshot is not { } snapshot ||
            _pendingRetryGeneration != _published.Generation)
        {
            _hasPendingRetry = false;
            return;
        }

        long generation = _pendingRetryGeneration;
        long revision = snapshot.LayoutRevision;
        if (!snapshot.TryBeginInteraction())
            return;

        HorizontalOverflowVisualState state = default;
        bool found = false;
        bool missing = false;
        try
        {
            if (IsCurrent(snapshot, generation, revision) &&
                snapshot.TryGetHorizontalOverflowForBlockIndex(
                    _pendingRetryBlockIndex,
                    out IHorizontalOverflowBox? target) &&
                target is not null)
            {
                found = TryCaptureUnderLease(
                    snapshot,
                    generation,
                    revision,
                    target,
                    _pendingRetryOldOffset,
                    out state);
                missing = !found &&
                    Math.Abs(target.HorizontalOffset - _pendingRetryOldOffset) <= 0.1;
            }
            else
            {
                missing = true;
            }
        }
        finally
        {
            snapshot.EndInteraction();
        }

        if (!IsCurrent(snapshot, generation, revision))
            return;
        if (found)
        {
            _hasPendingRetry = false;
            Publish(state);
        }
        else if (missing)
        {
            _hasPendingRetry = false;
        }
    }

    /// <summary>Scrolls <paramref name="target"/> by an automation scroll amount.</summary>
    internal void ScrollForAutomation(IHorizontalOverflowBox target, ScrollAmount amount)
    {
        if (_published.Snapshot is not { } snapshot)
            return;

        double physicalDelta = amount switch
        {
            ScrollAmount.LargeDecrement => -Math.Max(48, target.HorizontalViewport * 0.9),
            ScrollAmount.SmallDecrement => -48,
            ScrollAmount.SmallIncrement => 48,
            ScrollAmount.LargeIncrement => Math.Max(48, target.HorizontalViewport * 0.9),
            _ => 0,
        };
        double logicalDelta = MarkdownHorizontalScrollPolicy.GetLogicalDelta(
            physicalDelta,
            target.IsRightToLeft);
        double oldOffset = target.HorizontalOffset;
        if (snapshot.TryScrollHorizontal(target, logicalDelta))
            Refresh(target, oldOffset);
    }

    /// <summary>Scrolls <paramref name="target"/> to an automation scroll percent.</summary>
    internal void SetPercentForAutomation(IHorizontalOverflowBox target, double percent)
    {
        if (_published.Snapshot is not { } snapshot || !double.IsFinite(percent))
            return;

        double oldOffset = target.HorizontalOffset;
        double logicalOffset = MarkdownHorizontalScrollPolicy.GetLogicalOffset(
            percent,
            target.HorizontalExtent,
            target.HorizontalViewport,
            target.IsRightToLeft);
        if (snapshot.TrySetHorizontalOffset(target, logicalOffset))
            Refresh(target, oldOffset);
    }

    /// <remarks>
    /// Each step after the hosted element moves can call back into the host,
    /// so each is preceded by the same currency check; splitting the steps
    /// would hide that sequence.
    /// </remarks>
    private void Apply(HorizontalOverflowVisualState state)
    {
        LayoutSnapshot? snapshot = null;
        IHorizontalOverflowBox? target = null;
        if (_lifetime.IsDisposed || _lifetime.IsUnloaded ||
            state.Snapshot is null || state.Target is null ||
            !state.Snapshot.TryGetTarget(out snapshot) ||
            !state.Target.TryGetTarget(out target) ||
            !IsCurrent(snapshot, state.SnapshotGeneration, state.LayoutRevision))
        {
            if (snapshot is not null &&
                ReferenceEquals(snapshot, _published.Snapshot) &&
                state.SnapshotGeneration == _published.Generation)
            {
                QueueRetry(
                    snapshot, state.SnapshotGeneration, state.BlockIndex, state.OldOffset);
            }
            return;
        }

        bool movedEmbed = false;
        foreach (InlineEmbedPlacementUpdate placement in state.EmbedPlacements)
        {
            if (!IsCurrent(snapshot, state.SnapshotGeneration, state.LayoutRevision))
                return;
            if (!placement.Plan.TryGetTarget(out InlineEmbedPlan? plan) ||
                !_embeds.Contains(plan))
            {
                continue;
            }

            plan.Rect = placement.Rect;
            plan.UpdatePlacement();
            movedEmbed = true;
        }

        if (!IsCurrent(snapshot, state.SnapshotGeneration, state.LayoutRevision))
            return;
        if (movedEmbed)
            _embeds.RealizeVisible();
        if (!IsCurrent(snapshot, state.SnapshotGeneration, state.LayoutRevision))
            return;
        if (_selection.IsActive)
            _host.UpdateSelectionOverlay();
        if (!IsCurrent(snapshot, state.SnapshotGeneration, state.LayoutRevision))
            return;
        if (_focus.FocusedIndex >= 0)
            _host.UpdateFocusRing();
        _automationEvents.Queue(
            snapshot,
            state.SnapshotGeneration,
            target,
            state.OldOffset,
            state.NewOffset,
            state.Extent,
            state.Viewport,
            state.IsRightToLeft);

        if (_surface.Canvas is { } canvas)
        {
            if (MarkdownCanvasInvalidationPolicy.TryClipToCanvas(
                    new Rect(
                        state.ViewportBounds.X - 1,
                        state.ViewportBounds.Y - 1,
                        state.ViewportBounds.Width + 2,
                        Math.Max(state.ViewportBounds.Bottom, state.TrackBounds.Bottom) - state.ViewportBounds.Y + 2),
                    canvas.Width,
                    canvas.Height,
                    out Rect invalidation))
            {
                canvas.Invalidate(invalidation);
            }
        }
    }

    private static bool Contains(
        IHorizontalOverflowBox target,
        Layout.Boxes.InlineContainerBox box)
    {
        if (target is Layout.Boxes.CodeBlockBox code)
        {
            foreach (var chunk in code.Chunks)
            {
                if (ReferenceEquals(chunk, box))
                    return true;
            }
            return false;
        }

        if (target is Layout.Boxes.TableBox table)
        {
            foreach (var cell in table.GetCellBoxes())
            {
                if (ReferenceEquals(cell, box))
                    return true;
            }
        }
        return false;
    }

    private bool IsCurrent(LayoutSnapshot snapshot, long snapshotGeneration, long layoutRevision) =>
        !_lifetime.IsDisposed && !_lifetime.IsUnloaded &&
        _published.IsCurrent(snapshot, snapshotGeneration, layoutRevision);
}
