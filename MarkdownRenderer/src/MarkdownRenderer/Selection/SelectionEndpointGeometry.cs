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
            return TryFindInTable(snapshot, topLevelRoot, table,
                position, range, searchBand, clip, rangeStart,
                out caretPoint, out rectangle, out hasCaret, out geometryIsLogical);
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
            return TryFindInCodeBlock(snapshot, topLevelRoot, codeBlock,
                position, range, searchBand, clip, rangeStart,
                out caretPoint, out rectangle, out hasCaret, out geometryIsLogical);
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

    /// <summary>
    /// Searches the cells of <paramref name="table"/> in the search band,
    /// clipped to the table's horizontal viewport.
    /// </summary>
    private static bool TryFindInTable(
        LayoutSnapshot snapshot,
        BlockBox topLevelRoot,
        TableBox table,
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
        if (!SelectionBlockRects.TryGetViewportClip(
                snapshot, topLevelRoot, table.HorizontalViewportBounds, clip, out Rect tableClip))
        {
            return false;
        }

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

    /// <summary>
    /// Searches the chunks of <paramref name="codeBlock"/> in the search band,
    /// clipped to the code block's horizontal viewport.
    /// </summary>
    private static bool TryFindInCodeBlock(
        LayoutSnapshot snapshot,
        BlockBox topLevelRoot,
        CodeBlockBox codeBlock,
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
        (int first, int end) = codeBlock.GetVisibleChunkRange(searchBand.Top, searchBand.Bottom);
        if (!SelectionBlockRects.TryGetViewportClip(
                snapshot, topLevelRoot, codeBlock.HorizontalViewportBounds, clip, out Rect codeClip))
        {
            return false;
        }

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
