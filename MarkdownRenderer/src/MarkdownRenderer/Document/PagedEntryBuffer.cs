using System;
using System.Collections;
using System.Collections.Generic;
using System.Numerics;

namespace MarkdownRenderer.Document;

/// <summary>
/// Append/index-update storage that avoids copying all existing entries when
/// its backing capacity grows. Mutable construction has one owner and no
/// enumeration surface. Freeze transfers its pages to an immutable view for
/// concurrent readers. MarkdownSourceMap instead serializes access under its
/// own lock and copies a stable array-backed Entries snapshot with ToArray.
/// </summary>
internal sealed class PagedEntryBuffer<T>
{
    private const int InitialPageCapacity = 4;
    private const int GeometricPageLimit = 1_024;
    private const int FixedPageShift = 10;
    private const int FixedPageCapacity = 1 << FixedPageShift;
    private const int FirstFixedPageIndex = 9;

    private T[]? _firstPage;
    private List<T[]>? _laterPages;
    private FrozenPagedEntryList<T>? _frozen;
    private int _count;

    internal int Count => _count;

    internal T this[int index]
    {
        get
        {
            if (_frozen is { } frozen)
                return frozen[index];

            ValidateExistingIndex(index);
            (int pageIndex, int offset) = Locate(index);
            return GetPage(pageIndex)[offset];
        }
        set
        {
            ThrowIfFrozen();
            ValidateExistingIndex(index);
            (int pageIndex, int offset) = Locate(index);
            GetPage(pageIndex)[offset] = value;
        }
    }

    internal void Add(T value)
    {
        ThrowIfFrozen();
        int nextCount = checked(_count + 1);
        (int pageIndex, int offset) = Locate(_count);
        if (offset == 0)
            EnsurePage(pageIndex);
        GetPage(pageIndex)[offset] = value;
        _count = nextCount;
    }

    internal T[] ToArray()
    {
        if (_frozen is { } frozen)
            return frozen.ToArray();

        if (_count == 0)
            return Array.Empty<T>();

        T[] result = new T[_count];
        int copied = 0;
        T[] firstPage = _firstPage ?? throw new InvalidOperationException("A nonempty buffer has no first page.");
        int firstPageCount = Math.Min(firstPage.Length, _count);
        Array.Copy(firstPage, 0, result, 0, firstPageCount);
        copied = firstPageCount;

        if (copied < _count)
        {
            List<T[]> laterPages = _laterPages ?? throw new InvalidOperationException("A nonempty tail has no page directory.");
            for (int pageIndex = 0; copied < _count; pageIndex++)
            {
                T[] page = laterPages[pageIndex];
                int pageCount = Math.Min(page.Length, _count - copied);
                Array.Copy(page, 0, result, copied, pageCount);
                copied = checked(copied + pageCount);
            }
        }

        if (copied != _count)
            throw new InvalidOperationException("The page directory does not cover the buffer count.");

        return result;
    }

    /// <summary>
    /// Transfers the populated pages to a stable read-only view without
    /// copying their entries. The mutable builder remains readable but cannot
    /// append or replace entries after this point.
    /// </summary>
    internal IReadOnlyList<T> Freeze()
    {
        if (_frozen is { } existing)
            return existing;

        // Finish all allocations before changing builder ownership. If making
        // the exact directory fails, the original buffer remains mutable and
        // intact. The page arrays themselves are transferred, never exposed.
        T[] firstPage = _firstPage ?? Array.Empty<T>();
        T[][] laterPages = _laterPages?.ToArray() ?? Array.Empty<T[]>();
        var frozen = new FrozenPagedEntryList<T>(firstPage, laterPages, _count);

        _frozen = frozen;
        _firstPage = null;
        _laterPages = null;
        return frozen;
    }

    private void EnsurePage(int pageIndex)
    {
        if (pageIndex == 0)
        {
            _firstPage ??= new T[GetPageCapacity(0)];
            return;
        }

        List<T[]> laterPages = _laterPages ??= new List<T[]>();
        int laterPageIndex = pageIndex - 1;
        while (laterPages.Count <= laterPageIndex)
        {
            int nextPageIndex = laterPages.Count + 1;
            laterPages.Add(new T[GetPageCapacity(nextPageIndex)]);
        }
    }

    private T[] GetPage(int pageIndex)
    {
        if (pageIndex == 0)
            return _firstPage!;

        return _laterPages![pageIndex - 1];
    }

    private static (int PageIndex, int Offset) Locate(int index)
    {
        if (index < InitialPageCapacity)
            return (0, index);

        if (index < InitialPageCapacity * 2)
            return (1, index - InitialPageCapacity);

        if (index < GeometricPageLimit)
        {
            int log2 = BitOperations.Log2((uint)index);
            int pageIndex = log2 - 1;
            int pageStart = 1 << log2;
            return (pageIndex, index - pageStart);
        }

        int fixedPageOffset = index - GeometricPageLimit;
        int fixedPageIndex = FirstFixedPageIndex + (fixedPageOffset >> FixedPageShift);
        return (fixedPageIndex, fixedPageOffset & (FixedPageCapacity - 1));
    }

    private static int GetPageCapacity(int pageIndex) => pageIndex switch
    {
        0 or 1 => InitialPageCapacity,
        <= 8 => 1 << (pageIndex + 1),
        _ => FixedPageCapacity,
    };

    private void ValidateExistingIndex(int index)
    {
        if ((uint)index >= (uint)_count)
            throw new ArgumentOutOfRangeException(nameof(index));
    }

    private void ThrowIfFrozen()
    {
        if (_frozen is not null)
            throw new InvalidOperationException("A frozen paged entry buffer cannot be modified.");
    }

    /// <summary>
    /// Immutable owner of transferred pages. It exposes only indexed and
    /// enumerated values; neither the directory nor any backing page escapes.
    /// </summary>
    private sealed class FrozenPagedEntryList<TEntry> : IReadOnlyList<TEntry>
    {
        private readonly TEntry[] _firstPage;
        private readonly TEntry[][] _laterPages;
        private readonly int _count;

        internal FrozenPagedEntryList(TEntry[] firstPage, TEntry[][] laterPages, int count)
        {
            _firstPage = firstPage;
            _laterPages = laterPages;
            _count = count;
        }

        public int Count => _count;

        public TEntry this[int index]
        {
            get
            {
                if ((uint)index >= (uint)_count)
                    throw new ArgumentOutOfRangeException(nameof(index));

                (int pageIndex, int offset) = Locate(index);
                TEntry[] page = pageIndex == 0 ? _firstPage : _laterPages[pageIndex - 1];
                return page[offset];
            }
        }

        public Enumerator GetEnumerator() => new(this);

        IEnumerator<TEntry> IEnumerable<TEntry>.GetEnumerator() => GetEnumerator();

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        internal TEntry[] ToArray()
        {
            if (_count == 0)
                return Array.Empty<TEntry>();

            var result = new TEntry[_count];
            int copied = 0;
            int firstPageCount = Math.Min(_firstPage.Length, _count);
            Array.Copy(_firstPage, 0, result, 0, firstPageCount);
            copied = firstPageCount;

            for (int pageIndex = 0; copied < _count; pageIndex++)
            {
                TEntry[] page = _laterPages[pageIndex];
                int pageCount = Math.Min(page.Length, _count - copied);
                Array.Copy(page, 0, result, copied, pageCount);
                copied = checked(copied + pageCount);
            }

            return result;
        }

        public struct Enumerator : IEnumerator<TEntry>
        {
            private readonly FrozenPagedEntryList<TEntry> _owner;
            private int _index;
            private TEntry _current;

            internal Enumerator(FrozenPagedEntryList<TEntry> owner)
            {
                _owner = owner;
                _index = 0;
                _current = default!;
            }

            public TEntry Current => _current;

            object? IEnumerator.Current
            {
                get
                {
                    if (_index == 0 || _index == _owner.Count + 1)
                        throw new InvalidOperationException("The enumerator is not positioned on an entry.");
                    return _current;
                }
            }

            public bool MoveNext()
            {
                if ((uint)_index < (uint)_owner.Count)
                {
                    _current = _owner[_index++];
                    return true;
                }

                _index = _owner.Count + 1;
                _current = default!;
                return false;
            }

            public void Reset()
            {
                _index = 0;
                _current = default!;
            }

            public void Dispose()
            {
            }
        }
    }
}
