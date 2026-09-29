using System;
using System.Collections.Generic;

namespace MarkdownRenderer.CodeBlocks;

internal sealed class BoundedCodeBlockHighlightCache<TKey>
    where TKey : notnull
{
    private readonly int _capacity;
    private readonly long _budgetBytes;
    private readonly Func<TKey, long>? _keyWeightSelector;
    private readonly Dictionary<TKey, LinkedListNode<Entry>> _entries = new();
    private readonly LinkedList<Entry> _recency = new();
    private long _retainedBytes;

    public BoundedCodeBlockHighlightCache(int capacity)
        : this(capacity, long.MaxValue)
    {
    }

    public BoundedCodeBlockHighlightCache(
        int capacity,
        long budgetBytes,
        Func<TKey, long>? keyWeightSelector = null)
    {
        if (capacity <= 0)
            throw new ArgumentOutOfRangeException(nameof(capacity));
        if (budgetBytes < 0)
            throw new ArgumentOutOfRangeException(nameof(budgetBytes));

        _capacity = capacity;
        _budgetBytes = budgetBytes;
        _keyWeightSelector = keyWeightSelector;
    }

    public int Count => _entries.Count;

    public long RetainedBytes => _retainedBytes;

    public bool TryGetValue(TKey key, out CodeBlockHighlightResult result)
    {
        if (_entries.TryGetValue(key, out LinkedListNode<Entry>? node))
        {
            if (!ReferenceEquals(_recency.First, node))
            {
                _recency.Remove(node);
                _recency.AddFirst(node);
            }

            result = node.Value.Result;
            return true;
        }

        result = CodeBlockHighlightResult.Empty;
        return false;
    }

    public void Set(TKey key, CodeBlockHighlightResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        if (_entries.Remove(key, out LinkedListNode<Entry>? existing))
        {
            _recency.Remove(existing);
            _retainedBytes -= existing.Value.Weight;
        }

        long weight = EstimateWeight(key, result);
        if (_budgetBytes == 0 || weight > _budgetBytes)
            return;

        var node = _recency.AddFirst(new Entry(key, result, weight));
        _entries.Add(key, node);
        _retainedBytes += weight;
        Trim();
    }

    public void Clear()
    {
        _entries.Clear();
        _recency.Clear();
        _retainedBytes = 0;
    }

    private void Trim()
    {
        while ((_entries.Count > _capacity || _retainedBytes > _budgetBytes) &&
               _recency.Last is { } oldest)
        {
            _recency.RemoveLast();
            _entries.Remove(oldest.Value.Key);
            _retainedBytes -= oldest.Value.Weight;
        }
    }

    private long EstimateWeight(TKey key, CodeBlockHighlightResult result)
    {
        long keyWeight = Math.Max(0, _keyWeightSelector?.Invoke(key) ?? 0);
        long spanWeight = (long)result.Spans.Count * 24L;
        if (keyWeight >= long.MaxValue - 128L - spanWeight)
            return long.MaxValue;
        return 128L + keyWeight + spanWeight;
    }

    private sealed record Entry(
        TKey Key,
        CodeBlockHighlightResult Result,
        long Weight);
}
