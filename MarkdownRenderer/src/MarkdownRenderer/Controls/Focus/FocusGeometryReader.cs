using System.Collections.Generic;
using MarkdownRenderer.Layout;
using Windows.Foundation;

namespace MarkdownRenderer.Controls.Focus;

/// <summary>
/// Reads focusable item rectangles from the published snapshot under its
/// interaction lease. A read reports <see cref="FocusGeometryReadStatus.Busy"/>
/// while the lease is taken and <see cref="FocusGeometryReadStatus.Stale"/>
/// once the snapshot, generation or layout revision it was asked for is no
/// longer published, so callers retry instead of using old geometry.
/// </summary>
internal sealed class FocusGeometryReader
{
    private readonly MarkdownViewLifetime _lifetime;
    private readonly PublishedLayout _published;

    internal FocusGeometryReader(MarkdownViewLifetime lifetime, PublishedLayout published)
    {
        _lifetime = lifetime;
        _published = published;
    }

    /// <summary>The rectangle of <paramref name="item"/> in the published layout, or null if it cannot be read now.</summary>
    internal Rect? GetCurrentItemRect(FocusableItem item)
    {
        if (_published.Snapshot is not { } snapshot)
            return null;
        return TryCaptureItemRect(
            snapshot,
            _published.Generation,
            snapshot.LayoutRevision,
            item,
            out Rect? rect) == FocusGeometryReadStatus.Ready
            ? rect
            : null;
    }

    internal FocusGeometryReadStatus TryCaptureItemRect(
        LayoutSnapshot snapshot,
        long snapshotGeneration,
        long layoutRevision,
        FocusableItem item,
        out Rect? rect)
    {
        rect = null;
        if (!IsCurrent(snapshot, snapshotGeneration, layoutRevision))
            return FocusGeometryReadStatus.Stale;
        if (!snapshot.TryBeginInteraction())
            return FocusGeometryReadStatus.Busy;

        bool captured = false;
        try
        {
            if (IsCurrent(snapshot, snapshotGeneration, layoutRevision))
            {
                rect = FocusableItemQueries.GetLogicalRectUnderPaint(snapshot, item);
                captured = true;
            }
        }
        finally
        {
            snapshot.EndInteraction();
        }

        return captured && IsCurrent(snapshot, snapshotGeneration, layoutRevision)
            ? FocusGeometryReadStatus.Ready
            : FocusGeometryReadStatus.Stale;
    }

    /// <summary>
    /// Reads the non-empty rectangle of every item in <paramref name="items"/>,
    /// paired with the item's index.
    /// </summary>
    internal FocusGeometryReadStatus TryCaptureRectMap(
        LayoutSnapshot snapshot,
        long snapshotGeneration,
        long layoutRevision,
        IReadOnlyList<FocusableItem>? items,
        out List<(int Index, Rect Rect)> result)
    {
        result = new List<(int Index, Rect Rect)>();
        if (!IsCurrent(snapshot, snapshotGeneration, layoutRevision))
            return FocusGeometryReadStatus.Stale;
        if (items is null || items.Count == 0)
            return FocusGeometryReadStatus.Ready;
        if (!snapshot.TryBeginInteraction())
            return FocusGeometryReadStatus.Busy;

        bool captured = false;
        try
        {
            if (IsCurrent(snapshot, snapshotGeneration, layoutRevision))
            {
                for (int i = 0; i < items.Count; i++)
                {
                    if (FocusableItemQueries.GetLogicalRectUnderPaint(snapshot, items[i]) is
                        { Width: > 0, Height: > 0 } rect)
                    {
                        result.Add((i, rect));
                    }
                }
                captured = true;
            }
        }
        finally
        {
            snapshot.EndInteraction();
        }

        return captured && IsCurrent(snapshot, snapshotGeneration, layoutRevision)
            ? FocusGeometryReadStatus.Ready
            : FocusGeometryReadStatus.Stale;
    }

    private bool IsCurrent(LayoutSnapshot snapshot, long snapshotGeneration, long layoutRevision) =>
        !_lifetime.IsDisposed && !_lifetime.IsUnloaded &&
        _published.IsCurrent(snapshot, snapshotGeneration, layoutRevision);
}
