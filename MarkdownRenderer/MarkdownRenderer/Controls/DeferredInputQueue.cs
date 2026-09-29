using System;

namespace MarkdownRenderer.Controls;

/// <summary>
/// UI-thread-owned bounded input backlog. Only adjacent moves from the same
/// pointer may be coalesced; press/release/cancel transitions retain their order.
/// Overflow explicitly cancels a gesture instead of losing an arbitrary edge.
/// </summary>
internal sealed class DeferredInputQueue<T>
{
    private readonly Entry[] _entries;
    private int _head;
    internal int Count { get; private set; }

    internal DeferredInputQueue(int capacity = 128)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        _entries = new Entry[capacity];
    }

    internal bool TryEnqueue(T value, uint pointerId, bool coalescible)
    {
        if (Count > 0 && coalescible)
        {
            int last = (_head + Count - 1) % _entries.Length;
            if (_entries[last].Coalescible && _entries[last].PointerId == pointerId)
            {
                _entries[last] = new Entry(value, pointerId, true);
                return true;
            }
        }
        if (Count == _entries.Length)
            return false;
        _entries[(_head + Count++) % _entries.Length] = new Entry(value, pointerId, coalescible);
        return true;
    }

    internal bool TryDequeue(out T value)
    {
        if (Count == 0)
        {
            value = default!;
            return false;
        }
        value = _entries[_head].Value;
        _entries[_head] = default;
        _head = (_head + 1) % _entries.Length;
        Count--;
        return true;
    }

    internal void Clear()
    {
        Array.Clear(_entries);
        _head = Count = 0;
    }

    private readonly record struct Entry(T Value, uint PointerId, bool Coalescible);
}
