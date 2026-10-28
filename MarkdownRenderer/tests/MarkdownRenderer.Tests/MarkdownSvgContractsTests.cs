using System.Buffers;
using MarkdownRenderer.Images;
using Xunit;

namespace MarkdownRenderer.Tests;

public sealed class MarkdownSvgContractsTests
{
    [Fact]
    public void OpenAndRenderRequestsRetainEngineNeutralInputs()
    {
        byte[] source = [1, 2, 3];
        var semanticColor = new MarkdownSvgColor(10, 20, 30, 40);
        var open = new MarkdownSvgOpenRequest(
            source,
            "ar-SA",
            MarkdownSvgColorScheme.Dark,
            semanticColor);
        var tile = new MarkdownSvgTileRegion(12, 34, 56, 78);
        var render = new MarkdownSvgRenderRequest(
            1024,
            768,
            tile,
            MarkdownSvgPixelFormat.Bgra8Premultiplied);

        Assert.Equal(source, open.SanitizedSvgBytes.ToArray());
        Assert.Equal("ar-SA", open.Locale);
        Assert.Equal(MarkdownSvgColorScheme.Dark, open.ColorScheme);
        Assert.Equal(semanticColor, open.SemanticColor);
        Assert.Equal(1024, render.TargetWidthPixels);
        Assert.Equal(768, render.TargetHeightPixels);
        Assert.Equal(tile, render.TileRegion);
        Assert.Equal(MarkdownSvgPixelFormat.Bgra8Premultiplied, render.PixelFormat);
    }

    [Fact]
    public void RasterOwnsMemoryUntilDisposedAndReleasesItExactlyOnce()
    {
        var owner = new TrackingMemoryOwner(16);
        owner.Memory.Span.Fill(0x7f);
        var raster = new MarkdownSvgRaster(
            owner,
            lengthBytes: 16,
            widthPixels: 2,
            heightPixels: 2,
            strideBytes: 8,
            MarkdownSvgPixelFormat.Rgba8Premultiplied);

        Assert.Equal(16, raster.Pixels.Length);
        Assert.Equal(0x7f, raster.Pixels.Span[15]);
        Assert.Equal(16, raster.LengthBytes);

        raster.Dispose();
        raster.Dispose();

        Assert.Equal(1, owner.DisposeCount);
        Assert.Throws<ObjectDisposedException>(() => raster.Pixels);
    }

    [Fact]
    public void RasterRejectsAnUndersizedStrideOrOwner()
    {
        using var strideOwner = new TrackingMemoryOwner(16);
        Assert.Throws<ArgumentOutOfRangeException>(() => new MarkdownSvgRaster(
            strideOwner,
            lengthBytes: 16,
            widthPixels: 2,
            heightPixels: 2,
            strideBytes: 7,
            MarkdownSvgPixelFormat.Rgba8Premultiplied));

        using var lengthOwner = new TrackingMemoryOwner(15);
        Assert.Throws<ArgumentOutOfRangeException>(() => new MarkdownSvgRaster(
            lengthOwner,
            lengthBytes: 15,
            widthPixels: 2,
            heightPixels: 2,
            strideBytes: 8,
            MarkdownSvgPixelFormat.Rgba8Premultiplied));
    }

    [Fact]
    public void ExceptionRetainsTypedFailureReason()
    {
        var exception = new MarkdownSvgException(MarkdownSvgFailureReason.WorkerFailure);

        Assert.Equal(MarkdownSvgFailureReason.WorkerFailure, exception.Reason);
        Assert.NotEmpty(exception.Message);
    }

    private sealed class TrackingMemoryOwner : IMemoryOwner<byte>
    {
        private readonly byte[] _bytes;

        internal TrackingMemoryOwner(int length) => _bytes = new byte[length];

        internal int DisposeCount { get; private set; }

        public Memory<byte> Memory => _bytes;

        public void Dispose() => DisposeCount++;
    }
}
