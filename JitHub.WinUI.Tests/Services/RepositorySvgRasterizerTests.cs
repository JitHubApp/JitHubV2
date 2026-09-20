using System.Buffers;
using System.Text;
using JitHub.Services.CodeViewer;
using MarkdownRenderer.Images;
using Xunit;

namespace JitHub.WinUI.Tests.Services;

public sealed class RepositorySvgRasterizerTests
{
    private static readonly byte[] ValidSvg = Encoding.UTF8.GetBytes(
        "<svg xmlns='http://www.w3.org/2000/svg' width='10' height='8'/>");

    [Fact]
    public async Task LoadAsync_PassesRenderingEnvironmentAndIntrinsicMetadata()
    {
        FakeSvgDocument providerDocument = new(new MarkdownSvgDocumentInfo(
            IntrinsicWidthDips: 10,
            IntrinsicHeightDips: 8,
            Description: "Architecture diagram",
            UsesCurrentColor: true,
            UsesColorScheme: true));
        FakeSvgRenderer renderer = new(providerDocument);
        RepositorySvgRasterizer rasterizer = new(renderer);
        MarkdownSvgColor semanticColor = new(0x12, 0x34, 0x56);

        using RepositorySvgDocument? document = await rasterizer.LoadAsync(
            ValidSvg,
            "ar-SA",
            MarkdownSvgColorScheme.Dark,
            semanticColor,
            CancellationToken.None);

        Assert.NotNull(document);
        Assert.Equal(10, document.Width);
        Assert.Equal(8, document.Height);
        Assert.Equal(0, document.CacheGeneration);
        Assert.Equal("Architecture diagram", document.Info.Description);
        Assert.Equal("ar-SA", renderer.LastOpenRequest!.Locale);
        Assert.Equal(MarkdownSvgColorScheme.Dark, renderer.LastOpenRequest.ColorScheme);
        Assert.Equal(semanticColor, renderer.LastOpenRequest.SemanticColor);
        Assert.Equal(ValidSvg, renderer.LastOpenRequest.SanitizedSvgBytes.ToArray());
    }

    [Fact]
    public async Task RasterizeTileAsync_RequestsExactBgraTileAndOwnsRasterUntilDisposed()
    {
        TrackingMemoryOwner owner = new(new byte[8 * 6 * 4]);
        owner.Memory.Span.Fill(0x7f);
        FakeSvgDocument providerDocument = new(
            new MarkdownSvgDocumentInfo(10, 8),
            (request, _) => ValueTask.FromResult(new MarkdownSvgRaster(
                owner,
                owner.Memory.Length,
                request.TileRegion!.Value.Width,
                request.TileRegion.Value.Height,
                request.TileRegion.Value.Width * 4,
                request.PixelFormat)));
        RepositorySvgRasterizer rasterizer = new(new FakeSvgRenderer(providerDocument));
        using RepositorySvgDocument document = Assert.IsType<RepositorySvgDocument>(
            await rasterizer.LoadAsync(
                ValidSvg,
                null,
                MarkdownSvgColorScheme.Light,
                null,
                CancellationToken.None));

        using (RepositorySvgTile tile = await rasterizer.RasterizeTileAsync(
            document,
            new RepositorySvgTileRequest(40, 32, 4, 3, 8, 6),
            MarkdownSvgPixelFormat.Bgra8Premultiplied,
            CancellationToken.None))
        {
            Assert.Equal(8 * 6 * 4, tile.ByteCount);
            Assert.All(tile.BgraPixels.ToArray(), value => Assert.Equal(0x7f, value));
            Assert.False(owner.IsDisposed);
        }

        Assert.True(owner.IsDisposed);
        MarkdownSvgRenderRequest request = Assert.IsType<MarkdownSvgRenderRequest>(
            providerDocument.LastRenderRequest);
        Assert.Equal(40, request.TargetWidthPixels);
        Assert.Equal(32, request.TargetHeightPixels);
        Assert.Equal(new MarkdownSvgTileRegion(4, 3, 8, 6), request.TileRegion);
        Assert.Equal(MarkdownSvgPixelFormat.Bgra8Premultiplied, request.PixelFormat);
    }

    [Theory]
    [InlineData(MarkdownSvgPixelFormat.Rgba8Premultiplied)]
    [InlineData(MarkdownSvgPixelFormat.Bgra8Premultiplied)]
    public async Task RasterizeTileAsync_ForwardsNegotiatedPixelFormat(
        MarkdownSvgPixelFormat pixelFormat)
    {
        FakeSvgDocument providerDocument = new(new MarkdownSvgDocumentInfo(10, 8));
        RepositorySvgRasterizer rasterizer = new(new FakeSvgRenderer(providerDocument));
        using RepositorySvgDocument document = Assert.IsType<RepositorySvgDocument>(
            await rasterizer.LoadAsync(
                ValidSvg,
                null,
                MarkdownSvgColorScheme.Light,
                null,
                CancellationToken.None));

        using RepositorySvgTile tile = await rasterizer.RasterizeTileAsync(
            document,
            new RepositorySvgTileRequest(10, 8, 0, 0, 10, 8),
            pixelFormat,
            CancellationToken.None);

        Assert.Equal(pixelFormat, tile.PixelFormat);
        Assert.NotNull(providerDocument.LastRenderRequest);
        Assert.Equal(pixelFormat, providerDocument.LastRenderRequest.PixelFormat);
    }

    [Fact]
    public async Task LoadAsync_RejectsExternalResourcesBeforeOpeningWorkerDocument()
    {
        FakeSvgRenderer renderer = new(new FakeSvgDocument(new MarkdownSvgDocumentInfo(10, 8)));
        RepositorySvgRasterizer rasterizer = new(renderer);
        byte[] bytes = Encoding.UTF8.GetBytes(
            "<svg xmlns='http://www.w3.org/2000/svg' width='10' height='10'>" +
            "<image href='https://example.com/tracker.png'/></svg>");

        RepositorySvgDocument? document = await rasterizer.LoadAsync(
            bytes,
            null,
            MarkdownSvgColorScheme.Light,
            null,
            CancellationToken.None);

        Assert.Null(document);
        Assert.Equal(0, renderer.OpenCount);
    }

    [Fact]
    public async Task LoadAsync_AllowsSupportedEmbeddedImageDataUri()
    {
        FakeSvgRenderer renderer = new(new FakeSvgDocument(new MarkdownSvgDocumentInfo(10, 8)));
        RepositorySvgRasterizer rasterizer = new(renderer);
        byte[] bytes = Encoding.UTF8.GetBytes(
            "<svg xmlns='http://www.w3.org/2000/svg' width='10' height='8'>" +
            "<image href='data:image/png;base64,iVBORw0KGgo='/></svg>");

        using RepositorySvgDocument? document = await rasterizer.LoadAsync(
            bytes,
            null,
            MarkdownSvgColorScheme.Light,
            null,
            CancellationToken.None);

        Assert.NotNull(document);
        Assert.Equal(1, renderer.OpenCount);
    }

    [Fact]
    public async Task LoadAsync_ObservesCancellation()
    {
        RepositorySvgRasterizer rasterizer = new(
            new FakeSvgRenderer(new FakeSvgDocument(new MarkdownSvgDocumentInfo(10, 8))));
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await rasterizer.LoadAsync(
                ValidSvg,
                null,
                MarkdownSvgColorScheme.Light,
                null,
                cancellation.Token));
    }

    [Fact]
    public async Task DocumentDisposal_WaitsForAnActiveRender()
    {
        TaskCompletionSource<MarkdownSvgRaster> renderCompletion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        FakeSvgDocument providerDocument = new(
            new MarkdownSvgDocumentInfo(10, 8),
            (_, _) => new ValueTask<MarkdownSvgRaster>(renderCompletion.Task));
        RepositorySvgRasterizer rasterizer = new(new FakeSvgRenderer(providerDocument));
        RepositorySvgDocument document = Assert.IsType<RepositorySvgDocument>(
            await rasterizer.LoadAsync(
                ValidSvg,
                null,
                MarkdownSvgColorScheme.Light,
                null,
                CancellationToken.None));

        Task<RepositorySvgTile> render = rasterizer.RasterizeTileAsync(
            document,
            new RepositorySvgTileRequest(10, 8, 0, 0, 10, 8),
            MarkdownSvgPixelFormat.Bgra8Premultiplied,
            CancellationToken.None).AsTask();
        document.Dispose();
        Assert.False(providerDocument.IsDisposed);

        TrackingMemoryOwner owner = new(new byte[10 * 8 * 4]);
        renderCompletion.SetResult(new MarkdownSvgRaster(
            owner,
            owner.Memory.Length,
            10,
            8,
            10 * 4,
            MarkdownSvgPixelFormat.Bgra8Premultiplied));
        using RepositorySvgTile tile = await render;

        Assert.True(providerDocument.IsDisposed);
    }

    [Fact]
    public async Task CacheIdentity_UsesOnlyDocumentRelevantEnvironmentInputs()
    {
        FakeSvgRenderer renderer = new(
            new FakeSvgDocument(new MarkdownSvgDocumentInfo(10, 8)))
        {
            CacheGeneration = 4,
        };

        using RepositorySvgDocument first = Assert.IsType<RepositorySvgDocument>(
            await new RepositorySvgRasterizer(renderer).LoadAsync(
                ValidSvg,
                "en-US",
                MarkdownSvgColorScheme.Light,
                new MarkdownSvgColor(1, 2, 3),
                CancellationToken.None));
        using RepositorySvgDocument second = Assert.IsType<RepositorySvgDocument>(
            await new RepositorySvgRasterizer(renderer).LoadAsync(
                ValidSvg,
                "ar-SA",
                MarkdownSvgColorScheme.Dark,
                new MarkdownSvgColor(4, 5, 6),
                CancellationToken.None));

        Assert.Equal(first.CacheIdentity, second.CacheIdentity);
    }

    [Fact]
    public async Task CacheIdentity_PartitionsByRendererButIgnoresUnusedFontGeneration()
    {
        MarkdownSvgDocumentInfo info = new(10, 8);
        FakeSvgRenderer firstRenderer = new(new FakeSvgDocument(info)) { CacheGeneration = 4 };
        FakeSvgRenderer secondRenderer = new(new FakeSvgDocument(info)) { CacheGeneration = 4 };

        using RepositorySvgDocument first = Assert.IsType<RepositorySvgDocument>(
            await new RepositorySvgRasterizer(firstRenderer).LoadAsync(
                ValidSvg,
                null,
                MarkdownSvgColorScheme.Light,
                null,
                CancellationToken.None));
        using RepositorySvgDocument otherRenderer = Assert.IsType<RepositorySvgDocument>(
            await new RepositorySvgRasterizer(secondRenderer).LoadAsync(
                ValidSvg,
                null,
                MarkdownSvgColorScheme.Light,
                null,
                CancellationToken.None));

        firstRenderer.CacheGeneration = 5;
        using RepositorySvgDocument nextGeneration = Assert.IsType<RepositorySvgDocument>(
            await new RepositorySvgRasterizer(firstRenderer).LoadAsync(
                ValidSvg,
                null,
                MarkdownSvgColorScheme.Light,
                null,
                CancellationToken.None));

        Assert.NotEqual(first.CacheIdentity, otherRenderer.CacheIdentity);
        Assert.Equal(first.CacheIdentity, nextGeneration.CacheIdentity);
    }

    [Fact]
    public async Task CacheIdentity_ChangesForTextFontGenerationAndUsedThemeInputs()
    {
        MarkdownSvgDocumentInfo info = new(
            10,
            8,
            HasText: true,
            UsesCurrentColor: true,
            UsesColorScheme: true);
        FakeSvgRenderer renderer = new(new FakeSvgDocument(info)) { CacheGeneration = 4 };

        using RepositorySvgDocument first = Assert.IsType<RepositorySvgDocument>(
            await new RepositorySvgRasterizer(renderer).LoadAsync(
                ValidSvg,
                "en-US",
                MarkdownSvgColorScheme.Light,
                new MarkdownSvgColor(1, 2, 3),
                CancellationToken.None));
        renderer.CacheGeneration = 5;
        using RepositorySvgDocument second = Assert.IsType<RepositorySvgDocument>(
            await new RepositorySvgRasterizer(renderer).LoadAsync(
                ValidSvg,
                "ar-SA",
                MarkdownSvgColorScheme.Dark,
                new MarkdownSvgColor(4, 5, 6),
                CancellationToken.None));

        Assert.NotEqual(first.CacheIdentity, second.CacheIdentity);
    }

    private sealed class FakeSvgRenderer(FakeSvgDocument document) : IMarkdownSvgRenderer
    {
        public long CacheGeneration { get; set; }

        public int OpenCount { get; private set; }

        public MarkdownSvgOpenRequest? LastOpenRequest { get; private set; }

        public ValueTask<IMarkdownSvgDocument> OpenAsync(
            MarkdownSvgOpenRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            OpenCount++;
            LastOpenRequest = request;
            return ValueTask.FromResult<IMarkdownSvgDocument>(document);
        }
    }

    private sealed class FakeSvgDocument(
        MarkdownSvgDocumentInfo info,
        Func<MarkdownSvgRenderRequest, CancellationToken, ValueTask<MarkdownSvgRaster>>? render = null)
        : IMarkdownSvgDocument
    {
        public MarkdownSvgDocumentInfo Info { get; } = info;

        public bool IsDisposed { get; private set; }

        public MarkdownSvgRenderRequest? LastRenderRequest { get; private set; }

        public ValueTask<MarkdownSvgRaster> RenderAsync(
            MarkdownSvgRenderRequest request,
            CancellationToken cancellationToken = default)
        {
            ObjectDisposedException.ThrowIf(IsDisposed, this);
            cancellationToken.ThrowIfCancellationRequested();
            LastRenderRequest = request;
            if (render is not null)
            {
                return render(request, cancellationToken);
            }

            MarkdownSvgTileRegion region = request.TileRegion ??
                new MarkdownSvgTileRegion(0, 0, request.TargetWidthPixels, request.TargetHeightPixels);
            TrackingMemoryOwner owner = new(new byte[region.Width * region.Height * 4]);
            return ValueTask.FromResult(new MarkdownSvgRaster(
                owner,
                owner.Memory.Length,
                region.Width,
                region.Height,
                region.Width * 4,
                request.PixelFormat));
        }

        public void Dispose() => IsDisposed = true;
    }

    private sealed class TrackingMemoryOwner(byte[] bytes) : IMemoryOwner<byte>
    {
        private byte[]? _bytes = bytes;

        public bool IsDisposed => _bytes is null;

        public Memory<byte> Memory => _bytes ??
            throw new ObjectDisposedException(nameof(TrackingMemoryOwner));

        public void Dispose() => _bytes = null;
    }
}
