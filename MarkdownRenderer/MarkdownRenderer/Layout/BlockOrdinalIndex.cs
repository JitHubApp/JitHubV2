using System;
using System.Collections;
using System.Collections.Generic;

namespace MarkdownRenderer.Layout;

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
