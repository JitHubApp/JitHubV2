using System;
using System.Collections;
using System.Collections.Generic;

namespace MarkdownRenderer.Layout.Internal.Snapshots;

/// <summary>
/// Read-only-after-construction block-index map. Layout-generated positive IDs
/// use sparse fixed-size pages over a bounded domain; custom IDs and values
/// which cannot use the dense sentinel representation are kept in a spill map.
/// </summary>
internal sealed class BlockOrdinalIndex : IReadOnlyDictionary<int, int>
{
    internal const int MaximumDenseGeneratedUpperBound = 262_144;
    private const int PageSize = 512;

    private readonly int _denseGeneratedUpperBound;
    private readonly int[]?[]? _pages;
    private Dictionary<int, int>? _spill;
    private int _count;
    private volatile bool _isFrozen;

    internal BlockOrdinalIndex(int generatedUpperBound)
    {
        if (generatedUpperBound is > 0 and <= MaximumDenseGeneratedUpperBound)
        {
            _denseGeneratedUpperBound = generatedUpperBound;
            _pages = new int[]?[(generatedUpperBound + PageSize - 1) / PageSize];
        }
    }

    public int Count => _count;

    /// <summary>
    /// The generated block-index bound this index pages densely, or zero when
    /// the bound is missing or over <see cref="MaximumDenseGeneratedUpperBound"/>
    /// and every key goes to the spill dictionary.
    /// </summary>
    internal int DenseGeneratedUpperBound => _denseGeneratedUpperBound;

    /// <summary>Number of slots in the dense page directory; zero in dictionary fallback.</summary>
    internal int DensePageCount => _pages?.Length ?? 0;

    /// <summary>
    /// The length of dense page <paramref name="pageIndex"/>, or zero while it
    /// is unallocated. Pages are allocated on the first key that falls in them.
    /// </summary>
    internal int GetDensePageLength(int pageIndex) => _pages![pageIndex]?.Length ?? 0;

    /// <summary>
    /// The encoded dense slot for <paramref name="blockIndex"/>: zero when the
    /// key has no dense entry, otherwise its ordinal plus one.
    /// </summary>
    internal int ReadEncodedDenseSlot(int blockIndex)
    {
        if (!TryGetDenseSlot(blockIndex, out int pageIndex, out int pageOffset))
            throw new ArgumentOutOfRangeException(nameof(blockIndex));
        return _pages![pageIndex]?[pageOffset] ?? 0;
    }

    /// <summary>
    /// Whether <paramref name="blockIndex"/> is stored in the spill dictionary
    /// (custom and out-of-domain keys, and ordinals the dense encoding cannot hold).
    /// </summary>
    internal bool TryGetSpilled(int blockIndex, out int ordinal)
    {
        if (_spill is not null && _spill.TryGetValue(blockIndex, out ordinal))
            return true;
        ordinal = default;
        return false;
    }

    public IEnumerable<int> Keys
    {
        get
        {
            foreach (KeyValuePair<int, int> entry in this)
                yield return entry.Key;
        }
    }

    public IEnumerable<int> Values
    {
        get
        {
            foreach (KeyValuePair<int, int> entry in this)
                yield return entry.Value;
        }
    }

    public int this[int key]
        => TryGetValue(key, out int value)
            ? value
            : throw new KeyNotFoundException();

    /// <summary>Ends constructor-time mutation and returns the immutable lookup view.</summary>
    internal IReadOnlyDictionary<int, int> Freeze()
    {
        _isFrozen = true;
        return this;
    }

    internal bool TryAdd(int blockIndex, int topLevelOrdinal)
    {
        if (_isFrozen)
            throw new InvalidOperationException("A published block ordinal index cannot be changed.");

        if (TryGetValue(blockIndex, out _))
            return false;

        if (TryGetDenseSlot(blockIndex, out int pageIndex, out int pageOffset) &&
            topLevelOrdinal >= 0 && topLevelOrdinal < int.MaxValue)
        {
            int[] page = _pages![pageIndex] ??= new int[GetPageLength(pageIndex)];
            page[pageOffset] = topLevelOrdinal + 1;
        }
        else
        {
            (_spill ??= new Dictionary<int, int>()).Add(blockIndex, topLevelOrdinal);
        }

        _count++;
        return true;
    }

    public bool ContainsKey(int key) => TryGetValue(key, out _);

    public bool TryGetValue(int key, out int value)
    {
        if (TryGetDenseSlot(key, out int pageIndex, out int pageOffset))
        {
            int[]? page = _pages![pageIndex];
            if (page is not null)
            {
                int encoded = page[pageOffset];
                if (encoded != 0)
                {
                    value = encoded - 1;
                    return true;
                }
            }
        }

        if (_spill is not null && _spill.TryGetValue(key, out value))
            return true;

        value = default;
        return false;
    }

    public IEnumerator<KeyValuePair<int, int>> GetEnumerator()
    {
        if (_pages is not null)
        {
            for (int pageIndex = 0; pageIndex < _pages.Length; pageIndex++)
            {
                int[]? page = _pages[pageIndex];
                if (page is null)
                    continue;

                int firstDenseIndex = pageIndex * PageSize;
                for (int pageOffset = 0; pageOffset < page.Length; pageOffset++)
                {
                    int encoded = page[pageOffset];
                    if (encoded != 0)
                    {
                        int blockIndex = firstDenseIndex + pageOffset + 1;
                        yield return new KeyValuePair<int, int>(blockIndex, encoded - 1);
                    }
                }
            }
        }

        if (_spill is not null)
        {
            foreach (KeyValuePair<int, int> entry in _spill)
                yield return entry;
        }
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    private bool TryGetDenseSlot(int blockIndex, out int pageIndex, out int pageOffset)
    {
        if (_pages is not null && blockIndex > 0 && blockIndex <= _denseGeneratedUpperBound)
        {
            int denseIndex = blockIndex - 1;
            pageIndex = denseIndex / PageSize;
            pageOffset = denseIndex % PageSize;
            return true;
        }

        pageIndex = 0;
        pageOffset = 0;
        return false;
    }

    private int GetPageLength(int pageIndex)
    {
        int remaining = _denseGeneratedUpperBound - pageIndex * PageSize;
        return Math.Min(PageSize, remaining);
    }
}
