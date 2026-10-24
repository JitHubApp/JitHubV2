using System.Runtime.InteropServices.WindowsRuntime;
using MarkdownRenderer.Layout.Boxes;
using Windows.Foundation;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;
using Xunit;
using Xunit.Abstractions;

namespace MarkdownRenderer.GitHub.Tests;

/// <summary>
/// Extends the owned-WIC byte oracle to uncommon PNG sample layouts and
/// non-square opaque inputs. This remains fixture-specific qualification.
/// </summary>
[Collection(WicRasterMemoryQualificationCollection.CollectionName)]
public sealed class OwnedNativeWicFormatQualificationTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData("palette4-alpha", 17, 11)]
    [InlineData("palette4-alpha", 47, 29)]
    [InlineData("gray8", 17, 11)]
    [InlineData("gray8", 47, 29)]
    [InlineData("gray-alpha8", 17, 11)]
    [InlineData("gray-alpha8", 47, 29)]
    [InlineData("gray16", 17, 11)]
    [InlineData("gray16", 47, 29)]
    [InlineData("rgba16", 17, 11)]
    [InlineData("rgba16", 47, 29)]
    public async Task PaletteGrayscaleAndSixteenBitPngMatchEveryWinRtPreviewByte(
        string fixture, int outputWidth, int outputHeight)
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

        await AssertExactPreviewParityAsync(source, 37, 23, outputWidth, outputHeight);
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

        await AssertExactPreviewParityAsync(source, sourceWidth, sourceHeight, outputWidth, outputHeight);
    }

    private async Task AssertExactPreviewParityAsync(
        byte[] source,
        int sourceWidth,
        int sourceHeight,
        int outputWidth,
        int outputHeight)
    {
        RasterImageBudgetResult budget = RasterImageResourceBudget.Validate(source);
        Assert.True(budget.Accepted, budget.Reason);
        Assert.Equal(sourceWidth, budget.Width);
        Assert.Equal(sourceHeight, budget.Height);
        Assert.Equal(1, budget.FrameCount);

        byte[] reference = await DecodeWinRtPreviewAsync(source, outputWidth, outputHeight);
        NativeWicPreview candidate = await Task.Run(() => GeneralOwnedNativeWicDecoder.Decode(
            source, budget.Width, budget.Height, outputWidth, outputHeight, orientation: 1));
        try
        {
            Assert.Equal(outputWidth, candidate.Width);
            Assert.Equal(outputHeight, candidate.Height);
            Assert.Equal(checked(outputWidth * outputHeight * 4), candidate.Pixels.Length);
            Assert.Equal(reference.Length, candidate.Pixels.Length);

            int differingBytes = 0;
            int maximumDelta = 0;
            for (int index = 0; index < reference.Length; index++)
            {
                int delta = System.Math.Abs(reference[index] - candidate.Pixels[index]);
                if (delta != 0)
                    differingBytes++;
                maximumDelta = System.Math.Max(maximumDelta, delta);
            }

            output.WriteLine(
                $"{budget.Format} {sourceWidth}x{sourceHeight} -> {outputWidth}x{outputHeight}; " +
                $"differingBytes={differingBytes}; maximumDelta={maximumDelta}; " +
                $"colorContexts={candidate.ColorContextCount}.");
            Assert.Equal(0, differingBytes);
            Assert.Equal(0, maximumDelta);
            Assert.Equal(candidate.Retirement.AdoptedOwnedInterfaceCount,
                candidate.Retirement.SuccessfullyFinalReleasedOwnedInterfaceCount);
            Assert.Equal(0, candidate.Retirement.OutstandingQueryInterfaceReferences);
            Assert.True(candidate.Retirement.SourcePinReleased);
            Assert.True(candidate.Retirement.GraphRetired);
            Assert.False(candidate.Retirement.Quarantined);
            Assert.True(candidate.Retirement.ApartmentInitializationBalanced);
            // The renderer deliberately never upscales above intrinsic pixels.
            // The prototype's upscale oracle is extra coverage, not shipping.
            if (outputWidth <= sourceWidth && outputHeight <= sourceHeight)
                await RasterNativeWicProductParity.AssertExactAsync(source, budget, reference, outputWidth, outputHeight);
        }
        finally
        {
            Array.Clear(reference);
            Array.Clear(candidate.Pixels);
        }
    }

    private static async Task<byte[]> DecodeWinRtPreviewAsync(byte[] source, int width, int height)
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

        var transform = new BitmapTransform
        {
            ScaledWidth = checked((uint)width),
            ScaledHeight = checked((uint)height),
            InterpolationMode = BitmapInterpolationMode.Fant,
        };
        SoftwareBitmap? bitmap = null;
        IAsyncOperation<SoftwareBitmap> bitmapOperation = decoder.GetSoftwareBitmapAsync(
            BitmapPixelFormat.Bgra8,
            BitmapAlphaMode.Premultiplied,
            transform,
            ExifOrientationMode.RespectExifOrientation,
            ColorManagementMode.ColorManageToSRgb);
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
}
