using System.Buffers.Binary;
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
/// Characterizes the existing static-image contract for a tiny animated WebP:
/// ImageBox publishes the first frame as a canvas, including its offset and
/// alpha, and never hands animation to a static provider. This does not test
/// playback, arbitrary WebP fidelity, or resource-limit behavior at scale.
/// </summary>
[Collection(SharedCanvasBitmapCacheTestCollection.Name)]
public sealed class AnimatedWebpFirstCanvasImageBoxTests(ITestOutputHelper output)
{
    [Trait("Requires", "WicWebpDecoder")]
    [Fact]
    public async Task ImageBoxPublishesFirstCanvasAndSkipsStaticProviderForAnimation()
    {
        SharedCanvasBitmapCache.Clear();
        await DisplaySizedRasterDecodeTests.WaitUntilAsync(
            () => SharedCanvasBitmapCache.RetainedBytesForTests == 0);

        byte[] source = CreateAnimatedWebp();
        RasterImageBudgetResult budget = RasterImageResourceBudget.Validate(source);
        Assert.True(budget.Accepted, $"The valid tiny WebP should fit the raster budget: {budget.Reason}");
        Assert.Equal("WEBP", budget.Format);
        Assert.Equal((3, 1, 2), (budget.Width, budget.Height, budget.FrameCount));
        await ValidateFixtureWithBitmapDecoderAsync(source);

        using var device = new CanvasDevice(forceSoftwareRenderer: true);
        Assert.True(device.ForceSoftwareRenderer,
            "The first-canvas characterization must execute on the deterministic Win2D software renderer.");

        byte[]? noProviderPixels = null;
        try
        {
            foreach (bool registerStaticProvider in new[] { false, true })
            {
                var resolver = new AnimatedWebpResolver(
                    source,
                    $"animated-webp-first-canvas-{registerStaticProvider}-{Guid.NewGuid():N}");
                var provider = registerStaticProvider ? new StaticWebpDecoderProbe() : null;
                using var session = new MarkdownPerformanceSession(
                    MarkdownPerformanceOptions.Progressive with
                    {
                        UseDisplaySizedRasterDecode = true,
                        MaxRasterOutputPixels = 16,
                    });
                MarkdownLayoutContext context = CreateContext(device, resolver, session, provider);
                var image = new ImageBox(context, "https://images.example/partial-animation.webp", "frame one");

                try
                {
                    image.Measure(3);
                    await DisplaySizedRasterDecodeTests.WaitForLoadAsync(image);
                    await DisplaySizedRasterDecodeTests.WaitUntilAsync(
                        () => image.ActiveImageWorkCountForTests == 0);

                    Assert.NotNull(image.Bitmap);
                    Assert.Equal(MarkdownImageAccessibilityState.Loaded, image.AccessibilityState);
                    Assert.Equal((3u, 1u), (image.Bitmap.SizeInPixels.Width, image.Bitmap.SizeInPixels.Height));

                    // The ANIM black background matches the semi-transparent
                    // black pixel. A reader may apply or ignore
                    // the hint for unused canvas and the transparent first-frame
                    // pixel independently; untouched canvas pixels must still
                    // agree with each other. Both valid first-pixel outcomes are
                    // enumerated below. The oracle pins frame zero at x=2 and
                    // rejects a raw 1x1 subframe or the later x=0 sentinel.
                    byte[] actual = image.Bitmap.GetPixelBytes();
                    try
                    {
                        AssertFirstCanvasPixels(actual);
                        output.WriteLine(
                            $"providerRegistered={registerStaticProvider}; published=3x1; BGRA={Convert.ToHexString(actual)}");
                        if (registerStaticProvider)
                        {
                            Assert.Equal(0, provider!.SupportsFormatCalls);
                            Assert.Equal(0, provider.DecodeCalls);
                            Assert.Equal(noProviderPixels, actual);
                        }
                        else
                        {
                            noProviderPixels = actual.ToArray();
                        }
                    }
                    finally
                    {
                        Array.Clear(actual);
                    }
                }
                finally
                {
                    image.Dispose();
                    await DisplaySizedRasterDecodeTests.WaitUntilAsync(
                        () => image.ActiveImageWorkCountForTests == 0);
                }
            }
        }
        finally
        {
            if (noProviderPixels is not null)
                Array.Clear(noProviderPixels);
            SharedCanvasBitmapCache.Clear();
            await DisplaySizedRasterDecodeTests.WaitUntilAsync(
                () => SharedCanvasBitmapCache.RetainedBytesForTests == 0);
        }
    }

    private static void AssertFirstCanvasPixels(byte[] actual)
    {
        Assert.Equal(12, actual.Length);

        byte[] transparent = [0, 0, 0, 0];
        byte[] hintedBackground = [0, 0, 0, 255];

        // ANIM background is optional per WebP's spec: readers may use it for
        // unused canvas or transparent first-frame pixels, and may ignore it.
        // Check each affected pixel independently so all those choices remain
        // valid, while the sentinel's unrelated color is still rejected.
        AssertPixelIsAllowed(actual.AsSpan(0, 4), transparent, hintedBackground, pixelIndex: 0);
        AssertPixelIsAllowed(actual.AsSpan(4, 4), transparent, hintedBackground, pixelIndex: 1);
        Assert.True(
            actual.AsSpan(0, 4).SequenceEqual(actual.AsSpan(4, 4)),
            "Identical unused canvas pixels must receive the same background-hint treatment.");

        // The frame-zero source pixel is RGBA(0,0,0,127), so its exact
        // premultiplied BGRA8 value on a transparent canvas is [0,0,0,127].
        // If the hint is used, source-over onto the same RGB is opaque black.
        AssertPixelIsAllowed(actual.AsSpan(8, 4), [0, 0, 0, 127], hintedBackground, pixelIndex: 2);
    }

    private static void AssertPixelIsAllowed(
        ReadOnlySpan<byte> actual,
        ReadOnlySpan<byte> first,
        ReadOnlySpan<byte> second,
        int pixelIndex)
    {
        Assert.True(
            actual.SequenceEqual(first) || actual.SequenceEqual(second),
            $"Unexpected frame-zero canvas pixel {pixelIndex}, BGRA bytes: {Convert.ToHexString(actual)}");
    }

    private static async Task ValidateFixtureWithBitmapDecoderAsync(byte[] source)
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

        Assert.Equal(2u, decoder.FrameCount);
        Assert.Equal(3u, decoder.PixelWidth);
        Assert.Equal(1u, decoder.PixelHeight);

        // Decode each encoded subframe independently only to establish that
        // the authored VP8L payloads are valid. Their pixels are deliberately
        // not used by the ImageBox final-canvas oracle.
        for (uint frameIndex = 0; frameIndex < decoder.FrameCount; frameIndex++)
        {
            IAsyncOperation<BitmapFrame> frameOperation = decoder.GetFrameAsync(frameIndex);
            BitmapFrame frame;
            try
            {
                frame = await frameOperation;
            }
            finally
            {
                frameOperation.Close();
            }

            var transform = new BitmapTransform();
            IAsyncOperation<SoftwareBitmap> bitmapOperation = frame.GetSoftwareBitmapAsync(
                BitmapPixelFormat.Bgra8,
                BitmapAlphaMode.Premultiplied,
                transform,
                ExifOrientationMode.IgnoreExifOrientation,
                ColorManagementMode.DoNotColorManage);
            SoftwareBitmap bitmap;
            try
            {
                bitmap = await bitmapOperation;
            }
            finally
            {
                bitmapOperation.Close();
            }

            using (bitmap)
            {
                Assert.True(bitmap.PixelWidth > 0);
                Assert.True(bitmap.PixelHeight > 0);
            }
        }
    }

    private static MarkdownLayoutContext CreateContext(
        CanvasDevice device,
        IMarkdownImageResolver resolver,
        MarkdownPerformanceSession session,
        IMarkdownRasterDecoder? rasterDecoder)
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
            new MarkdownSourceMap("animated WebP first-canvas characterization"),
            new MarkdownExtensionRegistry(),
            FlowDirection.LeftToRight,
            language: "en-US")
        {
            ImageResolver = resolver,
            RasterDecoder = rasterDecoder,
            PerformanceSession = session,
            PerformanceDocumentOwner = new object(),
            RasterizationScale = 1,
            ImageCancellationToken = CancellationToken.None,
        };
    }

    private static byte[] CreateAnimatedWebp()
    {
        using var chunks = new MemoryStream();

        byte[] extendedHeader = new byte[10];
        extendedHeader[0] = 0x12; // Alpha plus animation feature bits.
        WriteUInt24(extendedHeader.AsSpan(4), 2); // Canvas width minus one: 3 pixels.
        WriteUInt24(extendedHeader.AsSpan(7), 0); // Canvas height minus one: 1 pixel.
        WriteChunk(chunks, "VP8X"u8, extendedHeader);

        // WebP specifies this background as BGRA and permits a reader to ignore
        // it. Black makes both permitted source-over outcomes exact even if
        // blending occurs in linear light.
        WriteChunk(chunks, "ANIM"u8, [0, 0, 0, 255, 0, 0]);
        WriteChunk(chunks, "ANMF"u8, CreateFramePayload(
            encodedX: 1, // Position is encoded in pairs of pixels: x=2.
            vp8l: CreateLiteralVp8l(red: 0, green: 0, blue: 0, alpha: 127)));
        WriteChunk(chunks, "ANMF"u8, CreateFramePayload(
            encodedX: 0,
            vp8l: CreateLiteralVp8l(red: 255, green: 255, blue: 255, alpha: 255)));

        byte[] body = chunks.ToArray();
        byte[] result = new byte[checked(12 + body.Length)];
        "RIFF"u8.CopyTo(result);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(4), checked((uint)(result.Length - 8)));
        "WEBP"u8.CopyTo(result.AsSpan(8));
        body.CopyTo(result, 12);
        return result;
    }

    private static byte[] CreateFramePayload(uint encodedX, byte[] vp8l)
    {
        using var frame = new MemoryStream();
        byte[] header = new byte[16];
        WriteUInt24(header.AsSpan(0), encodedX);
        WriteUInt24(header.AsSpan(3), 0);
        WriteUInt24(header.AsSpan(6), 0); // Frame width minus one: 1 pixel.
        WriteUInt24(header.AsSpan(9), 0); // Frame height minus one: 1 pixel.
        WriteUInt24(header.AsSpan(12), 100);
        header[15] = 0; // Alpha-blend, do not dispose.
        frame.Write(header);
        WriteChunk(frame, "VP8L"u8, vp8l);
        return frame.ToArray();
    }

    /// <summary>
    /// Builds a lossless 1x1 VP8L payload with five one-symbol Huffman trees.
    /// The independently supplied channel values require no pixel stream.
    /// </summary>
    private static byte[] CreateLiteralVp8l(byte red, byte green, byte blue, byte alpha)
    {
        const int encodedBits = 32 + 3 + 5 * 11;
        byte[] payload = new byte[1 + ((encodedBits + 7) / 8)];
        payload[0] = 0x2f;
        int bitOffset = 8;
        WriteBits(0, 14); // Width minus one.
        WriteBits(0, 14); // Height minus one.
        WriteBits(1, 1); // Alpha is used.
        WriteBits(0, 3); // VP8L version zero.
        WriteBits(0, 3); // No transforms, color cache, or meta-Huffman image.
        WriteSingleSymbolTree(green);
        WriteSingleSymbolTree(red);
        WriteSingleSymbolTree(blue);
        WriteSingleSymbolTree(alpha);
        WriteSingleSymbolTree(0); // Distance tree is unused by a literal pixel.
        return payload;

        void WriteSingleSymbolTree(byte symbol)
        {
            WriteBits(1, 1); // Simple code-length tree.
            WriteBits(0, 1); // One symbol.
            WriteBits(1, 1); // Symbol is stored in eight bits.
            WriteBits(symbol, 8);
        }

        void WriteBits(uint value, int bitCount)
        {
            for (int bit = 0; bit < bitCount; bit++, bitOffset++)
                payload[bitOffset >> 3] |= (byte)(((value >> bit) & 1) << (bitOffset & 7));
        }
    }

    private static void WriteChunk(Stream destination, ReadOnlySpan<byte> fourCc, ReadOnlySpan<byte> payload)
    {
        Span<byte> header = stackalloc byte[8];
        fourCc.CopyTo(header);
        BinaryPrimitives.WriteUInt32LittleEndian(header[4..], checked((uint)payload.Length));
        destination.Write(header);
        destination.Write(payload);
        if ((payload.Length & 1) != 0)
            destination.WriteByte(0);
    }

    private static void WriteUInt24(Span<byte> destination, uint value)
    {
        destination[0] = (byte)value;
        destination[1] = (byte)(value >> 8);
        destination[2] = (byte)(value >> 16);
    }

    private sealed class AnimatedWebpResolver(byte[] source, string cacheKey) : IMarkdownImageResolver
    {
        public ValueTask<MarkdownImageResolution> ResolveAsync(
            string requestedSource,
            MarkdownImageResolveContext context,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(MarkdownImageResolution.Resolved(
                new MarkdownImageAsset(source, "image/webp", CacheKey: cacheKey)));
    }

    private sealed class StaticWebpDecoderProbe : IMarkdownRasterDecoder
    {
        private int _supportsFormatCalls;
        private int _decodeCalls;

        internal int SupportsFormatCalls => Volatile.Read(ref _supportsFormatCalls);
        internal int DecodeCalls => Volatile.Read(ref _decodeCalls);

        public bool SupportsFormat(MarkdownRasterFormat format)
        {
            Interlocked.Increment(ref _supportsFormatCalls);
            return format == MarkdownRasterFormat.WebP;
        }

        public ValueTask<MarkdownRaster> DecodeAsync(
            MarkdownRasterDecodeRequest request,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _decodeCalls);
            return ValueTask.FromException<MarkdownRaster>(new InvalidOperationException(
                "Static WebP decoding must never receive a multi-frame animation."));
        }
    }
}
