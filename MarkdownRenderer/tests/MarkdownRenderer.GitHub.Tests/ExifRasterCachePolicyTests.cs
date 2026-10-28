using System.Runtime.InteropServices.WindowsRuntime;
using MarkdownRenderer.Images;
using MarkdownRenderer.Layout.Boxes;
using MarkdownRenderer.Performance;
using Windows.Foundation;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;
using Xunit;

namespace MarkdownRenderer.GitHub.Tests;

// Warm-cache identity requires isolation from other fixtures' process-wide
// bitmap-cache trims and admission pressure; the assertions remain unchanged.
[Collection(SharedCanvasBitmapCacheTestCollection.Name)]
public sealed class ExifRasterCachePolicyTests
{
    [Theory]
    [InlineData((ushort)1, 800u, 400u)]
    [InlineData((ushort)2, 800u, 400u)]
    [InlineData((ushort)3, 800u, 400u)]
    [InlineData((ushort)4, 800u, 400u)]
    [InlineData((ushort)5, 800u, 1600u)]
    [InlineData((ushort)6, 800u, 1600u)]
    [InlineData((ushort)7, 800u, 1600u)]
    [InlineData((ushort)8, 800u, 1600u)]
    public async Task EncodedJpegOrientationsProduceExpectedDisplayPixelAxes(
        ushort orientation,
        uint expectedWidth,
        uint expectedHeight)
    {
        byte[] bytes = await CreateJpegAsync(1600, 800, orientation);
        Assert.Equal(orientation, await ReadExifOrientationAsync(bytes));

        var resolver = new ByteResolver(bytes, $"jpeg-orientation-{orientation}-{Guid.NewGuid():N}");
        using var session = new MarkdownPerformanceSession(
            MarkdownPerformanceOptions.Progressive with
            {
                UseDisplaySizedRasterDecode = true,
                MaxRasterOutputPixels = 4_000_000,
            });
        var image = new ImageBox(
            DisplaySizedRasterDecodeTests.CreateContext(resolver, session, scale: 1),
            "https://images.example/oriented.jpg",
            string.Empty);

        try
        {
            await WaitForExactPixelsAsync(image, 800, expectedWidth, expectedHeight);

            Assert.NotNull(image.Bitmap);
            Assert.Equal(expectedWidth, image.Bitmap.SizeInPixels.Width);
            Assert.Equal(expectedHeight, image.Bitmap.SizeInPixels.Height);
            Assert.True((long)expectedWidth * expectedHeight <= 4_000_000);
        }
        finally
        {
            image.Dispose();
        }
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public async Task RotatedRasterCacheHonorsEachConsumersPixelCeiling(
        bool displaySized,
        bool highLimitFirst)
    {
        const long highPixelLimit = 500_000;
        const long lowPixelLimit = 400_000;
        byte[] bytes = await CreateJpegAsync(1600, 800, orientation: 6);
        Assert.Equal((ushort)6, await ReadExifOrientationAsync(bytes));

        var resolver = new ByteResolver(bytes, $"jpeg-cap-order-{displaySized}-{highLimitFirst}-{Guid.NewGuid():N}");
        using var highSession = new MarkdownPerformanceSession(
            MarkdownPerformanceOptions.Progressive with
            {
                UseDisplaySizedRasterDecode = displaySized,
                MaxRasterOutputPixels = highPixelLimit,
            });
        using var lowSession = new MarkdownPerformanceSession(
            MarkdownPerformanceOptions.Progressive with
            {
                UseDisplaySizedRasterDecode = displaySized,
                MaxRasterOutputPixels = lowPixelLimit,
            });

        ImageBox? highImage = null;
        ImageBox? lowImage = null;
        ImageBox? repeatedImage = null;
        try
        {
            if (highLimitFirst)
            {
                highImage = await LoadAtLimitAsync(
                    resolver,
                    highSession,
                    highPixelLimit,
                    expectedWidth: 500,
                    expectedHeight: 1000);
                long preparations = highSession.GetSnapshot().CpuPreparations;
                repeatedImage = await LoadAtLimitAsync(
                    resolver,
                    highSession,
                    highPixelLimit,
                    expectedWidth: 500,
                    expectedHeight: 1000);
                Assert.Same(highImage.Bitmap, repeatedImage.Bitmap);
                Assert.Equal(preparations, highSession.GetSnapshot().CpuPreparations);

                lowImage = await LoadAtLimitAsync(
                    resolver,
                    lowSession,
                    lowPixelLimit,
                    expectedWidth: 447,
                    expectedHeight: 894);
                Assert.NotSame(highImage.Bitmap, lowImage.Bitmap);
            }
            else
            {
                lowImage = await LoadAtLimitAsync(
                    resolver,
                    lowSession,
                    lowPixelLimit,
                    expectedWidth: 447,
                    expectedHeight: 894);

                long preparations = lowSession.GetSnapshot().CpuPreparations;
                repeatedImage = await LoadAtLimitAsync(
                    resolver,
                    lowSession,
                    lowPixelLimit,
                    expectedWidth: 447,
                    expectedHeight: 894);
                Assert.Same(lowImage.Bitmap, repeatedImage.Bitmap);
                Assert.Equal(preparations, lowSession.GetSnapshot().CpuPreparations);

                highImage = await LoadAtLimitAsync(
                    resolver,
                    highSession,
                    highPixelLimit,
                    expectedWidth: 500,
                    expectedHeight: 1000);
                Assert.NotSame(lowImage.Bitmap, highImage.Bitmap);
            }

            Assert.NotNull(highImage.Bitmap);
            Assert.NotNull(lowImage.Bitmap);
            Assert.Equal(500u, highImage.Bitmap.SizeInPixels.Width);
            Assert.Equal(1000u, highImage.Bitmap.SizeInPixels.Height);
            Assert.Equal(447u, lowImage.Bitmap.SizeInPixels.Width);
            Assert.Equal(894u, lowImage.Bitmap.SizeInPixels.Height);
            Assert.True(
                (long)highImage.Bitmap.SizeInPixels.Width * highImage.Bitmap.SizeInPixels.Height <= highPixelLimit);
            Assert.True(
                (long)lowImage.Bitmap.SizeInPixels.Width * lowImage.Bitmap.SizeInPixels.Height <= lowPixelLimit);
        }
        finally
        {
            highImage?.Dispose();
            lowImage?.Dispose();
            repeatedImage?.Dispose();
        }
    }

    [Theory]
    [InlineData(16_384, 1)]
    [InlineData(1, 16_384)]
    public async Task OnePixelCeilingHoldsForExtremelyThinRaster(int sourceWidth, int sourceHeight)
    {
        byte[] bytes = await CreateJpegAsync(sourceWidth, sourceHeight, orientation: 1);
        var resolver = new ByteResolver(bytes, $"jpeg-thin-{sourceWidth}x{sourceHeight}-{Guid.NewGuid():N}");
        using var session = new MarkdownPerformanceSession(
            MarkdownPerformanceOptions.Progressive with
            {
                UseDisplaySizedRasterDecode = false,
                MaxRasterOutputPixels = 1,
            });

        ImageBox? image = null;
        try
        {
            image = await LoadAtLimitAsync(
                resolver,
                session,
                pixelLimit: 1,
                expectedWidth: 1,
                expectedHeight: 1);
            Assert.Equal(1u, image.Bitmap!.SizeInPixels.Width);
            Assert.Equal(1u, image.Bitmap.SizeInPixels.Height);
        }
        finally
        {
            image?.Dispose();
        }
    }

    [Fact]
    public async Task RotatedDisplayVariantCacheSeparatesWidthsAndKeepsExactTargetWarm()
    {
        byte[] bytes = await CreateJpegAsync(800, 1600, orientation: 6);
        Assert.Equal((ushort)6, await ReadExifOrientationAsync(bytes));

        var resolver = new ByteResolver(bytes, $"jpeg-display-width-{Guid.NewGuid():N}");
        using var session = new MarkdownPerformanceSession(
            MarkdownPerformanceOptions.Progressive with
            {
                UseDisplaySizedRasterDecode = true,
                MaxRasterOutputPixels = 4_000_000,
            });
        ImageBox? narrow = null;
        ImageBox? repeatedNarrow = null;
        ImageBox? repeatedNarrowAfterWide = null;
        ImageBox? wide = null;
        ImageBox? repeatedWide = null;

        try
        {
            narrow = await LoadAtWidthAsync(resolver, session, 1000, 1000, 500);
            long preparations = session.GetSnapshot().CpuPreparations;
            repeatedNarrow = await LoadAtWidthAsync(resolver, session, 1000, 1000, 500);
            Assert.Same(narrow.Bitmap, repeatedNarrow.Bitmap);
            Assert.Equal(preparations, session.GetSnapshot().CpuPreparations);

            wide = await LoadAtWidthAsync(resolver, session, 1500, 1500, 750);
            Assert.NotSame(narrow.Bitmap, wide.Bitmap);
            Assert.Equal(1500u, wide.Bitmap!.SizeInPixels.Width);
            Assert.Equal(750u, wide.Bitmap.SizeInPixels.Height);
            Assert.True(
                (long)wide.Bitmap.SizeInPixels.Width * wide.Bitmap.SizeInPixels.Height <= 4_000_000);

            preparations = session.GetSnapshot().CpuPreparations;
            repeatedNarrowAfterWide = await LoadAtWidthAsync(resolver, session, 1000, 1000, 500);
            Assert.Same(narrow.Bitmap, repeatedNarrowAfterWide.Bitmap);
            Assert.Equal(preparations, session.GetSnapshot().CpuPreparations);

            preparations = session.GetSnapshot().CpuPreparations;
            repeatedWide = await LoadAtWidthAsync(resolver, session, 1500, 1500, 750);
            Assert.Same(wide.Bitmap, repeatedWide.Bitmap);
            Assert.Equal(preparations, session.GetSnapshot().CpuPreparations);
            Assert.Equal(1000u, narrow.Bitmap!.SizeInPixels.Width);
            Assert.Equal(500u, narrow.Bitmap.SizeInPixels.Height);
            Assert.True(
                (long)narrow.Bitmap.SizeInPixels.Width * narrow.Bitmap.SizeInPixels.Height <= 4_000_000);
        }
        finally
        {
            narrow?.Dispose();
            repeatedNarrow?.Dispose();
            repeatedNarrowAfterWide?.Dispose();
            wide?.Dispose();
            repeatedWide?.Dispose();
        }
    }

    private static async Task<ImageBox> LoadAtLimitAsync(
        IMarkdownImageResolver resolver,
        MarkdownPerformanceSession session,
        long pixelLimit,
        uint expectedWidth,
        uint expectedHeight)
    {
        var image = new ImageBox(
            DisplaySizedRasterDecodeTests.CreateContext(resolver, session, scale: 1),
            "https://images.example/capped.jpg",
            string.Empty);
        try
        {
            await WaitForExactPixelsAsync(image, 800, expectedWidth, expectedHeight);
            Assert.NotNull(image.Bitmap);
            Assert.Equal(expectedWidth, image.Bitmap.SizeInPixels.Width);
            Assert.Equal(expectedHeight, image.Bitmap.SizeInPixels.Height);
            Assert.True((long)expectedWidth * expectedHeight <= pixelLimit);
            return image;
        }
        catch
        {
            image.Dispose();
            throw;
        }
    }

    private static async Task<ImageBox> LoadAtWidthAsync(
        IMarkdownImageResolver resolver,
        MarkdownPerformanceSession session,
        float availableWidth,
        uint expectedWidth,
        uint expectedHeight)
    {
        var image = new ImageBox(
            DisplaySizedRasterDecodeTests.CreateContext(resolver, session, scale: 1),
            "https://images.example/display.jpg",
            string.Empty);
        try
        {
            await WaitForExactPixelsAsync(image, availableWidth, expectedWidth, expectedHeight);
            Assert.NotNull(image.Bitmap);
            Assert.Equal(expectedWidth, image.Bitmap.SizeInPixels.Width);
            Assert.Equal(expectedHeight, image.Bitmap.SizeInPixels.Height);
            return image;
        }
        catch
        {
            image.Dispose();
            throw;
        }
    }

    private static async Task WaitForExactPixelsAsync(
        ImageBox image,
        float availableWidth,
        uint expectedWidth,
        uint expectedHeight)
    {
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnLoadCompleted(object? sender, LoadCompletedEventArgs args)
        {
            if (HasExactPixels(image, expectedWidth, expectedHeight))
                completed.TrySetResult();
        }

        image.LoadCompleted += OnLoadCompleted;
        try
        {
            image.Measure(availableWidth);
            image.EnsureLoading();
            if (HasExactPixels(image, expectedWidth, expectedHeight))
                completed.TrySetResult();
            try
            {
                await completed.Task.WaitAsync(TimeSpan.FromSeconds(10));
            }
            catch (TimeoutException)
            {
                // Allow legitimate intermediate previews, but report the final
                // wrong cache/decode dimensions explicitly in baseline runs.
                Assert.NotNull(image.Bitmap);
                Assert.Equal(expectedWidth, image.Bitmap.SizeInPixels.Width);
                Assert.Equal(expectedHeight, image.Bitmap.SizeInPixels.Height);
                throw;
            }
        }
        finally
        {
            image.LoadCompleted -= OnLoadCompleted;
        }
    }

    private static bool HasExactPixels(ImageBox image, uint expectedWidth, uint expectedHeight) =>
        image.Bitmap is { } bitmap &&
        bitmap.SizeInPixels.Width == expectedWidth &&
        bitmap.SizeInPixels.Height == expectedHeight;

    private static async Task<ushort> ReadExifOrientationAsync(byte[] bytes)
    {
        using var stream = new InMemoryRandomAccessStream();
        await stream.WriteAsync(bytes.AsBuffer());
        stream.Seek(0);
        BitmapDecoder decoder = await BitmapDecoder.CreateAsync(stream);
        var properties = await decoder.BitmapProperties.GetPropertiesAsync(["System.Photo.Orientation"]);
        Assert.True(properties.ContainsKey("System.Photo.Orientation"));
        return Assert.IsType<ushort>(properties["System.Photo.Orientation"].Value);
    }

    internal static async Task<byte[]> CreateJpegAsync(int width, int height, ushort orientation)
    {
        byte[] pixels = new byte[checked(width * height * 4)];
        Array.Fill(pixels, (byte)0xFF);

        using var stream = new InMemoryRandomAccessStream();
        BitmapEncoder encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.JpegEncoderId, stream);
        encoder.SetPixelData(
            BitmapPixelFormat.Bgra8,
            BitmapAlphaMode.Premultiplied,
            checked((uint)width),
            checked((uint)height),
            96,
            96,
            pixels);
        var properties = new BitmapPropertySet();
        properties.Add(
            "System.Photo.Orientation",
            new BitmapTypedValue(orientation, PropertyType.UInt16));
        await encoder.BitmapProperties.SetPropertiesAsync(properties);
        await encoder.FlushAsync();

        stream.Seek(0);
        using var reader = new DataReader(stream.GetInputStreamAt(0));
        uint length = checked((uint)stream.Size);
        await reader.LoadAsync(length);
        byte[] bytes = new byte[checked((int)length)];
        reader.ReadBytes(bytes);
        return bytes;
    }

    private sealed class ByteResolver(byte[] bytes, string cacheKey) : IMarkdownImageResolver
    {
        public ValueTask<MarkdownImageResolution> ResolveAsync(
            string source,
            MarkdownImageResolveContext context,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(MarkdownImageResolution.Resolved(
                new MarkdownImageAsset(bytes, "image/jpeg", CacheKey: cacheKey)));
    }
}
