using System;
using System.Collections.Generic;

namespace MarkdownRenderer.Utilities;

/// <summary>
/// Small, allocation-conscious weighted LRU used by renderer-owned resource
/// caches. The cache never invokes eviction callbacks while holding its lock.
/// </summary>
internal sealed class WeightedLruCache<TKey, TValue> where TKey : notnull
{
    private readonly object _gate = new();
    private readonly Dictionary<TKey, LinkedListNode<Entry>> _entries;
    private readonly LinkedList<Entry> _recency = new();
    private readonly Func<TValue, long> _weightSelector;
    private readonly Action<TValue>? _onEvicted;
    private long _budgetBytes;
    private long _retainedBytes;

    public WeightedLruCache(
        long budgetBytes,
        Func<TValue, long> weightSelector,
        Action<TValue>? onEvicted = null,
        IEqualityComparer<TKey>? comparer = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(budgetBytes);
        ArgumentNullException.ThrowIfNull(weightSelector);

        _budgetBytes = budgetBytes;
        _weightSelector = weightSelector;
        _onEvicted = onEvicted;
        _entries = new Dictionary<TKey, LinkedListNode<Entry>>(comparer);
    }

    public long BudgetBytes
    {
        get
        {
            lock (_gate)
                return _budgetBytes;
        }
    }

    internal long RetainedBytes
    {
        get
        {
            lock (_gate)
                return _retainedBytes;
        }
    }

    internal int Count
    {
        get
        {
            lock (_gate)
                return _entries.Count;
        }
    }

    public bool TryGetValue(TKey key, out TValue value)
    {
        lock (_gate)
        {
            if (!_entries.TryGetValue(key, out LinkedListNode<Entry>? node))
            {
                value = default!;
                return false;
            }

            if (!ReferenceEquals(_recency.First, node))
            {
                _recency.Remove(node);
                _recency.AddFirst(node);
            }

            value = node.Value.Value;
            return true;
        }
    }

    public void Set(TKey key, TValue value)
    {
        long weight = NormalizeWeight(_weightSelector(value));
        List<TValue>? evicted = null;

        lock (_gate)
        {
            if (_entries.TryGetValue(key, out LinkedListNode<Entry>? existing))
            {
                _entries.Remove(key);
                _recency.Remove(existing);
                _retainedBytes -= existing.Value.Weight;
                (evicted ??= []).Add(existing.Value.Value);
            }

            if (_budgetBytes > 0 && weight <= _budgetBytes)
            {
                var node = _recency.AddFirst(new Entry(key, value, weight));
                _entries.Add(key, node);
                _retainedBytes += weight;
            }
            else
            {
                (evicted ??= []).Add(value);
            }

            TrimNoLock(ref evicted);
        }

        NotifyEvicted(evicted);
    }

    public bool Remove(TKey key)
    {
        TValue? evicted = default;
        bool removed;
        lock (_gate)
        {
            removed = _entries.Remove(key, out LinkedListNode<Entry>? node);
            if (removed && node is not null)
            {
                _recency.Remove(node);
                _retainedBytes -= node.Value.Weight;
                evicted = node.Value.Value;
            }
        }

        if (removed && _onEvicted is not null)
            _onEvicted(evicted!);
        return removed;
    }

    public int RemoveWhere(Predicate<TKey> predicate)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        List<TValue>? evicted = null;
        int removed = 0;

        lock (_gate)
        {
            LinkedListNode<Entry>? node = _recency.First;
            while (node is not null)
            {
                LinkedListNode<Entry>? next = node.Next;
                if (predicate(node.Value.Key))
                {
                    _recency.Remove(node);
                    _entries.Remove(node.Value.Key);
                    _retainedBytes -= node.Value.Weight;
                    (evicted ??= []).Add(node.Value.Value);
                    removed++;
                }

                node = next;
            }
        }

        NotifyEvicted(evicted);
        return removed;
    }

    public void SetBudget(long budgetBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(budgetBytes);
        List<TValue>? evicted = null;
        lock (_gate)
        {
            _budgetBytes = budgetBytes;
            TrimNoLock(ref evicted);
        }

        NotifyEvicted(evicted);
    }

    public void Clear()
    {
        List<TValue>? evicted = null;
        lock (_gate)
        {
            if (_onEvicted is not null && _entries.Count != 0)
            {
                evicted = new List<TValue>(_entries.Count);
                foreach (Entry entry in _recency)
                    evicted.Add(entry.Value);
            }

            _entries.Clear();
            _recency.Clear();
            _retainedBytes = 0;
        }

        NotifyEvicted(evicted);
    }

    private void TrimNoLock(ref List<TValue>? evicted)
    {
        while (_recency.Last is { } oldest &&
               (_budgetBytes == 0 || _retainedBytes > _budgetBytes))
        {
            _recency.RemoveLast();
            _entries.Remove(oldest.Value.Key);
            _retainedBytes -= oldest.Value.Weight;
            (evicted ??= []).Add(oldest.Value.Value);
        }
    }

    private void NotifyEvicted(List<TValue>? evicted)
    {
        if (_onEvicted is null || evicted is null)
            return;

        foreach (TValue value in evicted)
            _onEvicted(value);
    }

    private static long NormalizeWeight(long weight) => weight <= 0 ? 1 : weight;

    private sealed record Entry(TKey Key, TValue Value, long Weight);
}
