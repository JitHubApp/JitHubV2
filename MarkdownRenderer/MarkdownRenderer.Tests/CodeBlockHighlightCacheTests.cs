using MarkdownRenderer.CodeBlocks;
using Xunit;

namespace MarkdownRenderer.Tests;

public sealed class CodeBlockHighlightCacheTests
{
    [Fact]
    public void Set_EvictsOldestEntry_WhenCapacityIsExceeded()
    {
        var cache = new BoundedCodeBlockHighlightCache<string>(2);
        var first = new CodeBlockHighlightResult([new CodeBlockHighlightSpan(0, 1, default)]);
        var second = new CodeBlockHighlightResult([new CodeBlockHighlightSpan(1, 1, default)]);
        var third = new CodeBlockHighlightResult([new CodeBlockHighlightSpan(2, 1, default)]);

        cache.Set("first", first);
        cache.Set("second", second);
        cache.Set("third", third);

        Assert.Equal(2, cache.Count);
        Assert.False(cache.TryGetValue("first", out _));
        Assert.True(cache.TryGetValue("second", out var cachedSecond));
        Assert.Same(second, cachedSecond);
        Assert.True(cache.TryGetValue("third", out var cachedThird));
        Assert.Same(third, cachedThird);
    }

    [Fact]
    public void Set_UpdatesExistingEntry_WithoutGrowing()
    {
        var cache = new BoundedCodeBlockHighlightCache<string>(2);
        var original = new CodeBlockHighlightResult([]);
        var updated = new CodeBlockHighlightResult([new CodeBlockHighlightSpan(0, 4, default)]);

        cache.Set("code", original);
        cache.Set("code", updated);

        Assert.Equal(1, cache.Count);
        Assert.True(cache.TryGetValue("code", out var cached));
        Assert.Same(updated, cached);
    }

    [Fact]
    public void Clear_RemovesCachedEntries()
    {
        var cache = new BoundedCodeBlockHighlightCache<string>(2);

        cache.Set("code", new CodeBlockHighlightResult([]));
        cache.Clear();

        Assert.Equal(0, cache.Count);
        Assert.False(cache.TryGetValue("code", out _));
    }

    [Fact]
    public void TryGet_PromotesEntryForLeastRecentlyUsedEviction()
    {
        var cache = new BoundedCodeBlockHighlightCache<string>(2);
        var first = new CodeBlockHighlightResult([]);
        var second = new CodeBlockHighlightResult([]);

        cache.Set("first", first);
        cache.Set("second", second);
        Assert.True(cache.TryGetValue("first", out _));
        cache.Set("third", new CodeBlockHighlightResult([]));

        Assert.True(cache.TryGetValue("first", out _));
        Assert.False(cache.TryGetValue("second", out _));
        Assert.True(cache.TryGetValue("third", out _));
    }

    [Fact]
    public void ByteBudget_RejectsOversizedResultAndRemainsBounded()
    {
        var cache = new BoundedCodeBlockHighlightCache<string>(
            capacity: 10,
            budgetBytes: 200,
            keyWeightSelector: static key => key.Length * sizeof(char));
        var oversized = new CodeBlockHighlightResult(
            Enumerable.Range(0, 10)
                .Select(index => new CodeBlockHighlightSpan(index, 1, default))
                .ToArray());

        cache.Set("oversized", oversized);
        cache.Set("small", CodeBlockHighlightResult.Empty);

        Assert.False(cache.TryGetValue("oversized", out _));
        Assert.True(cache.TryGetValue("small", out _));
        Assert.InRange(cache.RetainedBytes, 1, 200);
    }
}
