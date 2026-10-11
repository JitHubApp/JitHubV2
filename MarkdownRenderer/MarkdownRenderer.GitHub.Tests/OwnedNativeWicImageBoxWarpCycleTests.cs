using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
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
using Windows.UI;
using Xunit;
using Xunit.Abstractions;

namespace MarkdownRenderer.GitHub.Tests;

/// <summary>
/// Exercises the shipping ImageBox raster path through WARP, publication,
/// bitmap-cache eviction, and source-cache reuse over a fixed-size format
/// matrix. Process memory and handle samples are observations, not limits.
/// </summary>
[Collection(WicRasterMemoryQualificationCollection.CollectionName)]
public sealed class OwnedNativeWicImageBoxWarpCycleTests(ITestOutputHelper output)
{
    private const int CycleCount = 100;
    private const float DisplayWidth = 17f;
    private const long RasterPixelLimit = 851;
    private const string WebpFixtureResource =
        "MarkdownRenderer.GitHub.Tests.Fixtures.oversized-static.webp";
    private const string WebpFixtureSha256 =
        "6FEB51F257EF7FAC608FFA54E0D942EA4FE4DE11DECD165A12EA254C3886DB85";

    [Fact]
    [Trait("Category", "NativeMemoryQualification")]
    public async Task ImageBoxWarpCyclesAllQualifiedStaticRasterVariantsAndDrainsEachOutput()
    {
        RasterFixture[] fixtures = [];
        MarkdownPerformanceSession? session = null;
        IMarkdownPerformanceDocumentScope? documentScope = null;
        MarkdownImagePreparationAdmission processAdmission = MarkdownImagePreparationAdmission.Shared;
        BitmapOutputAdmission outputAdmission = BitmapOutputAdmission.Shared;
        long livePixelWeightBefore = 0;
        long cacheRetainedBefore = 0;
        bool globalBaselinesInitialized = false;
        bool sharedCacheTouched = false;

        try
        {
            fixtures = await CreateFixturesAsync();

            SharedCanvasBitmapCache.Clear();
            sharedCacheTouched = true;
            await WaitUntilAsync(() => SharedCanvasBitmapCache.RetainedBytesForTests == 0);
            await WaitForProcessAdmissionIdleAsync(processAdmission);
            livePixelWeightBefore = SharedCanvasBitmapCache.LiveBitmapPixelWeightBytesForTests;
            cacheRetainedBefore = SharedCanvasBitmapCache.RetainedBytesForTests;
            globalBaselinesInitialized = true;

            foreach (RasterFixture fixture in fixtures)
                await PrepareFixtureAsync(fixture);

            using var device = new CanvasDevice(forceSoftwareRenderer: true);
            Assert.True(device.ForceSoftwareRenderer,
                "The test must use Win2D's software renderer and must not silently fall back to hardware.");

            session = new MarkdownPerformanceSession(
                MarkdownPerformanceOptions.Progressive with
                {
                    // Keep every expected output small and deterministic, including
                    // the pinned oversized WebP source. This limits ImageBox output,
                    // not WIC source/intermediate memory.
                    MaxRasterOutputPixels = RasterPixelLimit,
                });
            var resolver = new FixtureResolver(fixtures);
            documentScope = session.OpenDocument(
                resolver,
                new MarkdownImageResolveContext(BaseUri: null));
            object documentOwner = new();

            // Load each variant once so the resolver/source cache, decoder code paths,
            // and this fixed output shape are warm before collecting the 100-cycle
            // logical-retirement observations.
            foreach (RasterFixture fixture in fixtures)
            {
                await RunImageCycleAsync(
                    device, documentScope, session, documentOwner, fixture,
                    processAdmission, outputAdmission, livePixelWeightBefore, cacheRetainedBefore);
            }

            long sourceCacheBytes = session.GetSnapshot().SourceCacheBytes;
            Assert.True(sourceCacheBytes >= fixtures.Sum(static fixture => (long)fixture.Source.Length));
            Assert.Equal(fixtures.Length, resolver.TotalCalls);
            Assert.All(fixtures, fixture => Assert.Equal(1, resolver.GetCallCount(fixture.SourceUri)));
            Assert.Equal(livePixelWeightBefore, SharedCanvasBitmapCache.LiveBitmapPixelWeightBytesForTests);
            Assert.Equal(cacheRetainedBefore, SharedCanvasBitmapCache.RetainedBytesForTests);
            await WaitForProcessAdmissionIdleAsync(processAdmission);

            using Process process = Process.GetCurrentProcess();
            process.Refresh();
            long baselinePrivateBytes = process.PrivateMemorySize64;
            int baselineHandleCount = process.HandleCount;
            long maximumPrivateBytes = baselinePrivateBytes;
            int maximumHandleCount = baselineHandleCount;
            var observations = new StringBuilder("cycle,fixture,privateBytesAfterRetirement,handleCountAfterRetirement\n");
#if MARKDOWN_LAYOUT_DIAGNOSTICS
            RasterNativeWicDiagnosticSnapshot diagnosticBeforeCycles = RasterNativeWicDiagnostics.GetSnapshot();
#endif

            // A fixed coprime stride interleaves codecs/orientations instead of
            // grouping all samples from one WIC factory/codec together.
            const int InterleaveStride = 13;
            Assert.Equal(1, GreatestCommonDivisor(InterleaveStride, fixtures.Length));
            for (int cycle = 0; cycle < CycleCount; cycle++)
            {
                int fixtureIndex = (cycle * InterleaveStride) % fixtures.Length;
                RasterFixture fixture = fixtures[fixtureIndex];
                await RunImageCycleAsync(
                    device, documentScope, session, documentOwner, fixture,
                    processAdmission, outputAdmission, livePixelWeightBefore, cacheRetainedBefore);

                Assert.Equal(sourceCacheBytes, session.GetSnapshot().SourceCacheBytes);
                Assert.Equal(1, resolver.GetCallCount(fixture.SourceUri));
                process.Refresh();
                long sampledPrivateBytes = process.PrivateMemorySize64;
                int sampledHandleCount = process.HandleCount;
                maximumPrivateBytes = System.Math.Max(maximumPrivateBytes, sampledPrivateBytes);
                maximumHandleCount = System.Math.Max(maximumHandleCount, sampledHandleCount);
                observations.Append(cycle + 1).Append(',')
                    .Append(fixture.Name).Append(',')
                    .Append(sampledPrivateBytes).Append(',')
                    .Append(sampledHandleCount).AppendLine();
            }

            Assert.Equal(fixtures.Length, resolver.TotalCalls);
            Assert.All(fixtures, fixture => Assert.Equal(1, resolver.GetCallCount(fixture.SourceUri)));
            Assert.Equal(livePixelWeightBefore, SharedCanvasBitmapCache.LiveBitmapPixelWeightBytesForTests);
            Assert.Equal(cacheRetainedBefore, SharedCanvasBitmapCache.RetainedBytesForTests);
            await WaitForProcessAdmissionIdleAsync(processAdmission);
            Assert.Equal(0, processAdmission.PendingRequests);

            process.Refresh();
            long finalPrivateBytes = process.PrivateMemorySize64;
            int finalHandleCount = process.HandleCount;
            output.WriteLine(
                $"Product ImageBox/WARP raster-cycle diagnostic: warmups={fixtures.Length}; " +
                $"cycles={CycleCount}; variants={fixtures.Length}; sourceCacheBytes={sourceCacheBytes}; " +
                $"privateBytesBaseline={baselinePrivateBytes}; privateBytesPeakAtRetirement={maximumPrivateBytes}; " +
                $"privateBytesFinal={finalPrivateBytes}; handleCountBaseline={baselineHandleCount}; " +
                $"handleCountPeakAtRetirement={maximumHandleCount}; handleCountFinal={finalHandleCount}. " +
                "Values are process-wide observations after logical output retirement, not bounds on " +
                "codec intermediates, private bytes, RSS, or device memory. No forced GC is used.");
            output.WriteLine(observations.ToString());
#if MARKDOWN_LAYOUT_DIAGNOSTICS
            RasterNativeWicDiagnosticSnapshot diagnosticAfterCycles = RasterNativeWicDiagnostics.GetSnapshot();
            Assert.Equal(CycleCount, diagnosticAfterCycles.NativeStarted - diagnosticBeforeCycles.NativeStarted);
            Assert.Equal(CycleCount,
                diagnosticAfterCycles.PixelLeaseCompleted - diagnosticBeforeCycles.PixelLeaseCompleted);
            output.WriteLine(
                $"Diagnostic native branch proof for measured cycles: started=" +
                $"{diagnosticAfterCycles.NativeStarted - diagnosticBeforeCycles.NativeStarted}; " +
                $"pixelLeases={diagnosticAfterCycles.PixelLeaseCompleted - diagnosticBeforeCycles.PixelLeaseCompleted}.");
#else
            output.WriteLine(
                "Native branch identity counters are diagnostics-build-only; the non-diagnostic run " +
                "proves the WARP ImageBox output/lifetime contract but does not claim branch identity.");
#endif
        }
        finally
        {
            try
            {
                if (sharedCacheTouched)
                {
                    SharedCanvasBitmapCache.Clear();
                    if (globalBaselinesInitialized)
                    {
                        await WaitUntilAsync(() =>
                            SharedCanvasBitmapCache.RetainedBytesForTests == cacheRetainedBefore &&
                            SharedCanvasBitmapCache.LiveBitmapPixelWeightBytesForTests == livePixelWeightBefore);
                    }
                    else
                    {
                        await WaitUntilAsync(() => SharedCanvasBitmapCache.RetainedBytesForTests == 0);
                    }
                }
            }
            finally
            {
                try
                {
                    documentScope?.Dispose();
                }
                finally
                {
                    try
                    {
                        if (session is not null)
                            await session.DisposeAsync();
                    }
                    finally
                    {
                        await WaitForProcessAdmissionIdleAsync(processAdmission);
                        foreach (RasterFixture fixture in fixtures)
                        {
                            Array.Clear(fixture.Source);
                            if (fixture.ExpectedPixels is not null)
                                Array.Clear(fixture.ExpectedPixels);
                        }
                    }
                }
            }
        }
    }

    private static async Task RunImageCycleAsync(
        CanvasDevice device,
        IMarkdownPerformanceDocumentScope documentScope,
        MarkdownPerformanceSession session,
        object documentOwner,
        RasterFixture fixture,
        MarkdownImagePreparationAdmission processAdmission,
        BitmapOutputAdmission outputAdmission,
        long livePixelWeightBaseline,
        long cacheRetainedBaseline)
    {
        Assert.NotNull(fixture.ExpectedPixels);
        long outputChargeBaseline = outputAdmission.GetSnapshot().ChargedBytes;
        Assert.Equal(livePixelWeightBaseline, SharedCanvasBitmapCache.LiveBitmapPixelWeightBytesForTests);
        Assert.Equal(cacheRetainedBaseline, SharedCanvasBitmapCache.RetainedBytesForTests);
        await WaitForProcessAdmissionIdleAsync(processAdmission);
#if MARKDOWN_LAYOUT_DIAGNOSTICS
        RasterNativeWicDiagnosticSnapshot before = RasterNativeWicDiagnostics.GetSnapshot();
#endif
        ImageBox? image = null;
        CanvasBitmap? bitmap = null;
        try
        {
            image = CreateImage(device, documentScope, session, documentOwner, fixture.SourceUri);
            await LoadAndAssertGeometryAsync(image, fixture);
            bitmap = Assert.IsType<CanvasBitmap>(image.Bitmap);
            Assert.Equal((uint)fixture.OutputWidth, bitmap.SizeInPixels.Width);
            Assert.Equal((uint)fixture.OutputHeight, bitmap.SizeInPixels.Height);
            byte[] actualPixels = bitmap.GetPixelBytes();
            try
            {
                Assert.Equal(fixture.ExpectedPixels, actualPixels);
            }
            finally
            {
                Array.Clear(actualPixels);
            }
            Assert.Equal(MarkdownImageAccessibilityState.Loaded, image.AccessibilityState);

            await WaitForProcessAdmissionIdleAsync(processAdmission);
            Assert.Equal(outputChargeBaseline + checked((long)fixture.OutputWidth * fixture.OutputHeight * 4),
                outputAdmission.GetSnapshot().ChargedBytes);

#if MARKDOWN_LAYOUT_DIAGNOSTICS
            RasterNativeWicDiagnosticSnapshot after = RasterNativeWicDiagnostics.GetSnapshot();
            Assert.Equal(before.NativeStarted + 1, after.NativeStarted);
            Assert.Equal(before.PixelLeaseCompleted + 1, after.PixelLeaseCompleted);
#endif
        }
        finally
        {
            try
            {
                bitmap = null;
                image?.Dispose();
                ImageBox? retiredImage = image;
                if (retiredImage is not null)
                    await WaitUntilAsync(() => retiredImage.ActiveImageWorkCountForTests == 0);
            }
            finally
            {
                SharedCanvasBitmapCache.Clear();
                await WaitUntilAsync(() =>
                    processAdmission.ActiveCount == 0 &&
                    processAdmission.ActiveBytes == 0 &&
                    processAdmission.PendingRequests == 0 &&
                    outputAdmission.GetSnapshot().ChargedBytes == outputChargeBaseline &&
                    SharedCanvasBitmapCache.LiveBitmapPixelWeightBytesForTests == livePixelWeightBaseline &&
                    SharedCanvasBitmapCache.RetainedBytesForTests == cacheRetainedBaseline);
            }
        }
    }

    private static RasterFixture[] BuildFixtureSources(IReadOnlyList<byte[]> sources) =>
    [
        new("png-alpha", "image/png", sources[0], orientation: 1),
        new("png-palette4-alpha", "image/png", sources[1], orientation: 1),
        new("png-gray8", "image/png", sources[2], orientation: 1),
        new("png-gray-alpha8", "image/png", sources[3], orientation: 1),
        new("png-gray16", "image/png", sources[4], orientation: 1),
        new("png-rgba16", "image/png", sources[5], orientation: 1),
        new("png-icc-gamma18", "image/png", sources[6], orientation: 1),
        new("bmp-standard", "image/bmp", sources[7], orientation: 1),
        new("bmp-wide", "image/bmp", sources[8], orientation: 1),
        new("bmp-portrait", "image/bmp", sources[9], orientation: 1),
        new("jpeg-exif1", "image/jpeg", sources[10], orientation: 1),
        new("jpeg-exif2", "image/jpeg", sources[11], orientation: 2),
        new("jpeg-exif3", "image/jpeg", sources[12], orientation: 3),
        new("jpeg-exif4", "image/jpeg", sources[13], orientation: 4),
        new("jpeg-exif5", "image/jpeg", sources[14], orientation: 5),
        new("jpeg-exif6", "image/jpeg", sources[15], orientation: 6),
        new("jpeg-exif7", "image/jpeg", sources[16], orientation: 7),
        new("jpeg-exif8", "image/jpeg", sources[17], orientation: 8),
        new("jpeg-wide", "image/jpeg", sources[18], orientation: 1),
        new("jpeg-portrait", "image/jpeg", sources[19], orientation: 1),
        new("webp-pinned-oversized", "image/webp", sources[20], orientation: 1),
        new("png-gama-chrm-only", "image/png", sources[21], orientation: 1),
        new("png-srgb-chunk", "image/png", sources[22], orientation: 1),
        new("jpeg-icc-app2-gamma18", "image/jpeg", sources[23], orientation: 1),
        new("webp-iccp-gamma18", "image/webp", sources[24], orientation: 1),
    ];

    private static async Task<RasterFixture[]> CreateFixturesAsync()
    {
        var sources = new List<byte[]>(25)
        {
            await OwnedNativeWicGeneralFixtureFactory.CreatePatternedPngAsync(),
            OwnedNativeWicFormatFixtureFactory.CreatePalette4WithTransparencyPng(),
            OwnedNativeWicFormatFixtureFactory.CreateGrayscale8Png(),
            OwnedNativeWicFormatFixtureFactory.CreateGrayscaleAlpha8Png(),
            OwnedNativeWicFormatFixtureFactory.CreateGrayscale16Png(),
            OwnedNativeWicFormatFixtureFactory.CreateRgba16Png(),
            await OwnedNativeWicIccFixtureFactory.CreatePatternedPngWithGamma18ProfileAsync(),
            await OwnedNativeWicGeneralFixtureFactory.CreatePatternedBmpAsync(),
            await OwnedNativeWicGeneralFixtureFactory.CreatePatternedBmpAsync(width: 168, height: 24),
            await OwnedNativeWicGeneralFixtureFactory.CreatePatternedBmpAsync(width: 24, height: 168),
        };

        for (ushort orientation = 1; orientation <= 8; orientation++)
        {
            sources.Add(await OwnedNativeWicGeneralFixtureFactory.CreatePatternedJpegAsync(orientation));
        }
        sources.Add(await OwnedNativeWicGeneralFixtureFactory.CreatePatternedJpegAsync(
            orientation: 1, width: 168, height: 24));
        sources.Add(await OwnedNativeWicGeneralFixtureFactory.CreatePatternedJpegAsync(
            orientation: 1, width: 24, height: 168));
        sources.Add(await ReadPinnedWebpFixtureAsync());

        sources.Add(OwnedNativeWicIccFixtureFactory.EmbedGammaAndChromaticitiesInPng(
            await OwnedNativeWicGeneralFixtureFactory.CreatePatternedPngAsync(
                width: 37, height: 23, withAlphaEdges: true)));
        sources.Add(OwnedNativeWicIccFixtureFactory.EmbedSrgbChunkInPng(
            await OwnedNativeWicGeneralFixtureFactory.CreatePatternedPngAsync(
                width: 37, height: 23, withAlphaEdges: true)));

        byte[] profile = OwnedNativeWicIccFixtureFactory.CreateGamma18MatrixTrcProfile();
        try
        {
            sources.Add(OwnedNativeWicIccFixtureFactory.EmbedProfileInJpeg(
                await OwnedNativeWicGeneralFixtureFactory.CreatePatternedJpegAsync(
                    orientation: 1, width: 37, height: 23),
                profile));

            byte[] webp = await ReadPinnedWebpFixtureAsync();
            try
            {
                RasterImageBudgetResult webpBudget = RasterImageResourceBudget.Validate(webp);
                Assert.True(webpBudget.Accepted || webpBudget.CanRenderStaticPreview, webpBudget.Reason);
                Assert.Equal(1, webpBudget.FrameCount);
                sources.Add(OwnedNativeWicIccFixtureFactory.EmbedProfileInSimpleWebp(
                    webp, profile, webpBudget.Width, webpBudget.Height));
            }
            finally
            {
                Array.Clear(webp);
            }
        }
        finally
        {
            Array.Clear(profile);
        }

        Assert.Equal(25, sources.Count);
        return BuildFixtureSources(sources);
    }

    private static async Task<byte[]> ReadPinnedWebpFixtureAsync()
    {
        using Stream fixture = typeof(OwnedNativeWicLifetimeQualificationTests).Assembly
            .GetManifestResourceStream(WebpFixtureResource)
            ?? throw new InvalidOperationException("The pinned oversized static WebP fixture is missing.");
        using var bytes = new MemoryStream();
        await fixture.CopyToAsync(bytes);
        byte[] source = bytes.ToArray();
        Assert.Equal(51_112, source.Length);
        Assert.Equal(WebpFixtureSha256, Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(source)));
        return source;
    }

    private static async Task PrepareFixtureAsync(RasterFixture fixture)
    {
        RasterImageBudgetResult budget = RasterImageResourceBudget.Validate(fixture.Source);
        Assert.True(budget.Accepted || budget.CanRenderStaticPreview,
            $"{fixture.Name} was not admitted for rendering: {budget.Reason}");
        Assert.Equal(fixture.MimeType switch
        {
            "image/png" => "PNG",
            "image/bmp" => "BMP",
            "image/jpeg" => "JPEG",
            "image/webp" => "WEBP",
            _ => throw new InvalidOperationException("The cycle matrix contains an unexpected MIME type."),
        }, budget.Format);

        bool swapsAxes = fixture.Orientation is >= 5 and <= 8;
        int intrinsicWidth = swapsAxes ? budget.Height : budget.Width;
        int intrinsicHeight = swapsAxes ? budget.Width : budget.Height;
        var sizing = new RasterImageSizingRequest(
            DisplayWidth,
            rasterizationScale: 1,
            requestedWidth: null,
            requestedHeight: null,
            maximumPixels: RasterPixelLimit);
        (int width, int height) = sizing.GetSize(intrinsicWidth, intrinsicHeight);
        Assert.True(RasterImageDecodePlan.TryCreate(fixture.Source, budget, sizing, out RasterImageDecodePlan plan),
            $"{fixture.Name} did not produce a bounded native decode plan.");
        Assert.Equal(fixture.Orientation, plan.Orientation);
        Assert.Equal(width, plan.OutputWidth);
        Assert.Equal(height, plan.OutputHeight);
        Assert.True(RasterNativeWicDecoder.IsQualifiedPlan(plan),
            $"{fixture.Name} is outside the current native-decoder allowlist and must not silently use WinRT.");
        Assert.InRange(checked((long)width * height), 1, RasterPixelLimit);

        fixture.OutputWidth = width;
        fixture.OutputHeight = height;
        fixture.Budget = budget;
        fixture.ExpectedPixels = await OwnedNativeWicGeneralFidelityQualificationTests.DecodeWinRtPreviewAsync(
            fixture.Source, width, height, fixture.Orientation);
    }

    private static async Task LoadAndAssertGeometryAsync(ImageBox image, RasterFixture fixture)
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
            if (image.Bitmap is null || image.ActiveImageWorkCountForTests != 0)
                await completed.Task.WaitAsync(TimeSpan.FromSeconds(20));
        }
        finally
        {
            image.LoadCompleted -= handler;
        }

        await WaitUntilAsync(() => image.ActiveImageWorkCountForTests == 0);
        image.Measure(DisplayWidth);
        bool swapsAxes = fixture.Orientation is >= 5 and <= 8;
        double intrinsicWidth = swapsAxes ? fixture.Budget.Height : fixture.Budget.Width;
        double intrinsicHeight = swapsAxes ? fixture.Budget.Width : fixture.Budget.Height;
        Assert.Equal(DisplayWidth, image.MeasuredImageWidth, precision: 2);
        Assert.Equal(DisplayWidth * intrinsicHeight / intrinsicWidth, image.MeasuredImageHeight, precision: 1);
        Assert.NotNull(image.Bitmap);
    }

    private static ImageBox CreateImage(
        CanvasDevice device,
        IMarkdownPerformanceDocumentScope documentScope,
        MarkdownPerformanceSession session,
        object documentOwner,
        string source)
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
            new MarkdownSourceMap("owned WIC ImageBox WARP cycle qualification"),
            new MarkdownExtensionRegistry(),
            FlowDirection.LeftToRight,
            language: "en-US")
        {
            ImageResolver = documentScope,
            PerformanceSession = session,
            PerformanceDocumentOwner = documentOwner,
            RasterizationScale = 1,
            ImageCancellationToken = CancellationToken.None,
        };
        return new ImageBox(context, source, "project-authored static raster fixture");
    }

    private static async Task WaitForProcessAdmissionIdleAsync(MarkdownImagePreparationAdmission admission)
    {
        await WaitUntilAsync(() =>
            admission.ActiveCount == 0 && admission.ActiveBytes == 0 && admission.PendingRequests == 0);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        while (!condition())
            await Task.Delay(10, timeout.Token);
    }

    private static int GreatestCommonDivisor(int left, int right)
    {
        while (right != 0)
        {
            int remainder = left % right;
            left = right;
            right = remainder;
        }
        return left;
    }

    private sealed class RasterFixture(string name, string mimeType, byte[] source, ushort orientation)
    {
        internal string Name { get; } = name;
        internal string MimeType { get; } = mimeType;
        internal byte[] Source { get; } = source;
        internal ushort Orientation { get; } = orientation;
        internal string SourceUri { get; } = $"https://images.example/owned-wic-cycle/{Guid.NewGuid():N}/{name}";
        internal int OutputWidth { get; set; }
        internal int OutputHeight { get; set; }
        internal RasterImageBudgetResult Budget { get; set; } = null!;
        internal byte[]? ExpectedPixels { get; set; }
        internal string CacheKey { get; } = $"owned-wic-cycle:{Guid.NewGuid():N}:{name}";
    }

    private sealed class FixtureResolver : IMarkdownImageResolver
    {
        private readonly Dictionary<string, RasterFixture> _fixtures;
        private readonly ConcurrentDictionary<string, int> _calls = new(StringComparer.Ordinal);

        internal FixtureResolver(IEnumerable<RasterFixture> fixtures) =>
            _fixtures = fixtures.ToDictionary(static fixture => fixture.SourceUri, StringComparer.Ordinal);

        internal int TotalCalls => _calls.Values.Sum();
        internal int GetCallCount(string source) => _calls.TryGetValue(source, out int count) ? count : 0;

        public ValueTask<MarkdownImageResolution> ResolveAsync(
            string source,
            MarkdownImageResolveContext context,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!_fixtures.TryGetValue(source, out RasterFixture? fixture) || fixture is null)
                throw new InvalidOperationException("The product cycle requested an unregistered source.");
            _calls.AddOrUpdate(source, 1, static (_, count) => checked(count + 1));
            return ValueTask.FromResult(MarkdownImageResolution.Resolved(
                new MarkdownImageAsset(fixture.Source, fixture.MimeType, CacheKey: fixture.CacheKey)));
        }
    }
}
