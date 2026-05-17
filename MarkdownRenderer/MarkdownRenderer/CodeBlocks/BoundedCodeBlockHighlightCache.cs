using System;
using System.Collections.Generic;

namespace MarkdownRenderer.CodeBlocks;

internal sealed class BoundedCodeBlockHighlightCache<TKey>
    where TKey : notnull
{
    private readonly int _capacity;
    private readonly Dictionary<TKey, CodeBlockHighlightResult> _entries = new();
    private readonly Queue<TKey> _insertionOrder = new();

    public BoundedCodeBlockHighlightCache(int capacity)
    {
        if (capacity <= 0)
            throw new ArgumentOutOfRangeException(nameof(capacity));

        _capacity = capacity;
    }

    public int Count => _entries.Count;

    public bool TryGetValue(TKey key, out CodeBlockHighlightResult result)
    {
        if (_entries.TryGetValue(key, out var value))
        {
            result = value;
            return true;
        }

        result = CodeBlockHighlightResult.Empty;
        return false;
    }

    public void Set(TKey key, CodeBlockHighlightResult result)
    {
        if (_entries.ContainsKey(key))
        {
            _entries[key] = result;
            return;
        }

        _entries[key] = result;
        _insertionOrder.Enqueue(key);
        Trim();
    }

    public void Clear()
    {
        _entries.Clear();
        _insertionOrder.Clear();
    }

    private void Trim()
    {
        while (_entries.Count > _capacity && _insertionOrder.Count > 0)
        {
            var oldest = _insertionOrder.Dequeue();
            _entries.Remove(oldest);
        }
    }
}
