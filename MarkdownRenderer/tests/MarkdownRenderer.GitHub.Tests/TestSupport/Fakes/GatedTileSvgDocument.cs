using System.Buffers;
using MarkdownRenderer.Images.Svg;

namespace MarkdownRenderer.GitHub.Tests.TestSupport.Fakes;

/// <summary>
/// The SVG document that <see cref="GatedTileSvgRenderer"/> opens: it
/// waits on its owner before rendering the tile at (1024, 0) and returns
/// transparent tiles.
/// </summary>
internal sealed class GatedTileSvgDocument(
    GatedTileSvgRenderer owner,
    MarkdownSvgDocumentInfo info) : IMarkdownSvgDocument
{
    public MarkdownSvgDocumentInfo Info { get; } = info;

    public async ValueTask<MarkdownSvgRaster> RenderAsync(
        MarkdownSvgRenderRequest request,
        CancellationToken cancellationToken = default)
    {
        if (request.TileRegion is { X: 1024, Y: 0 })
        {
            owner.SecondVisibleTileStarted.TrySetResult();
            await owner.ReleaseSecondVisibleTile.Task.WaitAsync(cancellationToken);
        }

        cancellationToken.ThrowIfCancellationRequested();
        int width = request.TileRegion?.Width ?? request.TargetWidthPixels;
        int height = request.TileRegion?.Height ?? request.TargetHeightPixels;
        int length = checked(width * height * 4);
        IMemoryOwner<byte> memory = MemoryPool<byte>.Shared.Rent(length);
        memory.Memory.Span[..length].Fill(
            request.TileRegion is null && owner.OpaqueWholeBitmap ? (byte)255 : (byte)0);
        return new MarkdownSvgRaster(
            memory,
            length,
            width,
            height,
            checked(width * 4),
            request.PixelFormat);
    }

    public void Dispose()
    {
    }
}
