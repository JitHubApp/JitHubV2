using System;
using System.Collections.Generic;
using Microsoft.Graphics.Canvas;
using Windows.Foundation;
using Windows.UI;
using MarkdownRenderer.Document;
using MarkdownRenderer.Layout;

namespace MarkdownRenderer.Selection.Internal;

/// <summary>
/// Holds the active selection range and produces highlight rectangles for
/// painting. Hit tests are performed by the layout snapshot; this class only
/// owns state.
/// </summary>
internal sealed class SelectionController
{
    private readonly List<int> _visibleBlockOrdinals = new();
    private readonly List<Rect> _pointSelectionRectScratch = new();

    public DocumentRange Range { get; private set; } = DocumentRange.Empty;
    public bool IsActive => !Range.IsEmpty;

    public event System.EventHandler? Changed;

    public void SetAnchor(DocumentPosition anchor)
    {
        bool hadSelection = !Range.IsEmpty;
        Range = new DocumentRange(anchor, anchor);
        if (hadSelection)
            Changed?.Invoke(this, System.EventArgs.Empty);
    }

    public void ExtendTo(DocumentPosition position)
    {
        Range = new DocumentRange(Range.Start, position);
        Changed?.Invoke(this, System.EventArgs.Empty);
    }

    public void Clear()
    {
        if (Range.IsEmpty) return;
        Range = DocumentRange.Empty;
        Changed?.Invoke(this, System.EventArgs.Empty);
    }

    public IEnumerable<Rect> GetHighlightRects(LayoutSnapshot snapshot)
    {
        if (Range.IsEmpty) yield break;
        if (!snapshot.TryBeginInteraction()) yield break;
        var normalized = Range.Normalized();
        try
        {
            foreach (var block in snapshot.Blocks)
            {
                using LayoutPlacementTransformContext.Scope placementScope =
                    snapshot.EnterPlacementTransformScopeForTopLevelBlockUnderPaint(block);
                foreach (var rect in SelectionBlockRects.EnumerateBlockRects(snapshot, block, block, normalized, clip: null))
                    yield return rect;
            }
        }
        finally
        {
            snapshot.EndInteraction();
        }
    }

    /// <summary>
    /// Appends selection rectangles for measured roots intersecting the current
    /// viewport. The caller must hold the snapshot paint/interaction lease for
    /// the duration of this call. Top-level order remains document order even
    /// though the viewport index is ordered by visual position.
    /// </summary>
    internal void AppendVisibleHighlightRects(
        LayoutSnapshot snapshot,
        Rect viewport,
        List<Rect> destination)
    {
        AppendVisibleHighlightRects(snapshot, viewport, destination, selectionBackgrounds: null);
    }

    /// <summary>
    /// Appends both authoritative selection geometry and its paint-only
    /// background geometry in one viewport traversal. Selection geometry is
    /// unchanged; atomic visuals may provide narrower translucent paint
    /// backgrounds without affecting handles or point-in-selection checks.
    /// </summary>
    internal void AppendVisibleHighlightRects(
        LayoutSnapshot snapshot,
        Rect viewport,
        List<Rect> destination,
        List<SelectionBackgroundGeometry>? selectionBackgrounds)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(destination);
        if (Range.IsEmpty)
            return;

        DocumentRange normalized = Range.Normalized();
        ViewportRange visible = snapshot.GetMeasuredViewportRangeUnderPaint(viewport);
        _visibleBlockOrdinals.Clear();
        try
        {
            for (int slot = visible.Start; slot < visible.End; slot++)
            {
                if (!snapshot.TryGetMeasuredViewportBlockAtSlotUnderPaint(slot, viewport, out _))
                    continue;
                _visibleBlockOrdinals.Add(snapshot.GetBlockOrdinalAtViewportSlotUnderPaint(slot));
            }

            _visibleBlockOrdinals.Sort();
            foreach (int ordinal in _visibleBlockOrdinals)
            {
                if (snapshot.TryGetMeasuredBlockByOrdinalUnderPaint(ordinal, out BlockBox block))
                {
                    using LayoutPlacementTransformContext.Scope placementScope =
                        snapshot.EnterPlacementTransformScopeForTopLevelBlockUnderPaint(block);
                    Rect physicalViewport = snapshot.ToPhysicalViewportForTopLevelBlockUnderPaint(block, viewport);
                    SelectionBlockRects.AppendBlockRects(
                        snapshot,
                        block,
                        block,
                        normalized,
                        physicalViewport,
                        viewport,
                        clip: null,
                        destination,
                        selectionBackgrounds);
                }
            }
        }
        finally
        {
            _visibleBlockOrdinals.Clear();
        }
    }

    /// <summary>
    /// Tests a point against the current selection using only measured roots
    /// in its nearby viewport band. The caller must hold the snapshot read
    /// lease for this method's duration.
    /// </summary>
    internal bool ContainsPointUnderReadLease(
        LayoutSnapshot snapshot,
        Point point,
        Rect viewport,
        double rasterizationScale)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (Range.IsEmpty ||
            !double.IsFinite(point.X) || !double.IsFinite(point.Y) ||
            !double.IsFinite(viewport.X) || !double.IsFinite(viewport.Y) ||
            !double.IsFinite(viewport.Width) || viewport.Width <= 0 ||
            !double.IsFinite(viewport.Height) || viewport.Height <= 0 ||
            point.X < viewport.Left || point.X > viewport.Right ||
            point.Y < viewport.Top || point.Y > viewport.Bottom)
        {
            return false;
        }

        if (!double.IsFinite(rasterizationScale) || rasterizationScale <= 0)
            rasterizationScale = 1;

        // Selection geometry is snapped outward to physical pixels before it
        // is published to the adorner. Include one physical pixel on either
        // side of the test point so a hit on that snapped fringe still visits
        // the unsnapped source rectangle that produced it.
        double pixel = 1 / rasterizationScale;
        double bandTop = System.Math.Max(viewport.Top, point.Y - pixel);
        double bandBottom = System.Math.Min(viewport.Bottom, point.Y + pixel);
        Rect pointBand = new(viewport.X, bandTop, viewport.Width, bandBottom - bandTop);

        _pointSelectionRectScratch.Clear();
        try
        {
            AppendVisibleHighlightRects(snapshot, pointBand, _pointSelectionRectScratch);
            foreach (Rect rect in _pointSelectionRectScratch)
            {
                if (SnapToRasterizationScale(rect, rasterizationScale).Contains(point))
                    return true;
            }

            return false;
        }
        finally
        {
            _pointSelectionRectScratch.Clear();
        }
    }

    /// <summary>
    /// Rounds a canvas extent up to the next whole device pixel. XAML layout
    /// rounding otherwise rounds a fractional document height to the nearest
    /// pixel, which clips the document's last partial pixel row whenever the
    /// fraction is under half a pixel.
    /// </summary>
    internal static double SnapExtentUpToPixelGrid(double extent, double scale)
    {
        if (!double.IsFinite(extent) || extent <= 0)
            return extent;
        if (!double.IsFinite(scale) || scale <= 0)
            scale = 1;

        // Tolerate floating error so an extent already on the pixel grid
        // (for example 659.0000000001 physical pixels) does not gain a pixel.
        const double PixelEpsilon = 1e-6;
        double pixels = System.Math.Ceiling(extent * scale - PixelEpsilon);
        return System.Math.Max(pixels, 1) / scale;
    }

    /// <summary>
    /// Computes a selection handle's top-left in its overlay canvas from the
    /// selection endpoint: centered horizontally on the endpoint and kept
    /// inside the viewport, with the target's upper fifth above the endpoint.
    /// The result places both edges a sixteenth of a physical pixel past a
    /// whole pixel relative to the window root. At fractional scales the
    /// DIP-to-pixel transform carries float32 noise (858.99994 instead of
    /// 859), and UI Automation truncates each edge, so an edge on or near a
    /// whole pixel can lose one: the 44-DIP target then measured 65 px at
    /// 150%. Edges clearly inside a pixel truncate and round the
    /// same way, so the reported size is exactly the target size.
    /// </summary>
    internal static (double Left, double Top) ComputeSelectionHandleOrigin(
        Point endpoint,
        double targetSize,
        Rect viewport,
        Point overlayOriginInRoot,
        double scale)
    {
        if (!double.IsFinite(scale) || scale <= 0)
            scale = 1;

        double maxLeft = System.Math.Max(0, viewport.Width - targetSize);
        double left = System.Math.Clamp(endpoint.X - targetSize / 2.0, 0, maxLeft);
        double top = System.Math.Max(0, endpoint.Y - targetSize * 0.22);
        left = SnapLocalToRootPixelGrid(left, overlayOriginInRoot.X, scale, 0, maxLeft);
        top = SnapLocalToRootPixelGrid(top, overlayOriginInRoot.Y, scale, 0, double.PositiveInfinity);
        // Return doubles: Windows.Foundation.Point stores single precision,
        // which would move a snapped edge off the grid by a few millionths.
        return (left, top);
    }

    /// <summary>
    /// The selection handle's touch-target extent in DIPs for a minimum target
    /// size at a rasterization scale: the target's whole physical pixels plus
    /// one. UI Automation truncates each edge of the handle's screen bounds,
    /// and the overlay can shift by a fraction of a pixel after placement, so
    /// a handle exactly the target size can report one pixel short.
    /// One pixel of slack keeps the reported size at or above the target at
    /// any alignment.
    /// </summary>
    internal static double ComputeSelectionHandleExtent(double targetSize, double scale)
    {
        if (!double.IsFinite(targetSize) || targetSize <= 0)
            return targetSize;
        if (!double.IsFinite(scale) || scale <= 0)
            scale = 1;
        const double PixelEpsilon = 1e-6;
        double pixels = System.Math.Ceiling(targetSize * scale - PixelEpsilon) + 1;
        return pixels / scale;
    }

    // A small fixed offset into the pixel, far larger than float32 transform
    // noise (about 1e-4 px at window coordinates) and invisible on screen.
    internal const double SelectionHandlePixelBias = 1.0 / 16.0;

    // Moves a local coordinate to the nearest position whose root coordinate
    // is a whole physical pixel plus SelectionHandlePixelBias, preferring a
    // grid point inside [min, max].
    private static double SnapLocalToRootPixelGrid(double local, double originInRoot, double scale, double min, double max)
    {
        if (!double.IsFinite(originInRoot))
            return local;

        double pixels = System.Math.Round((originInRoot + local) * scale - SelectionHandlePixelBias, System.MidpointRounding.AwayFromZero)
            + SelectionHandlePixelBias;
        double snapped = pixels / scale - originInRoot;
        if (snapped < min - 1e-9)
            snapped = (pixels + 1) / scale - originInRoot;
        else if (snapped > max + 1e-9)
            snapped = (pixels - 1) / scale - originInRoot;
        return snapped;
    }

    internal static Rect SnapToRasterizationScale(Rect rect, double scale)
    {
        if (!double.IsFinite(scale) || scale <= 0)
            scale = 1;

        double pixelLeft = System.Math.Floor(rect.Left * scale);
        double pixelTop = System.Math.Floor(rect.Top * scale);
        double pixelRight = System.Math.Ceiling(rect.Right * scale);
        double pixelBottom = System.Math.Ceiling(rect.Bottom * scale);
        double width = (pixelRight - pixelLeft) / scale;
        double height = (pixelBottom - pixelTop) / scale;
        if (!double.IsFinite(width) || !double.IsFinite(height) || width <= 0 || height <= 0)
            return Rect.Empty;

        return new Rect(pixelLeft / scale, pixelTop / scale, width, height);
    }

    internal bool TryGetSelectionEndpointRect(
        LayoutSnapshot snapshot,
        DocumentPosition position,
        DocumentRange selectionRange,
        Rect viewport,
        bool rangeStart,
        out Rect rectangle)
    {
        return TryGetSelectionEndpointRect(
            snapshot,
            position,
            selectionRange,
            viewport,
            verticalSearchMargin: 64,
            rangeStart,
            out rectangle);
    }

    internal bool TryGetSelectionEndpointRect(
        LayoutSnapshot snapshot,
        DocumentPosition position,
        DocumentRange selectionRange,
        Rect viewport,
        double verticalSearchMargin,
        bool rangeStart,
        out Rect rectangle)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        bool found = TryGetSelectionEndpointGeometry(
            snapshot,
            position,
            selectionRange,
            viewport,
            verticalSearchMargin,
            rangeStart,
            out _,
            out rectangle,
            out bool hasCaret);
        return found && !hasCaret;
    }

    internal bool TryGetSelectionEndpointGeometry(
        LayoutSnapshot snapshot,
        DocumentPosition position,
        DocumentRange selectionRange,
        Rect viewport,
        double verticalSearchMargin,
        bool rangeStart,
        out Point caretPoint,
        out Rect rectangle,
        out bool hasCaret)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        caretPoint = default;
        rectangle = Rect.Empty;
        hasCaret = false;
        if (!double.IsFinite(verticalSearchMargin) || verticalSearchMargin < 0 ||
            !double.IsFinite(viewport.Top) || !double.IsFinite(viewport.Bottom) ||
            !double.IsFinite(viewport.Width) || viewport.Width < 0 ||
            !snapshot.TryGetMeasuredTopLevelBlockForBlockIndexUnderPaint(position.BlockIndex, out BlockBox root))
        {
            return false;
        }

        // A touch target may overlap the viewport while its logical endpoint is
        // just outside it. Search only the handle's vertical extent and the one
        // owning root, never the complete document tree.
        Rect searchBand = new(
            viewport.X,
            viewport.Top - verticalSearchMargin,
            viewport.Width,
            viewport.Height + verticalSearchMargin * 2);
        Rect physicalSearchBand = snapshot.ToPhysicalViewportForTopLevelBlockUnderPaint(root, searchBand);
        using (snapshot.EnterPlacementTransformScopeForTopLevelBlockUnderPaint(root))
        {
            if (!SelectionEndpointGeometry.TryFindSelectionEndpointGeometry(
                snapshot,
                root,
                root,
                position,
                selectionRange.Normalized(),
                physicalSearchBand,
                clip: null,
                rangeStart,
                out caretPoint,
                out rectangle,
                out hasCaret,
                out bool geometryIsLogical))
            {
                return false;
            }

            if (!geometryIsLogical)
            {
                if (hasCaret)
                    caretPoint = snapshot.ToLogicalPointForBlockIndexUnderPaint(position.BlockIndex, caretPoint);
                if (!rectangle.IsEmpty)
                    rectangle = snapshot.ToLogicalRectForTopLevelBlockUnderPaint(root, rectangle);
            }
        }

        return true;
    }

    public void PaintHighlight(CanvasDrawingSession ds, LayoutSnapshot snapshot, Color color)
    {
        foreach (var rect in GetHighlightRects(snapshot))
        {
            // Snap to integer DIPs and use square corners. Sub-pixel rect edges
            // (especially with rounded corners) cause neighbouring glyph dirty
            // rects to vary frame-to-frame during a drag, which manifests as
            // text "shake". Browser-style square selection avoids corner AA.
            double x = System.Math.Floor(rect.X);
            double y = System.Math.Floor(rect.Y);
            double w = System.Math.Ceiling(rect.X + rect.Width) - x;
            double h = System.Math.Ceiling(rect.Y + rect.Height) - y;
            ds.FillRectangle(new Rect(x, y, w, h), color);
        }
    }
}
