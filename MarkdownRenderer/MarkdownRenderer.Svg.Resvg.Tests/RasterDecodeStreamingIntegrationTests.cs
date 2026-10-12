using MarkdownRenderer.Images;
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
}
