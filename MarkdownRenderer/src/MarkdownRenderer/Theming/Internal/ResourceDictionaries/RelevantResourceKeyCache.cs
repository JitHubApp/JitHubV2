using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace MarkdownRenderer.Theming.Internal.ResourceDictionaries;

/// <summary>
/// Caches the small relevant-key projection of dictionary-like nodes without
/// retaining the nodes themselves. Values are deliberately not cached: graph
/// capture re-reads each current value so brush replacement and mutation are
/// reflected on the next snapshot. A key-count change refreshes discovery;
/// callers can invalidate explicitly for a same-count key-set substitution.
/// </summary>
internal sealed class RelevantResourceKeyCache<TNode>
    where TNode : class
{
    private readonly Func<TNode, int> _getKeyCount;
    private readonly Func<TNode, IEnumerable<string>> _enumerateKeys;
    private readonly Predicate<string> _isRelevant;
    private readonly ConditionalWeakTable<TNode, CacheEntry> _entries = new();

    internal RelevantResourceKeyCache(
        Func<TNode, int> getKeyCount,
        Func<TNode, IEnumerable<string>> enumerateKeys,
        Predicate<string> isRelevant)
    {
        _getKeyCount = getKeyCount ?? throw new ArgumentNullException(nameof(getKeyCount));
        _enumerateKeys = enumerateKeys ?? throw new ArgumentNullException(nameof(enumerateKeys));
        _isRelevant = isRelevant ?? throw new ArgumentNullException(nameof(isRelevant));
    }

    internal IReadOnlyList<string> GetRelevantKeys(TNode node)
    {
        ArgumentNullException.ThrowIfNull(node);
        CacheEntry entry = _entries.GetValue(node, static _ => new CacheEntry());
        return entry.GetOrRefresh(node, _getKeyCount, _enumerateKeys, _isRelevant);
    }

    /// <summary>
    /// Tests whether a relevant key is declared by the node without repeatedly
    /// enumerating a projected platform dictionary. The cached array is sorted
    /// so repeated per-property lookups remain logarithmic after one discovery
    /// pass, while values themselves are still read live by the caller.
    /// </summary>
    internal bool ContainsRelevantKey(TNode node, string key)
    {
        ArgumentNullException.ThrowIfNull(node);
        ArgumentNullException.ThrowIfNull(key);
        CacheEntry entry = _entries.GetValue(node, static _ => new CacheEntry());
        string[] relevantKeys = entry.GetOrRefresh(node, _getKeyCount, _enumerateKeys, _isRelevant);
        return Array.BinarySearch(relevantKeys, key, StringComparer.Ordinal) >= 0;
    }

    internal void Invalidate() => _entries.Clear();

    private sealed class CacheEntry
    {
        private readonly object _gate = new();
        private int _observedKeyCount = -1;
        private string[] _relevantKeys = [];

        internal string[] GetOrRefresh(
            TNode node,
            Func<TNode, int> getKeyCount,
            Func<TNode, IEnumerable<string>> enumerateKeys,
            Predicate<string> isRelevant)
        {
            int currentKeyCount = getKeyCount(node);
            lock (_gate)
            {
                if (currentKeyCount == _observedKeyCount)
                    return _relevantKeys;

                var relevantKeys = new List<string>();
                foreach (string key in enumerateKeys(node))
                {
                    if (isRelevant(key))
                        relevantKeys.Add(key);
                }

                _relevantKeys = relevantKeys.ToArray();
                Array.Sort(_relevantKeys, StringComparer.Ordinal);
                _observedKeyCount = currentKeyCount;
                return _relevantKeys;
            }
        }
    }
}
