using System.Buffers.Binary;
using MarkdownRenderer.Images;
using MarkdownRenderer.Svg.Resvg.Internal;
using MarkdownRenderer.Svg.Resvg.TestSupport;
using Xunit;

namespace MarkdownRenderer.Svg.Resvg.Tests;

public sealed class RasterDecodeIntegrationTests
{
    internal static byte[] SmallWebP() => SmokeRasterDecodeFixture.Create();

    [Theory]
    [InlineData(1, new[] { 0, 1, 2, 3, 4, 5 })]
    [InlineData(2, new[] { 1, 0, 3, 2, 5, 4 })]
    [InlineData(3, new[] { 5, 4, 3, 2, 1, 0 })]
    [InlineData(4, new[] { 4, 5, 2, 3, 0, 1 })]
    [InlineData(5, new[] { 0, 2, 4, 1, 3, 5 })]
    [InlineData(6, new[] { 4, 2, 0, 5, 3, 1 })]
    [InlineData(7, new[] { 5, 3, 1, 4, 2, 0 })]
    [InlineData(8, new[] { 1, 3, 5, 0, 2, 4 })]
    public async Task ExifOrientationAndPremultiplicationAreExactForNonSquareLosslessPixels(
        int orientation, int[] sourcePixelOrder)
    {
        byte[] source = SmokeRasterDecodeFixture.WithMetadata(
            SmokeRasterDecodeFixture.CreateRgba2By3(), 2, 3, orientation: orientation, hasAlpha: true);
        byte[] straightRgba = SmokeRasterDecodeFixture.Rgba2By3Pixels();
        int width = orientation >= 5 ? 3 : 2;
        int height = orientation >= 5 ? 2 : 3;
        await using var renderer = new ResvgMarkdownSvgRenderer();
        using MarkdownRaster raster = await renderer.DecodeAsync(new(source,
            MarkdownRasterFormat.WebP, width, height, 8 * 1024 * 1024));
        Assert.Equal(width, raster.WidthPixels);
        Assert.Equal(height, raster.HeightPixels);
        Assert.Equal(width * 4, raster.StrideBytes);
        byte[] expectedBgra = new byte[24];
        for (int pixel = 0; pixel < 6; pixel++)
        {
            int sourceOffset = sourcePixelOrder[pixel] * 4;
            int alpha = straightRgba[sourceOffset + 3];
            expectedBgra[pixel * 4] = (byte)((straightRgba[sourceOffset + 2] * alpha + 127) / 255);
            expectedBgra[pixel * 4 + 1] = (byte)((straightRgba[sourceOffset + 1] * alpha + 127) / 255);
            expectedBgra[pixel * 4 + 2] = (byte)((straightRgba[sourceOffset] * alpha + 127) / 255);
            expectedBgra[pixel * 4 + 3] = (byte)alpha;
        }
        Assert.Equal(expectedBgra, raster.Pixels.ToArray());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EmbeddedProfilePreservesAlphaAndProducesPremultipliedPixels(bool version4)
    {
        byte[] source = SmokeRasterDecodeFixture.WithMetadata(
            SmokeRasterDecodeFixture.CreateRgba2By3(), 2, 3,
            SmokeRasterDecodeFixture.CreateLinearRgbProfile(version4), hasAlpha: true);
        byte[] straightRgba = SmokeRasterDecodeFixture.Rgba2By3Pixels();
        await using var renderer = new ResvgMarkdownSvgRenderer();
        using MarkdownRaster raster = await renderer.DecodeAsync(new(source,
            MarkdownRasterFormat.WebP, 2, 3, 8 * 1024 * 1024));
        for (int offset = 0; offset < raster.LengthBytes; offset += 4)
        {
            byte alpha = straightRgba[offset + 3];
            Assert.Equal(alpha, raster.Pixels.Span[offset + 3]);
            for (int channel = 0; channel < 3; channel++)
                Assert.InRange(raster.Pixels.Span[offset + channel], (byte)0, alpha);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EmbeddedColorProfileIsAppliedInsideTheRestrictedWorker(bool version4)
    {
        // The tiny lossy smoke fixture is almost saturated red, so applying
        // a transfer curve need not change any channel by more than 20. Use
        // generated midtones to make an ignored profile observably wrong.
        byte[] source = SmokeRasterDecodeFixture.CreateRgba2By3();
        byte[] tagged = SmokeRasterDecodeFixture.WithMetadata(source, 2, 3,
            SmokeRasterDecodeFixture.CreateLinearRgbProfile(version4), hasAlpha: true);
        await using var renderer = new ResvgMarkdownSvgRenderer();
        using MarkdownRaster untagged = await renderer.DecodeAsync(new(source,
            MarkdownRasterFormat.WebP, 2, 3, 8 * 1024 * 1024));
        using MarkdownRaster converted = await renderer.DecodeAsync(new(tagged,
            MarkdownRasterFormat.WebP, 2, 3, 8 * 1024 * 1024));
        Assert.Equal(2, converted.WidthPixels);
        Assert.Equal(3, converted.HeightPixels);
        Assert.Equal(24, converted.LengthBytes);
        int maximumDifference = 0;
        for (int offset = 0; offset < converted.LengthBytes; offset += 4)
        {
            Assert.Equal(untagged.Pixels.Span[offset + 3], converted.Pixels.Span[offset + 3]);
            for (int channel = 0; channel < 3; channel++)
                maximumDifference = Math.Max(maximumDifference,
                    converted.Pixels.Span[offset + channel] - untagged.Pixels.Span[offset + channel]);
        }
        Assert.True(maximumDifference > 20, "The linear-RGB profile must visibly brighten a midtone.");
    }

    [Fact]
    public async Task InvalidEmbeddedProfileFailsWithoutPoisoningUnrelatedImages()
    {
        byte[] tagged = SmokeRasterDecodeFixture.WithMetadata(SmallWebP(), 2, 2, new byte[128]);
        await using var renderer = new ResvgMarkdownSvgRenderer();
        MarkdownRasterException failure = await Assert.ThrowsAsync<MarkdownRasterException>(() =>
            renderer.DecodeAsync(new(tagged, MarkdownRasterFormat.WebP, 2, 2,
                8 * 1024 * 1024)).AsTask());
        Assert.Equal(MarkdownRasterFailureReason.UnsupportedContent, failure.Reason);
        using MarkdownRaster valid = await renderer.DecodeAsync(new(SmallWebP(),
            MarkdownRasterFormat.WebP, 2, 2, 8 * 1024 * 1024));
        Assert.True(SmokeRasterDecodeFixture.IsValid(valid));
    }

    [Theory]
    [InlineData(false, 0)]
    [InlineData(true, 0)]
    [InlineData(false, 1)]
    [InlineData(true, 1)]
    [InlineData(true, 2)]
    public async Task MatrixAndLutProfilesMatchAnalyticLinearToSrgbConversion(bool version4, int profileKind)
    {
        byte[] profile = profileKind switch
        {
            1 => SmokeRasterDecodeFixture.CreateLinearRgbLutProfile(version4),
            2 => SmokeRasterDecodeFixture.CreateLinearRgbMultiStageProfile(),
            _ => SmokeRasterDecodeFixture.CreateLinearRgbProfile(version4),
        };
        byte[] source = SmokeRasterDecodeFixture.WithMetadata(
            SmokeRasterDecodeFixture.CreateRgba2By3(), 2, 3, profile, hasAlpha: true);
        byte[] straightRgba = SmokeRasterDecodeFixture.Rgba2By3Pixels();
        await using var renderer = new ResvgMarkdownSvgRenderer();
        using MarkdownRaster raster = await renderer.DecodeAsync(new(source,
            MarkdownRasterFormat.WebP, 2, 3, 8 * 1024 * 1024));
        for (int offset = 0; offset < raster.LengthBytes; offset += 4)
        {
            byte alpha = straightRgba[offset + 3];
            Assert.Equal(alpha, raster.Pixels.Span[offset + 3]);
            for (int channel = 0; channel < 3; channel++)
            {
                double linear = straightRgba[offset + (2 - channel)] / 255.0;
                double srgb = linear <= 0.0031308
                    ? linear * 12.92 : 1.055 * Math.Pow(linear, 1 / 2.4) - 0.055;
                int expected = (int)Math.Round(srgb * alpha);
                int actual = raster.Pixels.Span[offset + channel];
                Assert.InRange(Math.Abs(actual - expected), 0, 3);
            }
        }
    }

    [Fact]
    public async Task ColorManagementCannotExceedTheHostLoweredWorkingBudget()
    {
        byte[] source = SmokeRasterDecodeFixture.WithMetadata(SmallWebP(), 2, 2,
            SmokeRasterDecodeFixture.CreateLinearRgbProfile(version4: true));
        await using var renderer = new ResvgMarkdownSvgRenderer();
        // The shared section itself fits, but no decoder/CMS allocation does.
        // Rejection must be a resource result, not a crash or partial raster.
        long mappingOnlyBudget = RasterDecodePolicy.MappingBytes(source.Length, 16);
        MarkdownRasterException failure = await Assert.ThrowsAsync<MarkdownRasterException>(() =>
            renderer.DecodeAsync(new(source, MarkdownRasterFormat.WebP, 2, 2,
                mappingOnlyBudget)).AsTask());
        Assert.Equal(MarkdownRasterFailureReason.ResourceLimitExceeded, failure.Reason);

        // A lowered budget must not quarantine otherwise valid source content.
        using MarkdownRaster valid = await renderer.DecodeAsync(new(source,
            MarkdownRasterFormat.WebP, 2, 2, 8 * 1024 * 1024));
        Assert.True(SmokeRasterDecodeFixture.IsValid(valid));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MalformedIccTagBoundsCannotCrashTheWorker(bool oversizedCurve)
    {
        byte[] profile = SmokeRasterDecodeFixture.CreateLinearRgbProfile();
        const int redCurveTag = 132 + 6 * 12;
        int curveOffset = checked((int)BinaryPrimitives.ReadUInt32BigEndian(profile.AsSpan(redCurveTag + 4)));
        if (oversizedCurve)
            BinaryPrimitives.WriteUInt32BigEndian(profile.AsSpan(curveOffset + 8), uint.MaxValue);
        else
            BinaryPrimitives.WriteUInt32BigEndian(profile.AsSpan(redCurveTag + 4), 0x7FFFFFF0);
        byte[] source = SmokeRasterDecodeFixture.WithMetadata(SmallWebP(), 2, 2, profile);
        await using var renderer = new ResvgMarkdownSvgRenderer();
        MarkdownRasterException failure = await Assert.ThrowsAsync<MarkdownRasterException>(() =>
            renderer.DecodeAsync(new(source, MarkdownRasterFormat.WebP, 2, 2,
                8 * 1024 * 1024)).AsTask());
        Assert.Contains(failure.Reason, new[]
        {
            MarkdownRasterFailureReason.UnsupportedContent,
            MarkdownRasterFailureReason.ResourceLimitExceeded,
        });
        using MarkdownRaster valid = await renderer.DecodeAsync(new(SmallWebP(),
            MarkdownRasterFormat.WebP, 2, 2, 8 * 1024 * 1024));
        Assert.True(SmokeRasterDecodeFixture.IsValid(valid));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task StaticWebPDecodesThroughIsolatedWorkerToExactPremultipliedByteOrders(int size)
    {
        await using var renderer = new ResvgMarkdownSvgRenderer();
        var request = new MarkdownRasterDecodeRequest(SmallWebP(), MarkdownRasterFormat.WebP,
            size, size, 8 * 1024 * 1024);
        using MarkdownRaster bgra = await renderer.DecodeAsync(request);
        using MarkdownRaster rgba = await renderer.DecodeAsync(request with
        {
            PixelFormat = MarkdownRasterPixelFormat.Rgba8Premultiplied,
        });
        Assert.Equal(size, bgra.WidthPixels);
        Assert.Equal(size, bgra.HeightPixels);
        Assert.Equal(size * 4, bgra.StrideBytes);
        Assert.Equal(size * size * 4, bgra.LengthBytes);
        Assert.Equal(bgra.LengthBytes, rgba.LengthBytes);
        Assert.Equal(MarkdownRasterPixelFormat.Bgra8Premultiplied, bgra.PixelFormat);
        Assert.Equal(MarkdownRasterPixelFormat.Rgba8Premultiplied, rgba.PixelFormat);
        for (int offset = 0; offset < bgra.LengthBytes; offset += 4)
        {
            Assert.Equal(rgba.Pixels.Span[offset], bgra.Pixels.Span[offset + 2]);
            Assert.Equal(rgba.Pixels.Span[offset + 1], bgra.Pixels.Span[offset + 1]);
            Assert.Equal(rgba.Pixels.Span[offset + 2], bgra.Pixels.Span[offset]);
            Assert.Equal((byte)255, bgra.Pixels.Span[offset + 3]);
            Assert.Equal((byte)255, rgba.Pixels.Span[offset + 3]);
        }
        Assert.Contains(bgra.Pixels.ToArray(), value => value is > 0 and < 255);
    }

    [Fact]
    public async Task ReturnedRasterOutlivesProviderAndKeepsIndependentOutputSection()
    {
        var renderer = new ResvgMarkdownSvgRenderer();
        MarkdownRaster? raster = null;
        try
        {
            raster = await renderer.DecodeAsync(new(SmallWebP(), MarkdownRasterFormat.WebP,
                2, 2, 8 * 1024 * 1024));
            byte[] before = raster.Pixels.ToArray();
            await renderer.DisposeAsync();
            Assert.Equal(before, raster.Pixels.ToArray());
            raster.Dispose();
            Assert.Throws<ObjectDisposedException>(() => raster.Pixels);
        }
        finally
        {
            raster?.Dispose();
            await renderer.DisposeAsync();
        }
    }

    [Fact]
    public async Task LoweredOutputLimitRejectsBeforeWorkerStartup()
    {
        await using var renderer = new ResvgMarkdownSvgRenderer(new ResvgMarkdownSvgRendererOptions(
            maxOutputRasterBytes: 8,
            workerExecutablePath: Path.Combine(Path.GetTempPath(), "nonexistent-raster-worker.exe")));
        MarkdownRasterException failure = await Assert.ThrowsAsync<MarkdownRasterException>(() =>
            renderer.DecodeAsync(new(SmallWebP(), MarkdownRasterFormat.WebP, 2, 2,
                8 * 1024 * 1024)).AsTask());
        Assert.Equal(MarkdownRasterFailureReason.ResourceLimitExceeded, failure.Reason);
    }

    [Fact]
    public async Task SourceIsBorrowedOnlyUntilDecodeFinishes()
    {
        await using var renderer = new ResvgMarkdownSvgRenderer();
        byte[] source = SmallWebP();
        using MarkdownRaster first = await renderer.DecodeAsync(new(source, MarkdownRasterFormat.WebP,
            2, 2, 8 * 1024 * 1024));
        byte[] expected = first.Pixels.ToArray();
        Array.Fill(source, (byte)0);
        using MarkdownRaster second = await renderer.DecodeAsync(new(SmallWebP(), MarkdownRasterFormat.WebP,
            2, 2, 8 * 1024 * 1024));
        Assert.Equal(expected, first.Pixels.ToArray());
        Assert.Equal(expected, second.Pixels.ToArray());
    }
}
