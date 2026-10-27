using System.Buffers.Binary;
using System.Runtime.InteropServices.WindowsRuntime;
using MarkdownRenderer.Layout.Boxes;
using Windows.Foundation;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;
using Xunit;
using Xunit.Abstractions;

namespace MarkdownRenderer.GitHub.Tests;

/// <summary>
/// Full-byte, same-codec parity between the shipping native WIC decoder and the
/// WinRT preview decode for project-authored fixtures. A passing fixture does not
/// qualify arbitrary profiles, codec-private peaks or Edge parity.
/// </summary>
[Collection(NativeWicDecodeTestCollection.CollectionName)]
public sealed class RasterNativeWicWinRtParityTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData("PNG", 37, 23)]
    [InlineData("PNG", 17, 11)]
    [InlineData("PNG", 1, 1)]
    [InlineData("PNG", 7, 19)]
    [InlineData("BMP", 37, 23)]
    [InlineData("BMP", 17, 11)]
    [InlineData("BMP", 1, 1)]
    [InlineData("JPEG", 37, 23)]
    [InlineData("JPEG", 17, 11)]
    [InlineData("JPEG", 1, 1)]
    public async Task PatternedStaticRasterMatchesEveryWinRtPreviewByte(
        string format, int width, int height)
    {
        byte[] source = format switch
        {
            "PNG" => await OwnedNativeWicGeneralFixtureFactory.CreatePatternedPngAsync(),
            "BMP" => await OwnedNativeWicGeneralFixtureFactory.CreatePatternedBmpAsync(),
            "JPEG" => await OwnedNativeWicGeneralFixtureFactory.CreatePatternedJpegAsync(),
            _ => throw new InvalidOperationException("An unrecognized fixture must not become an oracle."),
        };
        RasterImageBudgetResult budget = RasterImageResourceBudget.Validate(source);
        Assert.True(budget.Accepted, budget.Reason);
        Assert.Equal(format, budget.Format);
        Assert.Equal(37, budget.Width);
        Assert.Equal(23, budget.Height);
        await AssertShippingParityAsync(source, budget, width, height, orientation: 1);
    }

    [Theory]
    [InlineData((ushort)1)]
    [InlineData((ushort)2)]
    [InlineData((ushort)3)]
    [InlineData((ushort)4)]
    [InlineData((ushort)5)]
    [InlineData((ushort)6)]
    [InlineData((ushort)7)]
    [InlineData((ushort)8)]
    public async Task AsymmetricJpegAllExifOrientationsMatchEveryWinRtPreviewByte(ushort orientation)
    {
        byte[] source = await OwnedNativeWicGeneralFixtureFactory.CreatePatternedJpegAsync(orientation);
        await AssertEncodedOrientationAsync(source, orientation);
        RasterImageBudgetResult budget = RasterImageResourceBudget.Validate(source);
        Assert.True(budget.Accepted, budget.Reason);
        Assert.Equal("JPEG", budget.Format);
        bool swapAxes = orientation is 5 or 6 or 7 or 8;
        await AssertShippingParityAsync(
            source, budget, swapAxes ? 23 : 37, swapAxes ? 37 : 23, orientation);
        await AssertShippingParityAsync(
            source, budget, swapAxes ? 11 : 17, swapAxes ? 17 : 11, orientation);
    }

    [Trait("Requires", "WicWebpDecoder")]
    [Fact]
    public async Task OversizedPinnedWebpStillMatchesEveryWinRtPreviewByte()
    {
        byte[] source = await ReadPinnedWebpFixtureAsync();
        RasterImageBudgetResult budget = RasterImageResourceBudget.Validate(source);
        Assert.False(budget.Accepted);
        Assert.True(budget.CanRenderStaticPreview);
        Assert.Equal(1, budget.FrameCount);
        await AssertShippingParityAsync(source, budget, 256, 1025, orientation: 1);
    }

    [Theory]
    [InlineData("palette4-alpha")]
    [InlineData("gray8")]
    [InlineData("gray-alpha8")]
    [InlineData("gray16")]
    [InlineData("rgba16")]
    public async Task PaletteGrayscaleAndSixteenBitPngMatchEveryWinRtPreviewByte(string fixture)
    {
        byte[] source = fixture switch
        {
            "palette4-alpha" => OwnedNativeWicFormatFixtureFactory.CreatePalette4WithTransparencyPng(),
            "gray8" => OwnedNativeWicFormatFixtureFactory.CreateGrayscale8Png(),
            "gray-alpha8" => OwnedNativeWicFormatFixtureFactory.CreateGrayscaleAlpha8Png(),
            "gray16" => OwnedNativeWicFormatFixtureFactory.CreateGrayscale16Png(),
            "rgba16" => OwnedNativeWicFormatFixtureFactory.CreateRgba16Png(),
            _ => throw new InvalidOperationException("An unknown fixture must not become an oracle."),
        };
        RasterImageBudgetResult budget = RasterImageResourceBudget.Validate(source);
        Assert.True(budget.Accepted, budget.Reason);
        Assert.Equal((37, 23, 1), (budget.Width, budget.Height, budget.FrameCount));
        // The renderer deliberately never upscales above intrinsic pixels.
        await AssertShippingParityAsync(source, budget, 17, 11, orientation: 1);
    }

    [Theory]
    [InlineData("BMP", 168, 24, 84, 12)]
    [InlineData("BMP", 24, 168, 12, 84)]
    [InlineData("JPEG", 168, 24, 84, 12)]
    [InlineData("JPEG", 24, 168, 12, 84)]
    public async Task OpaqueWideAndPortraitBmpJpegMatchEveryWinRtPreviewByte(
        string format, int sourceWidth, int sourceHeight, int outputWidth, int outputHeight)
    {
        byte[] source = format switch
        {
            "BMP" => await OwnedNativeWicGeneralFixtureFactory.CreatePatternedBmpAsync(sourceWidth, sourceHeight),
            "JPEG" => await OwnedNativeWicGeneralFixtureFactory.CreatePatternedJpegAsync(
                orientation: 1, width: sourceWidth, height: sourceHeight),
            _ => throw new InvalidOperationException("An unknown fixture must not become an oracle."),
        };
        RasterImageBudgetResult budget = RasterImageResourceBudget.Validate(source);
        Assert.True(budget.Accepted, budget.Reason);
        Assert.Equal((sourceWidth, sourceHeight, 1), (budget.Width, budget.Height, budget.FrameCount));
        await AssertShippingParityAsync(source, budget, outputWidth, outputHeight, orientation: 1);
    }

    [Fact]
    public async Task MissingPixelPayloadFailsAndRefundsTheDecodeCreditOnce()
    {
        byte[] source = await OwnedNativeWicGeneralFixtureFactory.CreatePatternedPngAsync();
        source = RemovePngPixelPayload(source);
        RasterImageBudgetResult budget = RasterImageResourceBudget.Validate(source);
        Assert.True(budget.Accepted, budget.Reason);
        Assert.Equal(37, budget.Width);
        Assert.Equal(23, budget.Height);

        var sizing = new RasterImageSizingRequest(17, 1, null, null, 1_000);
        Assert.True(RasterImageDecodePlan.TryCreate(source, budget, sizing, out var plan));
        var credit = new RasterNativeWicProductParity.CountingCredit();
#if MARKDOWN_LAYOUT_DIAGNOSTICS
        var before = RasterNativeWicDiagnostics.GetSnapshot();
#endif
        await Assert.ThrowsAnyAsync<Exception>(() => Task.Run(() =>
            RasterNativeWicDecoder.Decode(source, plan, credit, CancellationToken.None)));
        Assert.Equal(1, credit.DisposeCount);
#if MARKDOWN_LAYOUT_DIAGNOSTICS
        var after = RasterNativeWicDiagnostics.GetSnapshot();
        Assert.Equal(before.NativeStarted + 1, after.NativeStarted);
        Assert.Equal(before.PixelLeaseCompleted, after.PixelLeaseCompleted);
#endif
    }

    [Theory]
    [InlineData(false, 37, 23)]
    [InlineData(false, 17, 11)]
    [InlineData(true, 37, 23)]
    [InlineData(true, 17, 11)]
    public async Task ProjectAuthoredNonSrgbProfileIsAppliedAndMatchesEveryWinRtPreviewByte(
        bool withAlphaEdges, int width, int height)
    {
        byte[] unprofiled = await OwnedNativeWicGeneralFixtureFactory.CreatePatternedPngAsync(
            withAlphaEdges: withAlphaEdges);
        byte[] source = OwnedNativeWicIccFixtureFactory.EmbedProfileInPng(
            unprofiled, OwnedNativeWicIccFixtureFactory.CreateGamma18MatrixTrcProfile());
        RasterImageBudgetResult budget = RasterImageResourceBudget.Validate(source);
        Assert.True(budget.Accepted, budget.Reason);
        byte[] unprofiledPixels = await DecodeWinRtPreviewAsync(unprofiled, width, height, orientation: 1);
        byte[] colorManagedPixels = await DecodeWinRtPreviewAsync(source, width, height, orientation: 1);
        // Merely finding an ICC chunk is insufficient: this fixture must prove
        // that the Windows oracle actually applies its non-sRGB transfer curve.
        Assert.False(unprofiledPixels.AsSpan().SequenceEqual(colorManagedPixels));
        await AssertShippingParityAsync(source, budget, width, height, orientation: 1);
    }

    [Fact]
    public async Task StandalonePngGammaAndChromaticitiesMatchWinRtPreviewExactly()
    {
        byte[] encoded = await OwnedNativeWicGeneralFixtureFactory.CreatePatternedPngAsync(
            width: 37, height: 23, withAlphaEdges: true);
        byte[] untagged = OwnedNativeWicIccFixtureFactory.RemovePngColorMetadata(encoded);
        Array.Clear(encoded);
        byte[] tagged = OwnedNativeWicIccFixtureFactory.EmbedGammaAndChromaticitiesInPng(untagged);
        try
        {
            await AssertColorMetadataParityAsync(
                "PNG gAMA+cHRM without iCCP/sRGB", untagged, tagged, width: 37, height: 23);
        }
        finally
        {
            Array.Clear(untagged);
            Array.Clear(tagged);
        }
    }

    [Fact]
    public async Task PngSrgbChunkMatchesWinRtPreviewExactly()
    {
        byte[] encoded = await OwnedNativeWicGeneralFixtureFactory.CreatePatternedPngAsync(
            width: 37, height: 23, withAlphaEdges: true);
        byte[] untagged = OwnedNativeWicIccFixtureFactory.RemovePngColorMetadata(encoded);
        Array.Clear(encoded);
        byte[] tagged = OwnedNativeWicIccFixtureFactory.EmbedSrgbChunkInPng(untagged);
        try
        {
            await AssertColorMetadataParityAsync("PNG sRGB chunk", untagged, tagged, width: 37, height: 23);
        }
        finally
        {
            Array.Clear(untagged);
            Array.Clear(tagged);
        }
    }

    [Fact]
    public async Task ProjectAuthoredJpegApp2ProfileMatchesWinRtPreviewExactly()
    {
        byte[] untagged = await OwnedNativeWicGeneralFixtureFactory.CreatePatternedJpegAsync(
            orientation: 1, width: 37, height: 23);
        byte[] profile = OwnedNativeWicIccFixtureFactory.CreateGamma18MatrixTrcProfile();
        byte[] tagged = OwnedNativeWicIccFixtureFactory.EmbedProfileInJpeg(untagged, profile);
        try
        {
            await AssertColorMetadataParityAsync("JPEG APP2 ICC profile", untagged, tagged, width: 37, height: 23);
        }
        finally
        {
            Array.Clear(profile);
            Array.Clear(untagged);
            Array.Clear(tagged);
        }
    }

    [Trait("Requires", "WicWebpDecoder")]
    [Fact]
    public async Task ProjectAuthoredWebpIccpProfileMatchesWinRtPreviewExactly()
    {
        byte[] untagged = await ReadPinnedWebpFixtureAsync();
        RasterImageBudgetResult untaggedBudget = RasterImageResourceBudget.Validate(untagged);
        Assert.True(untaggedBudget.Accepted || untaggedBudget.CanRenderStaticPreview, untaggedBudget.Reason);
        Assert.Equal(1, untaggedBudget.FrameCount);
        byte[] profile = OwnedNativeWicIccFixtureFactory.CreateGamma18MatrixTrcProfile();
        byte[] tagged = OwnedNativeWicIccFixtureFactory.EmbedProfileInSimpleWebp(
            untagged, profile, untaggedBudget.Width, untaggedBudget.Height);
        try
        {
            await AssertColorMetadataParityAsync("WebP ICCP profile", untagged, tagged, width: 17, height: 68);
        }
        finally
        {
            Array.Clear(profile);
            Array.Clear(untagged);
            Array.Clear(tagged);
        }
    }

    private async Task AssertColorMetadataParityAsync(
        string description, byte[] untagged, byte[] tagged, int width, int height)
    {
        RasterImageBudgetResult budget = RasterImageResourceBudget.Validate(tagged);
        Assert.True(budget.Accepted || budget.CanRenderStaticPreview,
            $"{description} fixture was rejected: {budget.Reason}");
        Assert.Equal(1, budget.FrameCount);

        byte[] untaggedWinRt = await DecodeWinRtPreviewAsync(untagged, width, height, orientation: 1);
        byte[] taggedWinRt = await DecodeWinRtPreviewAsync(tagged, width, height, orientation: 1);
        try
        {
            // Windows is not required to apply this metadata; exact parity with
            // the observed WinRT result is.
            output.WriteLine(
                $"{description}: WinRT ColorManageToSRgb differs from untagged baseline=" +
                $"{!untaggedWinRt.AsSpan().SequenceEqual(taggedWinRt)}; output={width}x{height}.");
            await RasterNativeWicProductParity.AssertExactAsync(tagged, budget, taggedWinRt, width, height);
        }
        finally
        {
            Array.Clear(untaggedWinRt);
            Array.Clear(taggedWinRt);
        }
    }

    private static async Task AssertShippingParityAsync(
        byte[] source, RasterImageBudgetResult budget, int width, int height, ushort orientation)
    {
        byte[] reference = await DecodeWinRtPreviewAsync(source, width, height, orientation);
        try
        {
            await RasterNativeWicProductParity.AssertExactAsync(source, budget, reference, width, height);
        }
        finally
        {
            Array.Clear(reference);
        }
    }

    private static async Task<byte[]> ReadPinnedWebpFixtureAsync()
    {
        using Stream fixture = typeof(RasterNativeWicWinRtParityTests).Assembly
            .GetManifestResourceStream("MarkdownRenderer.GitHub.Tests.Fixtures.oversized-static.webp")
            ?? throw new InvalidOperationException("The pinned oversized WebP fixture is missing.");
        using var bytes = new MemoryStream();
        await fixture.CopyToAsync(bytes);
        byte[] source = bytes.ToArray();
        Assert.Equal(51_112, source.Length);
        Assert.Equal("6FEB51F257EF7FAC608FFA54E0D942EA4FE4DE11DECD165A12EA254C3886DB85",
            Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(source)));
        return source;
    }

    private static async Task<byte[]> DecodeWinRtPreviewAsync(
        byte[] source, int width, int height, ushort orientation)
    {
        using var backing = new MemoryStream(source, writable: false);
        using IRandomAccessStream stream = backing.AsRandomAccessStream();
        IAsyncOperation<BitmapDecoder> decoderOperation = BitmapDecoder.CreateAsync(stream);
        BitmapDecoder decoder;
        try
        {
            decoder = await decoderOperation;
        }
        finally
        {
            decoderOperation.Close();
        }
        Assert.Equal(orientation is 5 or 6 or 7 or 8 ? decoder.PixelHeight : decoder.PixelWidth,
            decoder.OrientedPixelWidth);
        Assert.Equal(orientation is 5 or 6 or 7 or 8 ? decoder.PixelWidth : decoder.PixelHeight,
            decoder.OrientedPixelHeight);
        var transform = new BitmapTransform
        {
            ScaledWidth = checked((uint)(orientation is 5 or 6 or 7 or 8 ? height : width)),
            ScaledHeight = checked((uint)(orientation is 5 or 6 or 7 or 8 ? width : height)),
            InterpolationMode = BitmapInterpolationMode.Fant,
        };
        SoftwareBitmap? bitmap = null;
        IAsyncOperation<SoftwareBitmap> bitmapOperation = decoder.GetSoftwareBitmapAsync(
            BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, transform,
            ExifOrientationMode.RespectExifOrientation, ColorManagementMode.ColorManageToSRgb);
        try
        {
            try
            {
                bitmap = await bitmapOperation;
            }
            finally
            {
                bitmapOperation.Close();
            }
            Assert.Equal(width, bitmap.PixelWidth);
            Assert.Equal(height, bitmap.PixelHeight);
            byte[] pixels = new byte[checked(width * height * 4)];
            bitmap.CopyToBuffer(pixels.AsBuffer());
            return pixels;
        }
        finally
        {
            bitmap?.Dispose();
        }
    }

    private static async Task AssertEncodedOrientationAsync(byte[] source, ushort expectedOrientation)
    {
        using var backing = new MemoryStream(source, writable: false);
        using IRandomAccessStream stream = backing.AsRandomAccessStream();
        IAsyncOperation<BitmapDecoder> decoderOperation = BitmapDecoder.CreateAsync(stream);
        BitmapDecoder decoder;
        try
        {
            decoder = await decoderOperation;
        }
        finally
        {
            decoderOperation.Close();
        }
        IAsyncOperation<BitmapPropertySet> propertiesOperation =
            decoder.BitmapProperties.GetPropertiesAsync(["System.Photo.Orientation"]);
        BitmapPropertySet properties;
        try
        {
            properties = await propertiesOperation;
        }
        finally
        {
            propertiesOperation.Close();
        }
        Assert.True(properties.TryGetValue("System.Photo.Orientation", out BitmapTypedValue? value));
        Assert.Equal(expectedOrientation, Assert.IsType<ushort>(value!.Value));
    }

    private static byte[] RemovePngPixelPayload(byte[] source)
    {
        using var malformed = new MemoryStream(source.Length);
        malformed.Write(source.AsSpan(0, 8));
        bool removed = false;
        int offset = 8;
        while (offset <= source.Length - 12)
        {
            int length = checked((int)BinaryPrimitives.ReadUInt32BigEndian(source.AsSpan(offset, 4)));
            if (length > source.Length - offset - 12)
                throw new InvalidDataException("The test encoder produced a truncated PNG.");
            if (source.AsSpan(offset + 4, 4).SequenceEqual("IDAT"u8))
            {
                // Some WIC versions recover malformed zlib/DEFLATE samples, so
                // do not depend on their optional error recovery. A PNG with its
                // entire pixel stream missing is invalid while its unchanged
                // metadata still passes the host budget.
                removed = true;
            }
            else
                malformed.Write(source.AsSpan(offset, length + 12));
            offset = checked(offset + length + 12);
        }
        Assert.True(removed, "The test encoder produced no PNG pixel payload.");
        return malformed.ToArray();
    }
}
