using MarkdownRenderer.Utilities;
using Xunit;

namespace MarkdownRenderer.Tests;

public sealed class WeightedLruCacheTests
{
    [Fact]
    public void Set_EvictsLeastRecentlyUsedEntriesByByteWeight()
    {
        var cache = new WeightedLruCache<string, byte[]>(10, static value => value.Length);

        cache.Set("first", new byte[4]);
        cache.Set("second", new byte[4]);
        Assert.True(cache.TryGetValue("first", out _));

        cache.Set("third", new byte[4]);

        Assert.True(cache.TryGetValue("first", out _));
        Assert.False(cache.TryGetValue("second", out _));
        Assert.True(cache.TryGetValue("third", out _));
        Assert.Equal(8, cache.RetainedBytes);
    }

    [Fact]
    public void Set_RejectsSingleEntryLargerThanBudget()
    {
        var evicted = new List<byte[]>();
        var cache = new WeightedLruCache<string, byte[]>(
            4,
            static value => value.Length,
            evicted.Add);
        byte[] value = new byte[5];

        cache.Set("too-large", value);

        Assert.False(cache.TryGetValue("too-large", out _));
        Assert.Same(value, Assert.Single(evicted));
        Assert.Equal(0, cache.RetainedBytes);
    }

    [Fact]
    public void SetBudgetAndRemoveWhere_ReleaseEntriesOutsideLock()
    {
        var evicted = new List<string>();
        WeightedLruCache<string, string>? cache = null;
        cache = new WeightedLruCache<string, string>(
            20,
            static value => value.Length,
            value =>
            {
                evicted.Add(value);
                _ = cache!.Count;
            });
        cache.Set("a", "aaaa");
        cache.Set("b", "bbbb");
        cache.Set("c", "cccc");

        Assert.Equal(2, cache.RemoveWhere(static key => key is "a" or "c"));
        cache.SetBudget(0);

        Assert.Equal(0, cache.Count);
        Assert.Equal(3, evicted.Count);
        Assert.Contains("aaaa", evicted);
        Assert.Contains("bbbb", evicted);
        Assert.Contains("cccc", evicted);
    }

    [Fact]
    public void ConcurrentReadersAndWriters_NeverExceedByteBudget()
    {
        const int budget = 4 * 1024;
        var cache = new WeightedLruCache<int, byte[]>(budget, static value => value.Length);

        Parallel.For(0, 20_000, index =>
        {
            int key = index % 257;
            cache.Set(key, new byte[16 + (index % 113)]);
            _ = cache.TryGetValue((index * 17) % 257, out _);
        });

        Assert.InRange(cache.RetainedBytes, 0, budget);
        Assert.InRange(cache.Count, 0, 257);
    }
}
