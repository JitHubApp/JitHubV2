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
    private bool _isDocumentOrder;

    internal ViewportBandIndex(int entryCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(entryCount);
        _entries = entryCount == 0
            ? Array.Empty<ViewportEntry>()
            : new ViewportEntry[entryCount];
    }

    internal int Count => _entries.Length;

    /// <summary>
    /// True when the committed entry order still maps slot <c>i</c> to document
    /// block <c>i</c>. A suffix update is valid only while this remains true.
    /// </summary>
    internal bool IsDocumentOrder => _isDocumentOrder;

    /// <summary>
    /// Replaces one entry while reusing the index's lifetime-sized backing array.
    /// Callers write every slot in document order before calling <see cref="Commit"/>.
    /// </summary>
    internal void SetEntry(int slot, int blockOrdinal, double top, double bottom)
    {
        if ((uint)slot >= (uint)_entries.Length)
            throw new ArgumentOutOfRangeException(nameof(slot));

        _entries[slot] = CreateEntry(blockOrdinal, top, bottom);
    }

    /// <summary>
    /// Starts a sequential update that computes entry geometry, ordering, and
    /// prefix maxima in one pass. A nonzero suffix can preserve its prefix only
    /// when the currently committed index is still in document order. Callers
    /// must publish entries only after the corresponding layout arrangement has
    /// succeeded.
    /// </summary>
    internal SuffixWriter BeginSuffixWrite(int firstSlot)
    {
        if ((uint)firstSlot > (uint)_entries.Length)
            throw new ArgumentOutOfRangeException(nameof(firstSlot));
        if (firstSlot > 0 && !_isDocumentOrder)
            throw new InvalidOperationException("A suffix update requires a document-order prefix.");

        double prefixMaximumBottom = firstSlot == 0
            ? double.NegativeInfinity
            : _entries[firstSlot - 1].PrefixMaximumBottom;
        ViewportEntry previous = firstSlot == 0
            ? default
            : _entries[firstSlot - 1];
        return new SuffixWriter(
            this,
            firstSlot,
            prefixMaximumBottom,
            previous.BlockOrdinal,
            previous.Top,
            previous.Bottom,
            previous.PrefixMaximumBottom);
    }

    /// <summary>
    /// Stack-only writer for a full or suffix index refresh. Entries must be
    /// supplied exactly once in increasing slot order, followed by Complete.
    /// </summary>
    internal ref struct SuffixWriter
    {
        private readonly ViewportBandIndex _owner;
        private int _nextSlot;
        private double _prefixMaximumBottom;
        private ViewportEntry _previous;
        private bool _isOrdered;
        private bool _completed;

        internal SuffixWriter(
            ViewportBandIndex owner,
            int firstSlot,
            double prefixMaximumBottom,
            int previousBlockOrdinal,
            double previousTop,
            double previousBottom,
            double previousPrefixMaximumBottom)
        {
            _owner = owner;
            _nextSlot = firstSlot;
            _prefixMaximumBottom = prefixMaximumBottom;
            _previous = new ViewportEntry(
                previousBlockOrdinal,
                previousTop,
                previousBottom,
                previousPrefixMaximumBottom);
            _isOrdered = true;
            _completed = false;
        }

        internal void WriteNext(int blockOrdinal, double top, double bottom)
        {
            if (_completed)
                throw new InvalidOperationException("The viewport suffix writer is already complete.");
            if (_nextSlot >= _owner._entries.Length)
                throw new InvalidOperationException("The viewport suffix writer received too many entries.");

            int slot = _nextSlot;
            ViewportEntry entry = CreateEntry(blockOrdinal, top, bottom);
            if (blockOrdinal != slot ||
                (slot > 0 && Compare(_previous, entry) > 0))
            {
                _isOrdered = false;
            }

            _prefixMaximumBottom = Math.Max(_prefixMaximumBottom, entry.Bottom);
            entry.PrefixMaximumBottom = _isOrdered
                ? _prefixMaximumBottom
                : entry.Bottom;
            _owner._entries[slot] = entry;
            _previous = entry;
            _nextSlot = slot + 1;
        }

        internal void Complete()
        {
            if (_completed)
                throw new InvalidOperationException("The viewport suffix writer is already complete.");
            if (_nextSlot != _owner._entries.Length)
                throw new InvalidOperationException("The viewport suffix writer did not receive every entry.");

            _completed = true;
            if (_isOrdered)
            {
                _owner._isDocumentOrder = true;
                return;
            }

            // Out-of-order custom boxes retain the established defensive sort
            // and full prefix-rebuild behavior.
            _owner.Commit();
        }
    }

    private static ViewportEntry CreateEntry(int blockOrdinal, double top, double bottom)
    {
        if (!double.IsFinite(top))
            top = 0;
        if (!double.IsFinite(bottom))
            bottom = top;

        bottom = Math.Max(top, bottom);
        return new ViewportEntry(blockOrdinal, top, bottom, bottom);
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
        bool documentOrder = true;
        for (int i = 0; i < _entries.Length; i++)
        {
            prefixMaximumBottom = Math.Max(prefixMaximumBottom, _entries[i].Bottom);
            _entries[i].PrefixMaximumBottom = prefixMaximumBottom;
            documentOrder &= _entries[i].BlockOrdinal == i;
        }

        _isDocumentOrder = documentOrder;
    }

    /// <summary>
    /// Commits slots from <paramref name="firstSlot"/> onward while preserving
    /// already committed prefix maxima. This is valid only when the prior index
    /// was in document order and the replacement suffix remains in visual order.
    /// A custom box that changes that ordering falls back to the full defensive
    /// sort, exactly like <see cref="Commit"/>.
    /// </summary>
    internal void CommitSuffix(int firstSlot)
    {
        if ((uint)firstSlot > (uint)_entries.Length)
            throw new ArgumentOutOfRangeException(nameof(firstSlot));

        if (!_isDocumentOrder)
        {
            Commit();
            return;
        }

        double prefixMaximumBottom = firstSlot == 0
            ? double.NegativeInfinity
            : _entries[firstSlot - 1].PrefixMaximumBottom;
        for (int index = firstSlot; index < _entries.Length; index++)
        {
            if (_entries[index].BlockOrdinal != index ||
                (index > 0 && Compare(_entries[index - 1], _entries[index]) > 0))
            {
                // The full commit recomputes every prefix after sorting. Any
                // suffix values already updated in this loop are therefore
                // harmless while the owner holds its layout gate.
                Commit();
                return;
            }

            prefixMaximumBottom = Math.Max(prefixMaximumBottom, _entries[index].Bottom);
            _entries[index].PrefixMaximumBottom = prefixMaximumBottom;
        }

        _isDocumentOrder = true;
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

    /// <summary>
    /// Finds the first visual-order entry whose bottom reaches the supplied
    /// offset. Prefix maxima make this logarithmic even when earlier bands
    /// overlap later entries; in a gap, the result is the next band, matching
    /// the scroll-anchor contract.
    /// </summary>
    internal bool TryFindFirstBottomAtOrAfter(double offset, out int blockOrdinal)
    {
        blockOrdinal = -1;
        if (_entries.Length == 0 || !double.IsFinite(offset))
            return false;

        int low = 0;
        int high = _entries.Length;
        while (low < high)
        {
            int middle = low + ((high - low) / 2);
            if (_entries[middle].PrefixMaximumBottom < offset)
                low = middle + 1;
            else
                high = middle;
        }

        if (low >= _entries.Length || _entries[low].Bottom < offset)
            return false;

        blockOrdinal = _entries[low].BlockOrdinal;
        return true;
    }

    internal int GetBlockOrdinal(int index)
    {
        if ((uint)index >= (uint)_entries.Length)
            throw new ArgumentOutOfRangeException(nameof(index));

        return _entries[index].BlockOrdinal;
    }

    /// <summary>
    /// Returns the committed geometry copied into this index. Consumers that
    /// run outside a snapshot interaction lease must use this value snapshot
    /// instead of reading mutable block geometry.
    /// </summary>
    internal (double Top, double Bottom) GetBounds(int index)
    {
        if ((uint)index >= (uint)_entries.Length)
            throw new ArgumentOutOfRangeException(nameof(index));

        ViewportEntry entry = _entries[index];
        return (entry.Top, entry.Bottom);
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

internal readonly struct ViewportRange(int Start, int End)
{
    internal readonly int Start = Start;
    internal readonly int End = End;
}
