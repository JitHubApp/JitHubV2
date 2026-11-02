using System;
using MarkdownRenderer.Controls.Focus;
using MarkdownRenderer.Controls.LazyLayout;
using MarkdownRenderer.Layout;
using Windows.Foundation;

namespace MarkdownRenderer.Controls.Navigation;

/// <summary>
/// Scrolls the viewport to a block or an internal fragment anchor. Owns the
/// one pending block request that waits for a busy snapshot or for its owner
/// block's lazy measurement to publish.
/// </summary>
internal sealed class BlockScrollNavigation
{
    private const double TopMargin = 24;

    private readonly MarkdownViewLifetime _lifetime;
    private readonly PublishedLayout _published;
    private readonly MarkdownViewSurface _surface;
    private readonly MarkdownViewport _viewport;
    private readonly LazyLayoutRealization _lazyLayout;
    private readonly FocusedItemReveal _reveal;
    private (LayoutSnapshot Snapshot, long SnapshotGeneration, int BlockIndex)? _pending;

    internal BlockScrollNavigation(
        MarkdownViewLifetime lifetime,
        PublishedLayout published,
        MarkdownViewSurface surface,
        MarkdownViewport viewport,
        LazyLayoutRealization lazyLayout,
        FocusedItemReveal reveal)
    {
        _lifetime = lifetime;
        _published = published;
        _surface = surface;
        _viewport = viewport;
        _lazyLayout = lazyLayout;
        _reveal = reveal;
    }

    /// <summary>
    /// True while the renderer is live and <paramref name="snapshot"/> at
    /// <paramref name="layoutRevision"/> is still the published layout.
    /// </summary>
    internal bool IsCurrentTarget(LayoutSnapshot snapshot, long snapshotGeneration, long layoutRevision) =>
        !_lifetime.IsDisposed && !_lifetime.IsUnloaded &&
        _published.IsCurrent(snapshot, snapshotGeneration, layoutRevision);

    /// <summary>Forgets the pending block request.</summary>
    internal void ClearPending() => _pending = null;

    /// <summary>
    /// Scrolls the document so the block with <paramref name="blockIndex"/>
    /// is near the top of the viewport.
    /// </summary>
    internal void ScrollTo(int blockIndex)
    {
        _reveal.NoteViewportRequest();
        if (_published.Snapshot is not { } snapshot) return;
        long snapshotGeneration = _published.Generation;
        SnapshotBlockScrollTargetState targetState = snapshot.TryCaptureBlockScrollTarget(
            blockIndex,
            out SnapshotBlockScrollTarget target);
        if (targetState == SnapshotBlockScrollTargetState.Busy)
        {
            _pending = (snapshot, snapshotGeneration, blockIndex);
            return;
        }

        if (targetState is SnapshotBlockScrollTargetState.NotFound or SnapshotBlockScrollTargetState.Unavailable)
        {
            ClearPending(snapshot, snapshotGeneration, blockIndex);
            return;
        }

        if (!IsCurrentTarget(snapshot, snapshotGeneration, target.LayoutRevision))
            return;

        double targetTop = target.TargetTop;
        if (targetState == SnapshotBlockScrollTargetState.RequiresRealization)
        {
            // The requested descendant's old coordinates are not authoritative
            // until its top-level owner is measured. Scroll to the owner's
            // committed estimate now, then retry this exact block after the
            // asynchronous layout result publishes.
            _pending = (snapshot, snapshotGeneration, blockIndex);
            LazyLayoutBand visibleVectorBand = _viewport.GetVisibleBand();
            _lazyLayout.QueueForBand(
                snapshot,
                LazyLayoutBand.Union(
                    new LazyLayoutBand(target.OwnerTop, Math.Max(target.OwnerTop + 1, target.OwnerBottom)),
                    visibleVectorBand),
                visibleVectorBand,
                preserveScrollAnchor: false);
        }
        else
        {
            ClearPending(snapshot, snapshotGeneration, blockIndex);
        }

        // Queueing can start worker measurement immediately. Do not apply a
        // captured offset after a newer geometry or document generation won.
        if (!IsCurrentTarget(snapshot, snapshotGeneration, target.LayoutRevision))
            return;

        if (_surface.Scroll is not null)
            _surface.Scroll.ChangeView(null, Math.Max(0, targetTop - TopMargin), null, disableAnimation: true);
        else
            _surface.BringIntoExternalViewport(
                new Rect(0, Math.Max(0, targetTop - TopMargin), 1, Math.Max(1, target.OwnerHeight)),
                animate: false);
    }

    /// <summary>
    /// Retries the pending block request once <paramref name="snapshot"/> has
    /// published new geometry, or drops it when the snapshot was superseded.
    /// </summary>
    internal void TryResumePending(LayoutSnapshot snapshot, long snapshotGeneration)
    {
        if (_pending is not { } pending)
            return;

        if (!ReferenceEquals(pending.Snapshot, snapshot) ||
            pending.SnapshotGeneration != snapshotGeneration ||
            !IsCurrentTarget(snapshot, snapshotGeneration, snapshot.LayoutRevision))
        {
            ClearPending(pending.Snapshot, pending.SnapshotGeneration, pending.BlockIndex);
            return;
        }

        _pending = null;
        ScrollTo(pending.BlockIndex);
    }

    /// <summary>
    /// Handles internal fragment navigation URLs (e.g. <c>#footnote-def-1</c>,
    /// <c>#footnote-ref-1</c>) by scrolling to the target block directly.
    /// </summary>
    internal bool TryScrollToInternalAnchor(string url)
    {
        if (_published.Snapshot is null) return false;
        // Resolve block index from footnote registry stored on the layout context.
        // The registry is embedded in the LayoutSnapshot's block tree; walk to find
        // the first InlineContainerBox whose block carries the matching footnote tag.
        // We look for a special metadata property set by FootnoteRenderer.
        //   #footnote-def-{order}  → scroll to the definition box
        //   #footnote-ref-{order}  → scroll to the inline citation box
        const string defPrefix = "#footnote-def-";
        const string refPrefix = "#footnote-ref-";
        bool isDef = url.StartsWith(defPrefix, StringComparison.OrdinalIgnoreCase);
        bool isRef = url.StartsWith(refPrefix, StringComparison.OrdinalIgnoreCase);
        if (isDef || isRef)
        {
            string orderStr = isDef ? url.Substring(defPrefix.Length) : url.Substring(refPrefix.Length);
            if (!int.TryParse(orderStr, out int order)) return false;

            // Walk blocks looking for the tagged block index stored in the
            // footnote index dictionary on the snapshot.
            int? targetIndex = isDef
                ? _published.Snapshot.FootnoteDefBlock(order)
                : _published.Snapshot.FootnoteRefBlock(order);
            if (targetIndex is { } idx)
            {
                ScrollTo(idx);
                return true;
            }
            return false;
        }

        if (url.StartsWith("#", StringComparison.Ordinal))
        {
            var id = Uri.UnescapeDataString(url.Substring(1));
            if (_published.Snapshot.FragmentTargetBlock(id) is { } idx)
            {
                ScrollTo(idx);
                return true;
            }
        }

        return false;
    }

    private void ClearPending(LayoutSnapshot snapshot, long snapshotGeneration, int blockIndex)
    {
        if (_pending is { } pending &&
            ReferenceEquals(pending.Snapshot, snapshot) &&
            pending.SnapshotGeneration == snapshotGeneration &&
            pending.BlockIndex == blockIndex)
        {
            _pending = null;
        }
    }
}
