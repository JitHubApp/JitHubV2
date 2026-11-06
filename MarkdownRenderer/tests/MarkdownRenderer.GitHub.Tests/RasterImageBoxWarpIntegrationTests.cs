using System.Runtime.InteropServices.WindowsRuntime;
using MarkdownRenderer.Accessibility;
using MarkdownRenderer.Document;
using MarkdownRenderer.Images;
using MarkdownRenderer.Layout;
using MarkdownRenderer.Layout.Boxes;
using MarkdownRenderer.Parsing;
using MarkdownRenderer.Performance;
using MarkdownRenderer.Theming;
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
/// End-to-end proof for the opted-in static-raster path: admitted JPEG bytes
/// flow through ImageBox, an explicitly software-rendered Win2D device, the
/// raster cache, and eviction. Diagnostic native-path assertions are present
/// only in the diagnostic build; this is not a memory-ceiling or device-matrix
/// qualification.
/// </summary>
[Collection(SharedCanvasBitmapCacheTestCollection.Name)]
public sealed class RasterImageBoxWarpIntegrationTests(ITestOutputHelper output)
{
    private readonly MarkdownRenderResources _renderResources = new();

    private const ushort Orientation = 6;
    private const int SourceWidth = 37;
    private const int SourceHeight = 23;
    private const float DisplayWidth = 17f;
    private const long RasterPixelLimit = 512;
    private const string ImageSource = "https://images.example/native-wic-warp.jpg";

    [Fact]
    public async Task ImageBoxWarpDecodeMatchesWinRtPixelsAndDrainsAcrossBitmapCacheEviction()
    {
        _renderResources.Images.BitmapCache.Clear();
        await DisplaySizedRasterDecodeTests.WaitUntilAsync(() =>
            _renderResources.Images.BitmapCache.GetSnapshot().RetainedBytes == 0);

        MarkdownImagePreparationAdmission processAdmission = _renderResources.Images.Preparation;
        await WaitForProcessAdmissionIdleAsync(processAdmission);
        BitmapOutputAdmission outputAdmission = _renderResources.Images.BitmapOutput;
        BitmapOutputAdmission.Snapshot outputBaseline = outputAdmission.GetSnapshot();
        long livePixelWeightBaseline = _renderResources.Images.BitmapCache.GetSnapshot().LivePixelWeightBytes;

        using var device = new CanvasDevice(forceSoftwareRenderer: true);
        Assert.True(device.ForceSoftwareRenderer,
            "The integration path must execute through Win2D's software renderer; do not silently use hardware fallback.");

        WarpReference reference = await CreateWarpReferenceAsync();
        var resolver = new CountingResolver(reference.Source, $"owned-native-wic-warp-{Guid.NewGuid():N}");
        object documentOwner = new();
        MarkdownPerformanceSession session = CreateExclusiveReservationSession();
        var resolveContext = new MarkdownImageResolveContext(BaseUri: null);
        IMarkdownPerformanceDocumentScope resolverScope = session.OpenDocument(resolver, resolveContext);

        RasterNativeWicDiagnosticSnapshot nativeBefore = _renderResources.Images.NativeWic.GetSnapshot();
        ImageBox? first = null;
        ImageBox? warmCache = null;
        ImageBox? coldBitmapWarmSource = null;
        try
        {
            first = CreateImage(device, resolverScope, session, documentOwner);
            await LoadAndAssertGeometryAsync(first);
            CanvasBitmap firstBitmap = Assert.IsType<CanvasBitmap>(first.Bitmap);
            AssertBitmapMatchesEveryReferenceByte(firstBitmap, reference);
            Assert.Equal(MarkdownImageAccessibilityState.Loaded, first.AccessibilityState);
            Assert.Equal(1, resolver.ResolveCount);
            long sourceCacheBytes = session.GetSnapshot().SourceCacheBytes;
            Assert.True(sourceCacheBytes >= reference.Source.Length,
                "The resolved source must actually reside in the session cache before bitmap eviction.");

            RasterNativeWicDiagnosticSnapshot afterFirst = await AssertFirstPublicationChargesAsync(
                processAdmission, outputAdmission, outputBaseline, reference, nativeBefore);

            // Hold the complete process decode admission while requesting an
            // identical image. A warm bitmap cache hit must finish without
            // needing a decoder slot; a hidden decode/fallback would remain
            // blocked here and time out.
            using (IDisposable decodeBlocker = await processAdmission.EnterAsync(
                new object(), processAdmission.CapacityBytes, exclusive: true, CancellationToken.None))
            {
                warmCache = CreateImage(device, resolverScope, session, documentOwner);
                await LoadAndAssertGeometryAsync(warmCache);
                AssertWarmCacheHitUsedNoDecoder(warmCache, firstBitmap, resolver, processAdmission, afterFirst);
            }

            warmCache.Dispose();
            first.Dispose();
            _renderResources.Images.BitmapCache.Clear();
            await WaitForRetirementBaselineAsync(
                processAdmission, outputAdmission, outputBaseline, livePixelWeightBaseline);
            Assert.Equal(sourceCacheBytes, session.GetSnapshot().SourceCacheBytes);

            // The resolver bytes remain in the same performance session's
            // authorized source cache while the device bitmap cache is empty.
            // Recreating the image must perform a new decode/upload, without
            // another host resolve, and produce the same orientation-aware pixels.
            RasterNativeWicDiagnosticSnapshot beforeSourceWarmDecode = _renderResources.Images.NativeWic.GetSnapshot();
            coldBitmapWarmSource = CreateImage(device, resolverScope, session, documentOwner);
            await LoadAndAssertGeometryAsync(coldBitmapWarmSource);
            AssertSourceCacheHitRedecoded(coldBitmapWarmSource, firstBitmap, reference, resolver, session,
                sourceCacheBytes, beforeSourceWarmDecode);
        }
        finally
        {
            await DrainScenarioAsync(
                [coldBitmapWarmSource, warmCache, first], processAdmission, outputAdmission, outputBaseline,
                livePixelWeightBaseline, resolverScope, session);
        }
    }

    private static async Task<WarpReference> CreateWarpReferenceAsync()
    {
        byte[] source = await OwnedNativeWicGeneralFixtureFactory.CreatePatternedJpegAsync(
            Orientation, SourceWidth, SourceHeight);
        RasterImageBudgetResult budget = RasterImageResourceBudget.Validate(source);
        Assert.True(budget.Accepted, budget.Reason);
        Assert.Equal("JPEG", budget.Format);
        Assert.Equal(SourceWidth, budget.Width);
        Assert.Equal(SourceHeight, budget.Height);
        await AssertEncodedOrientationAsync(source, Orientation);

        var sizing = new RasterImageSizingRequest(
            availableWidth: DisplayWidth,
            rasterizationScale: 1,
            requestedWidth: null,
            requestedHeight: null,
            maximumPixels: RasterPixelLimit);
        (int expectedWidth, int expectedHeight) = sizing.GetSize(SourceHeight, SourceWidth);
        Assert.Equal((17, 28), (expectedWidth, expectedHeight));
        Assert.True((long)expectedWidth * expectedHeight <= RasterPixelLimit);
        byte[] expectedPixels = await DecodeWinRtPreviewAsync(
            source, expectedWidth, expectedHeight, Orientation);
        return new WarpReference(source, expectedPixels, expectedWidth, expectedHeight);
    }

    private static MarkdownPerformanceSession CreateExclusiveReservationSession() =>
        new(MarkdownPerformanceOptions.Progressive with
        {
            // The accepted source is larger than this output cap, making
            // its codec reservation exclusive while preserving a useful
            // orientation-sensitive 17x28 output.
            MaxRasterOutputPixels = RasterPixelLimit,
        });

    private async Task<RasterNativeWicDiagnosticSnapshot> AssertFirstPublicationChargesAsync(
        MarkdownImagePreparationAdmission processAdmission,
        BitmapOutputAdmission outputAdmission,
        BitmapOutputAdmission.Snapshot outputBaseline,
        WarpReference reference,
        RasterNativeWicDiagnosticSnapshot nativeBefore)
    {
        await WaitForProcessAdmissionIdleAsync(processAdmission);
        Assert.Equal(0, processAdmission.ActiveCount);
        Assert.Equal(0, processAdmission.ActiveBytes);
        Assert.Equal(outputBaseline.ChargedBytes + reference.Width * (long)reference.Height * 4,
            outputAdmission.GetSnapshot().ChargedBytes);

        RasterNativeWicDiagnosticSnapshot afterFirst = _renderResources.Images.NativeWic.GetSnapshot();
        Assert.True(afterFirst.NativeStarted > nativeBefore.NativeStarted,
            "The diagnostic build must prove that ImageBox entered the owned native WIC branch.");
        Assert.True(afterFirst.PixelLeaseCompleted > nativeBefore.PixelLeaseCompleted,
            "The diagnostic build must prove that native WIC returned a pixel lease to ImageBox.");
        output.WriteLine($"Native path deltas after ImageBox publication: started=" +
            $"{afterFirst.NativeStarted - nativeBefore.NativeStarted}, " +
            $"pixelLeases={afterFirst.PixelLeaseCompleted - nativeBefore.PixelLeaseCompleted}.");
        return afterFirst;
    }

    private void AssertWarmCacheHitUsedNoDecoder(
        ImageBox warmCache,
        CanvasBitmap firstBitmap,
        CountingResolver resolver,
        MarkdownImagePreparationAdmission processAdmission,
        RasterNativeWicDiagnosticSnapshot afterFirst)
    {
        Assert.Same(firstBitmap, warmCache.Bitmap);
        Assert.Equal(1, resolver.ResolveCount);
        Assert.Equal(1, processAdmission.ActiveCount);
        Assert.Equal(processAdmission.CapacityBytes, processAdmission.ActiveBytes);

        RasterNativeWicDiagnosticSnapshot afterWarmCache = _renderResources.Images.NativeWic.GetSnapshot();
        Assert.Equal(afterFirst.NativeStarted, afterWarmCache.NativeStarted);
        Assert.Equal(afterFirst.PixelLeaseCompleted, afterWarmCache.PixelLeaseCompleted);
    }

    private void AssertSourceCacheHitRedecoded(
        ImageBox coldBitmapWarmSource,
        CanvasBitmap firstBitmapIdentity,
        WarpReference reference,
        CountingResolver resolver,
        MarkdownPerformanceSession session,
        long sourceCacheBytes,
        RasterNativeWicDiagnosticSnapshot beforeSourceWarmDecode)
    {
        CanvasBitmap redecodedBitmap = Assert.IsType<CanvasBitmap>(coldBitmapWarmSource.Bitmap);
        Assert.NotSame(firstBitmapIdentity, redecodedBitmap);
        AssertBitmapMatchesEveryReferenceByte(redecodedBitmap, reference);
        Assert.Equal(1, resolver.ResolveCount);
        Assert.Equal(sourceCacheBytes, session.GetSnapshot().SourceCacheBytes);
        Assert.Equal(MarkdownImageAccessibilityState.Loaded, coldBitmapWarmSource.AccessibilityState);

        RasterNativeWicDiagnosticSnapshot afterSourceWarmDecode = _renderResources.Images.NativeWic.GetSnapshot();
        Assert.True(afterSourceWarmDecode.NativeStarted > beforeSourceWarmDecode.NativeStarted,
            "Evicting the Win2D bitmap must make the ImageBox source-cache hit decode again.");
        Assert.True(afterSourceWarmDecode.PixelLeaseCompleted > beforeSourceWarmDecode.PixelLeaseCompleted,
            "The re-decode must complete native pixel ownership before publication.");
    }

    private async Task DrainScenarioAsync(
        ImageBox?[] imagesInDisposalOrder,
        MarkdownImagePreparationAdmission processAdmission,
        BitmapOutputAdmission outputAdmission,
        BitmapOutputAdmission.Snapshot outputBaseline,
        long livePixelWeightBaseline,
        IMarkdownPerformanceDocumentScope resolverScope,
        MarkdownPerformanceSession session)
    {
        try
        {
            foreach (ImageBox? image in imagesInDisposalOrder)
                image?.Dispose();
            await WaitForImageWorkToDrainAsync(imagesInDisposalOrder);
        }
        finally
        {
            try
            {
                _renderResources.Images.BitmapCache.Clear();
                await WaitForRetirementBaselineAsync(
                    processAdmission, outputAdmission, outputBaseline, livePixelWeightBaseline);
            }
            finally
            {
                try
                {
                    resolverScope.Dispose();
                }
                finally
                {
                    try
                    {
                        await session.DisposeAsync();
                    }
                    finally
                    {
                        await WaitForProcessAdmissionIdleAsync(processAdmission);
                    }
                }
            }
        }
    }

    private sealed record WarpReference(byte[] Source, byte[] Pixels, int Width, int Height);

    private ImageBox CreateImage(
        CanvasDevice device,
        IMarkdownPerformanceDocumentScope resolverScope,
        MarkdownPerformanceSession session,
        object documentOwner)
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
        var context = new MarkdownLayoutContext(
            device,
            theme,
            new MarkdownSourceMap("owned WIC ImageBox WARP integration"),
            new MarkdownExtensionRegistry(),
            FlowDirection.LeftToRight,
            language: "en-US")
        {
            RenderResources = _renderResources,
            ImageResolver = resolverScope,
            PerformanceSession = session,
            PerformanceDocumentOwner = documentOwner,
            RasterizationScale = 1,
            ImageCancellationToken = CancellationToken.None,
        };
        return new ImageBox(context, context.ImageResources, ImageSource, "orientation-six WARP image");
    }

    private static async Task LoadAndAssertGeometryAsync(ImageBox image)
    {
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        EventHandler<LoadCompletedEventArgs> handler = (_, args) =>
        {
            if (!args.IsBitmapOutputRecoverySignal)
                completed.TrySetResult();
        };
        image.LoadCompleted += handler;
        try
        {
            image.Measure(DisplayWidth);
            image.EnsureLoading();
            if (image.Bitmap is null || image.GetWorkState().ActiveWorkCount != 0)
                await completed.Task.WaitAsync(TimeSpan.FromSeconds(20));
        }
        finally
        {
            image.LoadCompleted -= handler;
        }

        await DisplaySizedRasterDecodeTests.WaitUntilAsync(
            () => image.GetWorkState().ActiveWorkCount == 0);
        image.Measure(DisplayWidth);
        Assert.Equal(DisplayWidth, image.MeasuredImageWidth, precision: 2);
        Assert.Equal(DisplayWidth * SourceWidth / SourceHeight,
            image.MeasuredImageHeight, precision: 2);
        Assert.NotNull(image.Bitmap);
    }

    private static void AssertBitmapMatchesEveryReferenceByte(CanvasBitmap bitmap, WarpReference reference)
    {
        byte[] expectedBgra = reference.Pixels;
        int expectedWidth = reference.Width;
        int expectedHeight = reference.Height;
        Assert.Equal((uint)expectedWidth, bitmap.SizeInPixels.Width);
        Assert.Equal((uint)expectedHeight, bitmap.SizeInPixels.Height);
        Color[] actualColors = bitmap.GetPixelColors();
        Assert.Equal(checked(expectedWidth * expectedHeight), actualColors.Length);
        Assert.Equal(checked(actualColors.Length * 4), expectedBgra.Length);
        for (int index = 0; index < actualColors.Length; index++)
        {
            int byteOffset = checked(index * 4);
            Color actual = actualColors[index];
            Assert.Equal(expectedBgra[byteOffset], actual.B);
            Assert.Equal(expectedBgra[byteOffset + 1], actual.G);
            Assert.Equal(expectedBgra[byteOffset + 2], actual.R);
            Assert.Equal(expectedBgra[byteOffset + 3], actual.A);
        }
    }

    private static async Task<byte[]> DecodeWinRtPreviewAsync(
        byte[] source,
        int width,
        int height,
        ushort orientation)
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

        Assert.Equal(checked((uint)(orientation is 5 or 6 or 7 or 8 ? SourceHeight : SourceWidth)),
            decoder.OrientedPixelWidth);
        Assert.Equal(checked((uint)(orientation is 5 or 6 or 7 or 8 ? SourceWidth : SourceHeight)),
            decoder.OrientedPixelHeight);
        var transform = new BitmapTransform
        {
            // WIC applies Fant scaling before applying EXIF's final axis map.
            ScaledWidth = checked((uint)(orientation is 5 or 6 or 7 or 8 ? height : width)),
            ScaledHeight = checked((uint)(orientation is 5 or 6 or 7 or 8 ? width : height)),
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

        IAsyncOperation<BitmapPropertySet> propertyOperation =
            decoder.BitmapProperties.GetPropertiesAsync(["System.Photo.Orientation"]);
        BitmapPropertySet properties;
        try
        {
            properties = await propertyOperation;
        }
        finally
        {
            propertyOperation.Close();
        }

        Assert.True(properties.TryGetValue("System.Photo.Orientation", out BitmapTypedValue? value));
        Assert.Equal(expectedOrientation, Assert.IsType<ushort>(value!.Value));
    }

    private static async Task WaitForProcessAdmissionIdleAsync(
        MarkdownImagePreparationAdmission admission)
    {
        await DisplaySizedRasterDecodeTests.WaitUntilAsync(
            () => admission.ActiveCount == 0 && admission.ActiveBytes == 0 && admission.PendingRequests == 0);
    }

    private async Task WaitForRetirementBaselineAsync(
        MarkdownImagePreparationAdmission processAdmission,
        BitmapOutputAdmission outputAdmission,
        BitmapOutputAdmission.Snapshot outputBaseline,
        long livePixelWeightBaseline)
    {
        await DisplaySizedRasterDecodeTests.WaitUntilAsync(() =>
            processAdmission.ActiveCount == 0 &&
            processAdmission.ActiveBytes == 0 &&
            processAdmission.PendingRequests == 0 &&
            outputAdmission.GetSnapshot().ChargedBytes == outputBaseline.ChargedBytes &&
            _renderResources.Images.BitmapCache.GetSnapshot().LivePixelWeightBytes == livePixelWeightBaseline &&
            _renderResources.Images.BitmapCache.GetSnapshot().RetainedBytes == 0);
    }

    private static async Task WaitForImageWorkToDrainAsync(params ImageBox?[] images)
    {
        await DisplaySizedRasterDecodeTests.WaitUntilAsync(() =>
            images.All(static image => image is null || image.GetWorkState().ActiveWorkCount == 0));
    }

    private sealed class CountingResolver(byte[] bytes, string cacheKey) : IMarkdownImageResolver
    {
        private int _resolveCount;

        internal int ResolveCount => Volatile.Read(ref _resolveCount);

        public ValueTask<MarkdownImageResolution> ResolveAsync(
            string source,
            MarkdownImageResolveContext context,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _resolveCount);
            return ValueTask.FromResult(MarkdownImageResolution.Resolved(
                new MarkdownImageAsset(bytes, "image/jpeg", CacheKey: cacheKey)));
        }
    }
}
