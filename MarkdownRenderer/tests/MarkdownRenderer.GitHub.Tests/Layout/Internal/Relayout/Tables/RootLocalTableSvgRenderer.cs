using System.Buffers;
using System.Text;
using MarkdownRenderer.Images.Svg;

namespace MarkdownRenderer.GitHub.Tests.Layout.Internal.Relayout.Tables;

/// <summary>
/// SVG renderer for the root-local table relayout tests. It recognises the
/// wide and tall table fixtures by fill colour, reports their intrinsic sizes
/// and provider descriptions, fills rasters with a solid colour, and counts
/// opens, full renders and tile renders.
/// </summary>
internal sealed class RootLocalTableSvgRenderer : IMarkdownSvgRenderer
{
    private long _cacheGeneration;
    private int _openCount;
    private int _fullRenderCount;
    private int _tileRenderCount;

    internal int OpenCount => Volatile.Read(ref _openCount);
    internal int FullRenderCount => Volatile.Read(ref _fullRenderCount);
    internal int TileRenderCount => Volatile.Read(ref _tileRenderCount);
    public long CacheGeneration => Interlocked.Read(ref _cacheGeneration);
    internal void AdvanceCacheGeneration() => Interlocked.Increment(ref _cacheGeneration);

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
        string source = Encoding.UTF8.GetString(request.SanitizedSvgBytes.Span);
        bool wide = source.Contains("fill=\"#c33b21\"", StringComparison.OrdinalIgnoreCase);
        bool tall = source.Contains("fill=\"#2179c3\"", StringComparison.OrdinalIgnoreCase);
        if (wide == tall)
            throw new MarkdownSvgException(MarkdownSvgFailureReason.UnsupportedContent,
                "The test SVG did not contain one known table fixture marker.");
        double width = wide ? 480 : 96;
        double height = wide ? 96 : 240;
        var info = new MarkdownSvgDocumentInfo(width, height, width / height,
            wide ? "Wide provider description" : "Tall provider description", HasText: true);
        return ValueTask.FromResult<IMarkdownSvgDocument>(new TableSvgDocument(this, info,
            wide ? (byte)0xC3 : (byte)0x21,
            wide ? (byte)0x3B : (byte)0x79,
            wide ? (byte)0x21 : (byte)0xC3));
    }

    private sealed class TableSvgDocument(
        RootLocalTableSvgRenderer owner,
        MarkdownSvgDocumentInfo info,
        byte red,
        byte green,
        byte blue) : IMarkdownSvgDocument
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
                    "The test SVG document has been retired.");
            if (request.TileRegion is not null)
                Interlocked.Increment(ref owner._tileRenderCount);
            else
                Interlocked.Increment(ref owner._fullRenderCount);

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
                    pixels[index] = blue;
                    pixels[index + 1] = green;
                    pixels[index + 2] = red;
                }
                else
                {
                    pixels[index] = red;
                    pixels[index + 1] = green;
                    pixels[index + 2] = blue;
                }
                pixels[index + 3] = byte.MaxValue;
            }
            return ValueTask.FromResult(new MarkdownSvgRaster(
                memory, length, width, height, stride, request.PixelFormat));
        }

        public void Dispose() => Interlocked.Exchange(ref _disposed, 1);
    }
}
