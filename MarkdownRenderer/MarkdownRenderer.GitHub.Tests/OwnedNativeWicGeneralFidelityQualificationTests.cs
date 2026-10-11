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
/// Full-byte, same-codec oracle for the test-only owned WIC candidate. A passing
/// fixture does not qualify arbitrary profiles, codec-private peaks or Edge parity.
/// </summary>
[Collection(WicRasterMemoryQualificationCollection.CollectionName)]
public sealed class OwnedNativeWicGeneralFidelityQualificationTests(ITestOutputHelper output)
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
        await CompareEveryPixelAsync(source, budget, width, height, orientation: 1);
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
        await CompareEveryPixelAsync(
            source, budget, swapAxes ? 23 : 37, swapAxes ? 37 : 23, orientation);
        await CompareEveryPixelAsync(
            source, budget, swapAxes ? 11 : 17, swapAxes ? 17 : 11, orientation);
    }

    [Fact]
    public async Task OversizedPinnedWebpStillMatchesEveryWinRtPreviewByteThroughGeneralGraph()
    {
        using Stream fixture = GetType().Assembly.GetManifestResourceStream(
            "MarkdownRenderer.GitHub.Tests.Fixtures.oversized-static.webp")
            ?? throw new InvalidOperationException("The pinned oversized WebP fixture is missing.");
        using var backing = new MemoryStream();
        await fixture.CopyToAsync(backing);
        byte[] source = backing.ToArray();
        Assert.Equal("6FEB51F257EF7FAC608FFA54E0D942EA4FE4DE11DECD165A12EA254C3886DB85",
            Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(source)));
        RasterImageBudgetResult budget = RasterImageResourceBudget.Validate(source);
        Assert.False(budget.Accepted);
        Assert.True(budget.CanRenderStaticPreview);
        Assert.Equal(1, budget.FrameCount);
        await CompareEveryPixelAsync(source, budget, 256, 1025, orientation: 1);
    }

    [Fact]
    public async Task InvalidHeaderAndPreCanceledWorkNeverProduceAnOwnedRaster()
    {
        byte[] source = await OwnedNativeWicGeneralFixtureFactory.CreatePatternedPngAsync();
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Task.Run(() =>
            GeneralOwnedNativeWicDecoder.Decode(source, 37, 23, 17, 11, 1, canceled.Token)));
        await Assert.ThrowsAnyAsync<InvalidDataException>(() => Task.Run(() =>
            GeneralOwnedNativeWicDecoder.Decode(source, 38, 23, 17, 11, 1)));
    }

    [Fact]
    public async Task MissingPixelPayloadAfterNativeAdoptionRetiresEveryOwnedResource()
    {
        byte[] source = await OwnedNativeWicGeneralFixtureFactory.CreatePatternedPngAsync();
        source = RemovePngPixelPayload(source);
        RasterImageBudgetResult budget = RasterImageResourceBudget.Validate(source);
        Assert.True(budget.Accepted, budget.Reason);
        Assert.Equal(37, budget.Width);
        Assert.Equal(23, budget.Height);
        NativeWicDecodeFailure failure = await Assert.ThrowsAsync<NativeWicDecodeFailure>(() => Task.Run(() =>
            GeneralOwnedNativeWicDecoder.Decode(source, 37, 23, 17, 11, 1)));
        Assert.NotNull(failure.InnerException);
        Assert.True(failure.Retirement.AdoptedOwnedInterfaceCount > 0);
        Assert.Equal(failure.Retirement.AdoptedOwnedInterfaceCount,
            failure.Retirement.SuccessfullyFinalReleasedOwnedInterfaceCount);
        Assert.Equal(0, failure.Retirement.OutstandingQueryInterfaceReferences);
        Assert.True(failure.Retirement.SourcePinReleased);
        Assert.True(failure.Retirement.GraphRetired);
        Assert.False(failure.Retirement.Quarantined);
        Assert.True(failure.Retirement.ApartmentInitializationBalanced);

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

    [Fact]
    public async Task CancellationAfterPixelCopyStillRetiresTheNativeGraphOnItsWorker()
    {
        byte[] source = await OwnedNativeWicGeneralFixtureFactory.CreatePatternedPngAsync();
        using var cancellation = new CancellationTokenSource();
        int checkpoints = 0;
        var hook = new NativeWicDecodeTestHook(
            NativeWicDecodeCheckpointPoint.PixelsCopied, ownedInterface: null,
            () => { checkpoints++; cancellation.Cancel(); });
        NativeWicDecodeFailure failure = await Assert.ThrowsAsync<NativeWicDecodeFailure>(() => Task.Run(() =>
            GeneralOwnedNativeWicDecoder.Decode(source, 37, 23, 17, 11, 1, cancellation.Token, hook)));

        Assert.IsAssignableFrom<OperationCanceledException>(failure.InnerException);
        Assert.Equal(1, checkpoints);
        Assert.True(failure.Retirement.AdoptedOwnedInterfaceCount > 0);
        Assert.Equal(failure.Retirement.AdoptedOwnedInterfaceCount,
            failure.Retirement.SuccessfullyFinalReleasedOwnedInterfaceCount);
        Assert.Equal(0, failure.Retirement.OutstandingQueryInterfaceReferences);
        Assert.True(failure.Retirement.SourcePinReleased);
        Assert.True(failure.Retirement.GraphRetired);
        Assert.False(failure.Retirement.Quarantined);
        Assert.True(failure.Retirement.ApartmentInitializationBalanced);
    }

    [Fact]
    public async Task ReleaseFailureQuarantinesUpstreamCustodyButBalancesTheWorkerApartment()
    {
        byte[] source = await OwnedNativeWicGeneralFixtureFactory.CreatePatternedPngAsync();
        int releaseAttempts = 0;
        var hook = new NativeWicDecodeTestHook(
            NativeWicDecodeCheckpointPoint.BeforeOwnedInterfaceFinalRelease,
            NativeWicOwnedInterfaceKind.Frame,
            () => { releaseAttempts++; throw new InvalidOperationException("Injected frame-release uncertainty."); });
        NativeWicDecodeFailure failure = await Assert.ThrowsAsync<NativeWicDecodeFailure>(() => Task.Run(() =>
            GeneralOwnedNativeWicDecoder.Decode(source, 37, 23, 17, 11, 1, testHook: hook)));

        Assert.Equal(1, releaseAttempts);
        Assert.InRange(failure.Retirement.SuccessfullyFinalReleasedOwnedInterfaceCount,
            1, failure.Retirement.AdoptedOwnedInterfaceCount - 1);
        Assert.Equal(0, failure.Retirement.OutstandingQueryInterfaceReferences);
        Assert.False(failure.Retirement.SourcePinReleased);
        Assert.False(failure.Retirement.GraphRetired);
        Assert.True(failure.Retirement.Quarantined);
        Assert.True(failure.Retirement.ApartmentInitializationBalanced);

        // The same one-shot hook must not replay and unrelated content remains
        // usable. This does not claim a process-memory ceiling or credit release:
        // the failed graph deliberately retains its remaining native custody.
        NativeWicPreview unrelated = await Task.Run(() => GeneralOwnedNativeWicDecoder.Decode(
            source, 37, 23, 17, 11, 1, testHook: hook));
        Array.Clear(unrelated.Pixels);
        Assert.Equal(1, releaseAttempts);
        Assert.True(unrelated.Retirement.GraphRetired);
        Assert.True(unrelated.Retirement.SourcePinReleased);
        Assert.True(unrelated.Retirement.ApartmentInitializationBalanced);
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
        await CompareEveryPixelAsync(source, budget, width, height, orientation: 1, requireColorContext: true);
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
                "PNG gAMA+cHRM without iCCP/sRGB",
                untagged, tagged, width: 37, height: 23);
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
            await AssertColorMetadataParityAsync(
                "PNG sRGB chunk",
                untagged, tagged, width: 37, height: 23);
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
            await AssertColorMetadataParityAsync(
                "JPEG APP2 ICC profile",
                untagged, tagged, width: 37, height: 23);
        }
        finally
        {
            Array.Clear(profile);
            Array.Clear(untagged);
            Array.Clear(tagged);
        }
    }

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
            await AssertColorMetadataParityAsync(
                "WebP ICCP profile",
                untagged, tagged, width: 17, height: 68);
        }
        finally
        {
            Array.Clear(profile);
            Array.Clear(untagged);
            Array.Clear(tagged);
        }
    }

    private async Task AssertColorMetadataParityAsync(
        string description,
        byte[] untagged,
        byte[] tagged,
        int width,
        int height)
    {
        RasterImageBudgetResult budget = RasterImageResourceBudget.Validate(tagged);
        Assert.True(budget.Accepted || budget.CanRenderStaticPreview,
            $"{description} fixture was rejected: {budget.Reason}");
        Assert.Equal(1, budget.FrameCount);

        byte[] untaggedWinRt = await DecodeWinRtPreviewAsync(untagged, width, height, orientation: 1);
        byte[] taggedWinRt = await DecodeWinRtPreviewAsync(tagged, width, height, orientation: 1);
        try
        {
            bool winRtProducedDifferentPixels = !untaggedWinRt.AsSpan().SequenceEqual(taggedWinRt);
            output.WriteLine(
                $"{description}: WinRT ColorManageToSRgb differs from untagged baseline="+
                $"{winRtProducedDifferentPixels}; output={width}x{height}. " +
                "The observation is not itself a requirement that Windows apply this metadata; exact parity " +
                "with the observed WinRT result remains required.");
            await CompareEveryPixelAsync(
                tagged, budget, width, height, orientation: 1,
                requireColorContext: winRtProducedDifferentPixels);
        }
        finally
        {
            Array.Clear(untaggedWinRt);
            Array.Clear(taggedWinRt);
        }
    }

    private static async Task<byte[]> ReadPinnedWebpFixtureAsync()
    {
        using Stream fixture = typeof(OwnedNativeWicGeneralFidelityQualificationTests).Assembly
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

    private async Task CompareEveryPixelAsync(
        byte[] source, RasterImageBudgetResult budget, int width, int height, ushort orientation,
        bool requireColorContext = false)
    {
        byte[] reference = await DecodeWinRtPreviewAsync(source, width, height, orientation);
        var candidate = await Task.Run(() => GeneralOwnedNativeWicDecoder.Decode(
            source, budget.Width, budget.Height, width, height, orientation));
        Assert.Equal(width, candidate.Width);
        Assert.Equal(height, candidate.Height);
        Assert.Equal(checked(width * height * 4), candidate.Pixels.Length);
        Assert.Equal(reference.Length, candidate.Pixels.Length);
        int differenceCount = 0;
        int maximumDelta = 0;
        for (int i = 0; i < reference.Length; i++)
        {
            int delta = System.Math.Abs(reference[i] - candidate.Pixels[i]);
            if (delta != 0)
                differenceCount++;
            maximumDelta = System.Math.Max(maximumDelta, delta);
        }
        output.WriteLine($"{budget.Format}: source={budget.Width}x{budget.Height}; orientation={orientation}; " +
            $"output={width}x{height}; differingBytes={differenceCount}; maximumDelta={maximumDelta}; " +
            $"colorContexts={candidate.ColorContextCount}.");
        Assert.Equal(0, differenceCount);
        Assert.Equal(0, maximumDelta);
        if (requireColorContext)
            Assert.True(candidate.ColorContextCount > 0);
        Assert.Equal(candidate.Retirement.AdoptedOwnedInterfaceCount,
            candidate.Retirement.SuccessfullyFinalReleasedOwnedInterfaceCount);
        Assert.Equal(0, candidate.Retirement.OutstandingQueryInterfaceReferences);
        Assert.True(candidate.Retirement.SourcePinReleased);
        Assert.True(candidate.Retirement.GraphRetired);
        Assert.False(candidate.Retirement.Quarantined);
        Assert.True(candidate.Retirement.ApartmentInitializationBalanced);
        await RasterNativeWicProductParity.AssertExactAsync(source, budget, reference, width, height);
    }

    internal static async Task<byte[]> DecodeWinRtPreviewAsync(
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
                // Some WIC versions recover malformed zlib/DEFLATE samples,
                // so do not depend on their optional error recovery here. A
                // PNG with its entire required pixel stream missing is invalid
                // while its unchanged metadata still passes the host budget.
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
