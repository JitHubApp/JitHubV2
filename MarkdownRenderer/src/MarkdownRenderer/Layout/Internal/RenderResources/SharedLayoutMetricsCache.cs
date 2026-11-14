using System;
using MarkdownRenderer.Internal.Caching;

namespace MarkdownRenderer.Layout.Internal.RenderResources;

/// <summary>
/// Cache of device-independent, measured top-level block heights. Each
/// <see cref="MarkdownEngine"/> owns one (<see cref="MarkdownEngine.LayoutMetrics"/>),
/// so only views that parse with the same engine share hints. The values are
/// hints only: every realized block is still measured by DirectWrite before it
/// is painted. Sharing these hints gives a second view of the same document a
/// stable initial extent without sharing Canvas resources.
/// </summary>
internal sealed class SharedLayoutMetricsCache
{
    internal const long DefaultBudgetBytes = 32L * 1024 * 1024;

    private readonly object _coordinationGate = new();
    private readonly WeightedLruCache<SharedLayoutMetricsKey, SharedLayoutMetrics> _entries;

    internal SharedLayoutMetricsCache(long budgetBytes = DefaultBudgetBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(budgetBytes);
        _entries = new WeightedLruCache<SharedLayoutMetricsKey, SharedLayoutMetrics>(
            budgetBytes,
            static metrics => metrics.EstimatedWeightBytes);
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
