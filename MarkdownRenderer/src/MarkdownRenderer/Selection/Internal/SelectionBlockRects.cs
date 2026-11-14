using System.Collections.Generic;
using Windows.Foundation;
using MarkdownRenderer.Document;
using MarkdownRenderer.Layout.Internal.Boxes;
using MarkdownRenderer.Layout.Internal.CodeBlocks;
using MarkdownRenderer.Layout.Internal.Inlines;
using MarkdownRenderer.Layout.Internal.Snapshots;
using MarkdownRenderer.Layout.Internal.Tables;

namespace MarkdownRenderer.Selection.Internal;

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
            if (!TryGetViewportClip(snapshot, topLevelRoot, table.HorizontalViewportBounds, clip, out Rect tableClip))
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
            if (!TryGetViewportClip(snapshot, topLevelRoot, codeBlock.HorizontalViewportBounds, clip, out Rect codeClip))
                return;

            IReadOnlyList<InlineContainerBox> chunks = codeBlock.Chunks;
            for (int index = first; index < end; index++)
            {
                AppendBlockRects(snapshot, topLevelRoot, chunks[index], range,
                    physicalViewport, logicalViewport, codeClip, destination, selectionBackgrounds);
            }
            return;
        }

        AppendLeafRects(snapshot, topLevelRoot, box, range, logicalViewport, clip, destination, selectionBackgrounds);
    }

    /// <summary>
    /// Appends the visible rectangles of a leaf box, which has no list, table,
    /// stack or code-block children, and its content-bounded selection
    /// backgrounds when <paramref name="selectionBackgrounds"/> is given.
    /// </summary>
    private static void AppendLeafRects(
        LayoutSnapshot snapshot,
        BlockBox topLevelRoot,
        BlockBox box,
        DocumentRange range,
        Rect logicalViewport,
        Rect? clip,
        List<Rect> destination,
        List<SelectionBackgroundGeometry>? selectionBackgrounds)
    {
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

    /// <summary>
    /// Maps a table or code block's horizontal viewport to logical coordinates
    /// and intersects it with the inherited <paramref name="clip"/>. False when
    /// nothing of the viewport remains visible.
    /// </summary>
    internal static bool TryGetViewportClip(
        LayoutSnapshot snapshot,
        BlockBox topLevelRoot,
        Rect horizontalViewportBounds,
        Rect? clip,
        out Rect viewportClip)
    {
        Rect logicalClip = snapshot.ToLogicalRectForTopLevelBlockUnderPaint(
            topLevelRoot, horizontalViewportBounds);
        viewportClip = clip is { } outerClip
            ? Intersect(logicalClip, outerClip)
            : logicalClip;
        return viewportClip.Width > 0 && viewportClip.Height > 0;
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
