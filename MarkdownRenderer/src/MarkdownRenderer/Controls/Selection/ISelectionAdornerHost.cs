using System;
using System.Collections.Generic;
using Microsoft.Graphics.Canvas;
using Windows.Foundation;
using MarkdownRenderer.Layout.Internal.Boxes;
using MarkdownRenderer.Layout.Internal.Snapshots;

namespace MarkdownRenderer.Controls.Selection;

/// <summary>
/// The renderer services a <see cref="SelectionAdornerPresenter"/> calls
/// while it refreshes under the snapshot's paint lease.
/// </summary>
internal interface ISelectionAdornerHost
{
    /// <summary>
    /// True while device-loss recovery is waiting to rebuild. Recovery owns
    /// its backoff, so the adorner does not retry every frame meanwhile.
    /// </summary>
    bool IsDeviceRecoveryQueued { get; }

    /// <summary>True when keyboard focus is on a link, which the adorner paints.</summary>
    bool HasFocusedLink { get; }

    /// <summary>
    /// Starts geometry recovery when <paramref name="snapshot"/> needs it.
    /// Returns true when recovery was requested.
    /// </summary>
    bool RequestGeometryRecoveryIfRequired(LayoutSnapshot snapshot);

    /// <summary>Handles a device-lost failure raised while painting.</summary>
    void HandleDeviceLost(Exception exception);

    /// <summary>
    /// Rebuilds the document because the shared Win2D device no longer
    /// matches the one that measured the snapshot.
    /// </summary>
    void RequestRebuildForDeviceChange();

    /// <summary>Positions the touch selection handles for the painted viewport.</summary>
    void UpdateSelectionHandlesUnderPaint(LayoutSnapshot snapshot, Rect viewport);

    /// <summary>
    /// Paints the selection, hovered link and focused link into the adorner
    /// surface for <paramref name="viewport"/>.
    /// </summary>
    void PaintInteractiveDocumentState(
        LayoutSnapshot snapshot,
        CanvasDrawingSession drawingSession,
        Rect viewport,
        IReadOnlyList<SelectionBackgroundGeometry> selectionBackgrounds);
}
