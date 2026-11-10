using System.Collections.Concurrent;
using System.Text;
using MarkdownRenderer.GitHub.Tests.TestSupport.Fakes;
using MarkdownRenderer.GitHub.Tests.TestSupport.Fixtures;
using MarkdownRenderer.Images;
using MarkdownRenderer.Layout.Internal.Images;
using MarkdownRenderer.Layout.Internal.Images.Svg;
using MarkdownRenderer.Layout.Internal.RenderResources;
using Xunit;
using static MarkdownRenderer.GitHub.Tests.TestSupport.Fixtures.ImageBoxSvgRendererFixture;

namespace MarkdownRenderer.GitHub.Tests;

/// <summary>
/// Checks how SVG image boxes share sources: resolver-owned and content
/// cache keys, shared provider opens and rasters for identical boxes, no
/// sharing for unkeyed resolvers, and concurrent opens of distinct boxes.
/// </summary>
[Collection(ProcessRasterDecodeCollection.CollectionName)]
public sealed class ImageBoxSvgSourceCacheTests
{
    private readonly MarkdownRenderResources _renderResources = new();

    [Fact]
    public async Task ResolverOwnedSvgCacheKeyCannotSatisfyBuiltInLocalFallback()
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            "MarkdownRendererSvgAlias-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "built-in.svg");
        string builtInUri = new Uri(path).AbsoluteUri;
        string resolverSource = "https://resolver.test/" + Guid.NewGuid().ToString("N") + ".svg";
        var renderer = new RecordingSvgRenderer(new MarkdownSvgDocumentInfo(
            IntrinsicWidthDips: 80,
            IntrinsicHeightDips: 40,
            IntrinsicAspectRatio: 2));
        ImageBox? resolverImage = null;
        ImageBox? builtInImage = null;

        try
        {
            await File.WriteAllTextAsync(path, CreateSvgMarkup("Built-in B"));
            var asset = new MarkdownImageAsset(
                CreateSvgBytes("Resolver A"),
                ContentType: "image/svg+xml",
                ResolvedUri: new Uri(resolverSource),
                CacheKey: builtInUri);
            resolverImage = new ImageBox(
                CreateContext(
                    _renderResources,
                    renderer,
                    imageResolver: new FixedImageResolver(asset)), _renderResources.Images,
                resolverSource,
                "Resolver-owned SVG");

            resolverImage.Measure(80);
            await WaitForLoadAsync(resolverImage);
            Assert.NotNull(resolverImage.Bitmap);
            Assert.Equal("Resolver A", resolverImage.SvgTitle);
            int opensAfterResolverLoad = renderer.OpenCount;
            Assert.Equal(1, opensAfterResolverLoad);

            builtInImage = new ImageBox(
                CreateContext(_renderResources, renderer, imageResolver: new NotHandledImageResolver()), _renderResources.Images,
                builtInUri,
                "Built-in SVG");
            builtInImage.Measure(80);
            await WaitForLoadAsync(builtInImage);

            Assert.NotNull(builtInImage.Bitmap);
            Assert.Equal("Built-in B", builtInImage.SvgTitle);
            Assert.Equal(opensAfterResolverLoad + 1, renderer.OpenCount);
        }
        finally
        {
            builtInImage?.Dispose();
            resolverImage?.Dispose();
            try
            {
                await Task.WhenAll(builtInImage?.RetirementTask ?? Task.CompletedTask,
                    resolverImage?.RetirementTask ?? Task.CompletedTask).WaitAsync(TimeSpan.FromSeconds(10));
            }
            finally
            {
                RemoveProcessSvgCacheEntry(builtInUri);
                File.Delete(path);
                Directory.Delete(directory);
            }
        }
    }

    [Fact]
    public async Task ResolverSvgContentHitStillReusesThePartitionedContentEntryWithoutSourceAlias()
    {
        string suffix = Guid.NewGuid().ToString("N");
        string source = "https://resolver.test/" + suffix + ".svg";
        string partitionedCacheKey = "github:private-repository:" + suffix;
        var asset = new MarkdownImageAsset(
            CreateSvgBytes("Partitioned content"),
            ContentType: "image/svg+xml",
            ResolvedUri: new Uri(source),
            CacheKey: partitionedCacheKey);
        var resolver = new FixedImageResolver(asset);
        var renderer = new RecordingSvgRenderer(new MarkdownSvgDocumentInfo(
            IntrinsicWidthDips: 80,
            IntrinsicHeightDips: 40,
            IntrinsicAspectRatio: 2));
        ImageBox? first = null;
        ImageBox? second = null;

        try
        {
            first = new ImageBox(
                CreateContext(_renderResources, renderer, imageResolver: resolver), _renderResources.Images,
                source,
                "First partitioned SVG");
            first.Measure(80);
            await WaitForLoadAsync(first);

            Assert.NotNull(first.Bitmap);
            Assert.Equal(1, renderer.OpenCount);
            Assert.Null(GetProcessSvgCacheEntry(partitionedCacheKey));

            first.Dispose();
            await first.RetirementTask.WaitAsync(TimeSpan.FromSeconds(10));
            first = null;
            int opensAfterFirstLoad = renderer.OpenCount;

            second = new ImageBox(
                CreateContext(_renderResources, renderer, imageResolver: resolver), _renderResources.Images,
                source,
                "Second partitioned SVG");
            second.Measure(80);
            await WaitForLoadAsync(second);

            Assert.NotNull(second.Bitmap);
            Assert.Equal(opensAfterFirstLoad, renderer.OpenCount);
            Assert.Null(GetProcessSvgCacheEntry(partitionedCacheKey));
        }
        finally
        {
            second?.Dispose();
            first?.Dispose();
            try
            {
                await Task.WhenAll(second?.RetirementTask ?? Task.CompletedTask,
                    first?.RetirementTask ?? Task.CompletedTask).WaitAsync(TimeSpan.FromSeconds(10));
            }
            finally { RemoveProcessSvgCacheEntry(partitionedCacheKey); }
        }
    }

    [Fact]
    public async Task ConcurrentIdenticalSvgBoxesShareProviderOpenAndRaster()
    {
        var renderer = new RecordingSvgRenderer(
            new MarkdownSvgDocumentInfo(
                IntrinsicWidthDips: 80,
                IntrinsicHeightDips: 40,
                IntrinsicAspectRatio: 2),
            openDelay: TimeSpan.FromMilliseconds(150));
        string source = CreateSvgDataUri(Guid.NewGuid().ToString("N"));
        var first = new ImageBox(CreateContext(_renderResources, renderer), _renderResources.Images, source, "duplicate");
        var second = new ImageBox(CreateContext(_renderResources, renderer), _renderResources.Images, source, "duplicate");

        try
        {
            first.Measure(80);
            second.Measure(80);

            await Task.WhenAll(
                WaitForLoadAsync(first),
                WaitForLoadAsync(second));

            Assert.NotNull(first.Bitmap);
            Assert.NotNull(second.Bitmap);
            Assert.Equal(1, renderer.OpenCount);
            Assert.Single(renderer.RenderRequests);
        }
        finally
        {
            first.Dispose();
            second.Dispose();
        }
    }

    [Fact]
    public async Task UnkeyedResolverSvgDoesNotShareSourceOrDeviceBitmapAcrossBoxes()
    {
        string source = $"https://resolver.test/unkeyed-{Guid.NewGuid():N}.svg";
        var resolver = new FixedImageResolver(new MarkdownImageAsset(
            CreateSvgBytes("Unkeyed source"), "image/svg+xml",
            ResolvedUri: new Uri(source), CacheKey: string.Empty));
        var renderer = new RecordingSvgRenderer(new MarkdownSvgDocumentInfo(80, 40));
        var first = new ImageBox(CreateContext(_renderResources, renderer, imageResolver: resolver), _renderResources.Images, source, "First unkeyed SVG");
        var second = new ImageBox(CreateContext(_renderResources, renderer, imageResolver: resolver), _renderResources.Images, source, "Second unkeyed SVG");
        try
        {
            first.Measure(80);
            await WaitForLoadAsync(first);
            Assert.NotNull(first.Bitmap);
            int opens = renderer.OpenCount;
            int renders = renderer.RenderRequests.Count;
            Assert.Equal(1, opens);
            Assert.Equal(1, renders);

            second.Measure(80);
            await WaitForLoadAsync(second);
            Assert.NotNull(second.Bitmap);
            Assert.Equal(opens + 1, renderer.OpenCount);
            Assert.Equal(renders + 1, renderer.RenderRequests.Count);
            Assert.NotSame(first.Bitmap, second.Bitmap);
        }
        finally
        {
            first.Dispose();
            second.Dispose();
            await Task.WhenAll(first.RetirementTask, second.RetirementTask)
                .WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    [Fact]
    public async Task DistinctSvgBoxesCanOpenConcurrently()
    {
        var renderer = new ConcurrentOpenSvgRenderer(new MarkdownSvgDocumentInfo(
            IntrinsicWidthDips: 80,
            IntrinsicHeightDips: 40,
            IntrinsicAspectRatio: 2));
        var first = new ImageBox(
            CreateContext(_renderResources, renderer), _renderResources.Images,
            CreateSvgDataUri("parallel-first-" + Guid.NewGuid().ToString("N")),
            "first independent SVG");
        var second = new ImageBox(
            CreateContext(_renderResources, renderer), _renderResources.Images,
            CreateSvgDataUri("parallel-second-" + Guid.NewGuid().ToString("N")),
            "second independent SVG");

        try
        {
            first.Measure(80);
            second.Measure(80);
            Task firstLoad = WaitForLoadAsync(first);
            Task secondLoad = WaitForLoadAsync(second);

            bool twoOpensStarted = await Task.WhenAny(
                    renderer.TwoOpensStarted.Task,
                    Task.Delay(TimeSpan.FromSeconds(5))) == renderer.TwoOpensStarted.Task;
            Assert.True(
                twoOpensStarted,
                $"Expected two distinct SVG opens to overlap; opens={renderer.OpenCount}, " +
                $"maximum concurrent={renderer.MaximumConcurrentOpens}.");
            Assert.Equal(2, renderer.MaximumConcurrentOpens);
            renderer.ReleaseOpens.TrySetResult();

            await Task.WhenAll(firstLoad, secondLoad);
            Assert.NotNull(first.Bitmap);
            Assert.NotNull(second.Bitmap);
            Assert.Equal(2, renderer.OpenCount);
        }
        finally
        {
            renderer.ReleaseOpens.TrySetResult();
            first.Dispose();
            second.Dispose();
        }
    }

    private static byte[] CreateSvgBytes(string title) =>
        Encoding.UTF8.GetBytes(CreateSvgMarkup(title));

    private object? GetProcessSvgCacheEntry(string key)
    {
        return _renderResources.Images.SvgSources.TryGetValue(key, out SvgCacheEntry? entry) ? entry : null;
    }

    private void RemoveProcessSvgCacheEntry(string key)
    {
        _ = _renderResources.Images.SvgSources.Remove(key);
    }

    private sealed class ConcurrentOpenSvgRenderer(MarkdownSvgDocumentInfo info)
        : IMarkdownSvgRenderer
    {
        private int _openCount;
        private int _activeOpenCount;
        private int _maximumConcurrentOpens;

        internal TaskCompletionSource TwoOpensStarted { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource ReleaseOpens { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        internal int OpenCount => Volatile.Read(ref _openCount);

        internal int MaximumConcurrentOpens => Volatile.Read(ref _maximumConcurrentOpens);

        public long CacheGeneration => 0;

        public MarkdownSvgSourcePreparation PrepareSource(
            byte[] source,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return MarkdownSvgSourcePreparation.Admit(source);
        }

        public async ValueTask<IMarkdownSvgDocument> OpenAsync(
            MarkdownSvgOpenRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _openCount);
            int active = Interlocked.Increment(ref _activeOpenCount);
            int observedMaximum;
            while (active > (observedMaximum = Volatile.Read(ref _maximumConcurrentOpens)) &&
                   Interlocked.CompareExchange(
                       ref _maximumConcurrentOpens,
                       active,
                       observedMaximum) != observedMaximum)
            {
            }

            if (active >= 2)
                TwoOpensStarted.TrySetResult();

            try
            {
                await ReleaseOpens.Task.WaitAsync(cancellationToken);
                return new RecordingSvgDocument(info, new ConcurrentQueue<MarkdownSvgRenderRequest>());
            }
            finally
            {
                Interlocked.Decrement(ref _activeOpenCount);
            }
        }
    }
}
