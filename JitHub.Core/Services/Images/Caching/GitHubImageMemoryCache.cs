using System;
using System.Collections.Generic;

namespace JitHub.Core.Services.Images.Caching;

/// <summary>
/// Bounded LRU of decoded-ready cache reads, keyed by account-partitioned
/// cache key and limited by both entry count and total bytes.
/// </summary>
internal sealed class GitHubImageMemoryCache
{
    private readonly long _byteBudget;
    private readonly int _entryBudget;
    private readonly object _gate = new();
    private readonly Dictionary<string, LinkedListNode<Entry>> _entries = new(StringComparer.Ordinal);
    private readonly LinkedList<Entry> _lru = new();
    private long _bytes;

    public GitHubImageMemoryCache(long byteBudget, int entryBudget)
    {
        _byteBudget = Math.Max(1, byteBudget);
        _entryBudget = Math.Max(1, entryBudget);
    }

    public bool TryGet(string key, out GitHubImageCacheRead? value)
    {
        lock (_gate)
        {
            if (!_entries.TryGetValue(key, out LinkedListNode<Entry>? node))
            {
                value = null;
                return false;
            }

            _lru.Remove(node);
            _lru.AddFirst(node);
            value = node.Value.Value;
            return true;
        }
    }

    public void Set(string key, GitHubImageCacheRead value)
    {
        long byteLength = value.Bytes.LongLength;
        if (byteLength <= 0 || byteLength > _byteBudget)
        {
            return;
        }

        lock (_gate)
        {
            if (_entries.Remove(key, out LinkedListNode<Entry>? existing))
            {
                _lru.Remove(existing);
                _bytes -= existing.Value.ByteLength;
            }

            LinkedListNode<Entry> node = new(new Entry(key, value, byteLength));
            _lru.AddFirst(node);
            _entries[key] = node;
            _bytes += byteLength;
            while (_entries.Count > _entryBudget || _bytes > _byteBudget)
            {
                LinkedListNode<Entry>? tail = _lru.Last;
                if (tail is null)
                {
                    break;
                }

                _lru.RemoveLast();
                _entries.Remove(tail.Value.Key);
                _bytes -= tail.Value.ByteLength;
            }
        }
    }

    public void Remove(string key)
    {
        lock (_gate)
        {
            if (_entries.Remove(key, out LinkedListNode<Entry>? node))
            {
                _lru.Remove(node);
                _bytes -= node.Value.ByteLength;
            }
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _entries.Clear();
            _lru.Clear();
            _bytes = 0;
        }
    }

    private sealed record Entry(string Key, GitHubImageCacheRead Value, long ByteLength);
}
