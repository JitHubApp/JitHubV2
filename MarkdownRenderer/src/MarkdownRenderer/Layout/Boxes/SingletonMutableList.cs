using System;
using System.Collections;
using System.Collections.Generic;

namespace MarkdownRenderer.Layout.Boxes;

/// <summary>
/// A stable, live read-only view over a mutable construction-time sequence.
/// The common empty and singleton states retain only the view object and one
/// item reference; a List is created only when a second item is appended.
/// </summary>
/// <remarks>
/// Once promoted, the backing List is retained after removals and Clear so
/// repeated construction/detach cycles keep the same growth behavior. The
/// negative sentinel distinguishes the List-backed state even when its count
/// is zero or one. A null singleton is represented by state, not by its item
/// reference, and therefore remains a real one-item collection.
/// </remarks>
internal sealed class SingletonMutableList<T> : IReadOnlyList<T> where T : class
{
    // 0 = never-promoted empty, 1 = singleton (which may itself be null),
    // -1 = List-backed; all other negative values are reserved.
    private const int ListBackedState = -1;

    private object? _storage;
    private int _state;
    private int _version;

    public int Count => _state switch
    {
        0 => 0,
        1 => 1,
        ListBackedState => ((List<T>)_storage!).Count,
        _ => throw new InvalidOperationException("Invalid singleton-list storage state."),
    };

    public T this[int index]
    {
        get
        {
            if (_state == ListBackedState)
                return ((List<T>)_storage!)[index];

            if (_state == 1 && index == 0)
                return (T)_storage!;

            throw new ArgumentOutOfRangeException(nameof(index));
        }
    }

    public void Add(T item)
    {
        if (_state == 0)
        {
            _storage = item;
            _state = 1;
        }
        else if (_state == 1)
        {
            // Publish the promoted representation only after both appends
            // succeed. A failed allocation leaves the original singleton and
            // its enumerators untouched.
            var promoted = new List<T>();
            promoted.Add((T)_storage!);
            promoted.Add(item);
            _storage = promoted;
            _state = ListBackedState;
        }
        else if (_state == ListBackedState)
        {
            ((List<T>)_storage!).Add(item);
        }
        else
        {
            throw new InvalidOperationException("Invalid singleton-list storage state.");
        }

        _version++;
    }

    public void RemoveAt(int index)
    {
        if (_state == ListBackedState)
        {
            ((List<T>)_storage!).RemoveAt(index);
            _version++;
            return;
        }

        if (_state == 1 && index == 0)
        {
            _storage = null;
            _state = 0;
            _version++;
            return;
        }

        throw new ArgumentOutOfRangeException(nameof(index));
    }

    public void Clear()
    {
        if (_state == ListBackedState)
        {
            ((List<T>)_storage!).Clear();
        }
        else
        {
            _storage = null;
            _state = 0;
        }

        // List<T>.Clear invalidates enumerators even when already empty.
        _version++;
    }

    public T[] ToArray()
    {
        if (_state == ListBackedState)
            return ((List<T>)_storage!).ToArray();
        if (_state == 0)
            return Array.Empty<T>();

        var result = new T[1];
        result[0] = (T)_storage!;
        return result;
    }

    // Concrete foreach binds to this value-returning pattern method and does
    // not box its enumerator. Interface callers keep the old BCL empty-sequence
    // adapter semantics, including an empty enumerator unaffected by later
    // mutations to this live view.
    public Enumerator GetEnumerator() => new(this);

    IEnumerator<T> IEnumerable<T>.GetEnumerator()
        => Count == 0
            ? ((IEnumerable<T>)Array.Empty<T>()).GetEnumerator()
            : GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator()
        => Count == 0
            ? ((IEnumerable<T>)Array.Empty<T>()).GetEnumerator()
            : GetEnumerator();

    public struct Enumerator : IEnumerator<T>
    {
        private readonly SingletonMutableList<T> _owner;
        private readonly int _version;
        private int _index;
        private T? _current;

        internal Enumerator(SingletonMutableList<T> owner)
        {
            _owner = owner;
            _version = owner._version;
            _index = 0;
            _current = null;
        }

        public T Current => _current!;

        object? IEnumerator.Current
        {
            get
            {
                return _index <= 0
                    ? throw new InvalidOperationException("The enumerator is not positioned on an item.")
                    : _current;
            }
        }

        public bool MoveNext()
        {
            if (_owner._version != _version)
                throw new InvalidOperationException("The collection was modified during enumeration.");

            int count = _owner.Count;
            if ((uint)_index < (uint)count)
            {
                _current = _owner[_index++];
                return true;
            }

            _index = -1;
            _current = null;
            return false;
        }

        public void Reset()
        {
            if (_owner._version != _version)
                throw new InvalidOperationException("The collection was modified during enumeration.");

            _index = 0;
            _current = null;
        }

        public void Dispose()
        {
        }
    }
}
