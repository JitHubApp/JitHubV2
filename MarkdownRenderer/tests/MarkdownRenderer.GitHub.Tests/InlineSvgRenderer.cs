using System.Buffers;
using MarkdownRenderer.Images;

namespace MarkdownRenderer.GitHub.Tests;

/// <summary>
/// Offline SVG renderer for the root-local inline image relayout tests. It
/// counts cache-generation reads (with an optional hook to observe the caller
/// thread state) and full versus tiled renders, and paints a solid raster.
/// </summary>
internal sealed class InlineSvgRenderer : IMarkdownSvgRenderer
{
    private long _generation;
    private int _cacheGenerationReadCount;
    private int _fullRenderCount;
    private int _tiledRenderCount;
    private readonly MarkdownSvgDocumentInfo _info = new(
        IntrinsicWidthDips: 80,
        IntrinsicHeightDips: 40,
        IntrinsicAspectRatio: 2,
        Description: "Provider supplied SVG description",
        HasText: true,
        UsesCurrentColor: false,
        UsesColorScheme: false);

    internal Action? GenerationReadHook { get; set; }
    internal int CacheGenerationReadCount => Volatile.Read(ref _cacheGenerationReadCount);
    internal int FullRenderCount => Volatile.Read(ref _fullRenderCount);
    internal int TiledRenderCount => Volatile.Read(ref _tiledRenderCount);

    public long CacheGeneration
    {
        get
        {
            Interlocked.Increment(ref _cacheGenerationReadCount);
            GenerationReadHook?.Invoke();
            return Interlocked.Read(ref _generation);
        }
    }

    internal void SetGeneration(long generation) => Interlocked.Exchange(ref _generation, generation);

    public MarkdownSvgSourcePreparation PrepareSource(
        byte[] source,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return MarkdownSvgSourcePreparation.Admit(source);
    }

    public ValueTask<IMarkdownSvgDocument> OpenAsync(
        MarkdownSvgOpenRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult<IMarkdownSvgDocument>(new InlineSvgDocument(this, _info));
    }

    private sealed class InlineSvgDocument(
        InlineSvgRenderer renderer,
        MarkdownSvgDocumentInfo info) : IMarkdownSvgDocument
    {
        private int _disposed;
        public MarkdownSvgDocumentInfo Info { get; } = info;

        public ValueTask<MarkdownSvgRaster> RenderAsync(
            MarkdownSvgRenderRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            int width = request.TileRegion?.Width ?? request.TargetWidthPixels;
            int height = request.TileRegion?.Height ?? request.TargetHeightPixels;
            if (request.TileRegion is null)
                Interlocked.Increment(ref renderer._fullRenderCount);
            else
                Interlocked.Increment(ref renderer._tiledRenderCount);

            int stride = checked(width * 4);
            int length = checked(stride * height);
            IMemoryOwner<byte> owner = MemoryPool<byte>.Shared.Rent(length);
            Span<byte> pixels = owner.Memory.Span[..length];
            bool bgra = request.PixelFormat == MarkdownSvgPixelFormat.Bgra8Premultiplied;
            byte red = 208;
            byte green = 64;
            byte blue = 32;
            byte first = bgra ? blue : red;
            byte third = bgra ? red : blue;
            for (int offset = 0; offset < length; offset += 4)
            {
                pixels[offset] = first;
                pixels[offset + 1] = green;
                pixels[offset + 2] = third;
                pixels[offset + 3] = byte.MaxValue;
            }

            return ValueTask.FromResult(new MarkdownSvgRaster(
                owner,
                length,
                width,
                height,
                stride,
                request.PixelFormat));
        }

        public void Dispose() => Interlocked.Exchange(ref _disposed, 1);
    }
}
