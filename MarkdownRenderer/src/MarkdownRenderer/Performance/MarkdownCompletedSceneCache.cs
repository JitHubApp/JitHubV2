using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace MarkdownRenderer.Performance;

/// <summary>
/// Session-wide byte-weighted LRUs for completed deferred-scene results and
/// reconstruction-only metadata. Active snapshots and pending publication
/// handoffs are owned elsewhere and excluded from these cache budgets.
/// </summary>
internal sealed class MarkdownCompletedSceneCache : IDisposable
{
    private const long MinimumEntryWeightBytes = 128;
    private const long CacheEntryOverheadBytes = 160;
    private const long MetadataEntryOverheadBytes = 160;
    private const long Megabyte = 1024 * 1024;

    private readonly object _gate = new();
    private Dictionary<SceneKey, LinkedListNode<Entry>> _entries = new();
    private LinkedList<Entry> _recency = new();
    private Dictionary<SceneKey, LinkedListNode<MetadataEntry>> _metadataEntries = new();
    private LinkedList<MetadataEntry> _metadataRecency = new();
    private readonly long _metadataBudgetBytes = (IntPtr.Size == 4 ? 2L : 4L) * Megabyte;
    private long _budgetBytes;
    private long _retainedWeightBytes;
    private long _metadataRetainedWeightBytes;
    private long _hits;
    private long _evictions;
    private long _metadataHits;
    private long _metadataEvictions;
    private bool _disposed;

    internal MarkdownCompletedSceneCache(long budgetBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(budgetBytes);
        _budgetBytes = budgetBytes;
    }

    internal long BudgetBytes
    {
        get
        {
            lock (_gate)
                return _budgetBytes;
        }
    }

    internal long RetainedWeightBytes
    {
        get
        {
            lock (_gate)
                return _retainedWeightBytes;
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

    internal long Hits
    {
        get
        {
            lock (_gate)
                return _hits;
        }
    }

    internal long Evictions
    {
        get
        {
            lock (_gate)
                return _evictions;
        }
    }

    internal long MetadataBudgetBytes => _metadataBudgetBytes;

    internal long MetadataRetainedWeightBytes
    {
        get
        {
            lock (_gate)
                return _metadataRetainedWeightBytes;
        }
    }

    internal int MetadataCount
    {
        get
        {
            lock (_gate)
                return _metadataEntries.Count;
        }
    }

    internal long MetadataHits
    {
        get
        {
            lock (_gate)
                return _metadataHits;
        }
    }

    internal long MetadataEvictions
    {
        get
        {
            lock (_gate)
                return _metadataEvictions;
        }
    }

    /// <summary>
    /// Retains a strong result only when it fits the configured byte budget.
    /// The caller remains responsible for any initial/pending layout handoff.
    /// </summary>
    internal bool Retain(
        object documentOwner,
        string marker,
        MarkdownDeferredSceneResult result,
        long estimatedWeightBytes)
    {
        ArgumentNullException.ThrowIfNull(documentOwner);
        ArgumentException.ThrowIfNullOrEmpty(marker);
        ArgumentNullException.ThrowIfNull(result);

        var key = new SceneKey(documentOwner, marker);
        long weight = Math.Max(
            MinimumEntryWeightBytes,
            SaturatingAdd(Math.Max(0, estimatedWeightBytes), CacheEntryOverheadBytes));
        lock (_gate)
        {
            if (_disposed)
                return false;
            RemoveNoLock(key);
            if (_budgetBytes == 0 || weight > _budgetBytes)
                return false;

            while (_retainedWeightBytes > _budgetBytes - weight && _recency.Last is { } oldest)
                RemoveNodeNoLock(oldest, countEviction: true);

            var entry = new Entry(key, result, weight);
            LinkedListNode<Entry> node = _recency.AddFirst(entry);
            _entries.Add(key, node);
            _retainedWeightBytes += weight;
            return _entries.TryGetValue(key, out LinkedListNode<Entry>? retained) &&
                ReferenceEquals(retained, node);
        }
    }

    internal bool TryGetResult(
        object documentOwner,
        string marker,
        out MarkdownDeferredSceneResult? result)
    {
        ArgumentNullException.ThrowIfNull(documentOwner);
        ArgumentException.ThrowIfNullOrEmpty(marker);
        var key = new SceneKey(documentOwner, marker);

        lock (_gate)
        {
            if (_disposed)
            {
                result = null;
                return false;
            }
            if (!_entries.TryGetValue(key, out LinkedListNode<Entry>? node))
            {
                result = null;
                return false;
            }

            if (!ReferenceEquals(_recency.First, node))
            {
                _recency.Remove(node);
                _recency.AddFirst(node);
            }

            _hits = SaturatingAdd(_hits, 1);
            result = node.Value.Result;
            return true;
        }
    }

    /// <summary>
    /// Retains reconstruction-only metadata independently of the completed
    /// scene graph budget. Payloads must not root vector scenes or commands.
    /// </summary>
    internal bool RetainMetadata(
        object documentOwner,
        string marker,
        object payload,
        long estimatedWeightBytes)
    {
        ArgumentNullException.ThrowIfNull(documentOwner);
        ArgumentException.ThrowIfNullOrEmpty(marker);
        ArgumentNullException.ThrowIfNull(payload);

        var key = new SceneKey(documentOwner, marker);
        long weight = Math.Max(
            MinimumEntryWeightBytes,
            SaturatingAdd(Math.Max(0, estimatedWeightBytes), MetadataEntryOverheadBytes));
        lock (_gate)
        {
            if (_disposed)
                return false;
            RemoveMetadataNoLock(key);
            if (weight > _metadataBudgetBytes)
                return false;

            while (_metadataRetainedWeightBytes > _metadataBudgetBytes - weight &&
                _metadataRecency.Last is { } oldest)
            {
                RemoveMetadataNodeNoLock(oldest, countEviction: true);
            }

            var entry = new MetadataEntry(key, payload, weight);
            LinkedListNode<MetadataEntry> node = _metadataRecency.AddFirst(entry);
            _metadataEntries.Add(key, node);
            _metadataRetainedWeightBytes += weight;
            return _metadataEntries.TryGetValue(key, out LinkedListNode<MetadataEntry>? retained) &&
                ReferenceEquals(retained, node);
        }
    }

    /// <summary>Gets and promotes one reconstruction metadata lease.</summary>
    internal bool TryGetMetadata(object documentOwner, string marker, out object? payload)
    {
        ArgumentNullException.ThrowIfNull(documentOwner);
        ArgumentException.ThrowIfNullOrEmpty(marker);
        var key = new SceneKey(documentOwner, marker);

        lock (_gate)
        {
            if (_disposed || !_metadataEntries.TryGetValue(key, out LinkedListNode<MetadataEntry>? node))
            {
                payload = null;
                return false;
            }

            if (!ReferenceEquals(_metadataRecency.First, node))
            {
                _metadataRecency.Remove(node);
                _metadataRecency.AddFirst(node);
            }

            _metadataHits = SaturatingAdd(_metadataHits, 1);
            payload = node.Value.Payload;
            return true;
        }
    }

    /// <summary>Removes strong cached results and metadata for one owner identity.</summary>
    internal void RemoveOwner(object documentOwner)
    {
        ArgumentNullException.ThrowIfNull(documentOwner);
        lock (_gate)
        {
            if (_disposed || (_entries.Count == 0 && _metadataEntries.Count == 0))
                return;

            LinkedListNode<Entry>? node = _recency.First;
            while (node is not null)
            {
                LinkedListNode<Entry>? next = node.Next;
                if (ReferenceEquals(node.Value.Key.DocumentOwner, documentOwner))
                    RemoveNodeNoLock(node, countEviction: false);
                node = next;
            }

            LinkedListNode<MetadataEntry>? metadataNode = _metadataRecency.First;
            while (metadataNode is not null)
            {
                LinkedListNode<MetadataEntry>? next = metadataNode.Next;
                if (ReferenceEquals(metadataNode.Value.Key.DocumentOwner, documentOwner))
                    RemoveMetadataNodeNoLock(metadataNode, countEviction: false);
                metadataNode = next;
            }
            if (_entries.Count == 0)
            {
                _entries = new Dictionary<SceneKey, LinkedListNode<Entry>>();
                _recency = new LinkedList<Entry>();
            }
            if (_metadataEntries.Count == 0)
            {
                _metadataEntries = new Dictionary<SceneKey, LinkedListNode<MetadataEntry>>();
                _metadataRecency = new LinkedList<MetadataEntry>();
            }
        }
    }

    /// <summary>Drops all strong scene results and metadata on a memory-pressure trim.</summary>
    internal void Trim()
    {
        lock (_gate)
        {
            if (_disposed)
                return;

            _evictions = SaturatingAdd(_evictions, _entries.Count);
            _metadataEvictions = SaturatingAdd(_metadataEvictions, _metadataEntries.Count);
            ResetCollectionsNoLock();
        }
    }

    internal void SetBudget(long budgetBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(budgetBytes);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _budgetBytes = budgetBytes;
            TrimNoLock();
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;

            _disposed = true;
            ResetCollectionsNoLock();
        }
    }

    private void TrimNoLock()
    {
        while (_retainedWeightBytes > _budgetBytes && _recency.Last is { } oldest)
            RemoveNodeNoLock(oldest, countEviction: true);

        if (_entries.Count == 0)
        {
            _entries = new Dictionary<SceneKey, LinkedListNode<Entry>>();
            _recency = new LinkedList<Entry>();
            _retainedWeightBytes = 0;
        }
    }

    private void ResetCollectionsNoLock()
    {
        // Replacing the backing collections releases their high-water arrays
        // without walking entries after a pressure trim or owner disposal.
        _entries = new Dictionary<SceneKey, LinkedListNode<Entry>>();
        _recency = new LinkedList<Entry>();
        _metadataEntries = new Dictionary<SceneKey, LinkedListNode<MetadataEntry>>();
        _metadataRecency = new LinkedList<MetadataEntry>();
        _retainedWeightBytes = 0;
        _metadataRetainedWeightBytes = 0;
    }

    private void RemoveNoLock(SceneKey key)
    {
        if (_entries.TryGetValue(key, out LinkedListNode<Entry>? node))
            RemoveNodeNoLock(node, countEviction: false);
    }

    private void RemoveMetadataNoLock(SceneKey key)
    {
        if (_metadataEntries.TryGetValue(key, out LinkedListNode<MetadataEntry>? node))
            RemoveMetadataNodeNoLock(node, countEviction: false);
    }

    private void RemoveNodeNoLock(LinkedListNode<Entry> node, bool countEviction)
    {
        _recency.Remove(node);
        _entries.Remove(node.Value.Key);
        _retainedWeightBytes = Math.Max(0, _retainedWeightBytes - node.Value.WeightBytes);
        if (countEviction)
            _evictions = SaturatingAdd(_evictions, 1);
    }

    private void RemoveMetadataNodeNoLock(LinkedListNode<MetadataEntry> node, bool countEviction)
    {
        _metadataRecency.Remove(node);
        _metadataEntries.Remove(node.Value.Key);
        _metadataRetainedWeightBytes = Math.Max(0, _metadataRetainedWeightBytes - node.Value.WeightBytes);
        if (countEviction)
            _metadataEvictions = SaturatingAdd(_metadataEvictions, 1);
    }

    private static long SaturatingAdd(long left, long right) =>
        right > 0 && left > long.MaxValue - right ? long.MaxValue : left + right;

    private readonly struct SceneKey : IEquatable<SceneKey>
    {
        internal SceneKey(object documentOwner, string marker)
        {
            DocumentOwner = documentOwner;
            Marker = marker;
        }

        internal object DocumentOwner { get; }
        internal string Marker { get; }

        public bool Equals(SceneKey other) =>
            ReferenceEquals(DocumentOwner, other.DocumentOwner) &&
            string.Equals(Marker, other.Marker, StringComparison.Ordinal);

        public override bool Equals(object? obj) => obj is SceneKey other && Equals(other);

        public override int GetHashCode() => HashCode.Combine(
            RuntimeHelpers.GetHashCode(DocumentOwner),
            StringComparer.Ordinal.GetHashCode(Marker));
    }

    private sealed class Entry(SceneKey key, MarkdownDeferredSceneResult result, long weightBytes)
    {
        internal SceneKey Key { get; } = key;
        internal MarkdownDeferredSceneResult Result { get; } = result;
        internal long WeightBytes { get; } = weightBytes;
    }

    private sealed class MetadataEntry(SceneKey key, object payload, long weightBytes)
    {
        internal SceneKey Key { get; } = key;
        internal object Payload { get; } = payload;
        internal long WeightBytes { get; } = weightBytes;
    }
}
