using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics.Tracing;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using MarkdownRenderer.Controls;
using MarkdownRenderer.Images;
using MarkdownRenderer.Svg.Resvg.Internal;
using Xunit;

namespace MarkdownRenderer.Svg.Resvg.Tests;

public sealed class ResvgProviderTests
{
    [Fact]
    public void HostPreparationAdmitsStaticSvgAndReturnsOrdinaryRejectionWithoutThrowing()
    {
        using var renderer = new ResvgMarkdownSvgRenderer();
        MarkdownSvgSourcePreparation admitted = renderer.PrepareSource(
            Svg("<svg xmlns='http://www.w3.org/2000/svg'><rect width='1' height='1'/></svg>"),
            CancellationToken.None);
        Assert.NotNull(admitted.SanitizedBytes);
        Assert.Null(admitted.FailureReason);

        MarkdownSvgSourcePreparation rejected = renderer.PrepareSource(
            Svg("<svg xmlns='http://www.w3.org/2000/svg'><image href='https://example.test/a.png'/></svg>"),
            CancellationToken.None);
        Assert.Null(rejected.SanitizedBytes);
        Assert.Equal(MarkdownSvgFailureReason.UnsupportedContent, rejected.FailureReason);
        Assert.Contains("external-image-reference", rejected.FailureDescription, StringComparison.Ordinal);
    }

    [Fact]
    public void HostPreflightRejectionEmitsReasonLengthAndHashWithoutSourceBytes()
    {
        using var listener = new PreflightListener();
        using var renderer = new ResvgMarkdownSvgRenderer();
        byte[] source = Svg("<html><body>not SVG</body></html>");

        MarkdownSvgSourcePreparation rejected = renderer.PrepareSource(source, CancellationToken.None);

        Assert.Equal(MarkdownSvgFailureReason.UnsupportedContent, rejected.FailureReason);
        Assert.Contains(
            ("missing-root", source.Length, Convert.ToHexString(SHA256.HashData(source))),
            listener.Rejections);
    }

    private sealed class PreflightListener : EventListener
    {
        internal ConcurrentQueue<(string Reason, int Length, string Hash)> Rejections { get; } = new();

        protected override void OnEventSourceCreated(EventSource eventSource)
        {
            if (eventSource.Name == "MarkdownRenderer.Svg.Resvg.Preflight")
                EnableEvents(eventSource, EventLevel.Warning);
        }

        protected override void OnEventWritten(EventWrittenEventArgs eventData)
        {
            if (eventData.EventId == 1 && eventData.Payload is { Count: 3 } payload &&
                payload[0] is string reason && payload[1] is int length &&
                payload[2] is string hash)
            {
                Rejections.Enqueue((reason, length, hash));
            }
        }
    }

    [Fact]
    public async Task WarmUp_UsesInitializationDeadlineInsteadOfContentDeadline()
    {
        var options = new ResvgMarkdownSvgRendererOptions(
            requestDeadline: TimeSpan.FromMilliseconds(1));
        await using var renderer = new ResvgMarkdownSvgRenderer(options);

        await renderer.WarmUpAsync();
    }

    [Fact]
    public async Task FirstTextOpen_DoesNotChargeColdTextPipelineToContentDeadline()
    {
        var options = new ResvgMarkdownSvgRendererOptions(
            requestDeadline: TimeSpan.FromMilliseconds(500));
        await using var renderer = new ResvgMarkdownSvgRenderer(options);

        using IMarkdownSvgDocument document = await renderer.OpenAsync(
            new MarkdownSvgOpenRequest(Svg(
                "<svg xmlns='http://www.w3.org/2000/svg' width='96' height='24'>" +
                "<text x='1' y='18'>Cold text</text></svg>")));
        using MarkdownSvgRaster raster = await document.RenderAsync(
            new MarkdownSvgRenderRequest(96, 24));

        Assert.True(document.Info.HasText);
        Assert.Equal(96, raster.WidthPixels);
        Assert.Equal(24, raster.HeightPixels);
    }

    [Fact]
    public async Task BrowserFontFallbackAndCssNamespace_AreWarmBeforeContentDeadline()
    {
        const string source = """
            <svg viewBox="0 0 800 130" xmlns="http://www.w3.org/2000/svg">
              <style>
                @namespace svg url(http://www.w3.org/2000/svg);
                svg { font-family: Helvetica, Arial, sans-serif; text-rendering: geometricPrecision; }
                svg|a:link, svg|a:visited { cursor: pointer; }
                .message { fill: white; font-size: 18px; }
                .call { fill: black; font-size: 22px; }
              </style>
              <defs><clipPath id="round-corners"><rect width="100%" height="100%" rx="3" /></clipPath></defs>
              <a href="https://example.invalid">
                <g clip-path="url(#round-corners)">
                  <rect width="100%" height="100%" fill="#ffd700"/>
                  <rect width="100%" height="90" fill="#0056b3"/>
                </g>
                <text x="0" y="25" class="message">
                  <tspan x="30" dy="0.8em">Static SVG text must not pay cold Arial initialization.</tspan>
                  <tspan x="30" dy="1.2em">The content deadline covers this document only.</tspan>
                </text>
                <text x="50%" y="86%" dominant-baseline="middle" text-anchor="middle" class="call">Help Now ➔</text>
              </a>
            </svg>
            """;
        var options = new ResvgMarkdownSvgRendererOptions(
            requestDeadline: TimeSpan.FromMilliseconds(500));
        await using var renderer = new ResvgMarkdownSvgRenderer(options);

        using IMarkdownSvgDocument document = await renderer.OpenAsync(
            new MarkdownSvgOpenRequest(Svg(source)));
        using MarkdownSvgRaster raster = await document.RenderAsync(
            new MarkdownSvgRenderRequest(800, 130));

        Assert.True(document.Info.HasText);
        Assert.Equal(800, raster.WidthPixels);
        Assert.Equal(130, raster.HeightPixels);
        Assert.True(HasVisiblePixel(raster.Pixels.Span));
    }

    [Fact]
    public async Task CjkBadgeFallback_IsWarmBeforeContentDeadline()
    {
        // Pinned, network-free forms of the two font-family orders used by
        // real-world Chinese SVG badges. Both must pay cold fallback setup
        // during HELLO, not inside a per-content Open transaction.
        string[] badges =
        [
            "<svg xmlns='http://www.w3.org/2000/svg' width='164' height='20'>" +
            "<text x='2' y='15' font-family='Verdana,Geneva,DejaVu Sans,sans-serif' " +
            "font-size='12'>前台商城项目 mall-app-web</text></svg>",
            "<svg xmlns='http://www.w3.org/2000/svg' width='76' height='20'>" +
            "<text x='2' y='15' font-family='DejaVu Sans,Verdana,Geneva,sans-serif' " +
            "font-size='12'>交流 微信群</text></svg>",
        ];
        var options = new ResvgMarkdownSvgRendererOptions(
            requestDeadline: TimeSpan.FromSeconds(1));
        await using var renderer = new ResvgMarkdownSvgRenderer(options);

        foreach (string badge in badges)
        {
            using IMarkdownSvgDocument document = await renderer.OpenAsync(
                new MarkdownSvgOpenRequest(Svg(badge)));
            using MarkdownSvgRaster raster = await document.RenderAsync(
                new MarkdownSvgRenderRequest(164, 20));

            Assert.True(document.Info.HasText);
            Assert.True(HasVisiblePixel(raster.Pixels.Span));
        }
    }

    private static byte[] Svg(string value) => Encoding.UTF8.GetBytes(value);

    [Fact]
    public async Task FullPipeline_RendersSharedMemoryPremultipliedRgbaAndTile()
    {
        await using var renderer = new ResvgMarkdownSvgRenderer();
        await renderer.WarmUpAsync();
        using IMarkdownSvgDocument document = await renderer.OpenAsync(new MarkdownSvgOpenRequest(
            Svg("<svg xmlns='http://www.w3.org/2000/svg' width='32' height='16'><desc>Red sample</desc><rect width='32' height='16' fill='#ff0000'/></svg>")));

        Assert.Equal(32, document.Info.IntrinsicWidthDips);
        Assert.Equal(16, document.Info.IntrinsicHeightDips);
        Assert.Equal("Red sample", document.Info.Description);
        using MarkdownSvgRaster raster = await document.RenderAsync(new MarkdownSvgRenderRequest(32, 16));
        Assert.Equal(32 * 16 * 4, raster.Pixels.Length);
        Assert.Equal(MarkdownSvgPixelFormat.Rgba8Premultiplied, raster.PixelFormat);
        int center = ((8 * 32) + 16) * 4;
        Assert.InRange(raster.Pixels.Span[center], (byte)240, byte.MaxValue);
        Assert.InRange(raster.Pixels.Span[center + 1], (byte)0, (byte)15);
        Assert.InRange(raster.Pixels.Span[center + 3], (byte)240, byte.MaxValue);

        using MarkdownSvgRaster tile = await document.RenderAsync(new MarkdownSvgRenderRequest(
            32,
            16,
            new MarkdownSvgTileRegion(8, 4, 8, 8),
            MarkdownSvgPixelFormat.Bgra8Premultiplied));
        Assert.Equal(8, tile.WidthPixels);
        Assert.Equal(8, tile.HeightPixels);
        Assert.Equal(MarkdownSvgPixelFormat.Bgra8Premultiplied, tile.PixelFormat);
        Assert.InRange(tile.Pixels.Span[2], (byte)240, byte.MaxValue);
    }

    [Fact]
    public async Task CurrentColor_UsesOnlyTheSemanticColorInput()
    {
        await using var renderer = new ResvgMarkdownSvgRenderer();
        using IMarkdownSvgDocument document = await renderer.OpenAsync(new MarkdownSvgOpenRequest(
            Svg("<svg xmlns='http://www.w3.org/2000/svg' width='4' height='4'><rect width='4' height='4' fill='currentColor'/></svg>"),
            SemanticColor: new MarkdownSvgColor(0, 120, 212)));
        Assert.True(document.Info.UsesCurrentColor);
        using MarkdownSvgRaster raster = await document.RenderAsync(new MarkdownSvgRenderRequest(4, 4));
        Assert.InRange(raster.Pixels.Span[0], (byte)0, (byte)10);
        Assert.InRange(raster.Pixels.Span[1], (byte)110, (byte)130);
        Assert.InRange(raster.Pixels.Span[2], (byte)200, (byte)220);
    }

    [Fact]
    public async Task CurrentColor_DistinguishesAbsentColorFromOpaqueWhite()
    {
        byte[] source = Svg("<svg xmlns='http://www.w3.org/2000/svg' width='2' height='2'><rect width='2' height='2' fill='currentColor'/></svg>");
        await using var renderer = new ResvgMarkdownSvgRenderer();
        using IMarkdownSvgDocument defaultDocument = await renderer.OpenAsync(new MarkdownSvgOpenRequest(source));
        using MarkdownSvgRaster defaultRaster = await defaultDocument.RenderAsync(new MarkdownSvgRenderRequest(2, 2));
        using IMarkdownSvgDocument whiteDocument = await renderer.OpenAsync(new MarkdownSvgOpenRequest(
            source,
            SemanticColor: new MarkdownSvgColor(byte.MaxValue, byte.MaxValue, byte.MaxValue)));
        using MarkdownSvgRaster whiteRaster = await whiteDocument.RenderAsync(new MarkdownSvgRenderRequest(2, 2));

        Assert.InRange(defaultRaster.Pixels.Span[0], (byte)0, (byte)10);
        Assert.InRange(defaultRaster.Pixels.Span[3], (byte)245, byte.MaxValue);
        Assert.InRange(whiteRaster.Pixels.Span[0], (byte)245, byte.MaxValue);
        Assert.InRange(whiteRaster.Pixels.Span[1], (byte)245, byte.MaxValue);
        Assert.InRange(whiteRaster.Pixels.Span[2], (byte)245, byte.MaxValue);
        Assert.InRange(whiteRaster.Pixels.Span[3], (byte)245, byte.MaxValue);
    }

    [Fact]
    public async Task CssDataImages_AreDecodedChargedAndDeduplicatedAcrossEscapesAndQuotes()
    {
        byte[] png = CreatePng(10, 20, 30, byte.MaxValue);
        string payload = Convert.ToBase64String(png);
        string source = $"<svg xmlns='http://www.w3.org/2000/svg' width='1' height='1'><style>.a{{fill:url(\"data\\3a image/png;base64,{payload}\")}}.b{{fill:url('data:image/png;base64,{payload}')}}</style><rect class='a' width='1' height='1'/></svg>";
        var options = new ResvgMarkdownSvgRendererOptions(
            maxEmbeddedImageBytes: png.Length,
            maxEmbeddedImagePixels: 1);
        await using var renderer = new ResvgMarkdownSvgRenderer(options);

        using IMarkdownSvgDocument document = await renderer.OpenAsync(new MarkdownSvgOpenRequest(Svg(source)));
        Assert.Equal(1, document.Info.IntrinsicWidthDips);
    }

    [Fact]
    public async Task OpenCollectiveFalseMediaTypePng_IsSniffedAndRendered()
    {
        byte[] png = CreatePng(24, 120, 220, byte.MaxValue);
        string source = $"<svg xmlns='http://www.w3.org/2000/svg' width='1' height='1'><image width='1' height='1' href='data:false;base64,{Convert.ToBase64String(png)}'/></svg>";
        await using var renderer = new ResvgMarkdownSvgRenderer();

        using IMarkdownSvgDocument document = await renderer.OpenAsync(new MarkdownSvgOpenRequest(Svg(source)));
        using MarkdownSvgRaster raster = await document.RenderAsync(new MarkdownSvgRenderRequest(1, 1));

        Assert.InRange(raster.Pixels.Span[0], (byte)20, (byte)28);
        Assert.InRange(raster.Pixels.Span[1], (byte)116, (byte)124);
        Assert.InRange(raster.Pixels.Span[2], (byte)216, (byte)224);
        Assert.InRange(raster.Pixels.Span[3], (byte)250, byte.MaxValue);
    }

    [Fact]
    public async Task MislabeledGifDataUriWithPngBytes_IsSniffedAndRendered()
    {
        byte[] png = CreatePng(24, 120, 220, byte.MaxValue);
        string source = $"<svg xmlns='http://www.w3.org/2000/svg' width='1' height='1'><image width='1' height='1' href='data:image/gif;base64,{Convert.ToBase64String(png)}'/></svg>";
        await using var renderer = new ResvgMarkdownSvgRenderer();

        using IMarkdownSvgDocument document = await renderer.OpenAsync(new MarkdownSvgOpenRequest(Svg(source)));
        using MarkdownSvgRaster raster = await document.RenderAsync(new MarkdownSvgRenderRequest(1, 1));

        Assert.InRange(raster.Pixels.Span[0], (byte)20, (byte)28);
        Assert.InRange(raster.Pixels.Span[1], (byte)116, (byte)124);
        Assert.InRange(raster.Pixels.Span[2], (byte)216, (byte)224);
    }

    [Fact]
    public async Task NaturalLanguageMetadataWithApostrophe_IsNotParsedAsCss()
    {
        const string source =
            "<svg xmlns='http://www.w3.org/2000/svg' width='10' height='10' " +
            "aria-label=\"OmniRoute's routing diagram\"><rect width='10' height='10' fill='green'/></svg>";
        await using var renderer = new ResvgMarkdownSvgRenderer();

        using IMarkdownSvgDocument document = await renderer.OpenAsync(
            new MarkdownSvgOpenRequest(Svg(source)));
        using MarkdownSvgRaster raster = await document.RenderAsync(new MarkdownSvgRenderRequest(10, 10));

        Assert.Equal(10, raster.WidthPixels);
        Assert.Equal(10, raster.HeightPixels);
    }

    [Fact]
    public async Task MotionNamedCssSelectorsRemainStaticContent()
    {
        const string source =
            "<svg xmlns='http://www.w3.org/2000/svg' width='10' height='10'><style>" +
            ".animation:hover{fill:#123456}.transition[data-state='ready']{stroke:#abcdef}" +
            "</style><rect class='animation transition' data-state='ready' width='10' height='10'/></svg>";
        await using var renderer = new ResvgMarkdownSvgRenderer();

        using IMarkdownSvgDocument document = await renderer.OpenAsync(
            new MarkdownSvgOpenRequest(Svg(source)));
        using MarkdownSvgRaster raster = await document.RenderAsync(new MarkdownSvgRenderRequest(10, 10));

        Assert.Equal(10, raster.WidthPixels);
        Assert.Equal(10, raster.HeightPixels);
    }

    [Theory]
    [InlineData("animation : pulse 1s infinite")]
    [InlineData("animation-name : pulse")]
    [InlineData("transition : opacity 1s")]
    [InlineData("-webkit-animation : pulse 1s infinite")]
    public async Task MotionDeclarationsWithWhitespaceRemainRejected(string declaration)
    {
        string source =
            $"<svg xmlns='http://www.w3.org/2000/svg'><style>.a{{{declaration}}}</style></svg>";
        await using var renderer = new ResvgMarkdownSvgRenderer();

        MarkdownSvgException failure = await Assert.ThrowsAsync<MarkdownSvgException>(async () =>
            await renderer.OpenAsync(new MarkdownSvgOpenRequest(Svg(source))));

        Assert.Equal(MarkdownSvgFailureReason.UnsupportedContent, failure.Reason);
    }

    [Fact]
    public async Task UnknownMediaTypeWithoutRecognizedRasterSignature_RemainsRejected()
    {
        string source = "<svg xmlns='http://www.w3.org/2000/svg' width='1' height='1'><image width='1' height='1' href='data:false;base64,AAECAwQ='/></svg>";
        await using var renderer = new ResvgMarkdownSvgRenderer();

        MarkdownSvgException failure = await Assert.ThrowsAsync<MarkdownSvgException>(async () =>
            await renderer.OpenAsync(new MarkdownSvgOpenRequest(Svg(source))));

        Assert.Equal(MarkdownSvgFailureReason.UnsupportedContent, failure.Reason);
    }

    [Theory]
    [InlineData("<svg xmlns='http://www.w3.org/2000/svg'><rect style=\"fill:u\\72 l('https\\3a //example.test/x')\"/></svg>")]
    [InlineData("<svg xmlns='http://www.w3.org/2000/svg'><style>.a{fill:url('data:image/png;base64,iVBORw0KGgo=')}</style></svg>")]
    public async Task HostPreflight_RejectsEscapedExternalAndMalformedCssImages(string source)
    {
        await using var renderer = new ResvgMarkdownSvgRenderer();
        MarkdownSvgException failure = await Assert.ThrowsAsync<MarkdownSvgException>(async () =>
            await renderer.OpenAsync(new MarkdownSvgOpenRequest(Svg(source))));
        Assert.Equal(MarkdownSvgFailureReason.UnsupportedContent, failure.Reason);
    }

    [Theory]
    [InlineData("<svg xmlns='http://www.w3.org/2000/svg'><script>alert(1)</script></svg>")]
    [InlineData("<svg xmlns='http://www.w3.org/2000/svg'><image href='https://example.test/a.png'/></svg>")]
    [InlineData("<!DOCTYPE svg [<!ENTITY x 'boom'>]><svg xmlns='http://www.w3.org/2000/svg'>&x;</svg>")]
    [InlineData("<svg xmlns='http://www.w3.org/2000/svg'><style>@keyframes x { from { opacity:0 } }</style></svg>")]
    public async Task HostPreflight_RejectsExecutableOrExternalContent(string source)
    {
        await using var renderer = new ResvgMarkdownSvgRenderer();
        MarkdownSvgException failure = await Assert.ThrowsAsync<MarkdownSvgException>(async () =>
            await renderer.OpenAsync(new MarkdownSvgOpenRequest(Svg(source))));
        Assert.Equal(MarkdownSvgFailureReason.UnsupportedContent, failure.Reason);
    }

    [Fact]
    public async Task OpenAsync_AcceptsLegacyExternalSvgDoctypeWithoutResolvingIt()
    {
        const string source =
            "<!DOCTYPE svg PUBLIC '-//W3C//DTD SVG 1.1//EN' " +
            "'http://www.w3.org/Graphics/SVG/1.1/DTD/svg11.dtd'>" +
            "<svg xmlns='http://www.w3.org/2000/svg' width='8' height='8'>" +
            "<rect width='8' height='8' fill='green'/></svg>";
        await using var renderer = new ResvgMarkdownSvgRenderer();

        using IMarkdownSvgDocument document = await renderer.OpenAsync(
            new MarkdownSvgOpenRequest(Svg(source)));

        Assert.Equal(8, document.Info.IntrinsicWidthDips);
        Assert.Equal(8, document.Info.IntrinsicHeightDips);
    }

    [Fact]
    public async Task OpenAsync_AcceptsCssNamespaceIdentifierWithoutResolvingIt()
    {
        const string source =
            "<svg xmlns='http://www.w3.org/2000/svg' width='8' height='8'><style>" +
            "@namespace svg url(http://www.w3.org/2000/svg);" +
            "svg|rect { fill: green; }</style><rect width='8' height='8'/></svg>";
        await using var renderer = new ResvgMarkdownSvgRenderer();

        using IMarkdownSvgDocument document = await renderer.OpenAsync(
            new MarkdownSvgOpenRequest(Svg(source)));

        Assert.Equal(8, document.Info.IntrinsicWidthDips);
        Assert.Equal(8, document.Info.IntrinsicHeightDips);
    }

    [Fact]
    public async Task Worker_RecursivelyRejectsExternalReferenceInsideNestedSvg()
    {
        string nested = Convert.ToBase64String(Svg(
            "<svg xmlns='http://www.w3.org/2000/svg' width='1' height='1'><image href='file:///c:/secret.png'/></svg>"));
        string outer = $"<svg xmlns='http://www.w3.org/2000/svg' width='1' height='1'><image width='1' height='1' href='data:image/svg+xml;base64,{nested}'/></svg>";
        await using var renderer = new ResvgMarkdownSvgRenderer();
        MarkdownSvgException failure = await Assert.ThrowsAsync<MarkdownSvgException>(async () =>
            await renderer.OpenAsync(new MarkdownSvgOpenRequest(Svg(outer))));
        Assert.Equal(MarkdownSvgFailureReason.UnsupportedContent, failure.Reason);
    }

    [Fact]
    public async Task Worker_EnforcesNestedSvgDepth()
    {
        string nested = "<svg xmlns='http://www.w3.org/2000/svg' width='1' height='1'><rect width='1' height='1'/></svg>";
        for (int depth = 0; depth < 5; depth++)
            nested = $"<svg xmlns='http://www.w3.org/2000/svg' width='1' height='1'><image width='1' height='1' href='data:image/svg+xml;base64,{Convert.ToBase64String(Svg(nested))}'/></svg>";
        await using var renderer = new ResvgMarkdownSvgRenderer();
        MarkdownSvgException failure = await Assert.ThrowsAsync<MarkdownSvgException>(async () =>
            await renderer.OpenAsync(new MarkdownSvgOpenRequest(Svg(nested))));
        Assert.Equal(MarkdownSvgFailureReason.ResourceLimitExceeded, failure.Reason);
    }

    [Fact]
    public void ProtocolDecoder_RejectsWrongRequestIdAndMalformedReservedData()
    {
        byte[] response = new byte[WorkerProtocol.ResponseSize];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(response, WorkerProtocol.Magic);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(response.AsSpan(4), WorkerProtocol.Version);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(response.AsSpan(8), 42);
        Assert.Throws<WorkerProtocolException>(() => WorkerProtocol.DecodeResponse(response, 41, 0, 0));

        System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(response.AsSpan(8), 41);
        response[207] = 1;
        Assert.Throws<WorkerProtocolException>(() => WorkerProtocol.DecodeResponse(response, 41, 0, 0));

        response[207] = 0;
        response[206] = 1;
        Assert.Throws<WorkerProtocolException>(() => WorkerProtocol.DecodeResponse(response, 41, 0, 0));
    }

    [Fact]
    public void ProtocolDecoder_AdmitsNonblockingFontCatalogPending()
    {
        byte[] response = new byte[WorkerProtocol.ResponseSize];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(response, WorkerProtocol.Magic);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(response.AsSpan(4), WorkerProtocol.Version);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(response.AsSpan(6), (ushort)WorkerStatus.FontCatalogPending);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(response.AsSpan(8), 41);

        WorkerResponse decoded = WorkerProtocol.DecodeResponse(response, 41, 0, 0);

        Assert.Equal(WorkerStatus.FontCatalogPending, decoded.Status);
        Assert.Equal(0, decoded.OutputLength);
    }

    [Fact]
    public void RenderProtocol_UsesDocumentTokenAndOutputOnlyMapping()
    {
        var request = new WorkerRequest(
            WorkerOperation.Render,
            7,
            11,
            13,
            "Local\\output",
            new byte[32],
            SourceLength: 0,
            OutputLength: 64,
            TargetWidth: 4,
            TargetHeight: 4,
            Tile: null,
            Locale: "en",
            ColorScheme: MarkdownSvgColorScheme.Light,
            SemanticColor: null,
            PixelFormat: MarkdownSvgPixelFormat.Rgba8Premultiplied,
            FontGeneration: 0,
            ParsedResourceCacheBytes: 1024,
            DocumentId: 0x1020_3040_5060_7080,
            Options: new ResvgMarkdownSvgRendererOptions());

        byte[] frame = WorkerProtocol.Encode(request);
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(frame.AsSpan(36)));
        Assert.Equal(64u, BinaryPrimitives.ReadUInt32LittleEndian(frame.AsSpan(40)));
        Assert.Equal(0x1020_3040_5060_7080ul, BinaryPrimitives.ReadUInt64LittleEndian(frame.AsSpan(368)));
    }

    [Fact]
    public void PublicPlumbing_ExposesControlBuilderAndDependencyProperty()
    {
        using var renderer = new ResvgMarkdownSvgRenderer();
        var builder = new MarkdownRendererControlBuilder();
        Assert.Same(builder, builder.WithSvgRenderer(renderer));
        Assert.Same(builder, builder.WithResvgSvgRenderer(renderer));
        Assert.NotNull(typeof(MarkdownScrollView).GetProperty(nameof(MarkdownScrollView.SvgRenderer)));
        Assert.NotNull(typeof(MarkdownScrollView).GetField(
            nameof(MarkdownScrollView.SvgRendererProperty),
            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.FlattenHierarchy));
    }

    [Fact]
    public void Options_CannotRaiseImmutableCeilings()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ResvgMarkdownSvgRendererOptions(
            maxSourceBytes: ResvgMarkdownSvgRendererOptions.HardMaxSourceBytes + 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ResvgMarkdownSvgRendererOptions(
            requestDeadline: TimeSpan.FromSeconds(3.1)));
    }

    [Fact]
    public async Task WeightedBudget_RejectsImplicitPathSegmentsAndLargeTextOrCss()
    {
        var options = new ResvgMarkdownSvgRendererOptions(maxStructuralCost: 100);
        await using var renderer = new ResvgMarkdownSvgRenderer(options);
        string[] sources =
        [
            $"<svg xmlns='http://www.w3.org/2000/svg'><path d='M0 0 {string.Concat(Enumerable.Repeat("1 1 ", 1_000))}'/></svg>",
            $"<svg xmlns='http://www.w3.org/2000/svg'><text>{new string('x', 6_500)}</text></svg>",
            $"<svg xmlns='http://www.w3.org/2000/svg'><style>{string.Concat(Enumerable.Repeat(".a{fill:red}", 700))}</style></svg>",
        ];

        foreach (string source in sources)
        {
            MarkdownSvgException failure = await Assert.ThrowsAsync<MarkdownSvgException>(async () =>
                await renderer.OpenAsync(new MarkdownSvgOpenRequest(Svg(source))));
            Assert.Equal(MarkdownSvgFailureReason.ResourceLimitExceeded, failure.Reason);
        }
    }

    [Fact]
    public void RestrictedWorkerEnvironment_IsAllowlistedAndOmitsHostSecrets()
    {
        string block = new(WindowsWorkerProcess.BuildSanitizedEnvironment());
        string[] entries = block.Split('\0', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(
            ["LOCALAPPDATA", "SystemRoot", "TEMP", "TMP", "USERPROFILE", "WINDIR"],
            entries.Select(entry => entry.Split('=', 2)[0]).Order(StringComparer.OrdinalIgnoreCase).ToArray());
        Assert.DoesNotContain("TOKEN", block, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("PASSWORD", block, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ActiveCancellation_DoesNotGetMaskedByConcurrentWorkerDisposal()
    {
        string shapes = string.Concat(Enumerable.Range(0, 20_000).Select(index =>
            $"<circle cx='{index % 512}' cy='{index / 40}' r='8' fill='#{index % 0xFFFFFF:x6}'/>"));
        await using var renderer = new ResvgMarkdownSvgRenderer();
        using IMarkdownSvgDocument document = await renderer.OpenAsync(new MarkdownSvgOpenRequest(Svg(
            $"<svg xmlns='http://www.w3.org/2000/svg' width='512' height='512'>{shapes}</svg>")));
        using var cancellation = new CancellationTokenSource();
        ValueTask<MarkdownSvgRaster> pending = document.RenderAsync(
            new MarkdownSvgRenderRequest(2048, 2048),
            cancellation.Token);
        cancellation.CancelAfter(TimeSpan.FromMilliseconds(1));

        MarkdownSvgException failure = await Assert.ThrowsAsync<MarkdownSvgException>(async () => await pending);
        Assert.Equal(MarkdownSvgFailureReason.Canceled, failure.Reason);
    }

    [Fact]
    public void FontGeneration_IsPublishedBeforeAllInvalidationSubscribersRun()
    {
        using var renderer = new ResvgMarkdownSvgRenderer();
        long initial = renderer.CacheGeneration;
        long observed = -1;
        renderer.CacheInvalidated += (_, _) => throw new InvalidOperationException("subscriber isolation");
        renderer.CacheInvalidated += (_, _) => observed = renderer.CacheGeneration;

        renderer.NotifyFontsChanged();

        Assert.Equal(initial + 1, renderer.CacheGeneration);
        Assert.Equal(renderer.CacheGeneration, observed);
    }

    [Fact]
    public async Task FontGenerationRotation_NeverQuarantinesValidContent()
    {
        string text = string.Concat(Enumerable.Range(0, 1_000).Select(index =>
            $"<text x='{index % 100}' y='{1 + (index / 100)}' font-size='1'>A</text>"));
        byte[] source = Svg($"<svg xmlns='http://www.w3.org/2000/svg' width='100' height='12'>{text}</svg>");
        await using var renderer = new ResvgMarkdownSvgRenderer();
        using IMarkdownSvgDocument document = await renderer.OpenAsync(new MarkdownSvgOpenRequest(source));
        Task<MarkdownSvgRaster> active = document.RenderAsync(new MarkdownSvgRenderRequest(2_000, 240)).AsTask();
        await Task.Yield();
        renderer.NotifyFontsChanged();
        try
        {
            using MarkdownSvgRaster completed = await active;
        }
        catch (MarkdownSvgException failure)
        {
            Assert.Equal(MarkdownSvgFailureReason.Canceled, failure.Reason);
        }

        using MarkdownSvgRaster replacement = await document.RenderAsync(new MarkdownSvgRenderRequest(200, 24));
        Assert.True(HasVisiblePixel(replacement.Pixels.Span));
    }

    [Fact]
    public async Task TiledFilterUsesParameterDerivedGutterAndRejectsUnboundedFilter()
    {
        const string bounded = "<svg xmlns='http://www.w3.org/2000/svg' width='128' height='64'><defs><filter id='f' filterUnits='userSpaceOnUse' x='0' y='0' width='128' height='64'><feGaussianBlur stdDeviation='6'/></filter></defs><rect x='42' y='12' width='40' height='40' fill='#2864dc' filter='url(#f)'/></svg>";
        await using var renderer = new ResvgMarkdownSvgRenderer();
        using IMarkdownSvgDocument document = await renderer.OpenAsync(new MarkdownSvgOpenRequest(Svg(bounded)));
        using MarkdownSvgRaster full = await document.RenderAsync(new MarkdownSvgRenderRequest(128, 64));
        using MarkdownSvgRaster tile = await document.RenderAsync(new MarkdownSvgRenderRequest(
            128,
            64,
            new MarkdownSvgTileRegion(48, 0, 32, 64)));
        AssertTileMatchesCrop(full.Pixels.Span, tile.Pixels.Span, 128, 48, 0, 32, 64);

        const string unbounded = "<svg xmlns='http://www.w3.org/2000/svg' width='16' height='16'><filter id='f'><feTurbulence/><feTile/></filter><rect width='16' height='16' filter='url(#f)'/></svg>";
        using IMarkdownSvgDocument unboundedDocument = await renderer.OpenAsync(new MarkdownSvgOpenRequest(Svg(unbounded)));
        MarkdownSvgException failure = await Assert.ThrowsAsync<MarkdownSvgException>(async () =>
            await unboundedDocument.RenderAsync(new MarkdownSvgRenderRequest(
                16,
                16,
                new MarkdownSvgTileRegion(0, 0, 8, 8))));
        Assert.Equal(MarkdownSvgFailureReason.UnsupportedContent, failure.Reason);
    }

    [Fact]
    public async Task OpenDocumentPinsOneSourceMappingAcrossSequentialTiles()
    {
        await using var renderer = new ResvgMarkdownSvgRenderer();
        using IMarkdownSvgDocument document = await renderer.OpenAsync(new MarkdownSvgOpenRequest(Svg(
            "<svg xmlns='http://www.w3.org/2000/svg' width='64' height='64'><circle cx='32' cy='32' r='30' fill='#5288e8'/></svg>")));
        long attachmentsAfterOpen = renderer.SourceAttachmentCount;
        Assert.Equal(1, attachmentsAfterOpen);

        for (int index = 0; index < 8; index++)
        {
            using MarkdownSvgRaster tile = await document.RenderAsync(new MarkdownSvgRenderRequest(
                64,
                64,
                new MarkdownSvgTileRegion((index % 4) * 16, (index / 4) * 16, 16, 16)));
            Assert.Equal(16, tile.WidthPixels);
        }

        Assert.Equal(attachmentsAfterOpen, renderer.SourceAttachmentCount);
    }

    [Fact]
    public async Task DisposeBeforeAndDuringRenderNeverReadsDisposedLifetimeSource()
    {
        await using var renderer = new ResvgMarkdownSvgRenderer();
        IMarkdownSvgDocument disposed = await renderer.OpenAsync(new MarkdownSvgOpenRequest(Svg(
            "<svg xmlns='http://www.w3.org/2000/svg' width='4' height='4'><rect width='4' height='4'/></svg>")));
        disposed.Dispose();
        MarkdownSvgException disposedFailure = await Assert.ThrowsAsync<MarkdownSvgException>(async () =>
            await disposed.RenderAsync(new MarkdownSvgRenderRequest(4, 4)));
        Assert.Equal(MarkdownSvgFailureReason.Canceled, disposedFailure.Reason);

        string shapes = string.Concat(Enumerable.Range(0, 5_000).Select(index =>
            $"<circle cx='{index % 128}' cy='{index / 40}' r='2'/>") );
        IMarkdownSvgDocument activeDocument = await renderer.OpenAsync(new MarkdownSvgOpenRequest(Svg(
            $"<svg xmlns='http://www.w3.org/2000/svg' width='128' height='128'>{shapes}</svg>")));
        Task<MarkdownSvgRaster>[] active = Enumerable.Range(0, 4)
            .Select(_ => activeDocument.RenderAsync(new MarkdownSvgRenderRequest(1024, 1024)).AsTask())
            .ToArray();
        activeDocument.Dispose();
        foreach (Task<MarkdownSvgRaster> render in active)
        {
            try
            {
                using MarkdownSvgRaster raster = await render;
            }
            catch (MarkdownSvgException failure)
            {
                Assert.Equal(MarkdownSvgFailureReason.Canceled, failure.Reason);
            }
        }
    }

    [Fact]
    public async Task KaTeXShapedFixture_Renders182UniqueDecodableEmbeddedPngs()
    {
        var body = new StringBuilder();
        for (int index = 0; index < 182; index++)
        {
            byte[] png = CreatePng(
                checked((byte)index),
                checked((byte)((index * 37) & 0xff)),
                checked((byte)((index * 83) & 0xff)),
                byte.MaxValue);
            int column = index % 26;
            int row = index / 26;
            body.Append($"<a href='https://github.com/example/{index}'><image x='{20 + (column * 33)}' y='{20 + (row * 17)}' width='8' height='8' xlink:href='data:image/png;base64,{Convert.ToBase64String(png)}'/></a>");
        }
        byte[] source = Svg($"<svg xmlns='http://www.w3.org/2000/svg' xmlns:xlink='http://www.w3.org/1999/xlink' width='890' height='158' viewBox='0 0 890 158'><path d='M5 145H885' stroke='currentColor'/>{body}</svg>");
        Assert.True(source.Length < ResvgMarkdownSvgRendererOptions.HardMaxSourceBytes);

        await using var renderer = new ResvgMarkdownSvgRenderer();
        using IMarkdownSvgDocument document = await renderer.OpenAsync(new MarkdownSvgOpenRequest(
            source,
            SemanticColor: new MarkdownSvgColor(32, 32, 32)));
        using MarkdownSvgRaster raster = await document.RenderAsync(new MarkdownSvgRenderRequest(890, 158));

        Assert.Equal(890, raster.WidthPixels);
        Assert.Equal(158, raster.HeightPixels);
        Assert.True(HasVisiblePixel(raster.Pixels.Span));
    }

    [Fact]
    public async Task SponsorShapedFixture_Opens576EmbeddedImagesUnderTheHardDeadline()
    {
        // Mirrors the element count, text/clip complexity, and source-byte
        // scale of an image-heavy sponsor grid without depending on live or
        // private README assets. A prior image-only shape missed its open cost.
        string[] payloads = Enumerable.Range(0, 380)
            .Select(index => Convert.ToBase64String(CreatePng(
                checked((byte)(index & 0xff)),
                checked((byte)(index >> 8)),
                64,
                byte.MaxValue,
                metadataBytes: 2_280,
                metadataSeed: index)))
            .ToArray();
        var body = new StringBuilder();
        for (int index = 0; index < 576; index++)
        {
            int column = index % 10;
            int row = index / 10;
            int x = column * 120;
            int y = row * 140;
            int clipId = index + 1;
            body.Append($"<a href='https://example.test/sponsor/{index}'>" +
                $"<image x='{x}' y='{y}' width='100' height='100' href='data:image/png;base64,{payloads[index % payloads.Length]}'/>" +
                $"<text x='{x}' y='{y + 105}' clip-path='url(#clip{clipId})' dominant-baseline='hanging'>Sponsor {index}</text>" +
                $"<text x='{x}' y='{y + 125}' clip-path='url(#clip{clipId})' dominant-baseline='hanging'>USD {index}</text></a>" +
                $"<clipPath id='clip{clipId}'><rect x='{x}' y='{y + 105}' width='105' height='40'/></clipPath>");
        }
        byte[] source = Svg($"<svg xmlns='http://www.w3.org/2000/svg' width='1200' height='8120'>{body}</svg>");
        Assert.InRange(source.Length, 1_700_000, 2_200_000);

        await using var renderer = new ResvgMarkdownSvgRenderer();
        using IMarkdownSvgDocument document = await renderer.OpenAsync(new MarkdownSvgOpenRequest(source));
        Assert.True(document.Info.HasText);
        using MarkdownSvgRaster raster = await document.RenderAsync(new MarkdownSvgRenderRequest(600, 4060));

        Assert.Equal(600, raster.WidthPixels);
        Assert.Equal(4060, raster.HeightPixels);
        Assert.True(HasVisiblePixel(raster.Pixels.Span));
    }

    [Fact]
    public async Task RepeatedEmbeddedPayload_IsDeduplicatedAndRendersWithinUniqueBudget()
    {
        byte[] png = CreatePng(20, 100, 220, byte.MaxValue);
        string data = Convert.ToBase64String(png);
        string images = string.Concat(Enumerable.Range(0, 200).Select(index =>
            $"<image x='{index % 20}' y='{index / 20}' width='1' height='1' href='data:image/png;base64,{data}'/>"));
        var options = new ResvgMarkdownSvgRendererOptions(
            maxEmbeddedImageBytes: png.Length,
            maxEmbeddedImagePixels: 1);
        await using var renderer = new ResvgMarkdownSvgRenderer(options);
        using IMarkdownSvgDocument document = await renderer.OpenAsync(new MarkdownSvgOpenRequest(Svg(
            $"<svg xmlns='http://www.w3.org/2000/svg' width='20' height='10'>{images}</svg>")));
        using MarkdownSvgRaster raster = await document.RenderAsync(new MarkdownSvgRenderRequest(20, 10));

        Assert.True(HasVisiblePixel(raster.Pixels.Span));
    }

    [Fact]
    public async Task TwelveVisibleUncachedDocuments_CanRemainInFlightTogether()
    {
        await using var renderer = new ResvgMarkdownSvgRenderer();
        Task<IMarkdownSvgDocument>[] opens = Enumerable.Range(0, 12).Select(async index =>
            await renderer.OpenAsync(new MarkdownSvgOpenRequest(Svg(
                $"<svg xmlns='http://www.w3.org/2000/svg' width='64' height='64'><rect width='64' height='64' fill='rgb({index * 17},40,180)'/></svg>")))).ToArray();
        IMarkdownSvgDocument[] documents = await Task.WhenAll(opens);
        try
        {
            Task<MarkdownSvgRaster>[] renders = documents.Select(async document =>
                await document.RenderAsync(new MarkdownSvgRenderRequest(64, 64))).ToArray();
            MarkdownSvgRaster[] rasters = await Task.WhenAll(renders);
            try
            {
                Assert.All(rasters, raster => Assert.True(HasVisiblePixel(raster.Pixels.Span)));
            }
            finally
            {
                foreach (MarkdownSvgRaster raster in rasters)
                    raster.Dispose();
            }
        }
        finally
        {
            foreach (IMarkdownSvgDocument document in documents)
                document.Dispose();
        }

        await renderer.TrimCachesAsync();
    }

    private static bool HasVisiblePixel(ReadOnlySpan<byte> pixels)
    {
        for (int index = 3; index < pixels.Length; index += 4)
        {
            if (pixels[index] != 0)
                return true;
        }
        return false;
    }

    private static void AssertTileMatchesCrop(
        ReadOnlySpan<byte> full,
        ReadOnlySpan<byte> tile,
        int fullWidth,
        int tileX,
        int tileY,
        int tileWidth,
        int tileHeight)
    {
        long totalDelta = 0;
        for (int row = 0; row < tileHeight; row++)
        {
            int fullStart = (((tileY + row) * fullWidth) + tileX) * 4;
            int tileStart = row * tileWidth * 4;
            for (int index = 0; index < tileWidth * 4; index++)
                totalDelta += Math.Abs(full[fullStart + index] - tile[tileStart + index]);
        }
        double meanDelta = totalDelta / (double)(tileWidth * tileHeight * 4);
        Assert.InRange(meanDelta, 0, 1);
    }

    private static byte[] CreatePng(
        byte red,
        byte green,
        byte blue,
        byte alpha,
        int metadataBytes = 0,
        int metadataSeed = 0)
    {
        using var output = new MemoryStream();
        output.Write([137, 80, 78, 71, 13, 10, 26, 10]);
        byte[] header = new byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(header, 1);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(4), 1);
        header[8] = 8;
        header[9] = 6;
        WritePngChunk(output, "IHDR", header);

        if (metadataBytes > 0)
        {
            byte[] metadata = new byte[metadataBytes];
            "Comment\0"u8.CopyTo(metadata);
            for (int index = 8; index < metadata.Length; index++)
                metadata[index] = checked((byte)('A' + ((index + metadataSeed) % 26)));
            WritePngChunk(output, "tEXt", metadata);
        }

        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.SmallestSize, leaveOpen: true))
            zlib.Write([0, red, green, blue, alpha]);
        WritePngChunk(output, "IDAT", compressed.ToArray());
        WritePngChunk(output, "IEND", []);
        return output.ToArray();
    }

    private static void WritePngChunk(Stream destination, string type, byte[] data)
    {
        byte[] length = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(length, checked((uint)data.Length));
        destination.Write(length);
        byte[] typeBytes = Encoding.ASCII.GetBytes(type);
        destination.Write(typeBytes);
        destination.Write(data);
        byte[] crcInput = new byte[typeBytes.Length + data.Length];
        typeBytes.CopyTo(crcInput, 0);
        data.CopyTo(crcInput, typeBytes.Length);
        byte[] crc = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crc, ComputeCrc32(crcInput));
        destination.Write(crc);
    }

    private static uint ComputeCrc32(ReadOnlySpan<byte> bytes)
    {
        uint crc = uint.MaxValue;
        foreach (byte value in bytes)
        {
            crc ^= value;
            for (int bit = 0; bit < 8; bit++)
                crc = (crc >> 1) ^ (0xedb88320u & (uint)-(int)(crc & 1));
        }
        return ~crc;
    }
}
