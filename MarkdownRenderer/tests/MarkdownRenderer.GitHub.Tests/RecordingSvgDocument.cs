using System.Buffers;
using System.Collections.Concurrent;
using MarkdownRenderer.Images;

namespace MarkdownRenderer.GitHub.Tests;

/// <summary>
/// The SVG document that <see cref="RecordingSvgRenderer"/> opens: it
/// records each render request and returns a cleared raster that the
/// test may fill.
/// </summary>
internal sealed class RecordingSvgDocument(
    MarkdownSvgDocumentInfo info,
    ConcurrentQueue<MarkdownSvgRenderRequest> requests,
    Action<Memory<byte>, int, int>? fillPixels = null)
    : IMarkdownSvgDocument
{
    public MarkdownSvgDocumentInfo Info { get; } = info;

    public ValueTask<MarkdownSvgRaster> RenderAsync(
        MarkdownSvgRenderRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        requests.Enqueue(request);
        int width = request.TileRegion?.Width ?? request.TargetWidthPixels;
        int height = request.TileRegion?.Height ?? request.TargetHeightPixels;
        int length = checked(width * height * 4);
        IMemoryOwner<byte> owner = MemoryPool<byte>.Shared.Rent(length);
        owner.Memory.Span[..length].Clear();
        fillPixels?.Invoke(owner.Memory[..length], width, height);
        return ValueTask.FromResult(new MarkdownSvgRaster(
            owner,
            length,
            width,
            height,
            checked(width * 4),
            request.PixelFormat));
    }

    public void Dispose()
    {
    }
}
