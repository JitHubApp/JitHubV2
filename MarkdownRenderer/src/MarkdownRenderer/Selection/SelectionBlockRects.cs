using System.Collections.Generic;
using Windows.Foundation;
using MarkdownRenderer.Document;
using MarkdownRenderer.Layout;
using MarkdownRenderer.Layout.Boxes;

namespace MarkdownRenderer.Selection;

/// <summary>
/// Collects the logical highlight rectangles a selection range covers inside a
/// block tree, clipped to table and code-block viewports.
/// </summary>
internal static class SelectionBlockRects
{
    internal static void AppendBlockRects(
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

    internal static IEnumerable<Rect> EnumerateBlockRects(
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

    internal static Rect Intersect(Rect left, Rect right)
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

    internal static bool Intersects(Rect left, Rect right)
        => left.Right >= right.Left && left.Left <= right.Right &&
           left.Bottom >= right.Top && left.Top <= right.Bottom;
}
