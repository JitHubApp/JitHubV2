using System;
using System.Collections;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using MarkdownRenderer.Images;
using MarkdownRenderer.Performance;
using Xunit;

namespace MarkdownRenderer.GitHub.Tests;

public sealed class ProgressiveSourceCacheRetentionTests
{
    [Fact]
    public async Task TinySourcesAreEvictedByMetadataWithoutChangingResolution()
    {
        var resolver = new TinyResolver();
        await using var session = new MarkdownPerformanceSession(MarkdownPerformanceOptions.Progressive);
        var context = new MarkdownImageResolveContext(null);
        using var document = session.OpenDocument(resolver, context);
        for (int index = 0; index < 10_000; index++)
        {
            MarkdownImageResolution resolution = await document.ResolveAsync(
                $"source-{index}.png", context, CancellationToken.None);
            Assert.True(resolution.IsHandled);
            Assert.NotNull(resolution.Asset);
            Assert.Equal((byte)1, Assert.Single(resolution.Asset.Bytes));
        }

        long budget = (IntPtr.Size == 4 ? 2L : 4L) * 1024 * 1024;
        Assert.InRange(GetField<long>(session, "_sourceCacheMetadataBytes"), 1, budget);
        Assert.True(GetField<IDictionary>(session, "_sourceCache").Count < 10_000);
        Assert.True(session.GetSnapshot().SourceCacheEvictions > 0);
        Assert.Equal(10_000, resolver.Calls);

        // Cache pressure is not an image rejection; an evicted source resolves
        // again and retains its authoritative handled result.
        Assert.NotNull((await document.ResolveAsync("source-0.png", context, CancellationToken.None)).Asset);
        Assert.Equal(10_001, resolver.Calls);
        await document.ResolveAsync("source-0.png", context, CancellationToken.None);
        Assert.Equal(10_001, resolver.Calls);
    }

    [Fact]
    public async Task ZeroSourceBudgetDoesNotRetainEmptyAssets()
    {
        var resolver = new TinyResolver(empty: true);
        await using var session = new MarkdownPerformanceSession(
            MarkdownPerformanceOptions.Progressive with { SourceCacheBudgetBytes = 0 });
        var context = new MarkdownImageResolveContext(null);
        using var document = session.OpenDocument(resolver, context);
        for (int index = 0; index < 4; index++)
        {
            MarkdownImageResolution resolution = await document.ResolveAsync(
                "empty.png", context, CancellationToken.None);
            Assert.NotNull(resolution.Asset);
            Assert.Empty(resolution.Asset.Bytes);
        }
        Assert.Equal(4, resolver.Calls);
        Assert.Equal(0, session.GetSnapshot().SourceCacheBytes);
        Assert.Equal(0, GetField<long>(session, "_sourceCacheMetadataBytes"));
        Assert.Empty(GetField<IDictionary>(session, "_sourceCache"));
    }

    [Fact]
    public async Task OversizedHostMetadataIsNotRetainedButStillResolves()
    {
        var resolver = new TinyResolver(cacheKey: new string('k', 3 * 1024 * 1024));
        await using var session = new MarkdownPerformanceSession(MarkdownPerformanceOptions.Progressive);
        var context = new MarkdownImageResolveContext(null);
        using var document = session.OpenDocument(resolver, context);
        for (int index = 0; index < 2; index++)
            Assert.NotNull((await document.ResolveAsync("tiny.png", context, CancellationToken.None)).Asset);
        Assert.Equal(2, resolver.Calls);
        Assert.Equal(0, session.GetSnapshot().SourceCacheBytes);
        Assert.Equal(0, GetField<long>(session, "_sourceCacheMetadataBytes"));
        Assert.Empty(GetField<IDictionary>(session, "_sourceCache"));
    }

    [Fact]
    public async Task KeyedSourceRetentionDoesNotOwnRetiredHostResolverState()
    {
        await using var session = new MarkdownPerformanceSession(MarkdownPerformanceOptions.Progressive);
        WeakReference<IMarkdownImageResolver> retiredResolver = await CacheWithTemporaryResolver(session);
        for (int attempt = 0; attempt < 20 && IsResolverAlive(retiredResolver); attempt++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            await Task.Delay(10);
        }
        Assert.False(IsResolverAlive(retiredResolver));
        Assert.Single(GetField<IDictionary>(session, "_sourceCache"));
        Assert.True(session.GetSnapshot().SourceCacheBytes > 0);
    }

    [Fact]
    public async Task ResolverIdentityRemainsReferenceBasedEvenWhenResolversCompareEqual()
    {
        var first = new ValueEqualResolver();
        var second = new ValueEqualResolver();
        Assert.Equal(first, second);
        await using var session = new MarkdownPerformanceSession(MarkdownPerformanceOptions.Progressive);
        var context = new MarkdownImageResolveContext(null);
        using var firstDocument = session.OpenDocument(first, context);
        using var secondDocument = session.OpenDocument(second, context);
        await firstDocument.ResolveAsync("same.png", context, CancellationToken.None);
        await secondDocument.ResolveAsync("same.png", context, CancellationToken.None);
        Assert.Equal(1, first.Calls);
        Assert.Equal(1, second.Calls);
        await firstDocument.ResolveAsync("same.png", context, CancellationToken.None);
        Assert.Equal(1, first.Calls);
        Assert.Equal(1, session.GetSnapshot().SourceCacheHits);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TrimOrFinalUnkeyedOwnerReleaseDropsHighWaterRootsAndCanReuse(bool releaseOwner)
    {
        var resolver = new TinyResolver(unkeyed: releaseOwner);
        await using var session = new MarkdownPerformanceSession(MarkdownPerformanceOptions.Progressive);
        var context = new MarkdownImageResolveContext(null);
        var document = session.OpenDocument(resolver, context);
        try
        {
            int entryCount = IntPtr.Size == 4 ? 2_048 : 4_096;
            for (int index = 0; index < entryCount; index++)
                await document.ResolveAsync($"source-{index}.png", context, CancellationToken.None);
            IDictionary priorCache = GetField<IDictionary>(session, "_sourceCache");
            object priorRecency = GetField<object>(session, "_sourceRecency");
            IDictionary priorOwnerKeys = GetField<IDictionary>(session, "_documentSourceKeys");
            Assert.Equal(entryCount, priorCache.Count);
            if (releaseOwner)
                document.Dispose();
            else
                session.Trim();

            IDictionary newCache = GetField<IDictionary>(session, "_sourceCache");
            Assert.NotSame(priorCache, newCache);
            Assert.NotSame(priorRecency, GetField<object>(session, "_sourceRecency"));
            Assert.NotSame(priorOwnerKeys, GetField<IDictionary>(session, "_documentSourceKeys"));
            Assert.Empty(newCache);
            Assert.Equal(0, session.GetSnapshot().SourceCacheBytes);
            Assert.Equal(0, GetField<long>(session, "_sourceCacheMetadataBytes"));
            Assert.Equal(0, GetCapacity(newCache));

            using var replacement = session.OpenDocument(resolver, context);
            Assert.NotNull((await replacement.ResolveAsync("replacement.png", context, CancellationToken.None)).Asset);
            Assert.Single(newCache);
            Assert.True(GetField<long>(session, "_sourceCacheMetadataBytes") > 0);
        }
        finally
        {
            document.Dispose();
        }
    }

    private static T GetField<T>(object target, string name)
        => (T)(target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(target) ?? throw new InvalidOperationException(name));

    private static int GetCapacity(IDictionary dictionary)
        => (int)dictionary.GetType().GetMethod("EnsureCapacity")!.Invoke(dictionary, [0])!;

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static async Task<WeakReference<IMarkdownImageResolver>> CacheWithTemporaryResolver(
        MarkdownPerformanceSession session)
    {
        var resolver = new TinyResolver();
        var weak = new WeakReference<IMarkdownImageResolver>(resolver);
        var context = new MarkdownImageResolveContext(null);
        using var document = session.OpenDocument(resolver, context);
        await document.ResolveAsync("temporary.png", context, CancellationToken.None);
        return weak;
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static bool IsResolverAlive(WeakReference<IMarkdownImageResolver> weak)
        => weak.TryGetTarget(out _);

    private sealed class ValueEqualResolver : IMarkdownImageResolver
    {
        internal int Calls;
        public override bool Equals(object? other) => other is ValueEqualResolver;
        public override int GetHashCode() => 0;

        public ValueTask<MarkdownImageResolution> ResolveAsync(
            string source, MarkdownImageResolveContext context, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref Calls);
            return ValueTask.FromResult(MarkdownImageResolution.Resolved(new MarkdownImageAsset(
                [1], "image/png", CacheKey: source)));
        }
    }

    private sealed class TinyResolver(bool empty = false, bool unkeyed = false, string? cacheKey = null)
        : IMarkdownImageResolver
    {
        internal int Calls;

        public ValueTask<MarkdownImageResolution> ResolveAsync(
            string source, MarkdownImageResolveContext context, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref Calls);
            return ValueTask.FromResult(MarkdownImageResolution.Resolved(new MarkdownImageAsset(
                empty ? [] : [1], "image/png", CacheKey: unkeyed ? null : cacheKey ?? source)));
        }
    }
}
