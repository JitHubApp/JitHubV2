using System;
using System.IO;
using System.Linq;
using MarkdownRenderer.Images;
using MarkdownRenderer.Layout.Boxes;
using MarkdownRenderer.Svg.Resvg;
using Xunit;

namespace MarkdownRenderer.Pixel.Tests;

/// <summary>
/// Auto-discovered SVG fixture suite. Each <c>Fixtures\svg\**\*.svg</c>
/// file is tested at 100%, 125%, 150%, 175%, 200%, 225%, 300% and 400% scale.
/// The fixture is rasterized through three paths:
///   1. The isolated resvg provider into an RGBA buffer at the target physical size.
///   2. A persistent headless Edge/Chrome at identical CSS size and device scale.
///   3. Markdown parsing, host image resolution, ImageBox and Win2D WARP
///      composition, including a second document hitting the device bitmap cache.
/// Buffers are compared with the unchanged release fidelity gates. Per-fixture artifacts
/// (ours.png, browser.png, markdown.png and their diff statistics) land in a unique temporary directory
/// by default, or MARKDOWN_RENDERER_SVG_ARTIFACT_ROOT when set. Tests never
/// overwrite checked-in comparison artifacts.
///
/// The enforced gates are the 1.0 browser-relative contract: geometry/raster
/// fixtures require mean channel delta ≤ 2, SSIM ≥ .995, and exact
/// alpha-coverage IoU ≥ .995; text/filter fixtures require mean
/// delta ≤ 5, SSIM ≥ .980, and exact alpha-coverage IoU ≥ .980.
///
/// Tests self-skip when no headless browser is installed (CI machines without
/// Chrome/Edge) — the resvg render still runs end-to-end so worker deployment,
/// protocol, and raster failures cannot be hidden by a missing oracle. Release
/// execution requires Edge and fails instead of skipping a missing oracle.
/// </summary>
public sealed class SvgComplianceTests(SvgComplianceFixture fixture) : IClassFixture<SvgComplianceFixture>
{
    private ResvgMarkdownSvgRenderer Renderer => fixture.Renderer;
    private static readonly string DefaultArtifactRoot = Path.Combine(
        Path.GetTempPath(), "MarkdownRenderer.SvgPixelTests", Guid.NewGuid().ToString("N"));

    public static IEnumerable<object[]> Fixtures()
    {
        var root = Path.Combine(AppContext.BaseDirectory, "Fixtures", "svg");
        if (!Directory.Exists(root)) yield break;
        foreach (var file in Directory.EnumerateFiles(root, "*.svg", SearchOption.AllDirectories)
                                       .OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
        {
            var rel = Path.GetRelativePath(root, file).Replace('\\', '/');
            foreach (double scale in new[] { 1d, 1.25d, 1.5d, 1.75d, 2d, 2.25d, 3d, 4d })
                yield return new object[] { rel, scale };
        }
    }

    [SkippableTheory]
    [MemberData(nameof(Fixtures))]
    public async Task Rasterize_Fixture_MatchesBrowser(string fixtureRelPath, double scale)
    {
        var root = Path.Combine(AppContext.BaseDirectory, "Fixtures", "svg");
        var path = Path.Combine(root, fixtureRelPath.Replace('/', Path.DirectorySeparatorChar));
        Assert.True(File.Exists(path), $"fixture not found: {path}");

        byte[] svg = File.ReadAllBytes(path);

        // Pull intrinsic dimensions from the fixture — every authored
        // fixture sets explicit width/height plus a viewBox.
        var (iw, ih) = SvgIntrinsics.TryExtractIntrinsicSize(svg);
        Assert.True(iw > 0 && ih > 0, $"fixture {fixtureRelPath} is missing intrinsic w/h");
        int cssWidth = (int)Math.Round(iw);
        int cssHeight = (int)Math.Round(ih);
        int w = checked((int)Math.Ceiling(cssWidth * scale));
        int h = checked((int)Math.Ceiling(cssHeight * scale));
        Assert.True(w > 0 && h > 0, $"fixture {fixtureRelPath} has zero-sized intrinsic ({w}×{h})");

        // 1) Full provider pipeline: host preflight, isolated worker, shared
        // memory protocol, parsed-tree cache, and returned premultiplied pixels.
        using IMarkdownSvgDocument document = await Renderer.OpenAsync(
            new MarkdownSvgOpenRequest(svg, Locale: "en-US"));
        using MarkdownSvgRaster raster = await document.RenderAsync(
            new MarkdownSvgRenderRequest(w, h)
            { PhysicalViewport = new MarkdownSvgPhysicalViewport(cssWidth * scale, cssHeight * scale) });
        Assert.Equal(w * h * 4, raster.LengthBytes);
        Assert.Equal(w, raster.WidthPixels);
        Assert.Equal(h, raster.HeightPixels);
        byte[] oursRgba = raster.PixelFormat switch
        {
            MarkdownSvgPixelFormat.Rgba8Premultiplied =>
                PixelComparer.RgbaPremulToRgba(raster.Pixels.Span, w, h),
            MarkdownSvgPixelFormat.Bgra8Premultiplied =>
                PixelComparer.BgraPremulToRgba(raster.Pixels.ToArray(), w, h),
            _ => throw new InvalidDataException($"Unknown SVG pixel format {raster.PixelFormat}."),
        };

        // Persist our render for inspection regardless of compare outcome.
        string? artifactRoot = Environment.GetEnvironmentVariable("MARKDOWN_RENDERER_SVG_ARTIFACT_ROOT");
        var artifactsDir = Path.Combine(
            string.IsNullOrWhiteSpace(artifactRoot)
                ? DefaultArtifactRoot
                : Path.GetFullPath(artifactRoot),
            fixtureRelPath.Replace('/', '_').Replace(".svg", "") + $"_scale-{scale * 100:0}");
        Directory.CreateDirectory(artifactsDir);
        PixelComparer.SaveRgbaAsPng(oursRgba, w, h, Path.Combine(artifactsDir, "ours.png"));
        if (fixtureRelPath == "20-stress/katex-182-unique-images.svg")
            AssertEveryKaTeXImageVisible(oursRgba, w, h, scale);

        // 2) Browser render — skip if no Chromium installed.
        var browserBin = HeadlessBrowserRasterizer.TryFindBrowser();
        bool requireEdgeOracle = string.Equals(
            Environment.GetEnvironmentVariable("MARKDOWN_RENDERER_REQUIRE_EDGE_ORACLE"),
            "1",
            StringComparison.Ordinal);
        if (requireEdgeOracle)
        {
            Assert.False(browserBin is null ||
                !string.Equals(Path.GetFileName(browserBin), "msedge.exe", StringComparison.OrdinalIgnoreCase),
                $"The release/PR SVG fidelity gate requires Microsoft Edge; discovered oracle was '{browserBin ?? "none"}'.");
        }
        if (browserBin is null)
        {
            File.WriteAllText(Path.Combine(artifactsDir, "skipped.txt"),
                "No headless Edge/Chrome found; browser comparison skipped.\n" +
                "resvg render produced " + oursRgba.Length + " bytes.\n");
            // Use xunit.skippablefact Skip.If so local dashboards show "skipped"
            // rather than a silent green pass — making it clear the pixel
            // comparison did not run on this machine.
            Skip.If(true,
                "No headless Edge/Chrome installed; browser comparison requires " +
                "msedge or google-chrome to be on PATH or in well-known locations.");
        }

        byte[] browserPng = await fixture.Browser.RasterizeAsync(svg, cssWidth, cssHeight, scale);
        Assert.False(string.IsNullOrEmpty(fixture.Browser.BackendEvidenceJson));
        File.WriteAllText(Path.Combine(artifactsDir, "edge-backend.json"), fixture.Browser.BackendEvidenceJson);

        {
            var (browserRgba, bw, bh) = PixelComparer.LoadPngAsRgba(browserPng);
            Assert.Equal(w, bw);
            Assert.Equal(h, bh);

            // CDP captures the exact requested physical viewport, with no
            // browser-frame allowance or cropped common comparison rectangle.
            int cw = w;
            int ch = h;
            byte[] oursCropped = oursRgba;
            byte[] browserCropped = PixelComparer.CropToRequiredBounds(
                browserRgba,
                bw,
                bh,
                w,
                h);

            PixelComparer.SaveRgbaAsPng(browserCropped, cw, ch, Path.Combine(artifactsDir, "browser.png"));
            if (fixtureRelPath == "20-stress/katex-182-unique-images.svg")
                AssertEveryKaTeXImageVisible(browserCropped, w, h, scale);

            var diff = PixelComparer.Compare(oursCropped, browserCropped, cw, ch, channelTolerance: 2);
            bool textOrFilter = fixtureRelPath.StartsWith("08-filters/", StringComparison.OrdinalIgnoreCase) ||
                fixtureRelPath.StartsWith("13-text/", StringComparison.OrdinalIgnoreCase);
            double maxMeanChannelDelta = textOrFilter ? 5 : 2;
            double minimumSsim = textOrFilter ? 0.980 : 0.995;
            double minimumAlphaIou = textOrFilter ? 0.980 : 0.995;
            File.WriteAllText(Path.Combine(artifactsDir, "diff-stats.txt"),
                $"{cw}x{ch}\nmaxChannelDelta={diff.MaxChannelDelta}\nmeanChannelDelta={diff.MeanChannelDelta:F3}\n" +
                $"differingPixelFraction={diff.DifferingPixelFraction:F4}\nssim={diff.StructuralSimilarity:F6}\n" +
                $"alphaBoundaryTolerancePx=0\nssimWindow=8x8-local-premultiplied\nalphaIoU={diff.AlphaIntersectionOverUnion:F6}\n");

            // Capture both native paths before enforcing either comparison,
            // so a provider mismatch cannot conceal an additional ImageBox,
            // composition, or device-cache defect in the same fixture.
            byte[] composed = await SvgMarkdownRasterizer.RasterizeAsync(svg, cssWidth, cssHeight, Renderer, scale);
            PixelComparer.SaveRgbaAsPng(composed, w, h, Path.Combine(artifactsDir, "markdown.png"));
            if (fixtureRelPath == "20-stress/katex-182-unique-images.svg")
                AssertEveryKaTeXImageVisible(composed, w, h, scale);
            var composedDiff = PixelComparer.Compare(composed, browserCropped, w, h, channelTolerance: 2);
            File.WriteAllText(Path.Combine(artifactsDir, "markdown-diff-stats.txt"),
                $"{w}x{h}\nmaxChannelDelta={composedDiff.MaxChannelDelta}\n" +
                $"meanChannelDelta={composedDiff.MeanChannelDelta:F3}\n" +
                $"differingPixelFraction={composedDiff.DifferingPixelFraction:F4}\n" +
                $"ssim={composedDiff.StructuralSimilarity:F6}\nalphaBoundaryTolerancePx=0\nssimWindow=8x8-local-premultiplied\n" +
                $"alphaIoU={composedDiff.AlphaIntersectionOverUnion:F6}\n");
            Assert.True(diff.MeanChannelDelta <= maxMeanChannelDelta,
                $"{fixtureRelPath}: mean channel delta {diff.MeanChannelDelta:F3} > {maxMeanChannelDelta}");
            Assert.True(diff.StructuralSimilarity >= minimumSsim,
                $"{fixtureRelPath}: SSIM {diff.StructuralSimilarity:F6} < {minimumSsim:F3}");
            Assert.True(diff.AlphaIntersectionOverUnion >= minimumAlphaIou,
                $"{fixtureRelPath}: exact alpha IoU " +
                $"{diff.AlphaIntersectionOverUnion:F6} < {minimumAlphaIou:F3}");
            Assert.True(composedDiff.MeanChannelDelta <= maxMeanChannelDelta,
                $"{fixtureRelPath} Markdown composition: mean channel delta " +
                $"{composedDiff.MeanChannelDelta:F3} > {maxMeanChannelDelta}");
            Assert.True(composedDiff.StructuralSimilarity >= minimumSsim,
                $"{fixtureRelPath} Markdown composition: SSIM " +
                $"{composedDiff.StructuralSimilarity:F6} < {minimumSsim:F3}");
            Assert.True(composedDiff.AlphaIntersectionOverUnion >= minimumAlphaIou,
                $"{fixtureRelPath} Markdown composition: exact alpha IoU " +
                $"{composedDiff.AlphaIntersectionOverUnion:F6} < {minimumAlphaIou:F3}");
        }
    }

    private static void AssertEveryKaTeXImageVisible(byte[] rgba, int width, int height, double scale)
    {
        Assert.Equal((int)Math.Ceiling(890 * scale), width);
        Assert.Equal((int)Math.Ceiling(158 * scale), height);
        // Aggregate similarity can conceal one missing small avatar. Require
        // every unique PNG's opaque interior, independently of the diff gates.
        for (int image = 0; image < 182; image++)
        {
            int x = (int)((24 + image % 26 * 33) * scale);
            int y = (int)((24 + image / 26 * 17) * scale);
            int pixel = (y * width + x) * 4;
            Assert.True(rgba[pixel] == image &&
                rgba[pixel + 1] == ((image * 37) & 255) &&
                rgba[pixel + 2] == ((image * 83) & 255) &&
                rgba[pixel + 3] == 255,
                $"KaTeX-shaped image {image} is missing or has incorrect pixels.");
        }
    }
}

public sealed class SvgComplianceFixture : IAsyncLifetime
{
    internal ResvgMarkdownSvgRenderer Renderer { get; } = new();
    internal PersistentEdgeSvgRasterizer Browser { get; } = new();

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        try { await Renderer.DisposeAsync(); }
        finally { await Browser.DisposeAsync(); }
    }
}
