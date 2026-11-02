using System;
using System.Collections.Generic;
using Windows.Foundation;
using MarkdownRenderer.Layout;
using MarkdownRenderer.Extensions;
using MarkdownRenderer.Controls.Input;

namespace MarkdownRenderer.Controls.Focus;

/// <summary>
/// Identity, lookup and geometry queries over the focusable items of a layout
/// snapshot.
/// </summary>
internal static class FocusableItemQueries
{
    internal static bool SameFocusableIdentity(Layout.FocusableItem left, Layout.FocusableItem right) =>
        left.BlockIndex == right.BlockIndex &&
        left.InlineIndex == right.InlineIndex &&
        left.Kind == right.Kind;

    internal static int FindNearestFocusableItem(
        IReadOnlyList<(int Index, Rect Rect)> map,
        Point point)
    {
        if (map.Count == 0)
            return -1;
        var rects = new List<Rect>(map.Count);
        foreach (var entry in map)
            rects.Add(entry.Rect);
        int nearest = FocusNavigationHelper.FindNearestIndex(rects, point);
        return nearest >= 0 ? map[nearest].Index : -1;
    }

    internal static int FindFocusableItemIndex(
        System.Collections.Generic.IReadOnlyList<Layout.FocusableItem>? items,
        Layout.FocusableItem target)
    {
        if (items is null)
            return -1;

        for (int index = 0; index < items.Count; index++)
        {
            Layout.FocusableItem candidate = items[index];
            if (candidate.BlockIndex == target.BlockIndex &&
                candidate.InlineIndex == target.InlineIndex &&
                candidate.Kind == target.Kind)
            {
                return index;
            }
        }

        return -1;
    }

    internal static Rect? GetFocusableItemRectFromBlock(BlockBox box, Layout.FocusableItem item)
    {
        if (item.IsHorizontalOverflow &&
            box.BlockIndex == item.BlockIndex &&
            box is IHorizontalOverflowBox overflow)
        {
            return overflow.HorizontalViewportBounds;
        }

        if (box is Layout.Boxes.DeclarativeHostedElementBox hosted &&
            item.IsDeclarativeHostedElement &&
            hosted.BlockIndex == item.BlockIndex)
        {
            return new Rect(
                hosted.Bounds.X + hosted.Margin.Left,
                hosted.Bounds.Y + hosted.Margin.Top,
                Math.Max(0, hosted.Bounds.Width - hosted.Margin.Left - hosted.Margin.Right),
                Math.Max(0, hosted.Bounds.Height - hosted.Margin.Top - hosted.Margin.Bottom));
        }
        if (box is Layout.Boxes.EmbedBox eb && eb.BlockIndex == item.BlockIndex)
        {
            return new Rect(
                eb.Bounds.X + eb.Margin.Left,
                eb.Bounds.Y + eb.Margin.Top,
                eb.Bounds.Width - eb.Margin.Left - eb.Margin.Right,
                eb.Bounds.Height - eb.Margin.Top - eb.Margin.Bottom);
        }
        if (box is Layout.Boxes.CodeBlockBox codeBlock && codeBlock.BlockIndex == item.BlockIndex)
        {
            if (item.IsCodeBlockCopy)
                return codeBlock.CopyButtonBounds;
        }
        if (box is Layout.Boxes.VectorSceneBox vector &&
            item.IsVectorSemantic &&
            vector.BlockIndex == item.BlockIndex)
        {
            return vector.GetSemanticBounds(item.InlineIndex);
        }
        if (box is Layout.Boxes.InlineContainerBox icb && icb.BlockIndex == item.BlockIndex)
            return icb.GetRunRect(item.InlineIndex);
        if (box is Layout.Boxes.ListItemBox lib)
        {
            return GetFocusableItemRectFromBlock(lib.Marker, item)
                ?? GetFocusableItemRectFromBlock(lib.Content, item);
        }
        if (box is Layout.Boxes.StackBox sb)
        {
            foreach (var c in sb.Children)
            {
                var r = GetFocusableItemRectFromBlock(c, item);
                if (r is not null) return r;
            }
        }
        if (box is Layout.Boxes.TableBox tb)
        {
            foreach (var cell in tb.GetCellBoxes())
            {
                var r = GetFocusableItemRectFromBlock(cell, item);
                if (r is not null) return r;
            }
        }
        return null;
    }

    internal static bool SamePendingLazyFocus(
        in PendingLazyFocus left,
        in PendingLazyFocus right) =>
        ReferenceEquals(left.Snapshot, right.Snapshot) &&
        left.SnapshotGeneration == right.SnapshotGeneration &&
        left.Reverse == right.Reverse &&
        SameFocusableIdentity(left.Item, right.Item);

    internal static bool IsFocusedIdentity(
        System.Collections.Generic.IReadOnlyList<Layout.FocusableItem> items,
        int focusedIndex,
        Layout.FocusableItem item) =>
        (uint)focusedIndex < (uint)items.Count &&
        SameFocusableIdentity(items[focusedIndex], item);

    internal static bool TryFindHorizontalOverflowFocusableIndex(
        LayoutSnapshot snapshot,
        IReadOnlyList<Layout.FocusableItem> items,
        IHorizontalOverflowBox overflow,
        out int index)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(overflow);
        index = -1;

        for (int i = 0; i < items.Count; i++)
        {
            if (items[i].IsHorizontalOverflow &&
                snapshot.TryGetHorizontalOverflowForBlockIndex(
                    items[i].BlockIndex,
                    out IHorizontalOverflowBox? candidate) &&
                ReferenceEquals(candidate, overflow))
            {
                index = i;
                return true;
            }
        }

        return false;
    }

    internal static bool TryGetLinkedAtomicForFocusable(
        LayoutSnapshot snapshot,
        Layout.FocusableItem item,
        out Layout.Boxes.InlineContainerBox inline,
        out InlineRun run)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        inline = null!;
        run = null!;
        if (!item.IsLink)
            return false;

        foreach (BlockBox block in snapshot.Blocks)
        {
            if (TryGetLinkedAtomicForFocusable(block, item, out inline, out run))
                return true;
        }

        return false;
    }

    internal static bool TryGetLinkedAtomicForFocusable(
        BlockBox box,
        Layout.FocusableItem item,
        out Layout.Boxes.InlineContainerBox inline,
        out InlineRun run)
    {
        inline = null!;
        run = null!;

        switch (box)
        {
            case Layout.Boxes.InlineContainerBox icb when icb.BlockIndex == item.BlockIndex:
                foreach (InlineRun candidate in icb.Runs)
                {
                    if (candidate.InlineIndex == item.InlineIndex && LinkTargets.IsLinkedRun(candidate))
                    {
                        inline = icb;
                        run = candidate;
                        return true;
                    }
                }
                return false;
            case Layout.Boxes.ListItemBox lib:
                return TryGetLinkedAtomicForFocusable(lib.Marker, item, out inline, out run)
                       || TryGetLinkedAtomicForFocusable(lib.Content, item, out inline, out run);
            case Layout.Boxes.TableBox tb:
                foreach (var cell in tb.GetCellBoxes())
                {
                    if (TryGetLinkedAtomicForFocusable(cell, item, out inline, out run))
                        return true;
                }
                return false;
            case Layout.Boxes.StackBox sb:
                foreach (var c in sb.Children)
                {
                    if (TryGetLinkedAtomicForFocusable(c, item, out inline, out run))
                        return true;
                }
                return false;
            default:
                return false;
        }
    }

    /// <summary>
    /// Finds the keyboard-focusable vector semantic item that
    /// <paramref name="item"/> names inside <paramref name="box"/> or its descendants.
    /// </summary>
    internal static bool TryGetVectorSemanticForFocusable(
        BlockBox box,
        Layout.FocusableItem item,
        out Layout.Boxes.VectorSceneBox vector,
        out int semanticIndex)
    {
        vector = null!;
        semanticIndex = -1;
        switch (box)
        {
            case Layout.Boxes.VectorSceneBox candidate
                when candidate.BlockIndex == item.BlockIndex &&
                     candidate.GetSemanticItem(item.InlineIndex) is { } semantic &&
                     MarkdownVectorSemanticPolicy.IsKeyboardFocusable(semantic.Flags):
                vector = candidate;
                semanticIndex = item.InlineIndex;
                return true;
            case Layout.Boxes.ListItemBox listItem:
                return TryGetVectorSemanticForFocusable(listItem.Marker, item, out vector, out semanticIndex) ||
                       TryGetVectorSemanticForFocusable(listItem.Content, item, out vector, out semanticIndex);
            case Layout.Boxes.TableBox table:
                foreach (Layout.Boxes.InlineContainerBox cell in table.GetCellBoxes())
                {
                    if (TryGetVectorSemanticForFocusable(cell, item, out vector, out semanticIndex))
                        return true;
                }
                return false;
            case Layout.Boxes.StackBox stack:
                foreach (BlockBox child in stack.Children)
                {
                    if (TryGetVectorSemanticForFocusable(child, item, out vector, out semanticIndex))
                        return true;
                }
                return false;
            default:
                return false;
        }
    }
}
