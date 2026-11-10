using MarkdownRenderer.Layout.Internal.Snapshots;

namespace MarkdownRenderer.Controls.Internal.Selection.Adorner;

/// <summary>
/// The paint half of the selection adorner that
/// <see cref="SelectionAdornerRefreshScheduler"/> drives from the rendering
/// frame.
/// </summary>
internal interface ISelectionAdornerPainter
{
    /// <summary>The snapshot layout revision the adorner last painted.</summary>
    long PaintedLayoutRevision { get; }

    /// <summary>Marks geometry and pixels stale without requesting a frame.</summary>
    void MarkGeometryDirty();

    /// <summary>
    /// Repaints the adorner for <paramref name="snapshot"/> while the caller
    /// holds the snapshot's paint lease.
    /// </summary>
    SelectionAdornerRefreshOperationResult RefreshUnderPaintLease(
        LayoutSnapshot snapshot,
        long generation);
}
