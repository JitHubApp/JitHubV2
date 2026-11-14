using System.Collections.Concurrent;
using System.Diagnostics.Tracing;
using System.Security.Cryptography;
using System.Text;
using MarkdownRenderer.Controls;
using MarkdownRenderer.Images.Svg;
using MarkdownRenderer.Svg.Resvg.Internal.Workers.Processes;
using MarkdownRenderer.Svg.Resvg.Tests.TestSupport;
using Xunit;

namespace MarkdownRenderer.Svg.Resvg.Tests;

[Collection(WorkerDeadlineSensitiveCollection.Name)]
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
        byte[] png = ProviderTestRasters.CreatePng(10, 20, 30, byte.MaxValue);
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
        byte[] png = ProviderTestRasters.CreatePng(24, 120, 220, byte.MaxValue);
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
        byte[] png = ProviderTestRasters.CreatePng(24, 120, 220, byte.MaxValue);
        string source = $"<svg xmlns='http://www.w3.org/2000/svg' width='1' height='1'><image width='1' height='1' href='data:image/gif;base64,{Convert.ToBase64String(png)}'/></svg>";
        await using var renderer = new ResvgMarkdownSvgRenderer();

        using IMarkdownSvgDocument document = await renderer.OpenAsync(new MarkdownSvgOpenRequest(Svg(source)));
        using MarkdownSvgRaster raster = await document.RenderAsync(new MarkdownSvgRenderRequest(1, 1));

        Assert.InRange(raster.Pixels.Span[0], (byte)20, (byte)28);
        Assert.InRange(raster.Pixels.Span[1], (byte)116, (byte)124);
        Assert.InRange(raster.Pixels.Span[2], (byte)216, (byte)224);
    }

    [Theory]
    [InlineData("<!-- literal <!DOCTYPE <!ENTITY <?xml-stylesheet text -->")]
    [InlineData("<title><![CDATA[<!DOCTYPE <!ENTITY <?xml-stylesheet]]></title>")]
    public async Task HarmlessSecurityMarkerTextInCommentsAndCdata_IsPreservedByWorker(string content)
    {
        string source =
            $"<svg xmlns='http://www.w3.org/2000/svg' width='1' height='1'>{content}" +
            "<rect width='1' height='1' fill='green'/></svg>";
        await using var renderer = new ResvgMarkdownSvgRenderer();

        using IMarkdownSvgDocument document = await renderer.OpenAsync(
            new MarkdownSvgOpenRequest(Svg(source)));
        using MarkdownSvgRaster raster = await document.RenderAsync(new MarkdownSvgRenderRequest(1, 1));

        Assert.Equal(128, raster.Pixels.Span[1]);
        Assert.Equal(255, raster.Pixels.Span[3]);
    }

    [Fact]
    public async Task HarmlessSecurityMarkerTextInsideNestedSvg_IsPreservedByWorker()
    {
        const string literalMarkers = "<!DOCTYPE <!ENTITY <?xml-stylesheet";
        string nested =
            "<svg xmlns='http://www.w3.org/2000/svg' width='1' height='1'>" +
            $"<!-- {literalMarkers} -->" +
            $"<title><![CDATA[{literalMarkers}]]></title>" +
            "<rect width='1' height='1' fill='green'/></svg>";
        string source =
            "<svg xmlns='http://www.w3.org/2000/svg' width='1' height='1'>" +
            $"<image width='1' height='1' href='data:image/svg+xml;base64,{Convert.ToBase64String(Svg(nested))}'/>" +
            "</svg>";
        await using var renderer = new ResvgMarkdownSvgRenderer();

        using IMarkdownSvgDocument document = await renderer.OpenAsync(
            new MarkdownSvgOpenRequest(Svg(source)));
        using MarkdownSvgRaster raster = await document.RenderAsync(new MarkdownSvgRenderRequest(1, 1));

        Assert.Equal(128, raster.Pixels.Span[1]);
        Assert.Equal(255, raster.Pixels.Span[3]);
    }

    [Fact]
    public async Task Worker_RejectsActualXmlStylesheetProcessingInstruction()
    {
        const string source =
            "<?xml-stylesheet href='https://example.test/style.css' type='text/css'?>" +
            "<svg xmlns='http://www.w3.org/2000/svg' width='1' height='1'>" +
            "<rect width='1' height='1' fill='green'/></svg>";
        await using var renderer = new ResvgMarkdownSvgRenderer();

        MarkdownSvgException failure = await Assert.ThrowsAsync<MarkdownSvgException>(async () =>
            await renderer.OpenAsync(new MarkdownSvgOpenRequest(Svg(source))));

        Assert.Equal(MarkdownSvgFailureReason.UnsupportedContent, failure.Reason);
    }

    [Fact]
    public async Task MislabeledRasterInsideNestedSvg_IsSniffedAndRendered()
    {
        byte[] png = ProviderTestRasters.CreatePng(24, 120, 220, byte.MaxValue);
        string nested =
            "<svg xmlns='http://www.w3.org/2000/svg' width='1' height='1'>" +
            $"<image width='1' height='1' href='data:image/gif;base64,{Convert.ToBase64String(png)}'/>" +
            "</svg>";
        string source =
            "<svg xmlns='http://www.w3.org/2000/svg' width='1' height='1'>" +
            $"<image width='1' height='1' href='data:image/svg+xml;base64,{Convert.ToBase64String(Svg(nested))}'/>" +
            "</svg>";
        await using var renderer = new ResvgMarkdownSvgRenderer();

        using IMarkdownSvgDocument document = await renderer.OpenAsync(
            new MarkdownSvgOpenRequest(Svg(source)));
        using MarkdownSvgRaster raster = await document.RenderAsync(new MarkdownSvgRenderRequest(1, 1));

        Assert.InRange(raster.Pixels.Span[0], (byte)20, (byte)28);
        Assert.InRange(raster.Pixels.Span[1], (byte)116, (byte)124);
        Assert.InRange(raster.Pixels.Span[2], (byte)216, (byte)224);
        Assert.InRange(raster.Pixels.Span[3], (byte)250, byte.MaxValue);
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
    public async Task OpenAsync_RejectsLegacyExternalSvgDoctypeBeforeWorkerAdmission()
    {
        const string source =
            "<!DOCTYPE svg PUBLIC '-//W3C//DTD SVG 1.1//EN' " +
            "'http://www.w3.org/Graphics/SVG/1.1/DTD/svg11.dtd'>" +
            "<svg xmlns='http://www.w3.org/2000/svg' width='8' height='8'>" +
            "<rect width='8' height='8' fill='green'/></svg>";
        byte[] bytes = Svg(source);
        byte[] original = (byte[])bytes.Clone();
        await using var renderer = new ResvgMarkdownSvgRenderer();

        MarkdownSvgException exception = await Assert.ThrowsAsync<MarkdownSvgException>(async () =>
            await renderer.OpenAsync(new MarkdownSvgOpenRequest(bytes)));

        Assert.Equal(MarkdownSvgFailureReason.UnsupportedContent, exception.Reason);
        Assert.Contains("DTD", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(original, bytes);
        Assert.Equal(0, renderer.SourceAttachmentCount);
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
        string block = new(WorkerProcessSandbox.BuildSanitizedEnvironment());
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
        Assert.True(
            failure.Reason == MarkdownSvgFailureReason.Canceled,
            $"Expected {MarkdownSvgFailureReason.Canceled}; actual exception: {failure}");
    }
}
