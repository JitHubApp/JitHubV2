using System;
using System.Collections.Generic;
using Microsoft.Graphics.Canvas;
using Windows.Foundation;
using Windows.UI;
using MarkdownRenderer.Diagnostics;
using MarkdownRenderer.Document;
using MarkdownRenderer.Layout;
using MarkdownRenderer.Layout.Boxes;

namespace MarkdownRenderer.Selection;

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
        if (ShakeLogger.IsEnabled)
            ShakeLogger.Log("sel-anchor",
                $"blk={anchor.BlockIndex} inl={anchor.InlineIndex} c={anchor.CharacterOffset}");
        if (hadSelection)
            Changed?.Invoke(this, System.EventArgs.Empty);
    }

    public void ExtendTo(DocumentPosition position)
    {
        Range = new DocumentRange(Range.Start, position);
        if (ShakeLogger.IsEnabled)
            ShakeLogger.Log("sel-extend",
                $"start=blk{Range.Start.BlockIndex}/c{Range.Start.CharacterOffset} " +
                $"end=blk{position.BlockIndex}/c{position.CharacterOffset}");
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
                foreach (var rect in EnumerateBlockRects(snapshot, block, block, normalized, clip: null))
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
                    AppendBlockRects(
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
            if (!TryFindSelectionEndpointGeometry(
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

    private static bool TryFindSelectionEndpointGeometry(
        LayoutSnapshot snapshot,
        BlockBox topLevelRoot,
        BlockBox box,
        DocumentPosition position,
        DocumentRange range,
        Rect searchBand,
        Rect? clip,
        bool rangeStart,
        out Point caretPoint,
        out Rect rectangle,
        out bool hasCaret,
        out bool geometryIsLogical)
    {
        caretPoint = default;
        rectangle = Rect.Empty;
        hasCaret = false;
        geometryIsLogical = false;

        if (box is ListItemBox listItem)
        {
            return TryFindSelectionEndpointGeometry(snapshot, topLevelRoot, listItem.Marker,
                       position, range, searchBand, clip, rangeStart,
                       out caretPoint, out rectangle, out hasCaret, out geometryIsLogical) ||
                   TryFindSelectionEndpointGeometry(snapshot, topLevelRoot, listItem.Content,
                       position, range, searchBand, clip, rangeStart,
                       out caretPoint, out rectangle, out hasCaret, out geometryIsLogical);
        }

        if (box is TableBox table)
        {
            Rect logicalTableClip = snapshot.ToLogicalRectForTopLevelBlockUnderPaint(
                topLevelRoot, table.HorizontalViewportBounds);
            Rect tableClip = clip is { } outerClip
                ? Intersect(logicalTableClip, outerClip)
                : logicalTableClip;
            if (tableClip.Width <= 0 || tableClip.Height <= 0)
                return false;

            foreach (InlineContainerBox cell in table.GetCellBoxesInVerticalBand(searchBand.Top, searchBand.Bottom))
            {
                if (TryFindSelectionEndpointGeometry(snapshot, topLevelRoot, cell,
                        position, range, searchBand, tableClip, rangeStart,
                        out caretPoint, out rectangle, out hasCaret, out geometryIsLogical))
                {
                    return true;
                }
            }
            return false;
        }

        if (box is StackBox stack)
        {
            (int first, int end) = stack.GetChildRangeInVerticalBand(searchBand.Top, searchBand.Bottom);
            for (int index = first; index < end; index++)
            {
                if (TryFindSelectionEndpointGeometry(snapshot, topLevelRoot, stack.Children[index],
                        position, range, searchBand, clip, rangeStart,
                        out caretPoint, out rectangle, out hasCaret, out geometryIsLogical))
                {
                    return true;
                }
            }
            return false;
        }

        if (box is CodeBlockBox codeBlock)
        {
            (int first, int end) = codeBlock.GetVisibleChunkRange(searchBand.Top, searchBand.Bottom);
            Rect logicalCodeClip = snapshot.ToLogicalRectForTopLevelBlockUnderPaint(
                topLevelRoot, codeBlock.HorizontalViewportBounds);
            Rect codeClip = clip is { } outerClip
                ? Intersect(logicalCodeClip, outerClip)
                : logicalCodeClip;
            if (codeClip.Width <= 0 || codeClip.Height <= 0)
                return false;

            for (int index = first; index < end; index++)
            {
                if (TryFindSelectionEndpointGeometry(snapshot, topLevelRoot, codeBlock.Chunks[index],
                        position, range, searchBand, codeClip, rangeStart,
                        out caretPoint, out rectangle, out hasCaret, out geometryIsLogical))
                {
                    return true;
                }
            }
            return false;
        }

        if (box.BlockIndex != position.BlockIndex)
            return false;

        if (box is InlineContainerBox inline &&
            inline.TryGetLogicalCaretPoint(position, rangeStart, out caretPoint))
        {
            hasCaret = true;
            geometryIsLogical = true;
            return true;
        }

        // Pick the true logical endpoint from the owning box's complete range
        // geometry. Filtering candidates to the visible search band first can
        // substitute a nearby line for a distant endpoint in a tall text run.
        return TryGetEndpointRectFromOwner(
            snapshot, topLevelRoot, box, range, clip, rangeStart,
            out rectangle, out geometryIsLogical);
    }

    private static bool TryGetEndpointRectFromOwner(
        LayoutSnapshot snapshot,
        BlockBox topLevelRoot,
        BlockBox box,
        DocumentRange range,
        Rect? clip,
        bool rangeStart,
        out Rect rectangle,
        out bool geometryIsLogical)
    {
        rectangle = Rect.Empty;
        bool inlineCoordinatesAreLogical = box is InlineContainerBox;
        IEnumerable<Rect> candidates = box is InlineContainerBox inline
            ? inline.GetLogicalRangeRects(range)
            : box.GetSelectionRects(range);
        Rect? first = null;
        Rect? last = null;
        foreach (Rect candidate in candidates)
        {
            if (candidate.IsEmpty)
                continue;
            Rect logicalCandidate = inlineCoordinatesAreLogical
                ? candidate
                : snapshot.ToLogicalRectForTopLevelBlockUnderPaint(topLevelRoot, candidate);
            Rect clipped = clip is { } outerClip ? Intersect(logicalCandidate, outerClip) : logicalCandidate;
            if (clipped.Width <= 0 || clipped.Height <= 0)
            {
                continue;
            }

            first ??= clipped;
            last = clipped;
        }

        if (first is not { } firstRect)
        {
            geometryIsLogical = inlineCoordinatesAreLogical;
            return false;
        }

        rectangle = rangeStart ? firstRect : last!.Value;
        geometryIsLogical = true;
        return true;
    }

    private static void AppendBlockRects(
        LayoutSnapshot snapshot,
        BlockBox topLevelRoot,
        BlockBox box,
        DocumentRange range,
        Rect physicalViewport,
        Rect logicalViewport,
        Rect? clip,
        List<Rect> destination,
        List<SelectionBackgroundGeometry>? selectionBackgrounds = null)
    {
        if (box is ListItemBox listItem)
        {
            AppendBlockRects(snapshot, topLevelRoot, listItem.Marker, range,
                physicalViewport, logicalViewport, clip, destination, selectionBackgrounds);
            AppendBlockRects(snapshot, topLevelRoot, listItem.Content, range,
                physicalViewport, logicalViewport, clip, destination, selectionBackgrounds);
            return;
        }

        if (box is TableBox table)
        {
            Rect logicalTableClip = snapshot.ToLogicalRectForTopLevelBlockUnderPaint(
                topLevelRoot, table.HorizontalViewportBounds);
            Rect tableClip = clip is { } outerClip
                ? Intersect(logicalTableClip, outerClip)
                : logicalTableClip;
            if (tableClip.Width <= 0 || tableClip.Height <= 0)
                return;

            foreach (InlineContainerBox cell in table.GetCellBoxesInVerticalBand(
                         physicalViewport.Top, physicalViewport.Bottom))
            {
                AppendBlockRects(snapshot, topLevelRoot, cell, range,
                    physicalViewport, logicalViewport, tableClip, destination, selectionBackgrounds);
            }
            return;
        }

        if (box is StackBox stack)
        {
            (int first, int end) = stack.GetChildRangeInVerticalBand(
                physicalViewport.Top, physicalViewport.Bottom);
            for (int index = first; index < end; index++)
            {
                AppendBlockRects(snapshot, topLevelRoot, stack.Children[index], range,
                    physicalViewport, logicalViewport, clip, destination, selectionBackgrounds);
            }
            return;
        }

        if (box is CodeBlockBox codeBlock)
        {
            (int first, int end) = codeBlock.GetVisibleChunkRange(
                physicalViewport.Top, physicalViewport.Bottom);
            Rect logicalCodeClip = snapshot.ToLogicalRectForTopLevelBlockUnderPaint(
                topLevelRoot, codeBlock.HorizontalViewportBounds);
            Rect codeClip = clip is { } outerClip
                ? Intersect(logicalCodeClip, outerClip)
                : logicalCodeClip;
            if (codeClip.Width <= 0 || codeClip.Height <= 0)
                return;

            IReadOnlyList<InlineContainerBox> chunks = codeBlock.Chunks;
            for (int index = first; index < end; index++)
            {
                AppendBlockRects(snapshot, topLevelRoot, chunks[index], range,
                    physicalViewport, logicalViewport, codeClip, destination, selectionBackgrounds);
            }
            return;
        }

        bool inlineCoordinatesAreLogical = box is InlineContainerBox;
        // Atomic Math/Mermaid/SVG scenes and block images keep full-block
        // interaction geometry but paint a content-bounded background.
        ISelectionBackgroundGeometrySource? backgroundOwner = selectionBackgrounds is null
            ? null
            : box as ISelectionBackgroundGeometrySource;
        IEnumerable<Rect> sourceRects = box is InlineContainerBox inline
            ? inline.GetLogicalRangeRects(range)
            : box.GetSelectionRects(range);
        foreach (Rect original in sourceRects)
        {
            if (original.IsEmpty)
                continue;
            Rect logical = inlineCoordinatesAreLogical
                ? original
                : snapshot.ToLogicalRectForTopLevelBlockUnderPaint(topLevelRoot, original);
            Rect visible = clip is { } inheritedClip
                ? Intersect(logical, inheritedClip)
                : logical;
            if (visible.Width > 0 && visible.Height > 0 && Intersects(visible, logicalViewport))
            {
                destination.Add(visible);
                if (selectionBackgrounds is not null && backgroundOwner is null)
                    selectionBackgrounds.Add(new SelectionBackgroundGeometry(visible, 1f));
            }
        }

        if (backgroundOwner is not null)
        {
            foreach (SelectionBackgroundGeometry background in
                     backgroundOwner.GetSelectionBackgroundGeometry(range))
            {
                Rect logical = snapshot.ToLogicalRectForTopLevelBlockUnderPaint(
                    topLevelRoot,
                    background.Bounds);
                Rect visible = clip is { } inheritedClip
                    ? Intersect(logical, inheritedClip)
                    : logical;
                if (visible.Width > 0 && visible.Height > 0 && Intersects(visible, logicalViewport))
                {
                    selectionBackgrounds!.Add(background with { Bounds = visible });
                }
            }
        }
    }

    private static IEnumerable<Rect> EnumerateBlockRects(
        LayoutSnapshot snapshot,
        BlockBox topLevelRoot,
        BlockBox box,
        DocumentRange range,
        Rect? clip)
    {
        // Inline text is emitted in logical coordinates directly so the final
        // snapped origin is applied before DirectWrite offsets cross the Rect
        // projection boundary. Other boxes still map their physical bounds.
        if (box is ListItemBox lib)
        {
            foreach (var r in EnumerateBlockRects(snapshot, topLevelRoot, lib.Marker, range, clip)) yield return r;
            foreach (var r in EnumerateBlockRects(snapshot, topLevelRoot, lib.Content, range, clip)) yield return r;
            yield break;
        }
        if (box is TableBox tb)
        {
            Rect tableClip = snapshot.ToLogicalRectForTopLevelBlockUnderPaint(
                topLevelRoot, tb.HorizontalViewportBounds);
            if (clip is { } inheritedClip)
                tableClip = Intersect(tableClip, inheritedClip);
            foreach (var cell in tb.GetCellBoxes())
                foreach (var r in EnumerateBlockRects(snapshot, topLevelRoot, cell, range, tableClip)) yield return r;
            yield break;
        }
        if (box is StackBox sb)
        {
            foreach (var c in sb.Children)
                foreach (var r in EnumerateBlockRects(snapshot, topLevelRoot, c, range, clip))
                    yield return r;
            yield break;
        }
        if (box is CodeBlockBox codeBlock)
        {
            Rect codeClip = snapshot.ToLogicalRectForTopLevelBlockUnderPaint(
                topLevelRoot, codeBlock.HorizontalViewportBounds);
            if (clip is { } inheritedClip)
                codeClip = Intersect(codeClip, inheritedClip);
            foreach (InlineContainerBox chunk in codeBlock.Chunks)
            {
                foreach (Rect rect in EnumerateBlockRects(
                             snapshot, topLevelRoot, chunk, range, codeClip))
                {
                    yield return rect;
                }
            }
            yield break;
        }

        bool alreadyLogical = box is InlineContainerBox;
        IEnumerable<Rect> sourceRects = box is InlineContainerBox inline
            ? inline.GetLogicalRangeRects(range)
            : box.GetSelectionRects(range);
        foreach (Rect source in sourceRects)
        {
            if (source.IsEmpty)
                continue;
            Rect logical = alreadyLogical
                ? source
                : snapshot.ToLogicalRectForTopLevelBlockUnderPaint(topLevelRoot, source);
            Rect visible = clip is { } inheritedClip ? Intersect(logical, inheritedClip) : logical;
            if (visible.Width > 0 && visible.Height > 0)
                yield return visible;
        }
    }

    private static Rect Intersect(Rect left, Rect right)
    {
        double x1 = System.Math.Max(left.Left, right.Left);
        double y1 = System.Math.Max(left.Top, right.Top);
        double x2 = System.Math.Min(left.Right, right.Right);
        double y2 = System.Math.Min(left.Bottom, right.Bottom);
        if (x2 <= x1 || y2 <= y1)
            return Rect.Empty;

        // Preserve the source extent on an axis whose edges were not clipped.
        // Windows.Foundation.Rect edge accessors can round origin + extent;
        // recomputing extent from that rounded edge makes an otherwise
        // unchanged selection rectangle depend on the root-local origin.
        double width = x1 == left.Left && x2 == left.Right
            ? left.Width
            : x2 - x1;
        double height = y1 == left.Top && y2 == left.Bottom
            ? left.Height
            : y2 - y1;
        return new Rect(x1, y1, width, height);
    }

    private static bool Intersects(Rect left, Rect right)
        => left.Right >= right.Left && left.Left <= right.Right &&
           left.Bottom >= right.Top && left.Top <= right.Bottom;

    public void PaintHighlight(CanvasDrawingSession ds, LayoutSnapshot snapshot, Color color)
    {
        bool first = true;
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
            var snapped = new Rect(x, y, w, h);
            if (first)
            {
                if (ShakeLogger.IsEnabled)
                    ShakeLogger.LogPaint(
                        "sel-rect-first", -1, snapped.X, snapped.Y, snapped.Width, snapped.Height);
                first = false;
            }
            ds.FillRectangle(snapped, color);
        }
    }
}
