using System.Buffers;
using MarkdownRenderer.Images.Svg;

namespace MarkdownRenderer.GitHub.Tests.Layout.Internal.Images.BitmapOutput;

/// <summary>
/// An SVG renderer that admits every source, reports fixed document info and
/// renders cleared pixels, counting opens and renders.
/// </summary>
internal sealed class CountingSvgRenderer(MarkdownSvgDocumentInfo info) : IMarkdownSvgRenderer
{
    private int _openCount;
    private int _renderCount;

    internal int OpenCount => Volatile.Read(ref _openCount);
    internal int RenderCount => Volatile.Read(ref _renderCount);

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
        Interlocked.Increment(ref _openCount);
        return ValueTask.FromResult<IMarkdownSvgDocument>(new CountingSvgDocument(this, info));
    }

    private sealed class CountingSvgDocument(
        CountingSvgRenderer renderer,
        MarkdownSvgDocumentInfo info) : IMarkdownSvgDocument
    {
        public MarkdownSvgDocumentInfo Info { get; } = info;

        public ValueTask<MarkdownSvgRaster> RenderAsync(
            MarkdownSvgRenderRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref renderer._renderCount);
            int width = request.TileRegion?.Width ?? request.TargetWidthPixels;
            int height = request.TileRegion?.Height ?? request.TargetHeightPixels;
            int stride = checked(width * 4);
            int length = checked(stride * height);
            IMemoryOwner<byte> owner = MemoryPool<byte>.Shared.Rent(length);
            owner.Memory.Span[..length].Clear();
            return ValueTask.FromResult(new MarkdownSvgRaster(
                owner,
                length,
                width,
                height,
                stride,
                request.PixelFormat));
        }

        public void Dispose() { }
    }
}
