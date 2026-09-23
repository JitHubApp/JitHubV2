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
    [Theory]
    [InlineData(1.0, 200, 100)]
    [InlineData(1.5, 300, 150)]
    [InlineData(2.0, 400, 200)]
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

    private static async Task WaitForLoadAsync(ImageBox image)
    {
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        image.LoadCompleted += (_, _) => completed.TrySetResult();
        image.EnsureLoading();
        await completed.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }

    private static MarkdownLayoutContext CreateContext(
        IMarkdownImageResolver resolver,
        MarkdownPerformanceSession session,
        double scale)
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
            RasterizationScale = scale,
            ImageCancellationToken = CancellationToken.None,
        };
    }

    private static byte[] CreatePng(int width, int height)
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
}
