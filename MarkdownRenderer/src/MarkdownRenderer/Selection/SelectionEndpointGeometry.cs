using System.Collections.Generic;
using Windows.Foundation;
using MarkdownRenderer.Document;
using MarkdownRenderer.Layout;
using MarkdownRenderer.Layout.Boxes;

namespace MarkdownRenderer.Selection;

/// <summary>
/// Finds the caret point or edge rectangle of one selection endpoint inside a
/// top-level block, descending through list, table, stack and code-block children.
/// </summary>
internal static class SelectionEndpointGeometry
{
    internal static bool TryFindSelectionEndpointGeometry(
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
                ? SelectionBlockRects.Intersect(logicalTableClip, outerClip)
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
                ? SelectionBlockRects.Intersect(logicalCodeClip, outerClip)
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
            Rect clipped = clip is { } outerClip ? SelectionBlockRects.Intersect(logicalCandidate, outerClip) : logicalCandidate;
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
}
