using MarkdownRenderer.GitHub.Tests.TestSupport.Fakes;
using MarkdownRenderer.Images;
using MarkdownRenderer.Performance;
using Xunit;

namespace MarkdownRenderer.GitHub.Tests;

/// <summary>
/// Checks how the session shares resolved sources: prefetch reuse and
/// promotion, context, document and partition scopes, and one active
/// resolution for simultaneous documents.
/// </summary>
public sealed class MarkdownPerformanceSourceSharingTests
{
    [Fact]
    public async Task Prefetch_ThenVisibleResolve_ReusesTheResolvedAsset()
    {
        var resolver = new RecordingResolver();
        using var session = new MarkdownPerformanceSession(MarkdownPerformanceOptions.Progressive);
        var context = new MarkdownImageResolveContext(new Uri("https://example.test/"));
        using var document = session.OpenDocument(resolver, context);

        await document.PrefetchAsync(["one.png", "two.png"], CancellationToken.None);
        MarkdownImageResolution result = await document.ResolveAsync(
            "one.png", context, CancellationToken.None);

        Assert.True(result.IsHandled);
        Assert.Equal(2, resolver.CallCount);
        Assert.Equal(1, session.GetSnapshot().SourceCacheHits);
    }

    [Fact]
    public async Task DifferentContexts_DoNotShareSourceBytes()
    {
        var resolver = new RecordingResolver();
        using var session = new MarkdownPerformanceSession(MarkdownPerformanceOptions.Progressive);
        var first = new MarkdownImageResolveContext(new Uri("https://example.test/a/"));
        var second = new MarkdownImageResolveContext(new Uri("https://example.test/b/"));
        using var firstDocument = session.OpenDocument(resolver, first);
        using var secondDocument = session.OpenDocument(resolver, second);

        await firstDocument.ResolveAsync("avatar.png", first, CancellationToken.None);
        await secondDocument.ResolveAsync("avatar.png", second, CancellationToken.None);

        Assert.Equal(2, resolver.CallCount);
    }

    [Fact]
    public async Task UnkeyedAssets_AreReusedOnlyForTheDocumentLifetime()
    {
        var resolver = new UnkeyedResolver();
        using var session = new MarkdownPerformanceSession(MarkdownPerformanceOptions.Progressive);
        var context = new MarkdownImageResolveContext(null);
        using (var first = session.OpenDocument(resolver, context))
        {
            await first.PrefetchAsync(["unkeyed.png"], CancellationToken.None);
            await first.ResolveAsync("unkeyed.png", context, CancellationToken.None);
            Assert.Equal(1, resolver.CallCount);
            Assert.True(session.GetSnapshot().SourceCacheBytes > 0);
        }

        Assert.Equal(0, session.GetSnapshot().SourceCacheBytes);
        using var second = session.OpenDocument(resolver, context);
        await second.ResolveAsync("unkeyed.png", context, CancellationToken.None);
        Assert.Equal(2, resolver.CallCount);
    }

    [Fact]
    public async Task KeyedAssets_AreSharedAcrossDocumentsInOnePartition()
    {
        var resolver = new RecordingResolver();
        using var session = new MarkdownPerformanceSession(MarkdownPerformanceOptions.Progressive);
        var context = new MarkdownImageResolveContext(null);
        using var first = session.OpenDocument(resolver, context);
        using var second = session.OpenDocument(resolver, context);

        await first.ResolveAsync("shared.png", context, CancellationToken.None);
        await second.ResolveAsync("shared.png", context, CancellationToken.None);

        Assert.Equal(1, resolver.CallCount);
        Assert.Equal(1, session.GetSnapshot().SourceCacheHits);
    }

    [Fact]
    public async Task VisibleResolve_PromotesAQueuedPrefetch()
    {
        var resolver = new BlockingResolver();
        await using var session = new MarkdownPerformanceSession(
            MarkdownPerformanceOptions.Progressive with
            {
                MaxConcurrentImageFetches = 2,
                ReservedVisibleImageFetches = 1,
            });
        var context = new MarkdownImageResolveContext(null);
        using var first = session.OpenDocument(resolver, context);
        using var second = session.OpenDocument(resolver, context);

        Task firstPrefetch = first.PrefetchAsync(["blocked.png"], CancellationToken.None);
        await resolver.BlockedStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Task secondPrefetch = second.PrefetchAsync(["visible.png"], CancellationToken.None);

        MarkdownImageResolution visible = await second.ResolveAsync(
            "visible.png", context, CancellationToken.None).AsTask()
            .WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(visible.IsHandled);
        Assert.Equal(1, resolver.VisibleCalls);

        resolver.ReleaseBlocked.TrySetResult(true);
        await Task.WhenAll(firstPrefetch, secondPrefetch).WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task SimultaneousDocuments_ShareOneActiveResolution()
    {
        var resolver = new BlockingResolver();
        await using var session = new MarkdownPerformanceSession(MarkdownPerformanceOptions.Progressive);
        var context = new MarkdownImageResolveContext(null);
        using var first = session.OpenDocument(resolver, context);
        using var second = session.OpenDocument(resolver, context);

        Task<MarkdownImageResolution> firstResult = first.ResolveAsync(
            "blocked.png", context, CancellationToken.None).AsTask();
        await resolver.BlockedStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Task<MarkdownImageResolution> secondResult = second.ResolveAsync(
            "blocked.png", context, CancellationToken.None).AsTask();
        Assert.Equal(1, resolver.BlockedCalls);

        resolver.ReleaseBlocked.TrySetResult(true);
        Assert.True((await firstResult.WaitAsync(TimeSpan.FromSeconds(5))).IsHandled);
        Assert.True((await secondResult.WaitAsync(TimeSpan.FromSeconds(5))).IsHandled);
        Assert.Equal(1, resolver.BlockedCalls);
    }

    private sealed class UnkeyedResolver : IMarkdownImageResolver
    {
        private int _callCount;
        internal int CallCount => Volatile.Read(ref _callCount);

        public ValueTask<MarkdownImageResolution> ResolveAsync(
            string source,
            MarkdownImageResolveContext context,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _callCount);
            return ValueTask.FromResult(MarkdownImageResolution.Resolved(
                new MarkdownImageAsset([1, 2, 3], "image/png")));
        }
    }
}
