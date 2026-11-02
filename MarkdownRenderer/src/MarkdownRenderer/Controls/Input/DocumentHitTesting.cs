using Windows.Foundation;
using MarkdownRenderer.Layout;

namespace MarkdownRenderer.Controls.Input;

/// <summary>
/// Maps a document point to the inline container, hovered run or vector scene
/// pointer state beneath it.
/// </summary>
internal static class DocumentHitTesting
{
    internal static (Layout.Boxes.InlineContainerBox? Box, InlineRun? Run) FindInlineHover(Layout.BlockBox box, Point pt)
    {
        switch (box)
        {
            case Layout.Boxes.InlineContainerBox icb:
                var r = icb.RunAt(pt);
                return r is not null ? (icb, r) : (null, null);
            case Layout.Boxes.CodeBlockBox codeBlock:
                foreach (var chunk in codeBlock.Chunks)
                {
                    var c = FindInlineHover(chunk, pt);
                    if (c.Run is not null) return c;
                }
                return (null, null);
            case Layout.Boxes.ListItemBox lib:
                var m = FindInlineHover(lib.Marker, pt);
                if (m.Run is not null) return m;
                return FindInlineHover(lib.Content, pt);
            case Layout.Boxes.TableBox tb:
                foreach (var cell in tb.GetCellBoxes())
                {
                    var c = FindInlineHover(cell, pt);
                    if (c.Run is not null) return c;
                }
                return (null, null);
            case Layout.Boxes.StackBox sb:
                foreach (var ch in sb.Children)
                {
                    var c = FindInlineHover(ch, pt);
                    if (c.Run is not null) return c;
                }
                return (null, null);
        }
        return (null, null);
    }

    internal static void ClearHover(Layout.BlockBox box)
    {
        switch (box)
        {
            case Layout.Boxes.InlineContainerBox icb:
                icb.HoveredRun = null;
                break;
            case Layout.Boxes.CodeBlockBox codeBlock:
                foreach (var chunk in codeBlock.Chunks) ClearHover(chunk);
                break;
            case Layout.Boxes.ListItemBox lib:
                ClearHover(lib.Marker);
                ClearHover(lib.Content);
                break;
            case Layout.Boxes.TableBox tb:
                foreach (var cell in tb.GetCellBoxes()) ClearHover(cell);
                break;
            case Layout.Boxes.StackBox sb:
                foreach (var c in sb.Children) ClearHover(c);
                break;
        }
    }

    internal static bool TryGetVectorPointerStateInBlock(
        BlockBox box,
        Point point,
        out bool linked,
        out bool selectable)
    {
        switch (box)
        {
            case Layout.Boxes.VectorSceneBox vector when vector.ContainsSceneViewport(point):
                linked = vector.TryGetLinkAt(point, out _);
                selectable = vector.IsSelectableAt(point);
                return true;
            case Layout.Boxes.ListItemBox listItem:
                if (TryGetVectorPointerStateInBlock(listItem.Marker, point, out linked, out selectable))
                    return true;
                return TryGetVectorPointerStateInBlock(listItem.Content, point, out linked, out selectable);
            case Layout.Boxes.TableBox table:
                foreach (Layout.Boxes.InlineContainerBox cell in table.GetCellBoxes())
                {
                    if (TryGetVectorPointerStateInBlock(cell, point, out linked, out selectable))
                        return true;
                }
                break;
            case Layout.Boxes.StackBox stack:
                foreach (BlockBox child in stack.Children)
                {
                    if (TryGetVectorPointerStateInBlock(child, point, out linked, out selectable))
                        return true;
                }
                break;
        }

        linked = false;
        selectable = false;
        return false;
    }

    internal static Layout.Boxes.InlineContainerBox? FindInlineContainerAt(LayoutSnapshot snapshot, int blockIndex)
    {
        foreach (var b in snapshot.Blocks)
        {
            var found = FindIcbInBlock(b, blockIndex);
            if (found is not null) return found;
        }
        return null;
    }

    internal static Layout.Boxes.InlineContainerBox? FindIcbInBlock(BlockBox box, int blockIndex)
    {
        if (box is Layout.Boxes.InlineContainerBox icb && icb.BlockIndex == blockIndex) return icb;
        if (box is Layout.Boxes.CodeBlockBox codeBlock)
        {
            foreach (var chunk in codeBlock.Chunks)
            {
                if (chunk.BlockIndex == blockIndex) return chunk;
            }
        }
        if (box is Layout.Boxes.ListItemBox lib)
            return FindIcbInBlock(lib.Marker, blockIndex) ?? FindIcbInBlock(lib.Content, blockIndex);
        if (box is Layout.Boxes.StackBox sb)
        {
            foreach (var c in sb.Children)
            {
                var found = FindIcbInBlock(c, blockIndex);
                if (found is not null) return found;
            }
        }
        if (box is Layout.Boxes.TableBox tb)
        {
            foreach (var cell in tb.GetCellBoxes())
            {
                var found = FindIcbInBlock(cell, blockIndex);
                if (found is not null) return found;
            }
        }
        return null;
    }
}
