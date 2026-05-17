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
}
