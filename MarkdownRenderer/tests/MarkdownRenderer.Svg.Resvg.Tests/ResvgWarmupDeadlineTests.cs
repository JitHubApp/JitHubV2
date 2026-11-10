using System.Security.Cryptography;
using System.Text;
using MarkdownRenderer.Images;
using MarkdownRenderer.Svg.Resvg.Tests.TestSupport;
using Xunit;

namespace MarkdownRenderer.Svg.Resvg.Tests;

/// <summary>
/// Pins which startup and cold-pipeline work is charged to the worker's
/// initialization deadline rather than to the first document's content
/// deadline, including warm-up shutdown and cold CJK text fallback.
/// </summary>
[Collection(WorkerDeadlineSensitiveCollection.Name)]
public sealed class ResvgWarmupDeadlineTests
{
    [Fact]
    public async Task WarmUp_UsesInitializationDeadlineInsteadOfContentDeadline()
    {
        var options = new ResvgMarkdownSvgRendererOptions(
            requestDeadline: TimeSpan.FromMilliseconds(1));
        await using var renderer = new ResvgMarkdownSvgRenderer(options);

        await renderer.WarmUpAsync();
    }

    [Fact]
    public async Task ShutdownDuringWarmupIsCancellationInsteadOfWorkerFailure()
    {
        for (int cycle = 0; cycle < 4; cycle++)
        {
            await using var renderer = new ResvgMarkdownSvgRenderer();
            Task warmup = renderer.WarmUpAsync();
            Task shutdown = renderer.DisposeAsync().AsTask();
            try { await warmup.WaitAsync(TimeSpan.FromSeconds(10)); }
            catch (MarkdownSvgException exception)
            {
                Assert.Equal(MarkdownSvgFailureReason.Canceled, exception.Reason);
            }
            await shutdown.WaitAsync(TimeSpan.FromSeconds(10));
        }
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
        Assert.True(ProviderTestRasters.HasVisiblePixel(raster.Pixels.Span));
    }

    [Fact]
    public async Task ColdKoreanShieldsBadge_OpensAndRendersWithinContentDeadline()
    {
        // Exact bytes of a real 43x20 README badge.
        // Hangul takes a different font fallback path from the existing CJK fixtures.
        const string source = "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"43\" height=\"20\" role=\"img\" aria-label=\"한국어\"><title>한국어</title><filter id=\"blur\"><feGaussianBlur stdDeviation=\"16\"/></filter><linearGradient id=\"s\" x2=\"0\" y2=\"100%\"><stop offset=\"0\" stop-color=\"#bbb\" stop-opacity=\".1\"/><stop offset=\"1\" stop-opacity=\".1\"/></linearGradient><clipPath id=\"r\"><rect width=\"43\" height=\"20\" rx=\"3\"/></clipPath><g clip-path=\"url(#r)\"><rect width=\"0\" height=\"20\" fill=\"#d9d9d9\"/><rect x=\"0\" width=\"43\" height=\"20\" fill=\"#d9d9d9\"/><rect width=\"43\" height=\"20\" fill=\"url(#s)\"/></g><g fill=\"#fff\" text-anchor=\"middle\" font-family=\"Verdana,Geneva,DejaVu Sans,sans-serif\" text-rendering=\"geometricPrecision\" font-size=\"110\"><g transform=\"scale(.1)\"><g aria-hidden=\"true\" fill=\"#ccc\"><text x=\"215\" y=\"150\" fill-opacity=\".8\" filter=\"url(#blur)\" textLength=\"330\">한국어</text><text x=\"215\" y=\"150\" fill-opacity=\".3\" textLength=\"330\">한국어</text></g><text x=\"215\" y=\"140\" textLength=\"330\" fill=\"#333\">한국어</text></g></g></svg>";
        byte[] bytes = Svg(source);
        Assert.Equal("3EC0BAD11F076CFE1D23AE2D750851B3BB1DD78A885723325EFB86EED6E841D0",
            Convert.ToHexString(SHA256.HashData(bytes)));

        var options = new ResvgMarkdownSvgRendererOptions(
            requestDeadline: TimeSpan.FromMilliseconds(500));
        await using var renderer = new ResvgMarkdownSvgRenderer(options);
        using IMarkdownSvgDocument document = await renderer.OpenAsync(
            new MarkdownSvgOpenRequest(bytes));
        using MarkdownSvgRaster raster = await document.RenderAsync(
            new MarkdownSvgRenderRequest(43, 20));

        Assert.True(document.Info.HasText);
        Assert.Equal(43, raster.WidthPixels);
        Assert.Equal(20, raster.HeightPixels);
        Assert.True(ProviderTestRasters.HasVisiblePixel(raster.Pixels.Span));
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
            Assert.True(ProviderTestRasters.HasVisiblePixel(raster.Pixels.Span));
        }
    }

    private static byte[] Svg(string value) => Encoding.UTF8.GetBytes(value);
}
