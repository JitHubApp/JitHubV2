using System;
using System.Runtime.CompilerServices;
using MarkdownRenderer.Utilities;

namespace MarkdownRenderer.Layout;

/// <summary>
/// Engine-scoped cache of device-independent, measured top-level block heights.
/// The values are hints only: every realized block is still measured by
/// DirectWrite before it is painted. Sharing these hints gives a second view of
/// the same document a stable initial extent without sharing Canvas resources.
/// </summary>
internal sealed class SharedLayoutMetricsCache
{
    internal const long DefaultBudgetBytes = 32L * 1024 * 1024;

    private static readonly ConditionalWeakTable<MarkdownEngine, SharedLayoutMetricsCache> EngineCaches = new();

    private readonly object _coordinationGate = new();
    private readonly WeightedLruCache<SharedLayoutMetricsKey, SharedLayoutMetrics> _entries;

    internal SharedLayoutMetricsCache(long budgetBytes = DefaultBudgetBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(budgetBytes);
        _entries = new WeightedLruCache<SharedLayoutMetricsKey, SharedLayoutMetrics>(
            budgetBytes,
            static metrics => metrics.EstimatedWeightBytes);
    }

    internal static SharedLayoutMetrics Acquire(
        MarkdownEngine? engine,
        SharedLayoutMetricsKey key,
        int topLevelBlockCount)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentOutOfRangeException.ThrowIfNegative(topLevelBlockCount);

        // The obsolete compatibility path can run without an engine. It still
        // receives a metrics buffer, but cannot retain or share it.
        if (engine is null)
            return new SharedLayoutMetrics(topLevelBlockCount, key.EstimatedKeyWeightBytes);

        return EngineCaches.GetValue(engine, static _ => new SharedLayoutMetricsCache())
            .GetOrCreate(key, topLevelBlockCount);
    }

    internal static (int Count, long RetainedBytes) GetStatistics(MarkdownEngine engine)
    {
        ArgumentNullException.ThrowIfNull(engine);
        return EngineCaches.TryGetValue(engine, out SharedLayoutMetricsCache? cache)
            ? (cache._entries.Count, cache._entries.RetainedBytes)
            : default;
    }

    internal static void Clear(MarkdownEngine engine)
    {
        ArgumentNullException.ThrowIfNull(engine);
        if (EngineCaches.TryGetValue(engine, out SharedLayoutMetricsCache? cache))
            cache._entries.Clear();
    }

    internal (int Count, long RetainedBytes) Statistics =>
        (_entries.Count, _entries.RetainedBytes);

    internal SharedLayoutMetrics GetOrCreate(
        SharedLayoutMetricsKey key,
        int topLevelBlockCount)
    {
        // WeightedLruCache is thread-safe, but creation must also be atomic so
        // concurrent controls update the same retained metrics buffer.
        lock (_coordinationGate)
        {
            if (_entries.TryGetValue(key, out SharedLayoutMetrics? existing) &&
                existing.TopLevelBlockCount == topLevelBlockCount)
            {
                return existing;
            }

            var created = new SharedLayoutMetrics(
                topLevelBlockCount,
                key.EstimatedKeyWeightBytes);
            _entries.Set(key, created);
            return created;
        }
    }
}
