using System.Buffers.Binary;
using System.Text;
using MarkdownRenderer.Images;
using MarkdownRenderer.Svg.Resvg.Internal;
using Xunit;

namespace MarkdownRenderer.Svg.Resvg.Tests;

public sealed class RasterDecodeStreamingIntegrationTests
{
    [Fact]
    public async Task OpaqueLossyPreviewMatchesFullFrameUnderForcedRowBudget()
    {
        using Stream fixture = typeof(RasterDecodeStreamingIntegrationTests).Assembly
            .GetManifestResourceStream("OpaqueVp8StreamingFixture")
            ?? throw new InvalidOperationException("The generated opaque VP8 fixture is missing.");
        using var buffer = new MemoryStream();
        await fixture.CopyToAsync(buffer);
        byte[] source = buffer.ToArray();
        Assert.Equal("B8502B8946730BC3A6B5B952437D25F882EE612F0534BB6A66DAA8B2FB460530",
            Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(source)));
        Assert.Equal("VP8 ", System.Text.Encoding.ASCII.GetString(source, 12, 4));

        await using var renderer = new ResvgMarkdownSvgRenderer();
        var request = new MarkdownRasterDecodeRequest(source, MarkdownRasterFormat.WebP,
            64, 64, 8 * 1024 * 1024);
        using MarkdownRaster full = await renderer.DecodeAsync(request);
        // The 256x256 source RGBA alone is 256 KiB. This unchanged lower
        // request limit can succeed only through the bounded VP8 row path.
        using MarkdownRaster rows = await renderer.DecodeAsync(request with
        {
            MaximumWorkingBytes = 200 * 1024,
        });
        Assert.Equal(64, rows.WidthPixels);
        Assert.Equal(64, rows.HeightPixels);
        Assert.Equal(full.Pixels.ToArray(), rows.Pixels.ToArray());
        for (int offset = 3; offset < rows.LengthBytes; offset += 4)
            Assert.Equal((byte)255, rows.Pixels.Span[offset]);
    }

    [Fact]
    public async Task LossyVp8AlphaStreamsRowsAndRejectsOnlyBelowWorkingBudget()
    {
        using Stream fixture = typeof(RasterDecodeStreamingIntegrationTests).Assembly
            .GetManifestResourceStream("OpaqueVp8StreamingFixture")
            ?? throw new InvalidOperationException("The generated opaque VP8 fixture is missing.");
        using var buffer = new MemoryStream();
        await fixture.CopyToAsync(buffer);
        byte[] source = WrapOpaqueVp8WithConstantAlpha(buffer.ToArray());
        Assert.Equal("8B1E9A7913986E7EAC97DAE408288B69FF4A8E6D9F84D7F2F4CCB6D065C43164",
            Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(source)));

        // Validate the pinned lossy VP8 + VP8X/ALPH bytes before using the worker result.
        // WIC independently decoded these exact authored bytes during one-off fixture
        // qualification; provider tests must not depend on the optional OS WebP codec.
        AssertLossyAlphaHeader(source);

        await using var renderer = new ResvgMarkdownSvgRenderer();
        var request = new MarkdownRasterDecodeRequest(source, MarkdownRasterFormat.WebP,
            64, 64, RasterDecodePolicy.MaximumWorkingBytes);
        using MarkdownRaster admitted = await renderer.DecodeAsync(request);
        Assert.Equal(64, admitted.WidthPixels);
        Assert.Equal(64, admitted.HeightPixels);
        Assert.Equal(64 * 64 * 4, admitted.LengthBytes);
        bool hasColor = false;
        for (int offset = 0; offset < admitted.LengthBytes; offset += 4)
        {
            Assert.Equal((byte)127, admitted.Pixels.Span[offset + 3]);
            for (int channel = 0; channel < 3; channel++)
            {
                Assert.InRange(admitted.Pixels.Span[offset + channel], (byte)0, (byte)127);
                hasColor |= admitted.Pixels.Span[offset + channel] != 0;
            }
        }
        Assert.True(hasColor, "The static lossy VP8 color frame must remain visible under its alpha plane.");

        // Lossy WebP with alpha must not need a full RGBA frame (about 593 KiB
        // of working memory here), or a large image exceeds the worker budget.
        // Rows stream with a one-byte reconstructed alpha plane: 400 KiB
        // suffices, with identical pixels.
        using MarkdownRaster streamed = await renderer.DecodeAsync(
            request with { MaximumWorkingBytes = 400 * 1024 });
        Assert.Equal(admitted.Pixels.ToArray(), streamed.Pixels.ToArray());

        // Below the alpha plane, VP8 bands and mapped 64x64 output the request
        // is intentionally insufficient, so a typed resource fallback is correct.
        MarkdownRasterException failure = await Assert.ThrowsAsync<MarkdownRasterException>(() =>
            renderer.DecodeAsync(request with { MaximumWorkingBytes = 200 * 1024 }).AsTask());
        Assert.Equal(MarkdownRasterFailureReason.ResourceLimitExceeded, failure.Reason);

        // A source-specific resource refusal must leave unrelated valid content decodable.
        using MarkdownRaster unrelated = await renderer.DecodeAsync(new(
            RasterDecodeIntegrationTests.SmallWebP(), MarkdownRasterFormat.WebP,
            2, 2, RasterDecodePolicy.MaximumWorkingBytes));
        Assert.Equal(2, unrelated.WidthPixels);
        Assert.Equal(2, unrelated.HeightPixels);
        Assert.Equal(16, unrelated.LengthBytes);
        for (int offset = 3; offset < unrelated.LengthBytes; offset += 4)
            Assert.Equal((byte)255, unrelated.Pixels.Span[offset]);
    }

    [Fact]
    public async Task GeneratedLiteralFixtureHasTheExactAuthoredPixels()
    {
        const int width = 13, height = 7;
        await using var renderer = new ResvgMarkdownSvgRenderer();
        using MarkdownRaster raster = await renderer.DecodeAsync(new(
            SmokeRasterDecodeFixture.CreateLiteralWebP(width, height, patterned: true),
            MarkdownRasterFormat.WebP, width, height, 8 * 1024 * 1024));
        byte[] expected = new byte[width * height * 4];
        for (int y = 0; y < height; y++)
        for (int x = 0; x < width; x++)
        {
            int offset = (y * width + x) * 4;
            expected[offset] = Premultiply(91);
            expected[offset + 1] = Premultiply(59);
            expected[offset + 2] = Premultiply(
                SmokeRasterDecodeFixture.LiteralPatternBit(x, y) == 0 ? 17 : 201);
            expected[offset + 3] = 127;
        }
        Assert.Equal(expected, raster.Pixels.ToArray());
    }

    [Theory]
    [InlineData(2048, 8193, 64)]
    [InlineData(10000, 4000, 32)]
    public async Task LargeSourcePreviewFitsTheUnchangedWorkingLimit(int sourceWidth, int sourceHeight, int budgetMiB)
    {
        // The source RGBA alone exceeds 64 MiB. Output remains display-sized;
        // success cannot depend on raising the request limit or an OS codec.
        const int width = 660, height = 220;
        await using var renderer = new ResvgMarkdownSvgRenderer();
        using MarkdownRaster raster = await renderer.DecodeAsync(new(
            SmokeRasterDecodeFixture.CreateLiteralWebP(sourceWidth, sourceHeight),
            MarkdownRasterFormat.WebP, width, height, budgetMiB * 1024L * 1024));
        Assert.Equal(width, raster.WidthPixels);
        Assert.Equal(height, raster.HeightPixels);
        Assert.Equal(width * height * 4, raster.LengthBytes);
        byte[] expectedPixel = [Premultiply(91), Premultiply(59), Premultiply(17), 127];
        for (int offset = 0; offset < raster.LengthBytes; offset += 4)
        {
            if (!raster.Pixels.Span.Slice(offset, 4).SequenceEqual(expectedPixel))
                Assert.Fail($"Large-image preview lost or changed pixel {offset / 4}.");
        }
    }

    [Theory]
    [InlineData(1, false)]
    [InlineData(2, true)]
    [InlineData(3, false)]
    [InlineData(4, true)]
    [InlineData(5, false)]
    [InlineData(6, true)]
    [InlineData(7, false)]
    [InlineData(8, true)]
    public async Task ForcedRowPreviewMatchesFullFramePixelsAcrossOrientationAndColorManagement(
        int orientation, bool withProfile)
    {
        const int sourceWidth = 1024, sourceHeight = 1025, width = 137, height = 101;
        byte[] source = SmokeRasterDecodeFixture.WithMetadata(
            SmokeRasterDecodeFixture.CreateLiteralWebP(sourceWidth, sourceHeight, patterned: true),
            sourceWidth, sourceHeight,
            withProfile ? SmokeRasterDecodeFixture.CreateLinearRgbProfile(version4: true) : null,
            orientation, hasAlpha: true);
        await using var renderer = new ResvgMarkdownSvgRenderer();
        var request = new MarkdownRasterDecodeRequest(source, MarkdownRasterFormat.WebP,
            width, height, 32 * 1024 * 1024);
        using MarkdownRaster full = await renderer.DecodeAsync(request);
        // Without ICC, source scratch fits but the full decoder's additional
        // frame does not; this also exercises resource-only retry. With ICC,
        // full scratch fails admission and row dispatch must occur immediately.
        using MarkdownRaster rows = await renderer.DecodeAsync(request with
        {
            MaximumWorkingBytes = (withProfile ? 8 : 6) * 1024 * 1024,
        });
        byte[] rowPixels = rows.Pixels.ToArray();
        Assert.Equal(full.Pixels.ToArray(), rowPixels);
        Assert.Contains(rowPixels, value => value is > 0 and < 127);
    }

    private static byte Premultiply(int channel) => (byte)((channel * 127 + 127) / 255);

    private static byte[] WrapOpaqueVp8WithConstantAlpha(byte[] opaque)
    {
        Assert.Equal("RIFF", Encoding.ASCII.GetString(opaque, 0, 4));
        Assert.Equal("WEBP", Encoding.ASCII.GetString(opaque, 8, 4));
        Assert.Equal("VP8 ", Encoding.ASCII.GetString(opaque, 12, 4));
        uint vp8Length = BinaryPrimitives.ReadUInt32LittleEndian(opaque.AsSpan(16, 4));
        Assert.Equal(opaque.Length, 20 + (int)vp8Length + ((int)vp8Length & 1));

        ReadOnlySpan<byte> vp8Payload = opaque.AsSpan(20, checked((int)vp8Length));
        byte[] originalVp8Payload = vp8Payload.ToArray();
        Assert.Equal(new byte[] { 0x9d, 0x01, 0x2a }, vp8Payload.Slice(3, 3).ToArray());
        int width = BinaryPrimitives.ReadUInt16LittleEndian(vp8Payload.Slice(6, 2)) & 0x3fff;
        int height = BinaryPrimitives.ReadUInt16LittleEndian(vp8Payload.Slice(8, 2)) & 0x3fff;
        Assert.Equal(256, width);
        Assert.Equal(256, height);

        byte[] extendedHeader = new byte[10];
        extendedHeader[0] = 0x10; // VP8X ALPHA flag; all reserved and other feature bits clear.
        Write24(extendedHeader.AsSpan(4), checked((uint)(width - 1)));
        Write24(extendedHeader.AsSpan(7), checked((uint)(height - 1)));

        // ALPH method 0 is uncompressed. It stores one alpha byte per canvas
        // pixel, following the one-byte ALPH control header.
        byte[] alphaPlane = new byte[checked(1 + width * height)];
        alphaPlane[0] = 0;
        alphaPlane.AsSpan(1).Fill(127);

        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true))
        {
            writer.Write("RIFF"u8);
            writer.Write(0u);
            writer.Write("WEBP"u8);
            WriteWebPChunk(writer, "VP8X"u8, extendedHeader);
            WriteWebPChunk(writer, "ALPH"u8, alphaPlane);
            // Copy the original VP8 chunk (header, payload, and padding) unchanged.
            writer.Write(opaque.AsSpan(12));
        }

        byte[] result = stream.ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(4), checked((uint)result.Length - 8));
        int vp8Offset = 30 + 8 + alphaPlane.Length + (alphaPlane.Length & 1);
        uint wrappedVp8Length = BinaryPrimitives.ReadUInt32LittleEndian(result.AsSpan(vp8Offset + 4, 4));
        Assert.Equal(vp8Length, wrappedVp8Length);
        Assert.Equal(originalVp8Payload,
            result.AsSpan(vp8Offset + 8, checked((int)wrappedVp8Length)).ToArray());
        return result;
    }

    private static void AssertLossyAlphaHeader(byte[] source)
    {
        Assert.Equal("RIFF", Encoding.ASCII.GetString(source, 0, 4));
        Assert.Equal(checked((uint)source.Length - 8),
            BinaryPrimitives.ReadUInt32LittleEndian(source.AsSpan(4, 4)));
        Assert.Equal("WEBP", Encoding.ASCII.GetString(source, 8, 4));
        Assert.Equal("VP8X", Encoding.ASCII.GetString(source, 12, 4));
        Assert.Equal(0x10, source[20] & 0x10);
        Assert.Equal("ALPH", Encoding.ASCII.GetString(source, 30, 4));
        uint alphaChunkLength = BinaryPrimitives.ReadUInt32LittleEndian(source.AsSpan(34, 4));
        Assert.Equal(1u + 256u * 256u, alphaChunkLength);
        Assert.Equal((byte)0, source[38]); // No compression, filtering, or preprocessing.
        Assert.All(source.AsSpan(39, 256 * 256).ToArray(), alpha => Assert.Equal((byte)127, alpha));

        int vp8ChunkOffset = 30 + 8 + checked((int)alphaChunkLength) + ((int)alphaChunkLength & 1);
        Assert.Equal("VP8 ", Encoding.ASCII.GetString(source, vp8ChunkOffset, 4));
        int vp8ChunkLength = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(
            source.AsSpan(vp8ChunkOffset + 4, 4)));
        Assert.Equal(source.Length,
            vp8ChunkOffset + 8 + vp8ChunkLength + (vp8ChunkLength & 1));
        ReadOnlySpan<byte> vp8Payload = source.AsSpan(vp8ChunkOffset + 8, vp8ChunkLength);
        Assert.Equal(new byte[] { 0x9d, 0x01, 0x2a }, vp8Payload.Slice(3, 3).ToArray());
        Assert.Equal(256, BinaryPrimitives.ReadUInt16LittleEndian(vp8Payload.Slice(6, 2)) & 0x3fff);
        Assert.Equal(256, BinaryPrimitives.ReadUInt16LittleEndian(vp8Payload.Slice(8, 2)) & 0x3fff);
    }

    private static void WriteWebPChunk(BinaryWriter writer, ReadOnlySpan<byte> fourCc,
        ReadOnlySpan<byte> payload)
    {
        writer.Write(fourCc);
        writer.Write(payload.Length);
        writer.Write(payload);
        if ((payload.Length & 1) != 0)
            writer.Write((byte)0);
    }

    private static void Write24(Span<byte> target, uint value)
    {
        target[0] = (byte)value;
        target[1] = (byte)(value >> 8);
        target[2] = (byte)(value >> 16);
    }
}
