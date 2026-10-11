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
    internal int BlockingSnapshotInteractionCount { get; private set; }
    internal bool HasBlockingSnapshotInteractions => BlockingSnapshotInteractionCount != 0;

    internal DeferredInputQueue(int capacity = 128)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        _entries = new Entry[capacity];
    }

    internal bool TryEnqueue(
        T value,
        uint pointerId,
        bool coalescible,
        bool blocksSnapshotInteraction = true)
    {
        if (Count > 0 && coalescible)
        {
            int last = (_head + Count - 1) % _entries.Length;
            if (_entries[last].Coalescible &&
                _entries[last].PointerId == pointerId &&
                _entries[last].BlocksSnapshotInteraction == blocksSnapshotInteraction)
            {
                _entries[last] = new Entry(value, pointerId, true, blocksSnapshotInteraction);
                return true;
            }
        }
        if (Count == _entries.Length)
            return false;
        _entries[(_head + Count++) % _entries.Length] =
            new Entry(value, pointerId, coalescible, blocksSnapshotInteraction);
        if (blocksSnapshotInteraction)
            BlockingSnapshotInteractionCount++;
        return true;
    }

    internal bool TryPeek(out T value, out bool blocksSnapshotInteraction)
    {
        if (Count == 0)
        {
            value = default!;
            blocksSnapshotInteraction = false;
            return false;
        }

        Entry entry = _entries[_head];
        value = entry.Value;
        blocksSnapshotInteraction = entry.BlocksSnapshotInteraction;
        return true;
    }

    internal bool TryReplaceHead(T value, bool blocksSnapshotInteraction)
    {
        if (Count == 0)
            return false;

        Entry entry = _entries[_head];
        if (entry.BlocksSnapshotInteraction != blocksSnapshotInteraction)
        {
            BlockingSnapshotInteractionCount += blocksSnapshotInteraction ? 1 : -1;
        }

        _entries[_head] = new Entry(
            value,
            entry.PointerId,
            entry.Coalescible,
            blocksSnapshotInteraction);
        return true;
    }

    internal bool TryDequeue(out T value)
    {
        if (Count == 0)
        {
            value = default!;
            return false;
        }
        Entry entry = _entries[_head];
        value = entry.Value;
        if (entry.BlocksSnapshotInteraction)
            BlockingSnapshotInteractionCount--;
        _entries[_head] = default;
        _head = (_head + 1) % _entries.Length;
        Count--;
        return true;
    }

    internal void Clear()
    {
        Array.Clear(_entries);
        _head = Count = 0;
        BlockingSnapshotInteractionCount = 0;
    }

    private readonly struct Entry(
        T Value,
        uint PointerId,
        bool Coalescible,
        bool BlocksSnapshotInteraction)
    {
        public readonly T Value = Value;
        public readonly uint PointerId = PointerId;
        public readonly bool Coalescible = Coalescible;
        public readonly bool BlocksSnapshotInteraction = BlocksSnapshotInteraction;
    }
}
