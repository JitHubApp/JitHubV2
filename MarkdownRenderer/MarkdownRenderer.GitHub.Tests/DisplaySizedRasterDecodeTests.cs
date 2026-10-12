using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
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

namespace MarkdownRenderer.GitHub.Tests;

public sealed class DisplaySizedRasterDecodeTests
{
    [Fact]
    public async Task RetiringBoxCancelsQueuedRasterPreparationAndDrainsItsLifetime()
    {
        using var session = new MarkdownPerformanceSession(
            MarkdownPerformanceOptions.Progressive with { MaxConcurrentCpuPreparations = 1 });
        using IDisposable held = await session.EnterCpuPreparationAsync(
            new object(), CancellationToken.None);
        var resolver = new ByteResolver(CreatePng(1024, 512), $"retired-raster-{Guid.NewGuid():N}");
        var image = new ImageBox(CreateContext(resolver, session, scale: 1),
            "https://images.example/retired.png", string.Empty);
        int publications = 0;
        image.LoadCompleted += (_, _) => Interlocked.Increment(ref publications);
        try
        {
            image.Measure(200);
            image.EnsureLoading();
            // Resolver and decode phases both hold the image lifetime now.
            // Observe the actual admission waiter, not a transient work count.
            await WaitUntilAsync(() => session.PendingCpuPreparations == 1);
            Assert.True(image.ActiveImageWorkCountForTests > 0);
            Assert.Null(image.Bitmap);
            Assert.Equal(1, session.GetSnapshot().CpuPreparations);

            image.Dispose();
            await WaitUntilAsync(() => image.ImageLifetimeDisposedForTests);
            Assert.Equal(0, image.ActiveImageWorkCountForTests);
            Assert.Equal(0, session.PendingCpuPreparations);
            Assert.Equal(0, Volatile.Read(ref publications));
            Assert.Equal(1, session.GetSnapshot().CpuPreparations);

            held.Dispose();
            using IDisposable next = await session.EnterCpuPreparationAsync(
                new object(), CancellationToken.None).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally { image.Dispose(); }
    }

    [Fact]
    public async Task EagerWin2DLoadAcceptsTheBorrowedSourceStream()
    {
        byte[] source = CreatePng(width: 1024, height: 512);
        var resolver = new ByteResolver(source, $"eager-raster-{Guid.NewGuid():N}");
        var image = new ImageBox(CreateContext(resolver, session: null, scale: 1),
            "https://images.example/large.png", string.Empty);
        try
        {
            image.Measure(200);
            await WaitForLoadAsync(image);
            Assert.NotNull(image.Bitmap);
            Assert.Equal(1024u, image.Bitmap.SizeInPixels.Width);
            Assert.Equal(512u, image.Bitmap.SizeInPixels.Height);
        }
        finally { image.Dispose(); }
    }

    [Theory]
    [InlineData(0.75, 150, 75)]
    [InlineData(1.0, 200, 100)]
    [InlineData(1.25, 250, 125)]
    [InlineData(1.5, 300, 150)]
    [InlineData(1.75, 350, 175)]
    [InlineData(2.0, 400, 200)]
    [InlineData(2.25, 450, 225)]
    [InlineData(3.0, 600, 300)]
    [InlineData(4.0, 800, 400)]
    public async Task OptedInRasterUsesPhysicalDisplayDimensions(
        double rasterizationScale,
        int expectedWidth,
        int expectedHeight)
    {
        byte[] source = CreatePng(width: 1024, height: 512);
        var resolver = new ByteResolver(source, $"display-raster-{Guid.NewGuid():N}");
        using var session = new MarkdownPerformanceSession(MarkdownPerformanceOptions.Progressive);
        var image = new ImageBox(
            CreateContext(resolver, session, rasterizationScale),
            "https://images.example/large.png",
            string.Empty);
        try
        {
            image.Measure(200);
            await WaitForLoadAsync(image);

            Assert.NotNull(image.Bitmap);
            Assert.Equal((uint)expectedWidth, image.Bitmap.SizeInPixels.Width);
            Assert.Equal((uint)expectedHeight, image.Bitmap.SizeInPixels.Height);
            image.Measure(200);
            Assert.InRange(image.Bounds.Height, 100, 120);
        }
        finally
        {
            image.Dispose();
        }
    }

    [Fact]
    public async Task DifferentDisplayWidthsUseDifferentCachedRasterSizes()
    {
        byte[] source = CreatePng(width: 1024, height: 512);
        var resolver = new ByteResolver(source, $"display-width-{Guid.NewGuid():N}");
        using var session = new MarkdownPerformanceSession(MarkdownPerformanceOptions.Progressive);
        var first = new ImageBox(CreateContext(resolver, session, 1),
            "https://images.example/large.png", string.Empty);
        var second = new ImageBox(CreateContext(resolver, session, 1),
            "https://images.example/large.png", string.Empty);
        try
        {
            first.Measure(200);
            await WaitForLoadAsync(first);
            second.Measure(100);
            await WaitForLoadAsync(second);

            Assert.Equal(200u, first.Bitmap!.SizeInPixels.Width);
            Assert.Equal(100u, second.Bitmap!.SizeInPixels.Width);
        }
        finally
        {
            first.Dispose();
            second.Dispose();
        }
    }

    [Theory]
    [InlineData(800f, 400f, 100)]
    [InlineData(800f, 0f, 100)]
    [InlineData(0f, 800f, 100)]
    [InlineData(0f, 0f, 100)]
    [InlineData(800f, 800f, 200)]
    public async Task AuthoredRasterDimensionsDecodeAtTheConstrainedDisplayRectangle(
        float authoredWidth, float authoredHeight, int displayHeight)
    {
        var resolver = new ByteResolver(CreatePng(1024, 512), $"authored-raster-{Guid.NewGuid():N}");
        using var session = new MarkdownPerformanceSession(MarkdownPerformanceOptions.Progressive);
        var image = new ImageBox(CreateContext(resolver, session, scale: 2),
            "https://images.example/authored.png", string.Empty,
            new SafeHtmlLength(authoredWidth, false), new SafeHtmlLength(authoredHeight, false));
        try
        {
            image.Measure(200);
            await WaitForLoadAsync(image);
            Assert.NotNull(image.Bitmap);
            Assert.Equal(400u, image.Bitmap.SizeInPixels.Width);
            Assert.Equal((uint)(displayHeight * 2), image.Bitmap.SizeInPixels.Height);
            image.Measure(200);
            Assert.Equal(200f, image.MeasuredImageWidth, precision: 2);
            Assert.Equal((float)displayHeight, image.MeasuredImageHeight, precision: 2);
        }
        finally { image.Dispose(); }
    }

    [Theory]
    [InlineData(true, 1.0, 4_194_304L, 200, 100)]
    [InlineData(true, 2.0, 4_194_304L, 400, 200)]
    [InlineData(true, 4.0, 4_194_304L, 800, 400)]
    [InlineData(false, 1.0, 10_000L, 141, 70)]
    public async Task EagerSourceCacheDoesNotBypassTheConsumersRasterPolicy(
        bool displaySized, double scale, long pixelLimit, int width, int height)
    {
        var resolver = new ByteResolver(CreatePng(1024, 512), $"policy-raster-{Guid.NewGuid():N}");
        using var session = new MarkdownPerformanceSession(
            MarkdownPerformanceOptions.Progressive with
            {
                UseDisplaySizedRasterDecode = displaySized,
                MaxRasterOutputPixels = pixelLimit,
            });
        var eager = new ImageBox(CreateContext(resolver, null, 1),
            "https://images.example/shared.png", string.Empty);
        var bounded = new ImageBox(CreateContext(resolver, session, scale),
            "https://images.example/shared.png", string.Empty);
        var reused = new ImageBox(CreateContext(resolver, session, scale),
            "https://images.example/shared.png", string.Empty);
        try
        {
            eager.Measure(200);
            await WaitForLoadAsync(eager);
            Assert.Equal(1024u, eager.Bitmap!.SizeInPixels.Width);
            bounded.Measure(200);
            await WaitForLoadAsync(bounded);
            Assert.Equal((uint)width, bounded.Bitmap!.SizeInPixels.Width);
            Assert.Equal((uint)height, bounded.Bitmap.SizeInPixels.Height);
            Assert.NotSame(eager.Bitmap, bounded.Bitmap);

            long preparations = session.GetSnapshot().CpuPreparations;
            reused.Measure(200);
            await WaitForLoadAsync(reused);
            Assert.Same(bounded.Bitmap, reused.Bitmap);
            Assert.Equal(preparations, session.GetSnapshot().CpuPreparations);
        }
        finally { eager.Dispose(); bounded.Dispose(); reused.Dispose(); }
    }

    [Fact]
    public async Task BuiltInSourceConstructorDoesNotBypassDisplaySizedDecode()
    {
        string source = "data:image/png;base64," + Convert.ToBase64String(CreatePng(1024, 512));
        var eager = new ImageBox(CreateContext(null!, null, 1), source, string.Empty);
        using var session = new MarkdownPerformanceSession(MarkdownPerformanceOptions.Progressive);
        ImageBox? bounded = null;
        try
        {
            eager.Measure(200);
            await WaitForLoadAsync(eager);
            Assert.Equal(1024u, eager.Bitmap!.SizeInPixels.Width);
            bounded = new ImageBox(CreateContext(null!, session, 1), source, string.Empty);
            Assert.Null(bounded.Bitmap);
            bounded.Measure(200);
            await WaitForLoadAsync(bounded);
            Assert.Equal(200u, bounded.Bitmap!.SizeInPixels.Width);
            Assert.Equal(100u, bounded.Bitmap.SizeInPixels.Height);
        }
        finally { eager.Dispose(); bounded?.Dispose(); }
    }

    [Fact]
    public async Task SourceSizedCacheStillReusesAnEntryWithinTheConsumersPixelCeiling()
    {
        var resolver = new ByteResolver(CreatePng(64, 32), $"small-policy-raster-{Guid.NewGuid():N}");
        var eager = new ImageBox(CreateContext(resolver, null, 1),
            "https://images.example/small.png", string.Empty);
        using var session = new MarkdownPerformanceSession(
            MarkdownPerformanceOptions.Progressive with
            {
                UseDisplaySizedRasterDecode = false,
                MaxRasterOutputPixels = 10_000,
            });
        var reused = new ImageBox(CreateContext(resolver, session, 1),
            "https://images.example/small.png", string.Empty);
        try
        {
            await WaitForLoadAsync(eager);
            await WaitForLoadAsync(reused);
            Assert.Same(eager.Bitmap, reused.Bitmap);
            Assert.Equal(0, session.GetSnapshot().CpuPreparations);
        }
        finally { eager.Dispose(); reused.Dispose(); }
    }

    [Fact]
    public async Task LargerRasterShowsCachedPreviewBeforePaintOnlyUpgrade()
    {
        byte[] source = CreatePng(width: 1024, height: 512);
        var resolver = new ByteResolver(source, $"display-upgrade-{Guid.NewGuid():N}");
        using var session = new MarkdownPerformanceSession(MarkdownPerformanceOptions.Progressive);
        var preview = new ImageBox(CreateContext(resolver, session, 1),
            "https://images.example/large.png", string.Empty);
        var larger = new ImageBox(CreateContext(resolver, session, 1),
            "https://images.example/large.png", string.Empty);
        try
        {
            preview.Measure(400);
            await WaitForLoadAsync(preview);
            Assert.Equal(400u, preview.Bitmap!.SizeInPixels.Width);
            preview.Dispose();

            var observations = new List<(uint Width, bool LayoutInvalidated)>();
            var upgraded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            larger.LoadCompleted += (_, result) =>
            {
                if (larger.Bitmap is not { } bitmap)
                    return;
                uint width = bitmap.SizeInPixels.Width;
                observations.Add((width, result.LayoutInvalidated));
                if (width == 800)
                    upgraded.TrySetResult();
            };
            larger.Measure(800);
            larger.EnsureLoading();
            await upgraded.Task.WaitAsync(TimeSpan.FromSeconds(10));

            Assert.Equal(
                [(400u, true), (800u, false)],
                observations);
        }
        finally
        {
            preview.Dispose();
            larger.Dispose();
        }
    }

    [Fact]
    public async Task ResolvedUriWithoutPartitionKeyDoesNotShareRasterAcrossResolvers()
    {
        Uri resolvedUri = new("https://images.example/account/avatar.png");
        using var session = new MarkdownPerformanceSession(MarkdownPerformanceOptions.Progressive);
        var portrait = new ImageBox(
            CreateContext(new UnkeyedResolver(CreatePng(512, 1024), resolvedUri), session, 1),
            resolvedUri.AbsoluteUri, string.Empty);
        var landscape = new ImageBox(
            CreateContext(new UnkeyedResolver(CreatePng(1024, 512), resolvedUri), session, 1),
            resolvedUri.AbsoluteUri, string.Empty);
        try
        {
            portrait.Measure(200);
            await WaitForLoadAsync(portrait);
            landscape.Measure(200);
            await WaitForLoadAsync(landscape);

            Assert.Equal(200u, portrait.Bitmap!.SizeInPixels.Width);
            Assert.Equal(400u, portrait.Bitmap.SizeInPixels.Height);
            Assert.Equal(200u, landscape.Bitmap!.SizeInPixels.Width);
            Assert.Equal(100u, landscape.Bitmap.SizeInPixels.Height);
        }
        finally
        {
            portrait.Dispose();
            landscape.Dispose();
        }
    }

    [Fact]
    public async Task CachedRasterPreviewCanBeDisabledIndependently()
    {
        byte[] source = CreatePng(1024, 512);
        var resolver = new ByteResolver(source, $"preview-opt-out-{Guid.NewGuid():N}");
        using var previewSession = new MarkdownPerformanceSession(MarkdownPerformanceOptions.Progressive);
        using var exactOnlySession = new MarkdownPerformanceSession(
            MarkdownPerformanceOptions.Progressive with { UseCachedRasterPreview = false });
        var cached = new ImageBox(CreateContext(resolver, previewSession, 1),
            "https://images.example/large.png", string.Empty);
        var exactOnly = new ImageBox(CreateContext(resolver, exactOnlySession, 1),
            "https://images.example/large.png", string.Empty);
        try
        {
            cached.Measure(400);
            await WaitForLoadAsync(cached);
            Assert.Equal(400u, cached.Bitmap!.SizeInPixels.Width);

            var widths = new List<uint>();
            exactOnly.LoadCompleted += (_, _) =>
            {
                if (exactOnly.Bitmap is { } bitmap)
                    widths.Add(bitmap.SizeInPixels.Width);
            };
            exactOnly.Measure(800);
            await WaitForLoadAsync(exactOnly);

            Assert.Equal([800u], widths);
        }
        finally
        {
            cached.Dispose();
            exactOnly.Dispose();
        }
    }

    [Fact]
    public async Task LoweredRasterPixelCeilingBoundsThePreparedBitmap()
    {
        byte[] source = CreatePng(width: 1024, height: 512);
        var resolver = new ByteResolver(source, $"display-budget-{Guid.NewGuid():N}");
        using var session = new MarkdownPerformanceSession(
            MarkdownPerformanceOptions.Progressive with { MaxRasterOutputPixels = 10_000 });
        var image = new ImageBox(CreateContext(resolver, session, 1),
            "https://images.example/large.png", string.Empty);
        try
        {
            image.Measure(200);
            await WaitForLoadAsync(image);

            Assert.NotNull(image.Bitmap);
            var pixels = image.Bitmap.SizeInPixels;
            Assert.True((long)pixels.Width * pixels.Height <= 10_000);
            image.Measure(200);
            Assert.InRange(image.Bounds.Height, 100, 120);
        }
        finally
        {
            image.Dispose();
        }
    }

    [Fact]
    public async Task OversizedTallRasterUsesBoundedStaticPreviewWithoutChangingImageGeometry()
    {
        // Just beyond the ordinary full-frame pixel ceiling, but within the
        // separately bounded single-frame preview allowance. Its displayed
        // width is small while its full source height is several thousand
        // pixels, so retaining and uploading source-sized bitmap pixels would
        // waste GPU memory on detail far below the viewport.
        const int sourceWidth = 2048;
        const int sourceHeight = 8193;
        const long outputPixelLimit = 65_536;
        byte[] source = CreatePng(sourceWidth, sourceHeight);
        var resolver = new ByteResolver(source, $"tall-preview-{Guid.NewGuid():N}");
        using var session = new MarkdownPerformanceSession(
            MarkdownPerformanceOptions.Progressive with
            {
                UseDisplaySizedRasterDecode = false,
                MaxRasterOutputPixels = outputPixelLimit,
            });
        var image = new ImageBox(CreateContext(resolver, session, 1),
            "https://images.example/tall.png", string.Empty);
        var publications = new List<bool>();
        image.LoadCompleted += (_, result) => publications.Add(result.LayoutInvalidated);

        try
        {
            image.Measure(200);
            await WaitForLoadAsync(image);

            Assert.NotNull(image.Bitmap);
            var pixels = image.Bitmap.SizeInPixels;
            Assert.True((long)pixels.Width * pixels.Height <= outputPixelLimit);
            Assert.InRange(
                pixels.Width / (double)pixels.Height,
                sourceWidth / (double)sourceHeight * 0.99,
                sourceWidth / (double)sourceHeight * 1.01);

            image.Measure(200);
            Assert.Equal(200f, image.MeasuredImageWidth, precision: 2);
            Assert.Equal(
                200f * sourceHeight / sourceWidth,
                image.MeasuredImageHeight,
                precision: 1);
            Assert.Equal([true], publications);
        }
        finally
        {
            image.Dispose();
        }
    }

    internal static async Task WaitForLoadAsync(ImageBox image)
    {
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        image.LoadCompleted += (_, args) =>
        {
            if (!args.IsBitmapOutputRecoverySignal)
                completed.TrySetResult();
        };
        image.EnsureLoading();
        await completed.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }

    internal static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!condition())
            await Task.Delay(10, timeout.Token);
    }

    internal static MarkdownLayoutContext CreateContext(
        IMarkdownImageResolver resolver,
        MarkdownPerformanceSession? session,
        double scale,
        bool hasDocumentOwner = true,
        Action<string, MarkdownImageUnavailableReason, MarkdownSvgFailureReason?>? imageUnavailable = null)
    {
        var style = new ElementStyle();
        var styles = new Dictionary<string, ElementStyle>(StringComparer.Ordinal)
        {
            [MarkdownElementKeys.Body] = style,
            [MarkdownElementKeys.ImageCaption] = style,
            [MarkdownElementKeys.ListMarker] = style,
        };
        Color transparent = Color.FromArgb(0, 0, 0, 0);
        var theme = new ThemeSnapshot(styles, new Dictionary<string, ElementStyleOverride>(),
            transparent, transparent, transparent, transparent,
            isDark: false, isHighContrast: false, textScaleFactor: 1);
        return new MarkdownLayoutContext(
            CanvasDevice.GetSharedDevice(), theme, new MarkdownSourceMap(string.Empty),
            new MarkdownExtensionRegistry(), FlowDirection.LeftToRight, language: "en-US")
        {
            ImageResolver = resolver,
            PerformanceSession = session,
            PerformanceDocumentOwner = hasDocumentOwner ? new object() : null,
            RasterizationScale = scale,
            ImageCancellationToken = CancellationToken.None,
            ImageUnavailable = imageUnavailable,
        };
    }

    internal static byte[] CreatePng(int width, int height)
    {
        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.Fastest, leaveOpen: true))
        {
            byte[] row = new byte[checked(width * 4 + 1)];
            for (int index = 0; index < height; index++)
                zlib.Write(row);
        }

        using var png = new MemoryStream();
        png.Write([137, 80, 78, 71, 13, 10, 26, 10]);
        byte[] header = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(0, 4), width);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4, 4), height);
        header[8] = 8;  // 8 bits per channel
        header[9] = 6;  // RGBA
        WriteChunk(png, "IHDR", header);
        WriteChunk(png, "IDAT", compressed.ToArray());
        WriteChunk(png, "IEND", []);
        return png.ToArray();
    }

    private static void WriteChunk(Stream destination, string name, byte[] content)
    {
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(length, content.Length);
        destination.Write(length);
        byte[] type = Encoding.ASCII.GetBytes(name);
        destination.Write(type);
        destination.Write(content);
        uint crc = 0xFFFFFFFF;
        foreach (byte value in type)
            crc = UpdateCrc(crc, value);
        foreach (byte value in content)
            crc = UpdateCrc(crc, value);
        Span<byte> checksum = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(checksum, ~crc);
        destination.Write(checksum);
    }

    private static uint UpdateCrc(uint crc, byte value)
    {
        crc ^= value;
        for (int bit = 0; bit < 8; bit++)
            crc = (crc >> 1) ^ ((crc & 1) != 0 ? 0xEDB88320u : 0u);
        return crc;
    }

    private sealed class ByteResolver(byte[] bytes, string cacheKey) : IMarkdownImageResolver
    {
        public ValueTask<MarkdownImageResolution> ResolveAsync(
            string source,
            MarkdownImageResolveContext context,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(MarkdownImageResolution.Resolved(
                new MarkdownImageAsset(bytes, "image/png", CacheKey: cacheKey)));
    }

    private sealed class UnkeyedResolver(byte[] bytes, Uri resolvedUri) : IMarkdownImageResolver
    {
        public ValueTask<MarkdownImageResolution> ResolveAsync(
            string source,
            MarkdownImageResolveContext context,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(MarkdownImageResolution.Resolved(
                new MarkdownImageAsset(bytes, "image/png", ResolvedUri: resolvedUri)));
    }
}
