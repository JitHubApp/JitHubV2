using System;
using System.Collections.Generic;
using MarkdownRenderer.Document;

namespace MarkdownRenderer.Engine.Internal;

/// <summary>
/// Byte-budgeted LRU of completed documents, plus a weakly held identity
/// shortcut for the most recent lookup. Not thread-safe:
/// <see cref="MarkdownParseCoordinator"/> calls every member under its parse
/// gate, so cache admission stays atomic with in-flight completion.
/// </summary>
internal sealed class MarkdownCompletedParseCache
{
    private readonly Dictionary<MarkdownParseSourceKey, MarkdownCompletedParse> _completedParses =
        new(MarkdownParseSourceKeyComparer.Instance);
    private readonly LinkedList<MarkdownCompletedParse> _completedRecency = new();
    private readonly long _budgetBytes;
    private long _completedParseWeightBytes;
    private MarkdownCompletedParse? _lastCompletedParse;
    private WeakReference<string>? _lastCompletedSourceIdentity;

    internal MarkdownCompletedParseCache(long budgetBytes)
    {
        _budgetBytes = budgetBytes;
    }

    internal int Count => _completedParses.Count;

    internal bool TryGet(MarkdownParseSourceKey sourceKey, out MarkdownDocument document)
    {
        if (_completedParses.TryGetValue(sourceKey, out var cached))
        {
            Touch(cached, sourceKey.Source);
            document = cached.Document;
            return true;
        }

        document = null!;
        return false;
    }

    internal bool TryGetBySourceIdentity(
        string source,
        bool deferOffscreenScenes,
        out MarkdownDocument document)
    {
        MarkdownCompletedParse? cached = _lastCompletedParse;
        if (cached is not null &&
            cached.Key.DeferOffscreenScenes == deferOffscreenScenes &&
            _lastCompletedSourceIdentity is { } identity &&
            identity.TryGetTarget(out string? lastSource) &&
            ReferenceEquals(lastSource, source))
        {
            Touch(cached, source);
            document = cached.Document;
            return true;
        }

        document = null!;
        return false;
    }

    internal void Add(
        MarkdownParseSourceKey sourceKey,
        MarkdownDocument document,
        long weight,
        long parseGeneration)
    {
        if (_budgetBytes == 0)
            return;

        if (weight > _budgetBytes)
            return;

        if (_completedParses.TryGetValue(sourceKey, out var existing))
            Remove(existing);

        while (_completedParseWeightBytes > _budgetBytes - weight)
        {
            var leastRecent = _completedRecency.Last;
            if (leastRecent is null)
                break;
            Remove(leastRecent.Value);
        }

        var completed = new MarkdownCompletedParse(sourceKey, document, weight, parseGeneration);
        completed.RecencyNode = _completedRecency.AddFirst(completed);
        _completedParses.Add(sourceKey, completed);
        _completedParseWeightBytes += weight;
        SetLast(completed, sourceKey.Source);
    }

    /// <summary>
    /// Retires the entry one parse generation admitted when no waiter ever
    /// received it. A newer same-source entry is never evicted.
    /// </summary>
    internal void RemoveIfUnobserved(MarkdownParseSourceKey sourceKey, long parseGeneration)
    {
        if (_completedParses.TryGetValue(sourceKey, out MarkdownCompletedParse? completed) &&
            completed.ParseGeneration == parseGeneration &&
            !completed.WasObserved)
        {
            Remove(completed);
        }
    }

    internal void Clear()
    {
        _completedParses.Clear();
        _completedRecency.Clear();
        _lastCompletedParse = null;
        _lastCompletedSourceIdentity = null;
        _completedParseWeightBytes = 0;
    }

    private void Touch(MarkdownCompletedParse completed, string lookupSource)
    {
        completed.WasObserved = true;
        if (!ReferenceEquals(_completedRecency.First, completed.RecencyNode))
        {
            _completedRecency.Remove(completed.RecencyNode);
            _completedRecency.AddFirst(completed.RecencyNode);
        }

        SetLast(completed, lookupSource);
    }

    private void SetLast(MarkdownCompletedParse completed, string lookupSource)
    {
        _lastCompletedParse = completed;
        if (_lastCompletedSourceIdentity is null)
            _lastCompletedSourceIdentity = new WeakReference<string>(lookupSource);
        else
            _lastCompletedSourceIdentity.SetTarget(lookupSource);
    }

    private void Remove(MarkdownCompletedParse completed)
    {
        _completedParses.Remove(completed.Key);
        _completedRecency.Remove(completed.RecencyNode);
        _completedParseWeightBytes -= completed.WeightBytes;
        if (ReferenceEquals(_lastCompletedParse, completed))
        {
            _lastCompletedParse = null;
            _lastCompletedSourceIdentity = null;
        }
    }
}
