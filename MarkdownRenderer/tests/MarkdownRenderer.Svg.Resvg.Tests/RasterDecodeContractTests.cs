using System.Buffers;
using System.Buffers.Binary;
using MarkdownRenderer.Images;
using MarkdownRenderer.Svg.Resvg.Internal;
using Xunit;

namespace MarkdownRenderer.Svg.Resvg.Tests;

public sealed class RasterDecodeContractTests
{
    [Fact]
    public void RasterOwnsOnlyValidatedOutputAndDisposesExactlyOnce()
    {
        var owner = new CountingOwner(32);
        var raster = new MarkdownRaster(owner, 2, 2, 8, MarkdownRasterPixelFormat.Bgra8Premultiplied);
        Assert.Equal(16, raster.Pixels.Length);
        Assert.Equal(16, raster.LengthBytes);
        Parallel.Invoke(raster.Dispose, raster.Dispose, raster.Dispose);
        Assert.Equal(1, owner.Disposals);
        Assert.Throws<ObjectDisposedException>(() => raster.Pixels);
    }

    [Fact]
    public void InvalidRasterDoesNotStealOwnership()
    {
        using var owner = new CountingOwner(16);
        Assert.Throws<ArgumentOutOfRangeException>(() => new MarkdownRaster(owner, 2, 2, 7, MarkdownRasterPixelFormat.Bgra8Premultiplied));
        Assert.Throws<ArgumentException>(() => new MarkdownRaster(owner, 2, 3, 8, MarkdownRasterPixelFormat.Bgra8Premultiplied));
        Assert.Throws<OverflowException>(() => new MarkdownRaster(owner, int.MaxValue, 1, int.MaxValue, MarkdownRasterPixelFormat.Bgra8Premultiplied));
        Assert.Throws<ArgumentOutOfRangeException>(() => new MarkdownRaster(owner, 2, 2, 8, (MarkdownRasterPixelFormat)99));
        Assert.Equal(0, owner.Disposals);
    }

    [Fact]
    public void RasterFrameHasSeparateFieldsAndNoSvgPolicyOrDocumentState()
    {
        WorkerRequest request = Request();
        byte[] bytes = WorkerProtocol.Encode(request);
        Assert.Equal((ushort)10, BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(4)));
        Assert.Equal((ushort)7, BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(6)));
        Assert.Equal(25u, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(36)));
        Assert.Equal(16u, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(40)));
        Assert.Equal(1024ul, BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(392)));
        Assert.Equal(2u, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(400)));
        Assert.Equal(2u, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(404)));
        Assert.Equal(1u, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(408)));
        Assert.Equal(2, bytes[125]);
        Assert.Equal(-1, bytes.AsSpan(52, 73).IndexOfAnyExcept((byte)0));
        Assert.Equal(-1, bytes.AsSpan(126, 10).IndexOfAnyExcept((byte)0));
        Assert.Equal(-1, bytes.AsSpan(168, 72).IndexOfAnyExcept((byte)0));
        Assert.Equal(-1, bytes.AsSpan(248, 144).IndexOfAnyExcept((byte)0));
        Assert.Equal(-1, bytes.AsSpan(412).IndexOfAnyExcept((byte)0));
    }

    [Fact]
    public void RasterFrameRejectsMixedPolicyUpscalingAndUnadmittedMappings()
    {
        WorkerRequest request = Request();
        foreach (WorkerRequest invalid in new[]
        {
            request with { TargetWidth = 3, OutputLength = 24 },
            request with { OutputLength = 15 },
            request with { RasterWorkingBytes = 143 }, // aligned source 64 + output 16 + trailer 64
            request with { RasterWorkingBytes = RasterDecodePolicy.MaximumWorkingBytes + 1 },
            request with { RasterOrientation = 9 },
            request with { RasterSourceWidth = 16_385 },
            request with { SourceLength = RasterDecodePolicy.MaximumSourceBytes + 1 },
            request with { Locale = "en" },
            request with { FontGeneration = 1 },
            request with { ParsedResourceCacheBytes = 1 },
            request with { DocumentId = 1 },
            request with { Tile = new MarkdownSvgTileRegion(0, 0, 2, 2) },
            request with { PhysicalViewport = new MarkdownSvgPhysicalViewport(2, 2) },
            request with { SemanticColor = new MarkdownSvgColor(1, 2, 3) },
        })
            Assert.Throws<WorkerProtocolException>(() => WorkerProtocol.Encode(invalid));
        Assert.Throws<InvalidOperationException>(() => WorkerProtocol.Encode(request with { MappingHandle = 0 }));
    }

    [Fact]
    public void SvgFrameCannotCarryRasterFields()
    {
        Assert.Throws<WorkerProtocolException>(() => WorkerProtocol.Encode(Request() with
        {
            Operation = WorkerOperation.Render,
        }));
    }

    [Fact]
    public void ResponseCannotPublishWrongOrPartialPixels()
    {
        var request = new MarkdownRasterDecodeRequest(new byte[25], MarkdownRasterFormat.WebP, 2, 2, 1024);
        var geometry = new RasterDecodeGeometry(2, 2, 1, 2, 2, 16, 1024);
        var valid = new WorkerResponse(WorkerStatus.Ok, 2, 2, 1, 16, 8, 2, 2,
            MarkdownSvgPixelFormat.Bgra8Premultiplied, false, false, false, string.Empty);
        RasterDecodePolicy.ValidateResponse(valid, request, geometry);
        foreach (WorkerResponse invalid in new[]
        {
            valid with { Width = 1 }, valid with { OutputLength = 32 }, valid with { Stride = 9 },
            valid with { IntrinsicWidth = double.NaN }, valid with { AspectRatio = double.PositiveInfinity },
            valid with { HasText = true }, valid with { ReadyFontGeneration = 1 },
            valid with { PixelFormat = MarkdownSvgPixelFormat.Rgba8Premultiplied },
            valid with { Status = WorkerStatus.DocumentMissing }, valid with { Status = WorkerStatus.ResourceLimit },
        })
            Assert.Throws<WorkerProtocolException>(() => RasterDecodePolicy.ValidateResponse(invalid, request, geometry));
        var failure = new WorkerResponse(WorkerStatus.ResourceLimit, 0, 0, 0, 0, 0, 0, 0,
            MarkdownSvgPixelFormat.Rgba8Premultiplied, false, false, false, "budget");
        RasterDecodePolicy.ValidateResponse(failure, request, geometry);
    }

    [Fact]
    public async Task RejectionAndCancellationNeedNoWorker()
    {
        await using var renderer = new ResvgMarkdownSvgRenderer(new ResvgMarkdownSvgRendererOptions(
            workerExecutablePath: Path.Combine(Path.GetTempPath(), "nonexistent-raster-worker.exe")));
        Assert.True(renderer.SupportsFormat(MarkdownRasterFormat.WebP));
        Assert.False(renderer.SupportsFormat(MarkdownRasterFormat.Jpeg));
        var request = new MarkdownRasterDecodeRequest(new byte[25], MarkdownRasterFormat.WebP, 2, 2, 1024);
        MarkdownRasterException invalid = await Assert.ThrowsAsync<MarkdownRasterException>(
            () => renderer.DecodeAsync(request).AsTask());
        Assert.Equal(MarkdownRasterFailureReason.UnsupportedContent, invalid.Reason);
        MarkdownRasterException canceled = await Assert.ThrowsAsync<MarkdownRasterException>(
            () => renderer.DecodeAsync(request, new CancellationToken(true)).AsTask());
        Assert.Equal(MarkdownRasterFailureReason.Canceled, canceled.Reason);
        renderer.Dispose();
        MarkdownRasterException disposed = await Assert.ThrowsAsync<MarkdownRasterException>(
            () => renderer.DecodeAsync(request).AsTask());
        Assert.Equal(MarkdownRasterFailureReason.Canceled, disposed.Reason);
    }

    [Fact]
    public void HostPreparationUsesExactSourceGeometryAndClampsOnlyTheWorkingCeiling()
    {
        byte[] source = RasterDecodeIntegrationTests.SmallWebP();
        var request = new MarkdownRasterDecodeRequest(source, MarkdownRasterFormat.WebP, 1, 2, long.MaxValue);
        RasterDecodeGeometry geometry = RasterDecodePolicy.Inspect(request, CancellationToken.None);
        Assert.Equal(2, geometry.SourceWidth);
        Assert.Equal(2, geometry.SourceHeight);
        Assert.Equal(2, geometry.IntrinsicWidth);
        Assert.Equal(2, geometry.IntrinsicHeight);
        Assert.Equal(1, geometry.Orientation);
        Assert.Equal(8, geometry.OutputLength);
        Assert.Equal(RasterDecodePolicy.MaximumWorkingBytes, geometry.WorkingBytes);
        Assert.Equal(source, RasterDecodeIntegrationTests.SmallWebP());

        var failure = Assert.Throws<MarkdownRasterException>(() => RasterDecodePolicy.Inspect(
            request with { TargetWidthPixels = 3 }, CancellationToken.None));
        Assert.Equal(MarkdownRasterFailureReason.UnsupportedContent, failure.Reason);
        failure = Assert.Throws<MarkdownRasterException>(() => RasterDecodePolicy.Inspect(
            request with { MaximumWorkingBytes = RasterDecodePolicy.MappingBytes(source.Length, 8) - 1 },
            CancellationToken.None));
        Assert.Equal(MarkdownRasterFailureReason.ResourceLimitExceeded, failure.Reason);
    }

    [Fact]
    public void CanceledPreparationDoesNotInspectBorrowedSource()
    {
        var request = new MarkdownRasterDecodeRequest(ReadOnlyMemory<byte>.Empty,
            MarkdownRasterFormat.WebP, 1, 1, 1024);
        Assert.Throws<OperationCanceledException>(() => RasterDecodePolicy.Inspect(request,
            new CancellationToken(true)));
    }

    private static WorkerRequest Request() => new(WorkerOperation.RasterDecode, 7, 11, 13, 0x10203040,
        new byte[32], 25, 16, 2, 2, null, string.Empty, MarkdownSvgColorScheme.Light, null,
        MarkdownSvgPixelFormat.Bgra8Premultiplied, 0, 0, 0, new ResvgMarkdownSvgRendererOptions(),
        RasterWorkingBytes: 1024, RasterSourceWidth: 2, RasterSourceHeight: 2, RasterOrientation: 1);

    private sealed class CountingOwner(int size) : IMemoryOwner<byte>
    {
        private int _disposals;
        public int Disposals => Volatile.Read(ref _disposals);
        public Memory<byte> Memory { get; } = new byte[size];
        public void Dispose() => Interlocked.Increment(ref _disposals);
    }
}
