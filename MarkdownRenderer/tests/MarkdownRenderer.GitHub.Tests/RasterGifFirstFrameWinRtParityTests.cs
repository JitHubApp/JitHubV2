using System.Runtime.InteropServices.WindowsRuntime;
using MarkdownRenderer.Accessibility.Internal.Semantics;
using MarkdownRenderer.Document;
using MarkdownRenderer.GitHub.Tests.TestSupport.Assertions;
using MarkdownRenderer.GitHub.Tests.TestSupport.Builders;
using MarkdownRenderer.Images;
using MarkdownRenderer.Images.Internal.Raster;
using MarkdownRenderer.Layout.Internal.Context;
using MarkdownRenderer.Layout.Internal.Images;
using MarkdownRenderer.Layout.Internal.Images.Raster;
using MarkdownRenderer.Layout.Internal.RenderResources;
using MarkdownRenderer.Parsing.Internal;
using MarkdownRenderer.Performance;
using MarkdownRenderer.Theming;
using MarkdownRenderer.Theming.Internal;
using Microsoft.Graphics.Canvas;
using Microsoft.UI.Xaml;
using Windows.Foundation;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;
using Windows.UI;
using Xunit;
using Xunit.Abstractions;

namespace MarkdownRenderer.GitHub.Tests;

/// <summary>
/// GIF first-frame fidelity proof for the shipping native WIC decoder. The
/// reference is the existing WinRT first-frame decode plus ImageBox's exact
/// transparent-canvas, fractional-rectangle, Linear Win2D composition. This
/// does not qualify animation playback, general GIF fidelity, or memory bounds.
/// </summary>
/// <remarks>
/// This file is longer than 500 lines because the parity proof needs its own
/// WinRT reference decoder and legacy Win2D composition path, and every case
/// compares the owned native decode against that same reference.
/// </remarks>
[Collection(SharedCanvasBitmapCacheTestCollection.Name)]
public sealed class RasterGifFirstFrameWinRtParityTests(ITestOutputHelper output)
{
    private readonly MarkdownRenderResources _renderResources = new();

    private const long RasterPixelLimit = 512;

    [Fact]
    [Trait("Category", "NativeRasterFidelity")]
    public async Task OwnedGifFramePlanAndDecoderMatchTheWinRtFrameZeroBytes()
    {
        foreach (OwnedNativeWicGifFixtureFactory.Fixture fixture in
                 OwnedNativeWicGifFixtureFactory.CreateCoreMatrix()
                     .Append(OwnedNativeWicGifFixtureFactory.CreateOverFrameLimitStaticPreview()))
        {
            RasterImageBudgetResult budget = RasterImageResourceBudget.Validate(fixture.Bytes);
            AssertBudgetDescribesFixture(fixture, budget);

            var sizing = new RasterImageSizingRequest(
                availableWidth: fixture.CanvasWidth,
                rasterizationScale: 1,
                requestedWidth: null,
                requestedHeight: null,
                maximumPixels: RasterPixelLimit);
            Assert.True(RasterGifFirstFrameDecodePlan.TryCreate(
                fixture.Bytes, budget, sizing, out RasterGifFirstFrameDecodePlan plan), fixture.Name);
            AssertPlanDescribesFixture(fixture, plan);

            byte[] expectedFrame = await DecodeWinRtGifFrameBytesAsync(
                fixture.Bytes,
                frameIndex: 0,
                plan.DecodeWidth,
                plan.DecodeHeight,
                fixture.FrameCount,
                fixture.FirstFrame.Width,
                fixture.FirstFrame.Height);
            var credit = new RasterNativeWicProductParity.CountingCredit();
            using RasterDecodedPixelLease decoded = await Task.Run(() =>
                RasterNativeWicDecoder.DecodeGifFirstFrame(
                    fixture.Bytes, plan, credit, _renderResources.NativeRetirement, CancellationToken.None));
            Assert.Equal(plan.DecodeWidth, decoded.Width);
            Assert.Equal(plan.DecodeHeight, decoded.Height);
            Assert.Equal(expectedFrame.Length, decoded.Pixels.Length);
            Assert.True(expectedFrame.AsSpan().SequenceEqual(decoded.Pixels),
                $"{fixture.Name}: native frame-zero PBGRA pixels differ from WinRT.");
            Assert.Equal(0, credit.DisposeCount);
            ReadOnlyMemory<byte> borrowedNativePixels = decoded.Pixels;
            decoded.Dispose();
            Assert.Equal(1, credit.DisposeCount);
            Assert.Throws<ObjectDisposedException>(() => _ = decoded.Pixels);
            foreach (byte value in borrowedNativePixels.Span)
                Assert.Equal((byte)0, value);

            if (fixture.FrameCount > 1)
                await AssertLaterFrameDiffersFromFrameZeroAsync(fixture, expectedFrame);
            Array.Clear(expectedFrame);

            output.WriteLine(
                $"{fixture.Name}: frames={fixture.FrameCount}; canvas={plan.OutputWidth}x{plan.OutputHeight}; " +
                $"frame={plan.DecodeWidth}x{plan.DecodeHeight}; composed={plan.RequiresComposition}.");
        }
    }

    [Fact]
    [Trait("Category", "NativeRasterFidelity")]
    public async Task ImageBoxGifCanvasMatchesLegacyWinRtAndLinearWin2DCompositionOnWarp()
    {
        _renderResources.Images.BitmapCache.Clear();
        await DisplaySizedRasterDecodeTests.WaitUntilAsync(
            () => _renderResources.Images.BitmapCache.GetSnapshot().RetainedBytes == 0);

        using var device = new CanvasDevice(forceSoftwareRenderer: true);
        Assert.True(device.ForceSoftwareRenderer,
            "GIF parity must run through Win2D's software renderer rather than silently using hardware.");

        RasterNativeWicDiagnosticSnapshot before = _renderResources.Images.NativeWic.GetSnapshot();
        int exercised = 0;
        try
        {
            foreach (OwnedNativeWicGifFixtureFactory.Fixture fixture in
                     OwnedNativeWicGifFixtureFactory.CreateCoreMatrix()
                         .Append(OwnedNativeWicGifFixtureFactory.CreateOverFrameLimitStaticPreview()))
            {
                await AssertImageBoxCanvasMatchesLegacyCompositionAsync(device, fixture);
                exercised++;
            }
        }
        finally
        {
            _renderResources.Images.BitmapCache.Clear();
            await DisplaySizedRasterDecodeTests.WaitUntilAsync(
                () => _renderResources.Images.BitmapCache.GetSnapshot().RetainedBytes == 0);
        }

        Assert.Equal(OwnedNativeWicGifFixtureFactory.CreateCoreMatrix().Count + 1, exercised);
        RasterNativeWicDiagnosticSnapshot after = _renderResources.Images.NativeWic.GetSnapshot();
        Assert.True(after.NativeStarted > before.NativeStarted,
            "The scope's decode counters should prove that ImageBox entered the owned WIC route for admitted GIFs.");
        Assert.True(after.PixelLeaseCompleted > before.PixelLeaseCompleted,
            "The scope's decode counters should prove the native frame lease reached ImageBox upload.");
        output.WriteLine($"GIF native decoder diagnostic deltas: started={after.NativeStarted - before.NativeStarted}; " +
            $"pixelLeases={after.PixelLeaseCompleted - before.PixelLeaseCompleted}.");
    }

    [Theory]
    [InlineData(0.75, 3)]
    [InlineData(1.0, 4)]
    [InlineData(1.25, 5)]
    [InlineData(1.5, 6)]
    [InlineData(1.75, 7)]
    [InlineData(2.0, 8)]
    [InlineData(2.25, 8)]
    [InlineData(3.0, 8)]
    [InlineData(4.0, 8)]
    public async Task OffsetFrameDpiScalingAndFractionalOnePixelCapMatchLegacyComposition(
        double rasterizationScale,
        int expectedCanvasPixels)
    {
        OwnedNativeWicGifFixtureFactory.Fixture fixture = CreateDpiFixture();
        RasterImageBudgetResult budget = RasterImageResourceBudget.Validate(fixture.Bytes);
        using var device = new CanvasDevice(forceSoftwareRenderer: true);
        Assert.True(device.ForceSoftwareRenderer);
        var resolver = new GifResolver(fixture.Bytes, $"owned-gif-dpi-{rasterizationScale}-{Guid.NewGuid():N}");
        using var session = new MarkdownPerformanceSession(
            MarkdownPerformanceOptions.Progressive with { MaxRasterOutputPixels = RasterPixelLimit });
        var image = new ImageBox(
            CreateContext(device, resolver, session, rasterizationScale), _renderResources.Images,
            "https://images.example/dpi-offset.gif",
            "offset frame");
        try
        {
            var sizing = new RasterImageSizingRequest(4, rasterizationScale, null, null, RasterPixelLimit);
            (int canvasWidth, int canvasHeight) = sizing.GetSize(fixture.CanvasWidth, fixture.CanvasHeight);
            Assert.Equal((expectedCanvasPixels, expectedCanvasPixels), (canvasWidth, canvasHeight));
            byte[] expected = await DecodeLegacyImageBoxCanvasAsync(
                fixture.Bytes, budget, device, canvasWidth, canvasHeight);
            await LoadImageAsync(image, availableWidth: 4);
            Assert.NotNull(image.Bitmap);
            Assert.Equal((uint)expectedCanvasPixels, image.Bitmap.SizeInPixels.Width);
            Assert.Equal((uint)expectedCanvasPixels, image.Bitmap.SizeInPixels.Height);
            byte[] actual = image.Bitmap.GetPixelBytes();
            try
            {
                Assert.Equal(expected, actual);
            }
            finally
            {
                Array.Clear(expected);
                Array.Clear(actual);
            }
        }
        finally
        {
            image.Dispose();
            await DisplaySizedRasterDecodeTests.WaitUntilAsync(
                () => image.GetWorkState().ActiveWorkCount == 0);
        }

        // A one-pixel cap must retain the same fractional alpha coverage as
        // the legacy partial-frame composition, never snap the frame opaque.
        using var onePixelSession = new MarkdownPerformanceSession(
            MarkdownPerformanceOptions.Progressive with { MaxRasterOutputPixels = 1 });
        var onePixelResolver = new GifResolver(
            fixture.Bytes, $"owned-gif-fractional-cap-{Guid.NewGuid():N}");
        var onePixel = new ImageBox(
            CreateContext(device, onePixelResolver, onePixelSession), _renderResources.Images,
            "https://images.example/fractional-cap.gif",
            "fractional frame");
        try
        {
            byte[] expected = await DecodeLegacyImageBoxCanvasAsync(
                fixture.Bytes, budget, device, canvasWidth: 1, canvasHeight: 1);
            await LoadImageAsync(onePixel, availableWidth: 4);
            Assert.NotNull(onePixel.Bitmap);
            Assert.Equal((1u, 1u), (onePixel.Bitmap.SizeInPixels.Width, onePixel.Bitmap.SizeInPixels.Height));
            byte[] actual = onePixel.Bitmap.GetPixelBytes();
            try
            {
                Assert.Equal(expected, actual);
                Assert.InRange((int)actual[3], 1, 254);
            }
            finally
            {
                Array.Clear(expected);
                Array.Clear(actual);
            }
        }
        finally
        {
            onePixel.Dispose();
            await DisplaySizedRasterDecodeTests.WaitUntilAsync(
                () => onePixel.GetWorkState().ActiveWorkCount == 0);
        }
    }

    [Fact]
    [Trait("Category", "NativeRasterFidelity")]
    public async Task IccApplicationExtensionMatchesWinRtWhetherWindowsHonorsTheProfileOrIgnoresIt()
    {
        OwnedNativeWicGifFixtureFactory.Fixture unprofiledFixture =
            OwnedNativeWicGifFixtureFactory.CreateCoreMatrix()[0];
        byte[] profile = OwnedNativeWicIccFixtureFactory.CreateGamma18MatrixTrcProfile();
        OwnedNativeWicGifFixtureFactory.Fixture profiledFixture =
            OwnedNativeWicGifFixtureFactory.EmbedIccProfile(unprofiledFixture, profile);
        RasterImageBudgetResult budget = RasterImageResourceBudget.Validate(profiledFixture.Bytes);
        Assert.True(budget.Accepted, budget.Reason);
        Assert.Equal("GIF", budget.Format);
        int profileBlocks = (profile.Length + 254) / 255;
        Assert.Equal(unprofiledFixture.Bytes.Length + profile.Length + profileBlocks + 15,
            profiledFixture.Bytes.Length);

        byte[] unprofiledWinRt = await DecodeWinRtFirstFrameBytesAsync(unprofiledFixture);
        byte[] profiledWinRt = await DecodeWinRtFirstFrameBytesAsync(profiledFixture);
        bool winRtAppliedProfile = !unprofiledWinRt.AsSpan().SequenceEqual(profiledWinRt);
        output.WriteLine(winRtAppliedProfile
            ? "WinRT changed GIF pixels for the project-authored ICCRGBG1 profile; exact parity is required."
            : "WinRT produced identical GIF pixels with and without ICCRGBG1; Windows appears to ignore this " +
              "application extension on this runtime, so no color-management support is claimed.");

        var sizing = new RasterImageSizingRequest(
            profiledFixture.CanvasWidth, 1, null, null, RasterPixelLimit);
        Assert.True(RasterGifFirstFrameDecodePlan.TryCreate(
            profiledFixture.Bytes, budget, sizing, out RasterGifFirstFrameDecodePlan plan));
        var credit = new RasterNativeWicProductParity.CountingCredit();
        using (RasterDecodedPixelLease decoded = await Task.Run(() =>
                   RasterNativeWicDecoder.DecodeGifFirstFrame(
                       profiledFixture.Bytes, plan, credit, _renderResources.NativeRetirement, CancellationToken.None)))
        {
            Assert.Equal(profiledWinRt.Length, decoded.Pixels.Length);
            Assert.True(profiledWinRt.AsSpan().SequenceEqual(decoded.Pixels),
                "Owned WIC must match WinRT's observed GIF ICC behavior exactly.");
        }
        Assert.Equal(1, credit.DisposeCount);

        try
        {
            await AssertProfiledImageBoxMatchesLegacyCanvasAsync(profiledFixture, budget, plan);
        }
        finally
        {
            Array.Clear(profiledWinRt);
            Array.Clear(unprofiledWinRt);
            Array.Clear(profile);
        }
    }

    private async Task AssertImageBoxCanvasMatchesLegacyCompositionAsync(
        CanvasDevice device,
        OwnedNativeWicGifFixtureFactory.Fixture fixture)
    {
        RasterImageBudgetResult budget = RasterImageResourceBudget.Validate(fixture.Bytes);
        Assert.True(budget.Accepted || budget.CanRenderStaticPreview,
            $"{fixture.Name}: {budget.Reason}");
        var resolver = new GifResolver(fixture.Bytes, $"owned-gif-first-frame-{fixture.Name}-{Guid.NewGuid():N}");
        using var session = new MarkdownPerformanceSession(
            MarkdownPerformanceOptions.Progressive with
            {
                UseDisplaySizedRasterDecode = true,
                MaxRasterOutputPixels = RasterPixelLimit,
            });
        var context = CreateContext(device, resolver, session);
        var image = new ImageBox(context, context.ImageResources, $"https://images.example/{fixture.Name}.gif", fixture.Name);
        try
        {
            float availableWidth = fixture.CanvasWidth;
            double scale = 1d;
            var sizing = new RasterImageSizingRequest(
                availableWidth,
                scale,
                requestedWidth: null,
                requestedHeight: null,
                maximumPixels: RasterPixelLimit);
            (int canvasWidth, int canvasHeight) = sizing.GetSize(fixture.CanvasWidth, fixture.CanvasHeight);
            byte[] expected = await DecodeLegacyImageBoxCanvasAsync(
                fixture.Bytes, budget, device, canvasWidth, canvasHeight);

            await LoadImageAsync(image, availableWidth);
            Assert.NotNull(image.Bitmap);
            Assert.Equal((uint)canvasWidth, image.Bitmap.SizeInPixels.Width);
            Assert.Equal((uint)canvasHeight, image.Bitmap.SizeInPixels.Height);
            byte[] actual = image.Bitmap.GetPixelBytes();
            try
            {
                Assert.Equal(expected, actual);
            }
            finally
            {
                Array.Clear(actual);
                Array.Clear(expected);
            }

            Assert.Equal(MarkdownImageAccessibilityState.Loaded, image.AccessibilityState);
            image.Measure(availableWidth);
            Assert.Equal(fixture.CanvasWidth, image.MeasuredImageWidth, precision: 2);
            Assert.Equal((float)fixture.CanvasHeight, image.MeasuredImageHeight, precision: 2);
        }
        finally
        {
            image.Dispose();
            await DisplaySizedRasterDecodeTests.WaitUntilAsync(
                () => image.GetWorkState().ActiveWorkCount == 0);
        }
    }

    private async Task AssertProfiledImageBoxMatchesLegacyCanvasAsync(
        OwnedNativeWicGifFixtureFactory.Fixture profiledFixture,
        RasterImageBudgetResult budget,
        RasterGifFirstFrameDecodePlan plan)
    {
        _renderResources.Images.BitmapCache.Clear();
        await DisplaySizedRasterDecodeTests.WaitUntilAsync(
            () => _renderResources.Images.BitmapCache.GetSnapshot().RetainedBytes == 0);
        using var device = new CanvasDevice(forceSoftwareRenderer: true);
        Assert.True(device.ForceSoftwareRenderer);
        using var session = new MarkdownPerformanceSession(
            MarkdownPerformanceOptions.Progressive with { MaxRasterOutputPixels = RasterPixelLimit });
        var resolver = new GifResolver(
            profiledFixture.Bytes, $"owned-gif-icc-{Guid.NewGuid():N}");
        var image = new ImageBox(
            CreateContext(device, resolver, session), _renderResources.Images,
            "https://images.example/profiled.gif",
            "profiled GIF");
        try
        {
            byte[] expected = await DecodeLegacyImageBoxCanvasAsync(
                profiledFixture.Bytes, budget, device,
                canvasWidth: plan.OutputWidth, canvasHeight: plan.OutputHeight);
            try
            {
                await LoadImageAsync(image, profiledFixture.CanvasWidth);
                Assert.NotNull(image.Bitmap);
                byte[] actual = image.Bitmap.GetPixelBytes();
                try
                {
                    Assert.Equal(expected, actual);
                }
                finally
                {
                    Array.Clear(actual);
                }
            }
            finally
            {
                Array.Clear(expected);
            }
        }
        finally
        {
            image.Dispose();
            await DisplaySizedRasterDecodeTests.WaitUntilAsync(
                () => image.GetWorkState().ActiveWorkCount == 0);
            _renderResources.Images.BitmapCache.Clear();
            await DisplaySizedRasterDecodeTests.WaitUntilAsync(
                () => _renderResources.Images.BitmapCache.GetSnapshot().RetainedBytes == 0);
        }
    }

    private static void AssertBudgetDescribesFixture(
        OwnedNativeWicGifFixtureFactory.Fixture fixture,
        RasterImageBudgetResult budget)
    {
        Assert.True(budget.Accepted || budget.CanRenderStaticPreview,
            $"{fixture.Name}: {budget.Reason}");
        Assert.Equal("GIF", budget.Format);
        Assert.Equal(fixture.CanvasWidth, budget.Width);
        Assert.Equal(fixture.CanvasHeight, budget.Height);
        Assert.Equal(fixture.FrameCount, budget.FrameCount);
        Assert.Equal((fixture.FirstFrame.Left, fixture.FirstFrame.Top,
                fixture.FirstFrame.Width, fixture.FirstFrame.Height),
            (budget.FirstFrame!.Value.Left, budget.FirstFrame.Value.Top,
                budget.FirstFrame.Value.Width, budget.FirstFrame.Value.Height));
    }

    private static void AssertPlanDescribesFixture(
        OwnedNativeWicGifFixtureFactory.Fixture fixture,
        RasterGifFirstFrameDecodePlan plan)
    {
        Assert.Equal(fixture.CanvasWidth, plan.CanvasWidth);
        Assert.Equal(fixture.CanvasHeight, plan.CanvasHeight);
        Assert.Equal(fixture.FrameCount, plan.FrameCount);
        Assert.Equal(fixture.FirstFrame.Left, plan.FrameLeft);
        Assert.Equal(fixture.FirstFrame.Top, plan.FrameTop);
        Assert.Equal(fixture.FirstFrame.Width, plan.FrameWidth);
        Assert.Equal(fixture.FirstFrame.Height, plan.FrameHeight);
        Assert.Equal(plan.RequiresComposition,
            fixture.FirstFrame.Left != 0 || fixture.FirstFrame.Top != 0 ||
            fixture.FirstFrame.Width != fixture.CanvasWidth ||
            fixture.FirstFrame.Height != fixture.CanvasHeight);
    }

    private static async Task AssertLaterFrameDiffersFromFrameZeroAsync(
        OwnedNativeWicGifFixtureFactory.Fixture fixture,
        byte[] frameZero)
    {
        byte[] secondFrame = await DecodeWinRtGifFrameBytesAsync(
            fixture.Bytes,
            frameIndex: 1,
            fixture.FirstFrame.Width,
            fixture.FirstFrame.Height,
            fixture.FrameCount,
            fixture.Frames[1].Width,
            fixture.Frames[1].Height);
        Assert.False(secondFrame.AsSpan().SequenceEqual(frameZero),
            $"{fixture.Name}: later-frame control data must differ from frame zero.");
        Array.Clear(secondFrame);
    }

    private static Task<byte[]> DecodeWinRtFirstFrameBytesAsync(OwnedNativeWicGifFixtureFactory.Fixture fixture) =>
        DecodeWinRtGifFrameBytesAsync(
            fixture.Bytes,
            frameIndex: 0,
            fixture.FirstFrame.Width,
            fixture.FirstFrame.Height,
            fixture.FrameCount,
            fixture.FirstFrame.Width,
            fixture.FirstFrame.Height);

    private static OwnedNativeWicGifFixtureFactory.Fixture CreateDpiFixture()
    {
        OwnedNativeWicGifFixtureFactory.Rgb[] palette =
        [
            new(0, 0, 0),
            new(241, 28, 19),
            new(18, 218, 42),
            new(24, 43, 239),
        ];
        return OwnedNativeWicGifFixtureFactory.Create(
            "dpi-offset-fractional",
            canvasWidth: 8,
            canvasHeight: 8,
            globalPalette: palette,
            OwnedNativeWicGifFixtureFactory.OffsetFrame(2, 1, 2, 3));
    }

    private static async Task<byte[]> DecodeLegacyImageBoxCanvasAsync(
        byte[] source,
        RasterImageBudgetResult budget,
        CanvasDevice device,
        int canvasWidth,
        int canvasHeight)
    {
        (int left, int top, int frameWidth, int frameHeight) = budget.FirstFrame!.Value;
        bool compose = left != 0 || top != 0 || frameWidth != budget.Width || frameHeight != budget.Height;
        int decodeWidth = compose
            ? System.Math.Max(1, (int)System.Math.Ceiling(frameWidth * (double)canvasWidth / budget.Width))
            : canvasWidth;
        int decodeHeight = compose
            ? System.Math.Max(1, (int)System.Math.Ceiling(frameHeight * (double)canvasHeight / budget.Height))
            : canvasHeight;

        using SoftwareBitmap frame = await DecodeWinRtGifSoftwareBitmapAsync(
            source, frameIndex: 0, decodeWidth, decodeHeight, budget.FrameCount,
            frameWidth, frameHeight);
        using CanvasBitmap decodedFrame = CanvasBitmap.CreateFromSoftwareBitmap(device, frame);
        if (!compose)
            return decodedFrame.GetPixelBytes();

        using var target = new CanvasRenderTarget(device, canvasWidth, canvasHeight, 96);
        using (CanvasDrawingSession drawing = target.CreateDrawingSession())
        {
            drawing.Clear(Color.FromArgb(0, 0, 0, 0));
            var destination = new Rect(
                left * (double)canvasWidth / budget.Width,
                top * (double)canvasHeight / budget.Height,
                frameWidth * (double)canvasWidth / budget.Width,
                frameHeight * (double)canvasHeight / budget.Height);
            drawing.DrawImage(
                decodedFrame,
                destination,
                new Rect(0, 0, decodedFrame.Size.Width, decodedFrame.Size.Height),
                1f,
                CanvasImageInterpolation.Linear);
        }
        return target.GetPixelBytes();
    }

    private static async Task<byte[]> DecodeWinRtGifFrameBytesAsync(
        byte[] source,
        uint frameIndex,
        int width,
        int height,
        int expectedFrameCount,
        int expectedSourceWidth,
        int expectedSourceHeight)
    {
        using SoftwareBitmap bitmap = await DecodeWinRtGifSoftwareBitmapAsync(
            source,
            frameIndex,
            width,
            height,
            expectedFrameCount,
            expectedSourceWidth,
            expectedSourceHeight);
        var buffer = new Windows.Storage.Streams.Buffer(checked((uint)(width * height * 4)))
        {
            Length = checked((uint)(width * height * 4)),
        };
        bitmap.CopyToBuffer(buffer);
        return buffer.ToArray();
    }

    private static async Task<SoftwareBitmap> DecodeWinRtGifSoftwareBitmapAsync(
        byte[] source,
        uint frameIndex,
        int width,
        int height,
        int expectedFrameCount,
        int expectedSourceWidth,
        int expectedSourceHeight)
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
        Assert.Equal((uint)expectedFrameCount, decoder.FrameCount);

        BitmapFrame? sourceFrame = null;
        if (frameIndex > 0)
        {
            IAsyncOperation<BitmapFrame> frameOperation = decoder.GetFrameAsync(frameIndex);
            try
            {
                sourceFrame = await frameOperation;
            }
            finally
            {
                frameOperation.Close();
            }
        }

        Assert.Equal((uint)expectedSourceWidth, sourceFrame?.PixelWidth ?? decoder.PixelWidth);
        Assert.Equal((uint)expectedSourceHeight, sourceFrame?.PixelHeight ?? decoder.PixelHeight);

        var transform = new BitmapTransform
        {
            ScaledWidth = checked((uint)width),
            ScaledHeight = checked((uint)height),
            InterpolationMode = BitmapInterpolationMode.Fant,
        };
        IAsyncOperation<SoftwareBitmap> bitmapOperation = sourceFrame is null
            ? decoder.GetSoftwareBitmapAsync(
                BitmapPixelFormat.Bgra8,
                BitmapAlphaMode.Premultiplied,
                transform,
                ExifOrientationMode.RespectExifOrientation,
                ColorManagementMode.ColorManageToSRgb)
            : sourceFrame.GetSoftwareBitmapAsync(
            BitmapPixelFormat.Bgra8,
            BitmapAlphaMode.Premultiplied,
            transform,
            ExifOrientationMode.RespectExifOrientation,
            ColorManagementMode.ColorManageToSRgb);
        try
        {
            SoftwareBitmap bitmap = await bitmapOperation;
            Assert.Equal(width, bitmap.PixelWidth);
            Assert.Equal(height, bitmap.PixelHeight);
            return bitmap;
        }
        finally
        {
            bitmapOperation.Close();
        }
    }

    private MarkdownLayoutContext CreateContext(
        CanvasDevice device,
        IMarkdownImageResolver resolver,
        MarkdownPerformanceSession session,
        double scale = 1)
    {
        var style = new ElementStyle();
        var styles = new Dictionary<string, ElementStyle>(StringComparer.Ordinal)
        {
            [MarkdownElementKeys.Body] = style,
            [MarkdownElementKeys.ImageCaption] = style,
            [MarkdownElementKeys.ListMarker] = style,
        };
        Color transparent = Color.FromArgb(0, 0, 0, 0);
        var theme = new ThemeSnapshot(
            styles,
            new Dictionary<string, ElementStyleOverride>(),
            transparent,
            transparent,
            transparent,
            transparent,
            isDark: false,
            isHighContrast: false,
            textScaleFactor: 1);
        return new MarkdownLayoutContext(
            device,
            theme,
            new MarkdownSourceMap("owned GIF native first-frame integration"),
            new MarkdownExtensionRegistry(),
            FlowDirection.LeftToRight,
            language: "en-US")
        {
            RenderResources = _renderResources,
            ImageResolver = resolver,
            PerformanceSession = session,
            PerformanceDocumentOwner = new object(),
            RasterizationScale = scale,
            ImageCancellationToken = CancellationToken.None,
        };
    }

    private static async Task LoadImageAsync(ImageBox image, float availableWidth)
    {
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnLoadCompleted(object? sender, LoadCompletedEventArgs args)
        {
            if (!args.IsBitmapOutputRecoverySignal)
                completed.TrySetResult();
        }

        image.LoadCompleted += OnLoadCompleted;
        try
        {
            image.Measure(availableWidth);
            image.EnsureLoading();
            if (image.Bitmap is null || image.GetWorkState().ActiveWorkCount != 0)
                await completed.Task.WaitAsync(TimeSpan.FromSeconds(15));
            await DisplaySizedRasterDecodeTests.WaitUntilAsync(
                () => image.GetWorkState().ActiveWorkCount == 0);
        }
        finally
        {
            image.LoadCompleted -= OnLoadCompleted;
        }
    }

    private sealed class GifResolver(byte[] source, string cacheKey) : IMarkdownImageResolver
    {
        public ValueTask<MarkdownImageResolution> ResolveAsync(
            string requestedSource,
            MarkdownImageResolveContext context,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(MarkdownImageResolution.Resolved(
                new MarkdownImageAsset(source, "image/gif", CacheKey: cacheKey)));
    }
}
