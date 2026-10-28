using System.Buffers.Binary;
using MarkdownRenderer.Images;
using MarkdownRenderer.Layout.Boxes;
using MarkdownRenderer.Performance;
using Xunit;

namespace MarkdownRenderer.GitHub.Tests;

public sealed class PrefetchedRasterSourceCacheTests
{
    [Fact]
    public async Task PrefetchPublishesOnlyExactResolverContextAndSourceMetadataWithoutCacheSideEffects()
    {
        var resolver = new RasterResolver(CreatePng(800, 450), includeCacheKey: true);
        var otherResolver = new RasterResolver(CreatePng(100, 50), includeCacheKey: true);
        await using var session = NewSession();
        var context = new MarkdownImageResolveContext(
            new Uri("https://images.example/repo/readme.md"),
            "repo/readme.md",
            AllowThirdPartyRemoteImages: true,
            DocumentSource: new MarkdownDocumentSource(
                "repo-readme", "owner", "repo", "main", "readme.md"));
        using var document = session.OpenDocument(resolver, context);

        await document.PrefetchAsync(["https://images.example/a.png"], CancellationToken.None);
        var scope = (IMarkdownPerformanceDocumentScope)document;
        MarkdownPerformanceSnapshot before = session.GetSnapshot();
        long activeBytesBefore = session.ActiveSourceBytes;
        int resolverCallsBefore = resolver.CallCount;

        Assert.True(scope.TryGetPrefetchedRasterIntrinsicMetadata(
            "https://images.example/a.png", out RasterIntrinsicMetadata metadata));
        Assert.Equal(new RasterIntrinsicMetadata(800, 450), metadata);
        Assert.False(scope.TryGetPrefetchedRasterIntrinsicMetadata(
            "https://images.example/other.png", out _));

        using var differentContextDocument = session.OpenDocument(
            resolver,
            context with { DocumentPath = "other/readme.md" });
        Assert.False(((IMarkdownPerformanceDocumentScope)differentContextDocument)
            .TryGetPrefetchedRasterIntrinsicMetadata("https://images.example/a.png", out _));

        using var differentPartitionDocument = session.OpenDocument(
            resolver,
            context with
            {
                DocumentSource = new MarkdownDocumentSource(
                    "other-account-document", "other-owner", "other-repo", "main", "readme.md"),
            });
        Assert.False(((IMarkdownPerformanceDocumentScope)differentPartitionDocument)
            .TryGetPrefetchedRasterIntrinsicMetadata("https://images.example/a.png", out _));

        using var differentResolverDocument = session.OpenDocument(otherResolver, context);
        Assert.False(((IMarkdownPerformanceDocumentScope)differentResolverDocument)
            .TryGetPrefetchedRasterIntrinsicMetadata("https://images.example/a.png", out _));

        MarkdownPerformanceSnapshot after = session.GetSnapshot();
        Assert.Equal(before.SourceCacheHits, after.SourceCacheHits);
        Assert.Equal(activeBytesBefore, session.ActiveSourceBytes);
        Assert.Equal(resolverCallsBefore, resolver.CallCount);
    }

    [Fact]
    public async Task UnkeyedAssetHintRemainsDocumentLocalAndIsRemovedWithItsScope()
    {
        var resolver = new RasterResolver(CreatePng(320, 180), includeCacheKey: false);
        await using var session = NewSession();
        var context = new MarkdownImageResolveContext(null);
        var first = session.OpenDocument(resolver, context);
        await first.PrefetchAsync(["unkeyed.png"], CancellationToken.None);

        Assert.True(((IMarkdownPerformanceDocumentScope)first)
            .TryGetPrefetchedRasterIntrinsicMetadata("unkeyed.png", out var firstHint));
        Assert.Equal(new RasterIntrinsicMetadata(320, 180), firstHint);

        using var second = session.OpenDocument(resolver, context);
        Assert.False(((IMarkdownPerformanceDocumentScope)second)
            .TryGetPrefetchedRasterIntrinsicMetadata("unkeyed.png", out _));

        first.Dispose();
        Assert.False(((IMarkdownPerformanceDocumentScope)first)
            .TryGetPrefetchedRasterIntrinsicMetadata("unkeyed.png", out _));
    }

    [Fact]
    public async Task MetadataIsDroppedOnTrimAndCacheReplacementPublishesNewDimensions()
    {
        var resolver = new SequencedRasterResolver(
            CreatePng(800, 450), CreatePng(300, 600));
        await using var session = NewSession();
        using var document = session.OpenDocument(resolver, new MarkdownImageResolveContext(null));
        var scope = (IMarkdownPerformanceDocumentScope)document;

        await document.PrefetchAsync(["image.png"], CancellationToken.None);
        Assert.True(scope.TryGetPrefetchedRasterIntrinsicMetadata("image.png", out var first));
        Assert.Equal(new RasterIntrinsicMetadata(800, 450), first);

        session.Trim();
        Assert.False(scope.TryGetPrefetchedRasterIntrinsicMetadata("image.png", out _));

        await document.PrefetchAsync(["image.png"], CancellationToken.None);
        Assert.True(scope.TryGetPrefetchedRasterIntrinsicMetadata("image.png", out var replacement));
        Assert.Equal(new RasterIntrinsicMetadata(300, 600), replacement);
        Assert.Equal(2, resolver.CallCount);

        await session.DisposeAsync();
        Assert.False(scope.TryGetPrefetchedRasterIntrinsicMetadata("image.png", out _));
    }

    [Fact]
    public async Task MetadataLookupDoesNotPromoteLruAndEvictionRemovesTheHint()
    {
        byte[] bytes = CreatePng(100, 50);
        var resolver = new RasterResolver(bytes, includeCacheKey: true);
        await using var session = NewSession(bytesBudget: bytes.Length * 2L);
        using var document = session.OpenDocument(resolver, new MarkdownImageResolveContext(null));
        var scope = (IMarkdownPerformanceDocumentScope)document;

        await document.PrefetchAsync(["a.png"], CancellationToken.None);
        await document.PrefetchAsync(["b.png"], CancellationToken.None);
        Assert.True(scope.TryGetPrefetchedRasterIntrinsicMetadata("a.png", out _));
        Assert.True(scope.TryGetPrefetchedRasterIntrinsicMetadata("a.png", out _));
        long hitsBefore = session.GetSnapshot().SourceCacheHits;

        await document.PrefetchAsync(["c.png"], CancellationToken.None);

        Assert.False(scope.TryGetPrefetchedRasterIntrinsicMetadata("a.png", out _));
        Assert.True(scope.TryGetPrefetchedRasterIntrinsicMetadata("b.png", out _));
        Assert.True(scope.TryGetPrefetchedRasterIntrinsicMetadata("c.png", out _));
        Assert.Equal(hitsBefore, session.GetSnapshot().SourceCacheHits);
        Assert.True(session.GetSnapshot().SourceCacheEvictions > 0);
    }

    [Fact]
    public async Task DisabledDisplaySizedDecodeDoesNotPublishIntrinsicMetadata()
    {
        var resolver = new RasterResolver(CreatePng(800, 450), includeCacheKey: true);
        await using var session = new MarkdownPerformanceSession(
            MarkdownPerformanceOptions.Progressive with
            {
                PrefetchDocumentImages = true,
                UseDisplaySizedRasterDecode = false,
            });
        using var document = session.OpenDocument(resolver, new MarkdownImageResolveContext(null));

        await document.PrefetchAsync(["image.png"], CancellationToken.None);

        Assert.False(((IMarkdownPerformanceDocumentScope)document)
            .TryGetPrefetchedRasterIntrinsicMetadata("image.png", out _));
        Assert.Equal(1, resolver.CallCount);
    }

    [Fact]
    public async Task DisabledDocumentPrefetchDoesNotPublishIntrinsicMetadata()
    {
        var resolver = new RasterResolver(CreatePng(800, 450), includeCacheKey: true);
        await using var session = new MarkdownPerformanceSession(
            MarkdownPerformanceOptions.Progressive with { PrefetchDocumentImages = false });
        using var document = session.OpenDocument(resolver, new MarkdownImageResolveContext(null));

        await document.PrefetchAsync(["image.png"], CancellationToken.None);

        Assert.False(((IMarkdownPerformanceDocumentScope)document)
            .TryGetPrefetchedRasterIntrinsicMetadata("image.png", out _));
        Assert.Equal(0, resolver.CallCount);
    }

    [Fact]
    public async Task CancelledPrefetchDoesNotPublishAProvisionalHint()
    {
        var resolver = new CancellableRasterResolver(CreatePng(800, 450));
        await using var session = NewSession();
        using var document = session.OpenDocument(resolver, new MarkdownImageResolveContext(null));
        using var cancellation = new CancellationTokenSource();

        Task prefetch = document.PrefetchAsync(["image.png"], cancellation.Token);
        await resolver.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        try
        {
            await prefetch.WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (OperationCanceledException)
        {
            // Cancellation may be represented by the outer parallel prefetch
            // task even though the per-source branch retires cleanly.
        }

        Assert.False(((IMarkdownPerformanceDocumentScope)document)
            .TryGetPrefetchedRasterIntrinsicMetadata("image.png", out _));
    }

    private static MarkdownPerformanceSession NewSession(long bytesBudget = 1024 * 1024) =>
        new(MarkdownPerformanceOptions.Progressive with
        {
            PrefetchDocumentImages = true,
            UseDisplaySizedRasterDecode = true,
            SourceCacheBudgetBytes = bytesBudget,
        });

    private sealed class RasterResolver(byte[] bytes, bool includeCacheKey) : IMarkdownImageResolver
    {
        private int _callCount;
        internal int CallCount => Volatile.Read(ref _callCount);

        public ValueTask<MarkdownImageResolution> ResolveAsync(
            string source,
            MarkdownImageResolveContext context,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _callCount);
            return ValueTask.FromResult(MarkdownImageResolution.Resolved(
                new MarkdownImageAsset(bytes, "image/png", CacheKey: includeCacheKey ? source : null)));
        }
    }

    private sealed class SequencedRasterResolver(params byte[][] images) : IMarkdownImageResolver
    {
        private int _callCount;
        internal int CallCount => Volatile.Read(ref _callCount);

        public ValueTask<MarkdownImageResolution> ResolveAsync(
            string source,
            MarkdownImageResolveContext context,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int index = Interlocked.Increment(ref _callCount) - 1;
            byte[] image = images[System.Math.Min(index, images.Length - 1)];
            return ValueTask.FromResult(MarkdownImageResolution.Resolved(
                new MarkdownImageAsset(image, "image/png", CacheKey: source)));
        }
    }

    private sealed class CancellableRasterResolver(byte[] bytes) : IMarkdownImageResolver
    {
        internal TaskCompletionSource<bool> Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async ValueTask<MarkdownImageResolution> ResolveAsync(
            string source,
            MarkdownImageResolveContext context,
            CancellationToken cancellationToken)
        {
            Started.TrySetResult(true);
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
            return MarkdownImageResolution.Resolved(
                new MarkdownImageAsset(bytes, "image/png", CacheKey: source));
        }
    }

    private static byte[] CreatePng(int width, int height)
    {
        using var stream = new MemoryStream();
        stream.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
        byte[] ihdr = new byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(ihdr, checked((uint)width));
        BinaryPrimitives.WriteUInt32BigEndian(ihdr.AsSpan(4), checked((uint)height));
        ihdr[8] = 8;
        ihdr[9] = 6;
        WriteChunk(stream, "IHDR"u8, ihdr);
        WriteChunk(stream, "IDAT"u8, []);
        WriteChunk(stream, "IEND"u8, []);
        return stream.ToArray();
    }

    private static void WriteChunk(Stream stream, ReadOnlySpan<byte> type, ReadOnlySpan<byte> payload)
    {
        Span<byte> header = stackalloc byte[8];
        BinaryPrimitives.WriteUInt32BigEndian(header, checked((uint)payload.Length));
        type.CopyTo(header[4..]);
        stream.Write(header);
        stream.Write(payload);
        stream.Write(new byte[4]);
    }
}
