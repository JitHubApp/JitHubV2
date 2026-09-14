using System;
using System.Collections.Generic;

namespace MarkdownRenderer.Layout;

/// <summary>
/// Allocation-stable vertical index for a fixed set of top-level layout blocks.
/// The owning <see cref="LayoutSnapshot"/> serializes updates and queries with its
/// layout lock, so this type deliberately does not add a second synchronization
/// boundary.
/// </summary>
internal sealed class ViewportBandIndex
{
    private readonly ViewportEntry[] _entries;

    internal ViewportBandIndex(int entryCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(entryCount);
        _entries = entryCount == 0
            ? Array.Empty<ViewportEntry>()
            : new ViewportEntry[entryCount];
    }

    internal int Count => _entries.Length;

    /// <summary>
    /// Replaces one entry while reusing the index's lifetime-sized backing array.
    /// Callers write every slot in document order before calling <see cref="Commit"/>.
    /// </summary>
    internal void SetEntry(int slot, int blockOrdinal, double top, double bottom)
    {
        if ((uint)slot >= (uint)_entries.Length)
            throw new ArgumentOutOfRangeException(nameof(slot));

        if (!double.IsFinite(top))
            top = 0;
        if (!double.IsFinite(bottom))
            bottom = top;

        bottom = Math.Max(top, bottom);
        _entries[slot] = new ViewportEntry(blockOrdinal, top, bottom, bottom);
    }

    /// <summary>
    /// Finalizes an update without replacing the backing array. Normal layout is
    /// already in document/top order, so the sort is skipped. A defensive in-place
    /// sort preserves correctness for custom boxes that violate that convention.
    /// </summary>
    internal void Commit()
    {
        bool sorted = true;
        for (int i = 1; i < _entries.Length; i++)
        {
            if (Compare(_entries[i - 1], _entries[i]) > 0)
            {
                sorted = false;
                break;
            }
        }

        if (!sorted)
            Array.Sort(_entries, ViewportEntryComparer.Instance);

        double prefixMaximumBottom = double.NegativeInfinity;
        for (int i = 0; i < _entries.Length; i++)
        {
            prefixMaximumBottom = Math.Max(prefixMaximumBottom, _entries[i].Bottom);
            _entries[i].PrefixMaximumBottom = prefixMaximumBottom;
        }
    }

    internal ViewportRange Find(double top, double bottom)
    {
        if (_entries.Length == 0)
            return default;

        if (!double.IsFinite(top))
            top = 0;
        if (!double.IsFinite(bottom))
            bottom = top;
        if (bottom < top)
            (top, bottom) = (bottom, top);

        int low = 0;
        int high = _entries.Length;
        while (low < high)
        {
            int middle = low + ((high - low) / 2);
            if (_entries[middle].PrefixMaximumBottom < top)
                low = middle + 1;
            else
                high = middle;
        }

        int end = low;
        while (end < _entries.Length && _entries[end].Top <= bottom)
            end++;

        return new ViewportRange(low, end);
    }

    internal int GetBlockOrdinal(int index)
    {
        if ((uint)index >= (uint)_entries.Length)
            throw new ArgumentOutOfRangeException(nameof(index));

        return _entries[index].BlockOrdinal;
    }

    private static int Compare(ViewportEntry left, ViewportEntry right)
    {
        int comparison = left.Top.CompareTo(right.Top);
        return comparison != 0
            ? comparison
            : left.BlockOrdinal.CompareTo(right.BlockOrdinal);
    }

    private struct ViewportEntry
    {
        internal ViewportEntry(
            int blockOrdinal,
            double top,
            double bottom,
            double prefixMaximumBottom)
        {
            BlockOrdinal = blockOrdinal;
            Top = top;
            Bottom = bottom;
            PrefixMaximumBottom = prefixMaximumBottom;
        }

        internal int BlockOrdinal;
        internal double Top;
        internal double Bottom;
        internal double PrefixMaximumBottom;
    }

    private sealed class ViewportEntryComparer : IComparer<ViewportEntry>
    {
        internal static readonly ViewportEntryComparer Instance = new();

        public int Compare(ViewportEntry left, ViewportEntry right)
            => ViewportBandIndex.Compare(left, right);
    }
}

internal readonly record struct ViewportRange(int Start, int End);
