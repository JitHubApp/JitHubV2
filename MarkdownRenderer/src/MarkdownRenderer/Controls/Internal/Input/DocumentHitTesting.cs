using Windows.Foundation;
using MarkdownRenderer.Layout.Internal.Boxes;
using MarkdownRenderer.Layout.Internal.CodeBlocks;
using MarkdownRenderer.Layout.Internal.Inlines;
using MarkdownRenderer.Layout.Internal.Inlines.Runs;
using MarkdownRenderer.Layout.Internal.Snapshots;
using MarkdownRenderer.Layout.Internal.Tables;
using MarkdownRenderer.Layout.Internal.Vectors;

namespace MarkdownRenderer.Controls.Internal.Input;

/// <summary>
/// Maps a document point to the inline container, hovered run or vector scene
/// pointer state beneath it.
/// </summary>
internal static class DocumentHitTesting
{
    internal static (InlineContainerBox? Box, InlineRun? Run) FindInlineHover(BlockBox box, Point pt)
    {
        switch (box)
        {
            case InlineContainerBox icb:
                var r = icb.RunAt(pt);
                return r is not null ? (icb, r) : (null, null);
            case CodeBlockBox codeBlock:
                foreach (var chunk in codeBlock.Chunks)
                {
                    var c = FindInlineHover(chunk, pt);
                    if (c.Run is not null) return c;
                }
                return (null, null);
            case ListItemBox lib:
                var m = FindInlineHover(lib.Marker, pt);
                if (m.Run is not null) return m;
                return FindInlineHover(lib.Content, pt);
            case TableBox tb:
                foreach (var cell in tb.GetCellBoxes())
                {
                    var c = FindInlineHover(cell, pt);
                    if (c.Run is not null) return c;
                }
                return (null, null);
            case StackBox sb:
                foreach (var ch in sb.Children)
                {
                    var c = FindInlineHover(ch, pt);
                    if (c.Run is not null) return c;
                }
                return (null, null);
        }
        return (null, null);
    }

    internal static void ClearHover(BlockBox box)
    {
        switch (box)
        {
            case InlineContainerBox icb:
                icb.HoveredRun = null;
                break;
            case CodeBlockBox codeBlock:
                foreach (var chunk in codeBlock.Chunks) ClearHover(chunk);
                break;
            case ListItemBox lib:
                ClearHover(lib.Marker);
                ClearHover(lib.Content);
                break;
            case TableBox tb:
                foreach (var cell in tb.GetCellBoxes()) ClearHover(cell);
                break;
            case StackBox sb:
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
            case VectorSceneBox vector when vector.ContainsSceneViewport(point):
                linked = vector.TryGetLinkAt(point, out _);
                selectable = vector.IsSelectableAt(point);
                return true;
            case ListItemBox listItem:
                if (TryGetVectorPointerStateInBlock(listItem.Marker, point, out linked, out selectable))
                    return true;
                return TryGetVectorPointerStateInBlock(listItem.Content, point, out linked, out selectable);
            case TableBox table:
                foreach (InlineContainerBox cell in table.GetCellBoxes())
                {
                    if (TryGetVectorPointerStateInBlock(cell, point, out linked, out selectable))
                        return true;
                }
                break;
            case StackBox stack:
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

    internal static InlineContainerBox? FindInlineContainerAt(LayoutSnapshot snapshot, int blockIndex)
    {
        foreach (var b in snapshot.Blocks)
        {
            var found = FindIcbInBlock(b, blockIndex);
            if (found is not null) return found;
        }
        return null;
    }

    internal static InlineContainerBox? FindIcbInBlock(BlockBox box, int blockIndex)
    {
        if (box is InlineContainerBox icb && icb.BlockIndex == blockIndex) return icb;
        if (box is CodeBlockBox codeBlock)
        {
            foreach (var chunk in codeBlock.Chunks)
            {
                if (chunk.BlockIndex == blockIndex) return chunk;
            }
        }
        if (box is ListItemBox lib)
            return FindIcbInBlock(lib.Marker, blockIndex) ?? FindIcbInBlock(lib.Content, blockIndex);
        if (box is StackBox sb)
        {
            foreach (var c in sb.Children)
            {
                var found = FindIcbInBlock(c, blockIndex);
                if (found is not null) return found;
            }
        }
        if (box is TableBox tb)
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
