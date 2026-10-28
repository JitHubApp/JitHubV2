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
    private readonly Func<TKey, long>? _keyWeightSelector;
    private readonly Action<TValue>? _onEvicted;
    private long _budgetBytes;
    private long _retainedBytes;

    public WeightedLruCache(
        long budgetBytes,
        Func<TValue, long> weightSelector,
        Action<TValue>? onEvicted = null,
        IEqualityComparer<TKey>? comparer = null,
        Func<TKey, long>? keyWeightSelector = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(budgetBytes);
        ArgumentNullException.ThrowIfNull(weightSelector);

        _budgetBytes = budgetBytes;
        _weightSelector = weightSelector;
        _keyWeightSelector = keyWeightSelector;
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

    /// <summary>
    /// Finds the highest-scoring matching entry under the same recency lock as
    /// ordinary lookups. Intended for small, byte-bounded variant sets such as
    /// already-uploaded image previews; this does not change the cache budget.
    /// </summary>
    internal bool TryGetBestMatch(
        Func<TKey, TValue, bool> matches,
        Func<TValue, long> score,
        out TValue value)
    {
        ArgumentNullException.ThrowIfNull(matches);
        ArgumentNullException.ThrowIfNull(score);

        lock (_gate)
        {
            LinkedListNode<Entry>? best = null;
            long bestScore = long.MinValue;
            for (LinkedListNode<Entry>? node = _recency.First; node is not null; node = node.Next)
            {
                if (!matches(node.Value.Key, node.Value.Value))
                    continue;
                long candidateScore = score(node.Value.Value);
                if (candidateScore <= bestScore)
                    continue;
                best = node;
                bestScore = candidateScore;
            }

            if (best is null)
            {
                value = default!;
                return false;
            }

            if (!ReferenceEquals(_recency.First, best))
            {
                _recency.Remove(best);
                _recency.AddFirst(best);
            }
            value = best.Value.Value;
            return true;
        }
    }

    /// <summary>
    /// Best-effort metadata replacement for immutable reference payloads. A
    /// missing, changed or over-budget entry is left untouched; this never
    /// inserts a key or evicts another entry. On failure the caller continues
    /// to own the proposed replacement. Equality overrides are not consulted.
    /// </summary>
    internal bool TryReplaceIfReferenceMatches(TKey key, TValue expected, TValue replacement) =>
        TryReplaceReferences(key, default!, hasAlias: false, expected, replacement);

    /// <summary>
    /// Replaces the primary entry and an optional still-identical alias in one
    /// transaction. A changed or missing alias is untouched; an over-budget
    /// pair leaves both entries untouched. This does not create an alias.
    /// </summary>
    internal bool TryReplaceWithAliasIfReferenceMatches(
        TKey key, TKey aliasKey, TValue expected, TValue replacement) =>
        TryReplaceReferences(key, aliasKey, hasAlias: true, expected, replacement);

    private bool TryReplaceReferences(
        TKey key, TKey aliasKey, bool hasAlias, TValue expected, TValue replacement)
    {
        long valueWeight = NormalizeValueWeight(_weightSelector(replacement));
        long keyWeight = NormalizeKeyWeight(_keyWeightSelector?.Invoke(key) ?? 0);
        long weight = SaturatingAdd(valueWeight, keyWeight);
        long aliasWeight = hasAlias
            ? SaturatingAdd(valueWeight, NormalizeKeyWeight(_keyWeightSelector?.Invoke(aliasKey) ?? 0))
            : 0;
        TValue evicted;
        bool replacedAlias;

        lock (_gate)
        {
            if (!_entries.TryGetValue(key, out LinkedListNode<Entry>? node) ||
                !ReferenceEquals(node.Value.Value, expected))
                return false;
            if (ReferenceEquals(expected, replacement))
                return true;

            LinkedListNode<Entry>? alias = null;
            if (hasAlias && _entries.TryGetValue(aliasKey, out LinkedListNode<Entry>? candidate) &&
                !ReferenceEquals(candidate, node) && ReferenceEquals(candidate.Value.Value, expected))
                alias = candidate;

            long remainingWeight = _retainedBytes - node.Value.Weight;
            long replacementWeight = weight;
            if (alias is not null)
            {
                // Do not saturate the pair's sum: that could admit two entries
                // whose actual total exceeds even a long.MaxValue budget.
                if (aliasWeight > _budgetBytes || weight > _budgetBytes - aliasWeight)
                    return false;
                replacementWeight += aliasWeight;
                remainingWeight -= alias.Value.Weight;
            }
            if (_budgetBytes == 0 || replacementWeight > _budgetBytes ||
                remainingWeight > _budgetBytes - replacementWeight)
                return false;

            // Allocate immutable payloads before changing either node. Even a
            // failed allocation cannot leave a half-published alias pair.
            var updated = new Entry(node.Value.Key, replacement, weight);
            Entry? updatedAlias = alias is not null
                ? new Entry(alias.Value.Key, replacement, aliasWeight)
                : null;
            evicted = node.Value.Value;
            node.Value = updated;
            replacedAlias = alias is not null;
            if (alias is not null)
            {
                alias.Value = updatedAlias!;
                _recency.Remove(alias);
                _recency.AddFirst(alias);
            }
            _retainedBytes = remainingWeight + replacementWeight;
            if (!ReferenceEquals(_recency.First, node))
            {
                _recency.Remove(node);
                _recency.AddFirst(node);
            }
        }

        _onEvicted?.Invoke(evicted);
        if (replacedAlias)
            _onEvicted?.Invoke(evicted);
        return true;
    }

    public void Set(TKey key, TValue value)
    {
        long valueWeight = NormalizeValueWeight(_weightSelector(value));
        long keyWeight = NormalizeKeyWeight(_keyWeightSelector?.Invoke(key) ?? 0);
        long weight = SaturatingAdd(valueWeight, keyWeight);
        List<TValue>? evicted = null;

        lock (_gate)
        {
            _entries.TryGetValue(key, out LinkedListNode<Entry>? existing);
            bool canInsert = _budgetBytes > 0 && weight <= _budgetBytes;
            long trimTargetBytes = canInsert ? _budgetBytes - weight : _budgetBytes;
            long retainedAfterReplacement =
                _retainedBytes - (existing?.Value.Weight ?? 0);
            int plannedVictimCount = 0;

            // Count the exact eviction batch before changing either data
            // structure. This scan is proportional only to the entries that
            // the commit will remove; it allocates no per-entry snapshot.
            for (LinkedListNode<Entry>? node = _recency.Last;
                 retainedAfterReplacement > trimTargetBytes && node is not null;
                 node = node.Previous)
            {
                if (ReferenceEquals(node, existing))
                    continue;

                retainedAfterReplacement -= node.Value.Weight;
                plannedVictimCount = checked(plannedVictimCount + 1);
            }

            int existingCount = existing is null ? 0 : 1;
            int rejectedValueCount = canInsert ? 0 : 1;
            int evictionCount = checked(
                existingCount + rejectedValueCount + plannedVictimCount);

            // Prepare every potentially allocating insertion component before
            // the first logical mutation. EnsureCapacity is a no-op unless the
            // resulting insertion would exceed the dictionary's current
            // capacity; it does not grow the map on every cache write.
            LinkedListNode<Entry>? preparedNode = null;
            if (canInsert)
            {
                preparedNode = new LinkedListNode<Entry>(
                    new Entry(key, value, weight));
                _entries.EnsureCapacity(checked(_entries.Count + (existing is null ? 1 : 0)));
            }

            if (_onEvicted is not null && evictionCount != 0)
                evicted = new List<TValue>(evictionCount);

            // Publish the dictionary mapping first. If hashing/insertion fails,
            // the recency chain, weights and callbacks are still untouched.
            // The remaining commit operations use prepared nodes/list capacity
            // and do not allocate.
            if (existing is not null)
            {
                if (canInsert)
                    _entries[key] = preparedNode!;
                else
                    _entries.Remove(key);

                _recency.Remove(existing);
                _retainedBytes -= existing.Value.Weight;
                evicted?.Add(existing.Value.Value);
            }
            else if (canInsert)
            {
                _entries.Add(key, preparedNode!);
            }

            if (!canInsert)
                evicted?.Add(value);

            while (_retainedBytes > trimTargetBytes && _recency.Last is { } oldest)
            {
                _entries.Remove(oldest.Value.Key);
                _recency.RemoveLast();
                _retainedBytes -= oldest.Value.Weight;
                evicted?.Add(oldest.Value.Value);
            }

            if (canInsert)
            {
                _recency.AddFirst(preparedNode!);
                _retainedBytes += weight;
            }
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
        List<LinkedListNode<Entry>>? victims = null;

        lock (_gate)
        {
            // Evaluate predicates and allocate the complete removal batch
            // before releasing any ownership. A failed predicate/allocation
            // cannot strand already-removed entries without their callbacks.
            for (LinkedListNode<Entry>? node = _recency.First; node is not null; node = node.Next)
            {
                if (predicate(node.Value.Key))
                    (victims ??= []).Add(node);
            }
            if (victims is not null)
            {
                foreach (LinkedListNode<Entry> node in victims)
                {
                    _entries.Remove(node.Value.Key);
                    _recency.Remove(node);
                    _retainedBytes -= node.Value.Weight;
                }
            }
        }

        NotifyEvictedNodes(victims);
        return victims?.Count ?? 0;
    }

    public void SetBudget(long budgetBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(budgetBytes);
        List<LinkedListNode<Entry>>? victims = null;
        lock (_gate)
        {
            long retained = _retainedBytes;
            for (LinkedListNode<Entry>? node = _recency.Last;
                 retained > budgetBytes && node is not null; node = node.Previous)
            {
                (victims ??= []).Add(node);
                retained -= node.Value.Weight;
            }
            _budgetBytes = budgetBytes;
            if (victims is not null)
            {
                foreach (LinkedListNode<Entry> node in victims)
                {
                    _entries.Remove(node.Value.Key);
                    _recency.Remove(node);
                    _retainedBytes -= node.Value.Weight;
                }
            }
        }

        NotifyEvictedNodes(victims);
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

    private void NotifyEvictedNodes(List<LinkedListNode<Entry>>? victims)
    {
        if (_onEvicted is not null && victims is not null)
            foreach (LinkedListNode<Entry> node in victims)
                _onEvicted(node.Value.Value);
    }

    private void NotifyEvicted(List<TValue>? evicted)
    {
        if (_onEvicted is null || evicted is null)
            return;

        foreach (TValue value in evicted)
            _onEvicted(value);
    }

    private static long NormalizeValueWeight(long weight) => weight <= 0 ? 1 : weight;

    private static long NormalizeKeyWeight(long weight) => weight <= 0 ? 0 : weight;

    private static long SaturatingAdd(long left, long right) =>
        left > long.MaxValue - right ? long.MaxValue : left + right;

    // Recency nodes have identity; their payload is never compared or cloned.
    private sealed class Entry(TKey key, TValue value, long weight)
    {
        internal readonly TKey Key = key;
        internal readonly TValue Value = value;
        internal readonly long Weight = weight;
    }
}
