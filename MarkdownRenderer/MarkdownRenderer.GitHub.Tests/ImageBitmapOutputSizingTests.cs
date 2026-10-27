using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics.Tracing;
using MarkdownRenderer.Accessibility;
using MarkdownRenderer.Diagnostics;
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
using Windows.UI;
using Xunit;

namespace MarkdownRenderer.GitHub.Tests;

[Collection(SharedCanvasBitmapCacheTestCollection.Name)]
public sealed class ImageBitmapOutputSizingTests
{
    [Fact]
    public async Task AdmissionBaselineWaitsForPreviousOutputOwnerToDrain()
    {
        BitmapOutputAdmission admission = BitmapOutputAdmission.Shared;
        await CaptureCleanAdmissionBaselineAsync(admission);

        Assert.True(admission.TryReserve(
            8,
            isVisible: true,
            isReplacement: true,
            out BitmapOutputAdmission.Reservation? previousOwner,
            out _));

        Task<long> baselineCapture = CaptureCleanAdmissionBaselineAsync(admission);
        try
        {
            Assert.False(baselineCapture.IsCompleted);
        }
        finally
        {
            previousOwner!.Dispose();
        }

        long baseline = await baselineCapture.WaitAsync(TimeSpan.FromSeconds(20));
        Assert.Equal(0, baseline);
        Assert.Equal(0, admission.GetSnapshot().ActiveBytes);
    }

    [Fact]
    public async Task OffscreenPolicyBoundsRasterEvenWhenDisplaySizedDecodeIsDisabled()
    {
        BitmapOutputAdmission admission = BitmapOutputAdmission.Shared;
        long baseline = await CaptureCleanAdmissionBaselineAsync(admission);
        int sourceWidth = Environment.Is64BitProcess ? 4096 : 2048;
        int sourceHeight = 1025;
        long sourcePixels = (long)sourceWidth * sourceHeight;
        var resolver = new RasterResolver(
            DisplaySizedRasterDecodeTests.CreatePng(sourceWidth, sourceHeight),
            $"bounded-without-display-size-{Guid.NewGuid():N}",
            "image/png");
        using var session = new MarkdownPerformanceSession(
            MarkdownPerformanceOptions.Progressive with
            {
                UseDisplaySizedRasterDecode = false,
                MaxRasterOutputPixels = sourcePixels,
            });
        var image = new ImageBox(
            DisplaySizedRasterDecodeTests.CreateContext(resolver, session, scale: 1),
            "https://images.example/bounded-without-display-size.png",
            string.Empty);

        try
        {
            await LoadAsync(image, sourceWidth);

            Assert.NotNull(image.Bitmap);
            var output = image.Bitmap.SizeInPixels;
            long outputBytes = checked((long)output.Width * output.Height * 4);
            Assert.True(sourcePixels * 4 > admission.MaximumSingleOutputBytes);
            Assert.True(outputBytes <= admission.MaximumSingleOutputBytes);
            Assert.True((long)output.Width * output.Height < sourcePixels);
            Assert.Equal(outputBytes, admission.GetSnapshot().ActiveBytes - baseline);
        }
        finally
        {
            await RetireImageAndWaitForAdmissionAsync(admission, image, baseline, "raster image retirement");
        }
    }

    [Fact]
    public async Task PartialGifChargesCanvasAndTemporaryFrameTogetherThenRetainsOnlyCanvas()
    {
        BitmapOutputAdmission admission = BitmapOutputAdmission.Shared;
        long baseline = await CaptureCleanAdmissionBaselineAsync(admission);
        byte[] gif = CreateSolidGif(
            logicalWidth: 3072,
            logicalHeight: 3072,
            left: 768,
            top: 768,
            frameWidth: 1536,
            frameHeight: 1536);
        var resolver = new RasterResolver(
            gif,
            $"partial-gif-output-peak-{Guid.NewGuid():N}",
            "image/gif");
        using var session = new MarkdownPerformanceSession(
            MarkdownPerformanceOptions.Progressive with { UseDisplaySizedRasterDecode = false });
        var image = new ImageBox(
            DisplaySizedRasterDecodeTests.CreateContext(resolver, session, scale: 1),
            "https://images.example/partial-output.gif",
            string.Empty);
        using var admissionTrace = new RasterAdmissionTrace(admission);

        try
        {
            await LoadAsync(image, availableWidth: 3072);

            Assert.NotNull(image.Bitmap);
            var output = image.Bitmap.SizeInPixels;
            long canvasBytes = checked((long)output.Width * output.Height * 4);
            Assert.True(output.Width < 3072 || output.Height < 3072,
                "The opted-in policy must keep the logical GIF canvas inside its bounded output allowance.");
            Assert.True(admissionTrace.WicDecodeStageCount > 0,
                "The deterministic WIC decode trace should observe the in-flight output reservations.");
            Assert.True(admissionTrace.MaximumActiveBytes - baseline > canvasBytes,
                "The live-output ledger should observe the canvas reservation together with the temporary frame.");
            Assert.Equal(canvasBytes, admission.GetSnapshot().ActiveBytes - baseline);
            Assert.True(canvasBytes <= admission.MaximumSingleOutputBytes);
        }
        finally
        {
            await RetireImageAndWaitForAdmissionAsync(admission, image, baseline, "GIF image retirement");
        }
    }

    [Fact]
    public async Task DisabledOffscreenPolicyKeepsLegacySourceSizedRasterOutput()
    {
        BitmapOutputAdmission admission = BitmapOutputAdmission.Shared;
        long baseline = await CaptureCleanAdmissionBaselineAsync(admission);
        int width = 4096;
        int height = Environment.Is64BitProcess ? 1025 : 513;
        long sourcePixels = checked((long)width * height);
        var resolver = new RasterResolver(
            DisplaySizedRasterDecodeTests.CreatePng(width, height),
            $"legacy-unbounded-pixel-output-{Guid.NewGuid():N}",
            "image/png");
        using var session = new MarkdownPerformanceSession(
            MarkdownPerformanceOptions.Progressive with
            {
                ReleaseOffscreenImageResources = false,
                UseDisplaySizedRasterDecode = false,
                MaxRasterOutputPixels = sourcePixels,
            });
        var image = new ImageBox(
            DisplaySizedRasterDecodeTests.CreateContext(resolver, session, scale: 1),
            "https://images.example/legacy-source-sized.png",
            string.Empty);

        try
        {
            await LoadAsync(image, width);

            Assert.NotNull(image.Bitmap);
            Assert.Equal((uint)width, image.Bitmap.SizeInPixels.Width);
            Assert.Equal((uint)height, image.Bitmap.SizeInPixels.Height);
            Assert.True(sourcePixels * 4 > admission.MaximumSingleOutputBytes);
            Assert.Equal(baseline, admission.GetSnapshot().ActiveBytes);
        }
        finally
        {
            await RetireImageAndWaitForAdmissionAsync(admission, image, baseline, "legacy raster image retirement");
        }
    }

    [Fact]
    public async Task OversizedSvgUsesHeadroomSizedVisibleTilesInsteadOfUnavailableFailure()
    {
        BitmapOutputAdmission admission = BitmapOutputAdmission.Shared;
        long baseline = await CaptureCleanAdmissionBaselineAsync(admission);
        var renderer = new RasterSizingSvgRenderer(new MarkdownSvgDocumentInfo(
            IntrinsicWidthDips: 5000,
            IntrinsicHeightDips: 1000,
            IntrinsicAspectRatio: 5));
        using var session = new MarkdownPerformanceSession(MarkdownPerformanceOptions.Progressive);
        var image = new ImageBox(
            CreateSvgContext(renderer, session, rasterizationScale: 4),
            CreateSvgDataUri(Guid.NewGuid().ToString("N")),
            "large diagram");

        try
        {
            await LoadAsync(image, availableWidth: 5000);

            Assert.True(image.UsesSvgTilesForAutomation);
            Assert.Equal((20_000, 4_000), image.SvgRasterPixelSize);
            Assert.Null(image.SvgFailureReason);
            Assert.Equal(MarkdownImageAccessibilityState.Loading, image.AccessibilityState);
            Assert.Empty(renderer.RenderRequests);

            using var target = new CanvasRenderTarget(CanvasDevice.GetSharedDevice(), 256, 256, 96);
            using (CanvasDrawingSession drawing = target.CreateDrawingSession())
            {
                drawing.Clear(Color.FromArgb(0, 0, 0, 0));
                image.Paint(drawing, new Rect(0, 0, 256, 256));
            }

            await WaitUntilAsync(() => renderer.RenderRequests.TryPeek(out _));
            await WaitUntilAsync(() => image.HasAllSvgTilesForAutomation(0, 0, 0, 0));
            Assert.Contains(renderer.RenderRequests, request => request.TileRegion.HasValue);
            Assert.All(renderer.RenderRequests, request =>
            {
                Assert.True(request.TileRegion.HasValue);
                MarkdownSvgTileRegion tile = request.TileRegion.Value;
                Assert.InRange(tile.Width, 1, 1024);
                Assert.InRange(tile.Height, 1, 1024);
                Assert.True(checked((long)tile.Width * tile.Height * 4) <= admission.MaximumSingleOutputBytes);
            });
            Assert.Equal(MarkdownImageAccessibilityState.Loaded, image.AccessibilityState);
            Assert.Null(image.SvgFailureReason);
            Assert.True(admission.GetSnapshot().ActiveBytes - baseline <= admission.CapacityBytes);
        }
        finally
        {
            await RetireImageAndWaitForAdmissionAsync(admission, image, baseline, "SVG tile image retirement");
        }
    }

    private static async Task<long> CaptureCleanAdmissionBaselineAsync(BitmapOutputAdmission admission)
    {
        SharedCanvasBitmapCache.Clear();
        await WaitForAdmissionActiveBytesAsync(admission, 0, "prior bitmap retirement");
        BitmapOutputAdmission.Snapshot snapshot = admission.GetSnapshot();
        Assert.Equal(0, snapshot.ActiveBytes);
        return snapshot.ActiveBytes;
    }

    private static async Task WaitForAdmissionActiveBytesAsync(
        BitmapOutputAdmission admission,
        long expectedActiveBytes,
        string phase,
        ImageBox? image = null)
    {
        try
        {
            await WaitUntilAsync(() => admission.GetSnapshot().ActiveBytes == expectedActiveBytes);
        }
        catch (OperationCanceledException exception)
        {
            throw CreateAdmissionDrainTimeout(admission, expectedActiveBytes, phase, image, exception);
        }
    }

    private static async Task RetireImageAndWaitForAdmissionAsync(
        BitmapOutputAdmission admission,
        ImageBox image,
        long expectedActiveBytes,
        string phase)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        Exception? retirementFailure = null;
        bool retirementTimedOut = false;
        try
        {
            image.Dispose();
            await image.RetirementTask.WaitAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            retirementTimedOut = true;
        }
        catch (Exception exception)
        {
            retirementFailure = exception;
        }
        finally
        {
            // Clear even when retirement faults so the cache cannot leak into
            // later tests. Re-throw the retirement fault below as the primary failure.
            SharedCanvasBitmapCache.Clear();
        }

        if (retirementFailure is not null)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(retirementFailure).Throw();

        if (retirementTimedOut)
        {
            throw CreateAdmissionDrainTimeout(
                admission,
                expectedActiveBytes,
                $"{phase} retirement task",
                image,
                new OperationCanceledException("The shared cleanup deadline expired while awaiting image retirement."));
        }

        try
        {
            while (admission.GetSnapshot().ActiveBytes != expectedActiveBytes)
                await Task.Delay(10, timeout.Token);
        }
        catch (OperationCanceledException exception) when (timeout.IsCancellationRequested)
        {
            throw CreateAdmissionDrainTimeout(admission, expectedActiveBytes, phase, image, exception);
        }
    }

    private static TimeoutException CreateAdmissionDrainTimeout(
        BitmapOutputAdmission admission,
        long expectedActiveBytes,
        string phase,
        ImageBox? image,
        Exception exception)
    {
        BitmapOutputAdmission.Snapshot snapshot = admission.GetSnapshot();
        string imageState = image is null
            ? "not supplied"
            : $"active work={image.ActiveImageWorkCountForTests}, " +
                $"lifetime disposed={image.ImageLifetimeDisposedForTests}, " +
                $"retirement task={image.RetirementTask.Status}, " +
                $"SVG tiles={image.UsesSvgTilesForAutomation}, " +
                $"SVG raster pixels={image.SvgRasterPixelSize.Width}x{image.SvgRasterPixelSize.Height}";
        return new TimeoutException(
            $"Bitmap output admission did not drain during {phase}. " +
            $"Expected active bytes={expectedActiveBytes}; actual active bytes={snapshot.ActiveBytes}, " +
            $"charged bytes={snapshot.ChargedBytes}, uncertain bytes={snapshot.UncertainBytes}, " +
            $"active reservations={snapshot.ActiveReservationCount}, " +
            $"uncertain reservations={snapshot.UncertainReservationCount}, waiters={snapshot.WaiterCount}; " +
            $"cache retained bytes={SharedCanvasBitmapCache.RetainedBytesForTests}, " +
            $"live bitmap pixel bytes={SharedCanvasBitmapCache.LiveBitmapPixelWeightBytesForTests}, " +
            $"uncertain bitmap pixel bytes={SharedCanvasBitmapCache.UncertainBitmapDisposePixelWeightBytesForTests}, " +
            $"cache disposal failures={SharedCanvasBitmapCache.CacheDisposalFailureCountForTests}, " +
            $"last cache disposal HResult=0x{SharedCanvasBitmapCache.LastCacheDisposalFailureHResultForTests:X8}; " +
            $"image state: {imageState}.",
            exception);
    }

    private static async Task LoadAsync(ImageBox image, float availableWidth)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        EventHandler<LoadCompletedEventArgs> handler = (_, _) => completion.TrySetResult();
        image.LoadCompleted += handler;
        try
        {
            image.Measure(availableWidth);
            image.EnsureLoading();
            await completion.Task.WaitAsync(TimeSpan.FromSeconds(20));
        }
        finally
        {
            image.LoadCompleted -= handler;
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        while (!condition())
            await Task.Delay(10, timeout.Token);
    }

    private static MarkdownLayoutContext CreateSvgContext(
        IMarkdownSvgRenderer renderer,
        MarkdownPerformanceSession session,
        double rasterizationScale)
    {
        var body = new ElementStyle { Foreground = Color.FromArgb(255, 12, 34, 56) };
        var styles = new Dictionary<string, ElementStyle>(StringComparer.Ordinal)
        {
            [MarkdownElementKeys.Body] = body,
            [MarkdownElementKeys.ImageCaption] = body,
            [MarkdownElementKeys.ListMarker] = body,
        };
        Color transparent = Color.FromArgb(0, 0, 0, 0);
        var theme = new ThemeSnapshot(styles, new Dictionary<string, ElementStyleOverride>(),
            transparent, transparent, transparent, transparent,
            isDark: false, isHighContrast: false, textScaleFactor: 1);
        return new MarkdownLayoutContext(
            CanvasDevice.GetSharedDevice(),
            theme,
            new MarkdownSourceMap(string.Empty),
            new MarkdownExtensionRegistry(),
            FlowDirection.LeftToRight,
            language: "en-US")
        {
            SvgRenderer = renderer,
            PerformanceSession = session,
            RasterizationScale = rasterizationScale,
            ImageCancellationToken = CancellationToken.None,
        };
    }

    private static string CreateSvgDataUri(string title)
    {
        string svg = $"<svg xmlns='http://www.w3.org/2000/svg' width='5000' height='1000'>" +
            $"<title>{title}</title><rect width='5000' height='1000'/></svg>";
        return "data:image/svg+xml," + Uri.EscapeDataString(svg);
    }

    private static byte[] CreateSolidGif(
        int logicalWidth,
        int logicalHeight,
        int left,
        int top,
        int frameWidth,
        int frameHeight)
    {
        using var gif = new MemoryStream();
        gif.Write("GIF89a"u8);
        WriteUInt16(gif, logicalWidth);
        WriteUInt16(gif, logicalHeight);
        gif.Write([0xF0, 0, 0]); // Two-entry global color table.
        gif.Write([0, 0, 0, 255, 255, 255]);
        gif.WriteByte(0x2C);
        WriteUInt16(gif, left);
        WriteUInt16(gif, top);
        WriteUInt16(gif, frameWidth);
        WriteUInt16(gif, frameHeight);
        gif.WriteByte(0); // No local table, interlace, or sort flag.
        gif.WriteByte(2); // LZW minimum code size.

        using var compressed = new MemoryStream();
        int bitBuffer = 0;
        int bitCount = 0;
        void WriteCode(int code)
        {
            bitBuffer |= code << bitCount;
            bitCount += 3;
            while (bitCount >= 8)
            {
                compressed.WriteByte((byte)bitBuffer);
                bitBuffer >>= 8;
                bitCount -= 8;
            }
        }

        // Reset the tiny LZW table for each literal. This creates a valid,
        // deterministic solid frame without carrying a compressor dependency.
        int pixelCount = checked(frameWidth * frameHeight);
        for (int i = 0; i < pixelCount; i++)
        {
            WriteCode(4); // clear
            WriteCode(1); // white palette index
        }
        WriteCode(5); // end of information
        if (bitCount > 0)
            compressed.WriteByte((byte)bitBuffer);

        byte[] data = compressed.ToArray();
        for (int offset = 0; offset < data.Length;)
        {
            int count = global::System.Math.Min(255, data.Length - offset);
            gif.WriteByte((byte)count);
            gif.Write(data.AsSpan(offset, count));
            offset += count;
        }
        gif.WriteByte(0);
        gif.WriteByte(0x3B);
        return gif.ToArray();
    }

    private static void WriteUInt16(Stream stream, int value)
    {
        Span<byte> bytes = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(bytes, checked((ushort)value));
        stream.Write(bytes);
    }

    private sealed class RasterResolver(byte[] bytes, string cacheKey, string mediaType) : IMarkdownImageResolver
    {
        public ValueTask<MarkdownImageResolution> ResolveAsync(
            string source,
            MarkdownImageResolveContext context,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(MarkdownImageResolution.Resolved(
                new MarkdownImageAsset(bytes, mediaType, CacheKey: cacheKey)));
    }

    private sealed class RasterSizingSvgRenderer(MarkdownSvgDocumentInfo info) : IMarkdownSvgRenderer
    {
        public long CacheGeneration => 0;

        internal ConcurrentQueue<MarkdownSvgRenderRequest> RenderRequests { get; } = new();

        public MarkdownSvgSourcePreparation PrepareSource(byte[] source, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return MarkdownSvgSourcePreparation.Admit(source);
        }

        public ValueTask<IMarkdownSvgDocument> OpenAsync(
            MarkdownSvgOpenRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult<IMarkdownSvgDocument>(new RasterSizingSvgDocument(info, RenderRequests));
        }
    }

    private sealed class RasterSizingSvgDocument(
        MarkdownSvgDocumentInfo info,
        ConcurrentQueue<MarkdownSvgRenderRequest> requests) : IMarkdownSvgDocument
    {
        public MarkdownSvgDocumentInfo Info { get; } = info;

        public ValueTask<MarkdownSvgRaster> RenderAsync(
            MarkdownSvgRenderRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            requests.Enqueue(request);
            int width = request.TileRegion?.Width ?? request.TargetWidthPixels;
            int height = request.TileRegion?.Height ?? request.TargetHeightPixels;
            int length = checked(width * height * 4);
            IMemoryOwner<byte> owner = MemoryPool<byte>.Shared.Rent(length);
            owner.Memory.Span[..length].Clear();
            return ValueTask.FromResult(new MarkdownSvgRaster(
                owner,
                length,
                width,
                height,
                checked(width * 4),
                request.PixelFormat));
        }

        public void Dispose() { }
    }

    private sealed class RasterAdmissionTrace(BitmapOutputAdmission admission) : EventListener
    {
        private long _maximumActiveBytes;
        private int _wicDecodeStageCount;

        internal long MaximumActiveBytes => Interlocked.Read(ref _maximumActiveBytes);
        internal int WicDecodeStageCount => Volatile.Read(ref _wicDecodeStageCount);

        protected override void OnEventSourceCreated(EventSource eventSource)
        {
            if (eventSource.Name == "MarkdownRenderer-RasterPreparation")
                EnableEvents(eventSource, EventLevel.Informational);
        }

        protected override void OnEventWritten(EventWrittenEventArgs eventData)
        {
            if (eventData.EventSource.Name != "MarkdownRenderer-RasterPreparation" ||
                eventData.EventId != 2 ||
                eventData.Payload is not { Count: 3 } payload ||
                payload[1] is not long stage ||
                stage != (long)MarkdownRasterPreparationStage.WicPixelDecode)
            {
                return;
            }

            long activeBytes = admission.GetSnapshot().ActiveBytes;
            long observed;
            while (activeBytes > (observed = Interlocked.Read(ref _maximumActiveBytes)) &&
                   Interlocked.CompareExchange(ref _maximumActiveBytes, activeBytes, observed) != observed)
            {
            }
            Interlocked.Increment(ref _wicDecodeStageCount);
        }
    }
}
