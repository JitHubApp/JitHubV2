using System;
using System.Collections.Generic;
using MarkdownRenderer.Utilities;
using Xunit;

namespace MarkdownRenderer.GitHub.Tests;

public sealed class WeightedLruCacheTransactionalSetTests
{
    [Fact]
    public void NewKeyInsertionFailureDoesNotPublishARecencyNodeOrEvictExistingValues()
    {
        var comparer = new ThrowOnHashCodeComparer();
        var evicted = new List<Probe>();
        var cache = new WeightedLruCache<int, Probe>(
            100,
            static item => item.Weight,
            evicted.Add,
            comparer);
        var existing = new Probe("existing", 5);
        cache.Set(1, existing);

        // The next Set performs one lookup hash and then the insertion hash.
        // Throwing at insertion used to occur after AddFirst had already
        // linked the incoming value into the recency list.
        comparer.ThrowOnHashCodeCall = comparer.HashCodeCallCount + 2;
        var candidate = new Probe("candidate", 3);

        Assert.Throws<InvalidOperationException>(() => cache.Set(2, candidate));

        Assert.Equal(1, cache.Count);
        Assert.Equal(5, cache.RetainedBytes);
        Assert.True(cache.TryGetValue(1, out Probe? actualExisting));
        Assert.Same(existing, actualExisting);
        Assert.False(cache.TryGetBestMatch(
            static (key, item) => key == 2 && item.Name == "candidate",
            static _ => 1,
            out _));
        Assert.Empty(evicted);
    }

    [Fact]
    public void FailedRemovalPredicatePreservesEveryEntryAndItsOwnership()
    {
        var evicted = new List<Probe>();
        var cache = new WeightedLruCache<int, Probe>(10, static item => item.Weight, evicted.Add);
        var first = new Probe("first", 2);
        var second = new Probe("second", 3);
        cache.Set(1, first);
        cache.Set(2, second);
        Assert.Throws<InvalidOperationException>(() => cache.RemoveWhere(key =>
            key == 1 ? throw new InvalidOperationException("predicate failed") : true));
        Assert.Equal(2, cache.Count);
        Assert.Equal(5, cache.RetainedBytes);
        Assert.Empty(evicted);
        Assert.True(cache.TryGetValue(1, out Probe? one));
        Assert.True(cache.TryGetValue(2, out Probe? two));
        Assert.Same(first, one);
        Assert.Same(second, two);
    }

    [Fact]
    public void BudgetTrimPublishesOneOldestFirstBatchAfterCommit()
    {
        var evicted = new List<Probe>();
        WeightedLruCache<int, Probe>? cache = null;
        cache = new WeightedLruCache<int, Probe>(10, static item => item.Weight, item =>
        {
            Assert.Equal(1, cache!.Count);
            Assert.Equal(3, cache.RetainedBytes);
            Assert.Equal(3, cache.BudgetBytes);
            evicted.Add(item);
        });
        var first = new Probe("first", 2);
        var second = new Probe("second", 2);
        var third = new Probe("third", 3);
        cache.Set(1, first);
        cache.Set(2, second);
        cache.Set(3, third);
        cache.SetBudget(3);
        Assert.Equal(new[] { first, second }, evicted);
    }

    [Fact]
    public void ReplacementAndLruTrimmingKeepEvictionOrderAndRetainedWeights()
    {
        var evicted = new List<Probe>();
        var cache = new WeightedLruCache<int, Probe>(
            5,
            static item => item.Weight,
            evicted.Add);

        var replaced = new Probe("replaced", 2);
        var leastRecent = new Probe("least-recent", 2);
        var replacement = new Probe("replacement", 2);
        var incoming = new Probe("incoming", 3);

        cache.Set(1, replaced);
        cache.Set(2, leastRecent);
        cache.Set(1, replacement);

        Assert.Equal(new[] { replaced }, evicted);
        evicted.Clear();

        cache.Set(3, incoming);

        Assert.Equal(new[] { leastRecent }, evicted);
        Assert.Equal(5, cache.RetainedBytes);
        Assert.Equal(2, cache.Count);
        Assert.True(cache.TryGetValue(1, out Probe? actualReplacement));
        Assert.Same(replacement, actualReplacement);
        Assert.True(cache.TryGetValue(3, out Probe? actualIncoming));
        Assert.Same(incoming, actualIncoming);
    }

    private sealed class Probe(string name, long weight)
    {
        internal string Name { get; } = name;

        internal long Weight { get; } = weight;
    }

    private sealed class ThrowOnHashCodeComparer : IEqualityComparer<int>
    {
        private int _hashCodeCallCount;

        internal int HashCodeCallCount => _hashCodeCallCount;

        internal int ThrowOnHashCodeCall { get; set; }

        public bool Equals(int left, int right) => left == right;

        public int GetHashCode(int value)
        {
            int call = ++_hashCodeCallCount;
            if (call == ThrowOnHashCodeCall)
            {
                ThrowOnHashCodeCall = 0;
                throw new InvalidOperationException("Injected dictionary insertion failure.");
            }

            return value;
        }
    }
}
