using System.Buffers.Binary;
using MarkdownRenderer.Layout.Boxes;
using MarkdownRenderer.Parsing;
using Xunit;

namespace MarkdownRenderer.GitHub.Tests;

public sealed class RasterImageDecodePlanTests
{
    [Theory]
    [InlineData(1, 640, 360, 240, 135)]
    [InlineData(2, 640, 360, 240, 135)]
    [InlineData(3, 640, 360, 240, 135)]
    [InlineData(4, 640, 360, 240, 135)]
    [InlineData(5, 360, 640, 427, 240)]
    [InlineData(6, 360, 640, 427, 240)]
    [InlineData(7, 360, 640, 427, 240)]
    [InlineData(8, 360, 640, 427, 240)]
    public void OrientationProducesDisplayAndPreRotationDimensions(
        int orientation,
        int intrinsicWidth,
        int intrinsicHeight,
        int decodeWidth,
        int decodeHeight)
    {
        byte[] source = CreateJpeg(640, 360, CreateTiff(orientation));
        RasterImageBudgetResult budget = RasterImageResourceBudget.Validate(source);
        var sizing = new RasterImageSizingRequest(
            availableWidth: 120,
            rasterizationScale: 2,
            requestedWidth: null,
            requestedHeight: null,
            maximumPixels: 1_000_000);

        Assert.True(RasterIntrinsicMetadata.TryReadValidated(
            source, budget, out RasterIntrinsicMetadata metadata, out int parsedOrientation));
        Assert.Equal(orientation, parsedOrientation);
        Assert.Equal(new RasterIntrinsicMetadata(intrinsicWidth, intrinsicHeight), metadata);

        Assert.True(RasterImageDecodePlan.TryCreate(source, budget, sizing, out var plan));
        Assert.Equal(640, plan.SourceWidth);
        Assert.Equal(360, plan.SourceHeight);
        Assert.Equal(orientation, plan.Orientation);
        Assert.Equal(intrinsicWidth, plan.IntrinsicWidth);
        Assert.Equal(intrinsicHeight, plan.IntrinsicHeight);
        Assert.Equal(decodeWidth, plan.DecodeWidth);
        Assert.Equal(decodeHeight, plan.DecodeHeight);
        Assert.Equal(plan.OutputWidth, orientation is >= 5 ? plan.DecodeHeight : plan.DecodeWidth);
        Assert.Equal(plan.OutputHeight, orientation is >= 5 ? plan.DecodeWidth : plan.DecodeHeight);
        Assert.Equal((long)plan.OutputWidth * plan.OutputHeight, plan.OutputPixels);
    }

    [Fact]
    public void StaticSupportedFormatsCreateBoundedPlans()
    {
        byte[][] sources =
        [
            CreatePng(640, 360, null),
            CreateBmp(640, 360),
            CreateJpeg(640, 360, null),
            CreateWebP(640, 360, null),
        ];
        var sizing = new RasterImageSizingRequest(availableWidth: 320, rasterizationScale: 1.5,
            requestedWidth: null, requestedHeight: null, maximumPixels: 250_000);

        foreach (byte[] source in sources)
        {
            RasterImageBudgetResult budget = RasterImageResourceBudget.Validate(source);
            Assert.True(budget.Accepted);
            Assert.True(RasterImageDecodePlan.TryCreate(source, budget, sizing, out var plan));
            Assert.Equal(budget.Format, plan.Format);
            Assert.Equal(640, plan.SourceWidth);
            Assert.Equal(360, plan.SourceHeight);
            Assert.Equal(1, plan.Orientation);
            Assert.InRange(plan.OutputPixels, 1, sizing.MaximumPixels);
            Assert.InRange(plan.OutputWidth, 1, RasterImageResourceBudget.MaxDimension);
            Assert.InRange(plan.OutputHeight, 1, RasterImageResourceBudget.MaxDimension);
        }
    }

    [Fact]
    public void RequestedSizeAndPixelCeilingAreAppliedToFinalPhysicalOutput()
    {
        byte[] source = CreateJpeg(640, 360, null);
        RasterImageBudgetResult budget = RasterImageResourceBudget.Validate(source);
        var sizing = new RasterImageSizingRequest(
            availableWidth: 100,
            rasterizationScale: 2,
            requestedWidth: new SafeHtmlLength(80, IsPercent: false),
            requestedHeight: new SafeHtmlLength(50, IsPercent: false),
            maximumPixels: 1_000);

        Assert.True(RasterImageDecodePlan.TryCreate(source, budget, sizing, out var plan));
        Assert.InRange(plan.OutputPixels, 1, 1_000);
        Assert.True(plan.OutputWidth <= 160);
        Assert.True(plan.OutputHeight <= 100);
        Assert.Equal(plan.OutputWidth, plan.DecodeWidth);
        Assert.Equal(plan.OutputHeight, plan.DecodeHeight);

        var onePixelSizing = new RasterImageSizingRequest(100, 1,
            requestedWidth: null, requestedHeight: null, maximumPixels: 1);
        Assert.True(RasterImageDecodePlan.TryCreate(source, budget, onePixelSizing, out var onePixel));
        Assert.Equal(1, onePixel.OutputPixels);
        Assert.Equal(1, onePixel.OutputWidth);
        Assert.Equal(1, onePixel.OutputHeight);
    }

    [Fact]
    public void StaticPreviewBudgetCanProduceOnlyBoundedDownsampledPlan()
    {
        byte[] source = CreatePng(5_000, 5_000, null);
        RasterImageBudgetResult budget = RasterImageResourceBudget.Validate(source);
        Assert.False(budget.Accepted);
        Assert.True(budget.CanRenderStaticPreview);

        var sizing = new RasterImageSizingRequest(availableWidth: 100, rasterizationScale: 1,
            requestedWidth: null, requestedHeight: null, maximumPixels: 10_000);
        Assert.True(RasterImageDecodePlan.TryCreate(source, budget, sizing, out var plan));
        Assert.Equal(25_000_000, plan.SourcePixels);
        Assert.InRange(plan.OutputPixels, 1, 10_000);

        byte[] nearLimit = CreatePng(6_324, 6_324, null);
        RasterImageBudgetResult nearLimitBudget = RasterImageResourceBudget.Validate(nearLimit);
        Assert.True(nearLimitBudget.CanRenderStaticPreview);
        Assert.True(RasterImageDecodePlan.TryCreate(nearLimit, nearLimitBudget, sizing, out var nearLimitPlan));
        Assert.Equal(39_992_976, nearLimitPlan.SourcePixels);

        byte[] aboveLimit = CreatePng(6_325, 6_325, null);
        AssertRejected(aboveLimit, sizing);
    }

    [Fact]
    public void UnsupportedContainersAndAnimatedSourcesAreRejected()
    {
        var sizing = new RasterImageSizingRequest(availableWidth: 100, rasterizationScale: 1,
            requestedWidth: null, requestedHeight: null, maximumPixels: 10_000);

        AssertRejected(CreateGif(640, 360, frameCount: 1), sizing);
        AssertRejected(CreateGif(640, 360, frameCount: 2), sizing);
        AssertRejected(CreateWebP(640, 360, null, animated: true), sizing);
        AssertRejected(CreateAvif(640, 360), sizing);
        AssertRejected(CreateIcon(), sizing);
        AssertRejected(new byte[64], sizing);
    }

    [Fact]
    public void InvalidOrientationMalformedMetadataAndIncompletePngAreRejected()
    {
        var sizing = new RasterImageSizingRequest(availableWidth: 100, rasterizationScale: 1,
            requestedWidth: null, requestedHeight: null, maximumPixels: 10_000);

        AssertRejected(CreateJpeg(640, 360, CreateTiff(9)), sizing);
        AssertRejected(CreateJpeg(640, 360, CreateTiff(6)[..^3]), sizing);
        AssertRejected(CreatePng(640, 360, null, includeEnd: false), sizing);
        AssertRejected(CreatePng(RasterImageResourceBudget.MaxDimension + 1, 1, null), sizing);
    }

    [Fact]
    public void RefinedMetadataRejectsAnUnvalidatedBudgetAndPropagatesCancellation()
    {
        byte[] source = CreatePng(640, 360, null);
        RasterImageBudgetResult rejected = RasterImageBudgetResult.Reject("test");
        Assert.False(RasterIntrinsicMetadata.TryReadValidated(source, rejected, out _, out _));

        RasterImageBudgetResult valid = RasterImageResourceBudget.Validate(source);
        var defaultSizingPlan = RasterImageDecodePlan.TryCreate(source, valid, default, out _);
        Assert.False(defaultSizingPlan);

        var inconsistent = new RasterImageBudgetResult(
            true, false, "PNG", 640, 360, 1, 640L * 360 + 1,
            640L * 360 * 4, null);
        Assert.False(RasterImageDecodePlan.TryCreate(
            source, inconsistent, new RasterImageSizingRequest(100, 1, null, null, 10_000), out _));

        var firstFrameBudget = new RasterImageBudgetResult(
            true, false, "PNG", 640, 360, 1, 640L * 360,
            640L * 360 * 4, null, (0, 0, 640, 360));
        Assert.False(RasterImageDecodePlan.TryCreate(
            source, firstFrameBudget, new RasterImageSizingRequest(100, 1, null, null, 10_000), out _));

        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        Assert.Throws<OperationCanceledException>(() =>
            RasterImageDecodePlan.TryCreate(source, RasterImageResourceBudget.Validate(source),
                new RasterImageSizingRequest(100, 1, null, null, 10_000), out _, canceled.Token));
    }

    private static void AssertRejected(byte[] source, RasterImageSizingRequest sizing)
    {
        RasterImageBudgetResult budget = RasterImageResourceBudget.Validate(source);
        Assert.False(RasterImageDecodePlan.TryCreate(source, budget, sizing, out _));
    }

    private static byte[] CreateTiff(int orientation)
    {
        byte[] tiff = new byte[26];
        "II"u8.CopyTo(tiff);
        BinaryPrimitives.WriteUInt16LittleEndian(tiff.AsSpan(2), 42);
        BinaryPrimitives.WriteUInt32LittleEndian(tiff.AsSpan(4), 8);
        BinaryPrimitives.WriteUInt16LittleEndian(tiff.AsSpan(8), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(tiff.AsSpan(10), 0x0112);
        BinaryPrimitives.WriteUInt16LittleEndian(tiff.AsSpan(12), 3);
        BinaryPrimitives.WriteUInt32LittleEndian(tiff.AsSpan(14), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(tiff.AsSpan(18), checked((ushort)orientation));
        return tiff;
    }

    private static byte[] CreateJpeg(int width, int height, byte[]? tiff)
    {
        using var stream = new MemoryStream();
        stream.Write([0xFF, 0xD8]);
        if (tiff is not null)
        {
            byte[] payload = [.. "Exif\0\0"u8.ToArray(), .. tiff];
            stream.Write([0xFF, 0xE1]);
            WriteUInt16BigEndian(stream, checked((ushort)(payload.Length + 2)));
            stream.Write(payload);
        }

        stream.Write([0xFF, 0xC0, 0x00, 0x11, 0x08]);
        WriteUInt16BigEndian(stream, checked((ushort)height));
        WriteUInt16BigEndian(stream, checked((ushort)width));
        stream.Write([0x03, 0x01, 0x11, 0x00, 0x02, 0x11, 0x00, 0x03, 0x11, 0x00]);
        stream.Write([0xFF, 0xDA, 0x00, 0x08, 0x03, 0x01, 0x00, 0x02, 0x11, 0x03, 0x11, 0x00]);
        return stream.ToArray();
    }

    private static byte[] CreatePng(int width, int height, byte[]? tiff, bool includeEnd = true)
    {
        using var stream = new MemoryStream();
        stream.Write([137, 80, 78, 71, 13, 10, 26, 10]);
        byte[] ihdr = new byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(ihdr, checked((uint)width));
        BinaryPrimitives.WriteUInt32BigEndian(ihdr.AsSpan(4), checked((uint)height));
        ihdr[8] = 8;
        ihdr[9] = 6;
        WritePngChunk(stream, "IHDR"u8, ihdr);
        if (tiff is not null)
            WritePngChunk(stream, "eXIf"u8, tiff);
        WritePngChunk(stream, "IDAT"u8, []);
        if (includeEnd)
            WritePngChunk(stream, "IEND"u8, []);
        return stream.ToArray();
    }

    private static byte[] CreateWebP(int width, int height, byte[]? tiff, bool animated = false)
    {
        using var body = new MemoryStream();
        byte[] extendedHeader = new byte[10];
        if (animated)
            extendedHeader[0] = 0x02;
        WriteUInt24LittleEndian(extendedHeader.AsSpan(4), checked((uint)(width - 1)));
        WriteUInt24LittleEndian(extendedHeader.AsSpan(7), checked((uint)(height - 1)));
        WriteWebPChunk(body, "VP8X"u8, extendedHeader);
        WriteWebPChunk(body, "VP8 "u8, [0, 0, 0, 0x9D, 0x01, 0x2A, 0, 0, 0, 0]);
        if (tiff is not null)
            WriteWebPChunk(body, "EXIF"u8, [.. "Exif\0\0"u8.ToArray(), .. tiff]);
        if (animated)
            WriteWebPChunk(body, "ANMF"u8, new byte[16]);

        byte[] chunks = body.ToArray();
        byte[] bytes = new byte[12 + chunks.Length];
        "RIFF"u8.CopyTo(bytes);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), checked((uint)(bytes.Length - 8)));
        "WEBP"u8.CopyTo(bytes.AsSpan(8));
        chunks.CopyTo(bytes, 12);
        return bytes;
    }

    private static byte[] CreateBmp(int width, int height)
    {
        byte[] bytes = new byte[54];
        bytes[0] = (byte)'B';
        bytes[1] = (byte)'M';
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(2), (uint)bytes.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(10), 54);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(14), 40);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(18), width);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(22), height);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(26), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(28), 24);
        return bytes;
    }

    private static byte[] CreateGif(int width, int height, int frameCount)
    {
        using var stream = new MemoryStream();
        stream.Write("GIF89a"u8);
        Span<byte> screen = stackalloc byte[7];
        Span<byte> frameSize = stackalloc byte[4];
        BinaryPrimitives.WriteUInt16LittleEndian(screen, checked((ushort)width));
        BinaryPrimitives.WriteUInt16LittleEndian(screen[2..], checked((ushort)height));
        stream.Write(screen);
        for (int i = 0; i < frameCount; i++)
        {
            stream.Write([0x2C, 0, 0, 0, 0]);
            BinaryPrimitives.WriteUInt16LittleEndian(frameSize, checked((ushort)width));
            BinaryPrimitives.WriteUInt16LittleEndian(frameSize[2..], checked((ushort)height));
            stream.Write(frameSize);
            stream.Write([0, 2, 1, 0x44, 0]);
        }
        stream.WriteByte(0x3B);
        return stream.ToArray();
    }

    private static byte[] CreateAvif(int width, int height)
    {
        byte[] bytes = new byte[72];
        WriteBmffBox(bytes, 0, 24, "ftyp"u8);
        "avif"u8.CopyTo(bytes.AsSpan(8));
        "mif1"u8.CopyTo(bytes.AsSpan(16));
        "avif"u8.CopyTo(bytes.AsSpan(20));
        WriteBmffBox(bytes, 24, 48, "meta"u8);
        WriteBmffBox(bytes, 36, 36, "iprp"u8);
        WriteBmffBox(bytes, 44, 28, "ipco"u8);
        WriteBmffBox(bytes, 52, 20, "ispe"u8);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(64), checked((uint)width));
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(68), checked((uint)height));
        return bytes;
    }

    private static byte[] CreateIcon()
    {
        byte[] bytes = new byte[22 + 48];
        BinaryPrimitives.WriteUInt16LittleEndian(bytes, 0);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(2), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(4), 1);
        bytes[6] = 1;
        bytes[7] = 1;
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(10), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(12), 32);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(14), 48);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(18), 22);

        Span<byte> dib = bytes.AsSpan(22);
        BinaryPrimitives.WriteUInt32LittleEndian(dib, 40);
        BinaryPrimitives.WriteInt32LittleEndian(dib[4..], 1);
        BinaryPrimitives.WriteInt32LittleEndian(dib[8..], 2);
        BinaryPrimitives.WriteUInt16LittleEndian(dib[12..], 1);
        BinaryPrimitives.WriteUInt16LittleEndian(dib[14..], 32);
        return bytes;
    }

    private static void WritePngChunk(Stream stream, ReadOnlySpan<byte> type, ReadOnlySpan<byte> payload)
    {
        Span<byte> header = stackalloc byte[8];
        BinaryPrimitives.WriteUInt32BigEndian(header, checked((uint)payload.Length));
        type.CopyTo(header[4..]);
        stream.Write(header);
        stream.Write(payload);
        stream.Write(new byte[4]);
    }

    private static void WriteWebPChunk(Stream stream, ReadOnlySpan<byte> type, ReadOnlySpan<byte> payload)
    {
        Span<byte> header = stackalloc byte[8];
        type.CopyTo(header);
        BinaryPrimitives.WriteUInt32LittleEndian(header[4..], checked((uint)payload.Length));
        stream.Write(header);
        stream.Write(payload);
        if ((payload.Length & 1) != 0)
            stream.WriteByte(0);
    }

    private static void WriteBmffBox(byte[] bytes, int offset, int length, ReadOnlySpan<byte> type)
    {
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(offset), checked((uint)length));
        type.CopyTo(bytes.AsSpan(offset + 4));
    }

    private static void WriteUInt16BigEndian(Stream stream, ushort value)
    {
        Span<byte> bytes = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(bytes, value);
        stream.Write(bytes);
    }

    private static void WriteUInt24LittleEndian(Span<byte> bytes, uint value)
    {
        bytes[0] = (byte)value;
        bytes[1] = (byte)(value >> 8);
        bytes[2] = (byte)(value >> 16);
    }
}
