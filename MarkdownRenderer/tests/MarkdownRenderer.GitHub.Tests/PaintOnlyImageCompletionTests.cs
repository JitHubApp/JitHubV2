using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MarkdownRenderer.Document;
using MarkdownRenderer.Extensions;
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
public sealed class PaintOnlyImageCompletionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CompletedAuthoredRasterPreservesCommittedLazyOrInitialGeometry(bool lazy)
    {
        const float layoutWidth = 320;
        const string imageUrl = "https://images.example/paint-only.png";
        string source = "![badge](https://images.example/paint-only.png)";
        var resolver = new ByteResolver(CreatePng(2, 1), $"paint-only-{Guid.NewGuid():N}");
        using var session = new MarkdownPerformanceSession(MarkdownPerformanceOptions.Progressive);
        MarkdownSourceMap sourceMap = new(source);
        MarkdownLayoutContext context = CreateContext(sourceMap, resolver, session);
        int blockIndex = context.NextBlockIndex();
        var paragraph = new InlineContainerBox(context, MarkdownElementKeys.Body)
        {
            BlockIndex = blockIndex,
        };
        var imageRun = new InlineImageRun(
            context,
            "badge",
            imageUrl,
            requestedWidth: new SafeHtmlLength(80, IsPercent: false),
            requestedHeight: new SafeHtmlLength(40, IsPercent: false))
        {
            SourceSpan = new SourceSpan(0, source.Length),
        };
        paragraph.Add(imageRun);
        LayoutSnapshot? snapshot = null;
        try
        {
            paragraph.Measure(layoutWidth);
            paragraph.Arrange(0, 0, layoutWidth);
            snapshot = new LayoutSnapshot(
                [paragraph], sourceMap, layoutWidth, (float)paragraph.Bounds.Height);
            Assert.Equal(0L, snapshot.LayoutRevision);
            if (lazy)
            {
                snapshot.EnableLazyLayout(
                    layoutWidth,
                    viewportTop: 0,
                    viewportHeight: System.Math.Max(80, paragraph.Bounds.Height + 1),
                    overscan: 0,
                    CancellationToken.None);
                Assert.Equal(1, snapshot.MeasuredTopLevelBlockCount);
            }

            ImageBox image = imageRun.Image;
            Assert.False(snapshot.TryCanUsePaintOnlyImageCompletion(image, layoutWidth));
            Rect beforeBounds = paragraph.Bounds;
            Size beforeSize = snapshot.Size;
            long beforeRevision = snapshot.LayoutRevision;
            string beforeSemanticText = snapshot.SemanticDocument.Text;
            var beforeTextProjection = snapshot.SemanticDocument.TextProjectionFingerprint;
            var beforeSemanticRanges = snapshot.SemanticDocument.SemanticRangeFingerprint;
            var beforeImageRect = Assert.Single(paragraph.EnumerateInlineImageRects()).Rect;
            bool beforeHit = snapshot.HitTest(
                Center(beforeImageRect), out DocumentPosition beforePosition);
            Assert.True(beforeHit);

            await DisplaySizedRasterDecodeTests.WaitForLoadAsync(image);

            Assert.NotNull(image.Bitmap);
            Assert.Equal(80f, image.MeasuredImageWidth, precision: 3);
            Assert.Equal(40f, image.MeasuredImageHeight, precision: 3);
            Assert.True(snapshot.TryCanUsePaintOnlyImageCompletion(image, layoutWidth));

            Assert.Equal(beforeRevision, snapshot.LayoutRevision);
            Assert.Equal(beforeSize, snapshot.Size);
            Assert.Equal(beforeBounds, paragraph.Bounds);
            Assert.Equal(beforeSemanticText, snapshot.SemanticDocument.Text);
            Assert.Equal(beforeTextProjection, snapshot.SemanticDocument.TextProjectionFingerprint);
            Assert.Equal(beforeSemanticRanges, snapshot.SemanticDocument.SemanticRangeFingerprint);
            Assert.Equal(beforeImageRect, Assert.Single(paragraph.EnumerateInlineImageRects()).Rect);
            Assert.True(snapshot.HitTest(Center(beforeImageRect), out DocumentPosition afterPosition));
            Assert.Equal(beforePosition, afterPosition);
        }
        finally
        {
            if (snapshot is not null)
                await snapshot.Retire().WaitAsync(TimeSpan.FromSeconds(10));
            else
                paragraph.Dispose();
        }
    }

    [Fact]
    public async Task FailedOuterRelayoutTaintsPaintOnlyProofEvenAfterPlacementRestore()
    {
        const float layoutWidth = 320;
        const string imageUrl = "https://images.example/paint-only-tainted.png";
        var resolver = new ByteResolver(CreatePng(2, 1), $"paint-only-tainted-{Guid.NewGuid():N}");
        using var session = new MarkdownPerformanceSession(MarkdownPerformanceOptions.Progressive);
        var sourceMap = new MarkdownSourceMap(imageUrl);
        MarkdownLayoutContext context = CreateContext(sourceMap, resolver, session);
        var image = new ImageBox(
            context,
            imageUrl,
            "badge",
            new SafeHtmlLength(80, IsPercent: false),
            new SafeHtmlLength(40, IsPercent: false))
        {
            BlockIndex = context.NextBlockIndex(),
        };
        var second = new ThrowingMeasureBlock { BlockIndex = context.NextBlockIndex() };
        LayoutSnapshot? snapshot = null;
        try
        {
            image.Measure(layoutWidth);
            image.Arrange(0, 0, layoutWidth);
            float secondHeight = second.Measure(layoutWidth);
            second.Arrange(0, (float)image.Bounds.Bottom, layoutWidth);
            snapshot = new LayoutSnapshot(
                [image, second], sourceMap, layoutWidth, (float)image.Bounds.Height + secondHeight);
            await DisplaySizedRasterDecodeTests.WaitForLoadAsync(image);
            Assert.NotNull(image.Bitmap);
            Assert.True(snapshot.TryCanUsePaintOnlyImageCompletion(image, layoutWidth));

            Rect committedImageBounds = image.Bounds;
            second.ThrowOnMeasure = true;
            Assert.Throws<InvalidOperationException>(
                () => snapshot.RelayoutMeasuredBlocks(layoutWidth, CancellationToken.None));

            Assert.Equal(committedImageBounds, image.Bounds);
            Assert.False(snapshot.TryCanUsePaintOnlyImageCompletion(image, layoutWidth));
        }
        finally
        {
            if (snapshot is not null)
                await snapshot.Retire().WaitAsync(TimeSpan.FromSeconds(10));
            else
            {
                image.Dispose();
                second.Dispose();
            }
        }
    }

    [Fact]
    public async Task FastPathRequiresOptInAndTwoFinitePositiveAuthoredDimensions()
    {
        const float layoutWidth = 320;
        const string imageUrl = "https://images.example/paint-only-rejected.png";
        var resolver = new ByteResolver(CreatePng(2, 1), $"paint-only-rejected-{Guid.NewGuid():N}");
        var sourceMap = new MarkdownSourceMap(imageUrl);
        MarkdownLayoutContext context = CreateContext(sourceMap, resolver, session: null);
        var image = new ImageBox(
            context,
            imageUrl,
            "badge",
            new SafeHtmlLength(80, IsPercent: false),
            new SafeHtmlLength(40, IsPercent: false))
        {
            BlockIndex = context.NextBlockIndex(),
        };
        LayoutSnapshot? snapshot = null;
        try
        {
            image.Measure(layoutWidth);
            image.Arrange(0, 0, layoutWidth);
            snapshot = new LayoutSnapshot([image], sourceMap, layoutWidth, (float)image.Bounds.Height);
            await DisplaySizedRasterDecodeTests.WaitForLoadAsync(image);
            Assert.NotNull(image.Bitmap);
            Assert.False(snapshot.TryCanUsePaintOnlyImageCompletion(image, layoutWidth));
            Assert.False(snapshot.TryCanUsePaintOnlyImageCompletion(image, layoutWidth + 1));
        }
        finally
        {
            if (snapshot is not null)
                await snapshot.Retire().WaitAsync(TimeSpan.FromSeconds(10));
            else
                image.Dispose();
        }
    }

    [Theory]
    [InlineData(1, 0)]
    [InlineData(0, 1)]
    [InlineData(2, 1)]
    [InlineData(1, 2)]
    public async Task FastPathRejectsMissingOrPercentAuthoredDimensions(
        int widthKind,
        int heightKind)
    {
        const float layoutWidth = 320;
        const string imageUrl = "https://images.example/paint-only-dimension-shape.png";
        var resolver = new ByteResolver(CreatePng(2, 1), $"paint-only-dimension-{Guid.NewGuid():N}");
        using var session = new MarkdownPerformanceSession(MarkdownPerformanceOptions.Progressive);
        var sourceMap = new MarkdownSourceMap(imageUrl);
        MarkdownLayoutContext context = CreateContext(sourceMap, resolver, session);
        SafeHtmlLength? requestedWidth = widthKind == 0
            ? null
            : new SafeHtmlLength(80, IsPercent: widthKind == 2);
        SafeHtmlLength? requestedHeight = heightKind == 0
            ? null
            : new SafeHtmlLength(40, IsPercent: heightKind == 2);
        var image = new ImageBox(
            context,
            imageUrl,
            "badge",
            requestedWidth,
            requestedHeight)
        {
            BlockIndex = context.NextBlockIndex(),
        };
        LayoutSnapshot? snapshot = null;
        try
        {
            image.Measure(layoutWidth);
            image.Arrange(0, 0, layoutWidth);
            snapshot = new LayoutSnapshot([image], sourceMap, layoutWidth, (float)image.Bounds.Height);
            await DisplaySizedRasterDecodeTests.WaitForLoadAsync(image);

            Assert.NotNull(image.Bitmap);
            Assert.False(snapshot.TryCanUsePaintOnlyImageCompletion(image, layoutWidth));
        }
        finally
        {
            if (snapshot is not null)
                await snapshot.Retire().WaitAsync(TimeSpan.FromSeconds(10));
            else
                image.Dispose();
        }
    }

    private static Point Center(Rect rect)
        => new(rect.X + rect.Width / 2, rect.Y + rect.Height / 2);

    private static MarkdownLayoutContext CreateContext(
        MarkdownSourceMap sourceMap,
        IMarkdownImageResolver resolver,
        MarkdownPerformanceSession? session)
    {
        var body = new ElementStyle();
        var styles = new Dictionary<string, ElementStyle>(StringComparer.Ordinal)
        {
            [MarkdownElementKeys.Body] = body,
            [MarkdownElementKeys.ImageCaption] = body,
            [MarkdownElementKeys.ListMarker] = body,
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
            CanvasDevice.GetSharedDevice(),
            theme,
            sourceMap,
            new MarkdownExtensionRegistry(),
            FlowDirection.LeftToRight,
            language: "en-US")
        {
            ImageResolver = resolver,
            PerformanceSession = session,
            PerformanceDocumentOwner = session is null ? null : new object(),
            RasterizationScale = 1,
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
        header[8] = 8;
        header[9] = 6;
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

    private sealed class ThrowingMeasureBlock : BlockBox
    {
        internal bool ThrowOnMeasure { get; set; }

        public override float Measure(float availableWidth)
        {
            if (ThrowOnMeasure)
                throw new InvalidOperationException("Synthetic measure failure.");

            Bounds = new Rect(0, 0, availableWidth, 8);
            return 8;
        }

        public override void Paint(CanvasDrawingSession ds, Rect viewport) { }
    }
}
