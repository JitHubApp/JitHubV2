using System.Buffers;
using MarkdownRenderer.Images;

namespace MarkdownRenderer.GitHub.Tests.Layout.Internal.Relayout.Containers;

/// <summary>
/// A counting SVG renderer for the root-local container relayout tests. It
/// paints an opaque solid raster and records opens, full renders and tile
/// renders so a test can prove relayout reused the prepared SVG.
/// </summary>
internal sealed class ContainerSvgRenderer : IMarkdownSvgRenderer
{
    private int _openCount;
    private int _fullRenderCount;
    private int _tileRenderCount;

    internal int OpenCount => Volatile.Read(ref _openCount);
    internal int FullRenderCount => Volatile.Read(ref _fullRenderCount);
    internal int TileRenderCount => Volatile.Read(ref _tileRenderCount);
    public long CacheGeneration => 0;

    public MarkdownSvgSourcePreparation PrepareSource(byte[] source, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return MarkdownSvgSourcePreparation.Admit(source);
    }

    public ValueTask<IMarkdownSvgDocument> OpenAsync(
        MarkdownSvgOpenRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Interlocked.Increment(ref _openCount);
        var info = new MarkdownSvgDocumentInfo(64, 32, 2,
            "Nested container SVG description", HasText: false);
        return ValueTask.FromResult<IMarkdownSvgDocument>(new ContainerSvgDocument(this, info));
    }

    private sealed class ContainerSvgDocument(
        ContainerSvgRenderer owner,
        MarkdownSvgDocumentInfo info) : IMarkdownSvgDocument
    {
        private int _disposed;
        public MarkdownSvgDocumentInfo Info { get; } = info;

        public ValueTask<MarkdownSvgRaster> RenderAsync(
            MarkdownSvgRenderRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Volatile.Read(ref _disposed) != 0)
                throw new MarkdownSvgException(MarkdownSvgFailureReason.Canceled,
                    "The container SVG document has been retired.");
            if (request.TileRegion is null)
                Interlocked.Increment(ref owner._fullRenderCount);
            else
                Interlocked.Increment(ref owner._tileRenderCount);

            int width = request.TileRegion?.Width ?? request.TargetWidthPixels;
            int height = request.TileRegion?.Height ?? request.TargetHeightPixels;
            int stride = checked(width * 4);
            int length = checked(stride * height);
            IMemoryOwner<byte> memory = MemoryPool<byte>.Shared.Rent(length);
            Span<byte> pixels = memory.Memory.Span[..length];
            for (int index = 0; index < length; index += 4)
            {
                if (request.PixelFormat == MarkdownSvgPixelFormat.Bgra8Premultiplied)
                {
                    pixels[index] = 0xA8;
                    pixels[index + 1] = 0x67;
                    pixels[index + 2] = 0x23;
                }
                else
                {
                    pixels[index] = 0x23;
                    pixels[index + 1] = 0x67;
                    pixels[index + 2] = 0xA8;
                }
                pixels[index + 3] = byte.MaxValue;
            }
            return ValueTask.FromResult(new MarkdownSvgRaster(
                memory, length, width, height, stride, request.PixelFormat));
        }

        public void Dispose() => Interlocked.Exchange(ref _disposed, 1);
    }
}
