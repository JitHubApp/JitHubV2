using System.Buffers.Binary;
using System.Numerics;
using MarkdownRenderer.Images;
using MarkdownRenderer.Layout.Boxes;
using MarkdownRenderer.Performance;
using Microsoft.Graphics.Canvas;
using Windows.Foundation;
using Windows.UI;
using Xunit;

namespace MarkdownRenderer.GitHub.Tests;

[Collection(SharedCanvasBitmapCacheTestCollection.Name)]
public sealed class GifRasterCanvasTests
{
    [Fact]
    public async Task PublishedBitmapKeepsLogicalCanvasAndTransparentPaddingAroundOffsetFrame()
    {
        var resolver = new GifResolver(
            CreateGif(4, 4, left: 1, top: 1, frameWidth: 2, frameHeight: 3),
            $"gif-offset-{Guid.NewGuid():N}");
        using var session = ProgressiveSession();
        var image = new ImageBox(
            DisplaySizedRasterDecodeTests.CreateContext(resolver, session, scale: 1),
            "https://images.example/offset.gif",
            string.Empty);

        try
        {
            await LoadAsync(image, availableWidth: 4);

            Assert.NotNull(image.Bitmap);
            Assert.Equal((4u, 4u), (
                image.Bitmap.SizeInPixels.Width,
                image.Bitmap.SizeInPixels.Height));

            image.Measure(4);
            Assert.Equal(4f, image.MeasuredImageWidth, precision: 2);
            Assert.Equal(4f, image.MeasuredImageHeight, precision: 2);

            Color[] pixels = Paint(image, 4, 4);
            for (int y = 0; y < 4; y++)
            {
                for (int x = 0; x < 4; x++)
                {
                    Color pixel = pixels[(y * 4) + x];
                    bool insideFrame = x is >= 1 and < 3 && y is >= 1 and < 4;
                    if (insideFrame)
                    {
                        Assert.Equal(255, pixel.A);
                        Assert.True(pixel.R > pixel.G && pixel.R > pixel.B,
                            $"Expected the frame's red pixel at ({x}, {y}); got {pixel}.");
                    }
                    else
                    {
                        Assert.Equal(0, pixel.A);
                    }
                }
            }
        }
        finally
        {
            image.Dispose();
        }
    }

    [Fact]
    public async Task TransparentGifPaletteIndexRemainsTransparentAfterCanvasComposition()
    {
        var resolver = new GifResolver(
            CreateGif(4, 4, left: 1, top: 1, frameWidth: 2, frameHeight: 3, transparentIndexZero: true),
            $"gif-transparent-{Guid.NewGuid():N}");
        using var session = ProgressiveSession();
        var image = new ImageBox(
            DisplaySizedRasterDecodeTests.CreateContext(resolver, session, scale: 1),
            "https://images.example/transparent-offset.gif",
            string.Empty);

        try
        {
            await LoadAsync(image, availableWidth: 4);

            Assert.NotNull(image.Bitmap);
            Color[] pixels = Paint(image, 4, 4);
            Assert.All(pixels, pixel => Assert.Equal(0, pixel.A));
        }
        finally
        {
            image.Dispose();
        }
    }

    [Theory]
    [InlineData(1.0)]
    [InlineData(2.0)]
    public async Task SourceSizedDefaultUsesLogicalCanvasEvenWhenDisplayedMuchSmaller(
        double rasterizationScale)
    {
        var resolver = new GifResolver(
            CreateGif(4, 4, left: 1, top: 1, frameWidth: 2, frameHeight: 3),
            $"gif-source-sized-{rasterizationScale}-{Guid.NewGuid():N}");
        var image = new ImageBox(
            DisplaySizedRasterDecodeTests.CreateContext(resolver, session: null, rasterizationScale),
            "https://images.example/source-sized.gif",
            string.Empty);

        try
        {
            await LoadAsync(image, availableWidth: 1);

            Assert.NotNull(image.Bitmap);
            Assert.Equal((4u, 4u), (
                image.Bitmap.SizeInPixels.Width,
                image.Bitmap.SizeInPixels.Height));
            image.Measure(1);
            Assert.Equal(1f, image.MeasuredImageWidth, precision: 2);
            Assert.Equal(1f, image.MeasuredImageHeight, precision: 2);
        }
        finally
        {
            image.Dispose();
        }
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
    public async Task DisplaySizedCanvasUsesBoundedPhysicalDimensionsAcrossDpiScales(
        double rasterizationScale,
        int expectedPixels)
    {
        var resolver = new GifResolver(
            CreateGif(8, 8, left: 2, top: 1, frameWidth: 2, frameHeight: 3),
            $"gif-dpi-{rasterizationScale}-{Guid.NewGuid():N}");
        using var session = ProgressiveSession();
        var image = new ImageBox(
            DisplaySizedRasterDecodeTests.CreateContext(resolver, session, rasterizationScale),
            "https://images.example/dpi.gif",
            string.Empty);

        try
        {
            await LoadAsync(image, availableWidth: 4);

            Assert.NotNull(image.Bitmap);
            Assert.Equal((uint)expectedPixels, image.Bitmap.SizeInPixels.Width);
            Assert.Equal((uint)expectedPixels, image.Bitmap.SizeInPixels.Height);
            Assert.True((long)expectedPixels * expectedPixels <= session.Options.MaxRasterOutputPixels);
            image.Measure(4);
            Assert.Equal(4f, image.MeasuredImageWidth, precision: 2);
            Assert.Equal(4f, image.MeasuredImageHeight, precision: 2);
        }
        finally
        {
            image.Dispose();
        }
    }

    [Fact]
    public async Task OnePixelCapRetainsFractionalFrameCoverageInsteadOfSnappingItOpaque()
    {
        var resolver = new GifResolver(
            CreateGif(8, 8, left: 2, top: 1, frameWidth: 2, frameHeight: 3),
            $"gif-fractional-cap-{Guid.NewGuid():N}");
        using var session = ProgressiveSession(maximumPixels: 1);
        var image = new ImageBox(
            DisplaySizedRasterDecodeTests.CreateContext(resolver, session, scale: 1),
            "https://images.example/fractional.gif",
            string.Empty);

        try
        {
            await LoadAsync(image, availableWidth: 4);

            Assert.NotNull(image.Bitmap);
            Assert.Equal((1u, 1u), (
                image.Bitmap.SizeInPixels.Width,
                image.Bitmap.SizeInPixels.Height));
            Color storedPixel = image.Bitmap.GetPixelColors()[0];
            Assert.InRange((int)storedPixel.A, 1, 254);
            Color[] pixels = Paint(image, 4, 4);
            Assert.Contains(pixels, pixel => pixel.A is > 0 and < 255);
            Assert.DoesNotContain(pixels, pixel => pixel.A == 255);
        }
        finally
        {
            image.Dispose();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FrameOutsideLogicalCanvasIsRejectedBeforeAnyBitmapPublication(
        bool displaySizedDecode)
    {
        var resolver = new GifResolver(
            CreateGif(1, 1, left: 0, top: 0, frameWidth: 2, frameHeight: 3),
            $"gif-outside-canvas-{displaySizedDecode}-{Guid.NewGuid():N}");
        using var session = ProgressiveSession(
            maximumPixels: 1,
            displaySizedDecode: displaySizedDecode);
        var image = new ImageBox(
            DisplaySizedRasterDecodeTests.CreateContext(resolver, session, scale: 1),
            "https://images.example/outside-canvas.gif",
            string.Empty);

        try
        {
            await LoadAsync(image, availableWidth: 1);

            Assert.Null(image.Bitmap);
            Assert.False(image.HasBitmapForAutomation);
        }
        finally
        {
            image.Dispose();
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ComposedCanvasCacheNeverAliasesAcrossConsumerPixelCaps(bool lowCapFirst)
    {
        byte[] bytes = CreateGif(8, 8, left: 2, top: 1, frameWidth: 2, frameHeight: 3);
        string sourceKey = $"gif-canvas-cap-order-{lowCapFirst}-{Guid.NewGuid():N}";
        var resolver = new GifResolver(bytes, sourceKey);
        using var highSession = ProgressiveSession(maximumPixels: 64);
        using var lowSession = ProgressiveSession(maximumPixels: 1);
        var high = CreateImage(resolver, highSession, scale: 1, "high-cap.gif");
        var low = CreateImage(resolver, lowSession, scale: 1, "low-cap.gif");
        ImageBox? highRepeat = null;
        ImageBox? lowRepeat = null;

        try
        {
            ImageBox first = lowCapFirst ? low : high;
            ImageBox second = lowCapFirst ? high : low;
            await LoadAsync(first, availableWidth: 4);
            await LoadAsync(second, availableWidth: 4);

            Assert.NotNull(high.Bitmap);
            Assert.NotNull(low.Bitmap);
            Assert.Equal((4u, 4u), (high.Bitmap.SizeInPixels.Width, high.Bitmap.SizeInPixels.Height));
            Assert.Equal((1u, 1u), (low.Bitmap.SizeInPixels.Width, low.Bitmap.SizeInPixels.Height));
            Assert.NotSame(high.Bitmap, low.Bitmap);
            Assert.True((long)low.Bitmap.SizeInPixels.Width * low.Bitmap.SizeInPixels.Height <= 1);

            long highPreparations = highSession.GetSnapshot().CpuPreparations;
            long lowPreparations = lowSession.GetSnapshot().CpuPreparations;
            highRepeat = CreateImage(resolver, highSession, scale: 1, "high-cap-repeat.gif");
            lowRepeat = CreateImage(resolver, lowSession, scale: 1, "low-cap-repeat.gif");
            await LoadAsync(highRepeat, availableWidth: 4);
            await LoadAsync(lowRepeat, availableWidth: 4);

            Assert.Same(high.Bitmap, highRepeat.Bitmap);
            Assert.Same(low.Bitmap, lowRepeat.Bitmap);
            Assert.Equal(highPreparations, highSession.GetSnapshot().CpuPreparations);
            Assert.Equal(lowPreparations, lowSession.GetSnapshot().CpuPreparations);
        }
        finally
        {
            high.Dispose();
            low.Dispose();
            highRepeat?.Dispose();
            lowRepeat?.Dispose();
        }
    }

    private static ImageBox CreateImage(
        IMarkdownImageResolver resolver,
        MarkdownPerformanceSession session,
        double scale,
        string name) => new(
            DisplaySizedRasterDecodeTests.CreateContext(resolver, session, scale),
            $"https://images.example/{name}",
            string.Empty);

    private static MarkdownPerformanceSession ProgressiveSession(
        long maximumPixels = 64,
        bool displaySizedDecode = true) =>
        new(MarkdownPerformanceOptions.Progressive with
        {
            UseDisplaySizedRasterDecode = displaySizedDecode,
            MaxRasterOutputPixels = maximumPixels,
        });

    private static async Task LoadAsync(ImageBox image, float availableWidth)
    {
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnLoadCompleted(object? sender, LoadCompletedEventArgs args) => completed.TrySetResult();

        image.LoadCompleted += OnLoadCompleted;
        try
        {
            image.Measure(availableWidth);
            image.EnsureLoading();
            if (image.Bitmap is not null)
                return;

            await completed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally
        {
            image.LoadCompleted -= OnLoadCompleted;
        }
    }

    private static Color[] Paint(ImageBox image, int width, int height)
    {
        image.Measure(width);
        using var target = new CanvasRenderTarget(CanvasDevice.GetSharedDevice(), width, height, 96);
        using (CanvasDrawingSession drawingSession = target.CreateDrawingSession())
        {
            drawingSession.Clear(Color.FromArgb(0, 0, 0, 0));
            // Crop the document-space image content, not its outer six-DIP
            // block margin. Keep production layout and drawing unchanged.
            Rect destination = image.AutomationRenderDestination;
            drawingSession.Transform = Matrix3x2.CreateTranslation(
                -(float)destination.X, -(float)destination.Y);
            image.Paint(drawingSession,
                new Rect(destination.X, destination.Y, width, height));
        }

        return target.GetPixelColors();
    }

    private static byte[] CreateGif(
        int logicalWidth,
        int logicalHeight,
        int left,
        int top,
        int frameWidth,
        int frameHeight,
        bool transparentIndexZero = false)
    {
        byte[] bytes = Convert.FromBase64String(
            "R0lGODlhAgADAHAAACH5BAEAAAEALAAAAAACAAMAgf8AAAAAAAAAAAAAAAIChF8AOw==");
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(6, 2), checked((ushort)logicalWidth));
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(8, 2), checked((ushort)logicalHeight));
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(22, 2), checked((ushort)left));
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(24, 2), checked((ushort)top));
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(26, 2), checked((ushort)frameWidth));
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(28, 2), checked((ushort)frameHeight));
        if (transparentIndexZero)
            bytes[19] = 0;
        return bytes;
    }

    private sealed class GifResolver(byte[] bytes, string cacheKey) : IMarkdownImageResolver
    {
        public ValueTask<MarkdownImageResolution> ResolveAsync(
            string source,
            MarkdownImageResolveContext context,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(MarkdownImageResolution.Resolved(
                new MarkdownImageAsset(bytes, "image/gif", CacheKey: cacheKey)));
    }
}
