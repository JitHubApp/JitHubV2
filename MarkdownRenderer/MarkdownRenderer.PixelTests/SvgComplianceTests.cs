using System;
using System.IO;
using System.Linq;
using MarkdownRenderer.Images;
using MarkdownRenderer.Layout.Boxes;
using MarkdownRenderer.Svg.Resvg;
using Xunit;

namespace MarkdownRenderer.PixelTests;

/// <summary>
/// Auto-discovered SVG fixture suite. Each <c>Fixtures\svg\**\*.svg</c>
/// file becomes one test case. The fixture is rasterized twice:
///   1. The isolated resvg provider into an RGBA buffer at the SVG's intrinsic size.
///   2. Headless Edge/Chrome at the same pixel dimensions (ground truth).
/// Buffers are compared with a tolerant per-pixel diff. Per-fixture artifacts
/// (ours.png, browser.png, diff-stats.txt) land in artifacts\svg-pixel-diffs\
/// so manual inspection is possible after the run.
///
/// The enforced gates are the 1.0 browser-relative contract: geometry/raster
/// fixtures require mean channel delta ≤ 2, SSIM ≥ .995, and one-pixel
/// boundary-tolerant alpha IoU ≥ .995; text/filter fixtures require mean
/// delta ≤ 5, SSIM ≥ .980, and tolerant alpha IoU ≥ .980.
///
/// Tests self-skip when no headless browser is installed (CI machines without
/// Chrome/Edge) — the resvg render still runs end-to-end so worker deployment,
/// protocol, and raster failures cannot be hidden by a missing oracle.
/// </summary>
public sealed class SvgComplianceTests
{
    private static readonly ResvgMarkdownSvgRenderer Renderer = new();

    public static IEnumerable<object[]> Fixtures()
    {
        var root = Path.Combine(AppContext.BaseDirectory, "Fixtures", "svg");
        if (!Directory.Exists(root)) yield break;
        foreach (var file in Directory.EnumerateFiles(root, "*.svg", SearchOption.AllDirectories)
                                       .OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
        {
            var rel = Path.GetRelativePath(root, file).Replace('\\', '/');
            yield return new object[] { rel };
        }
    }

    [SkippableTheory]
    [MemberData(nameof(Fixtures))]
    public async Task Rasterize_Fixture_MatchesBrowser(string fixtureRelPath)
    {
        var root = Path.Combine(AppContext.BaseDirectory, "Fixtures", "svg");
        var path = Path.Combine(root, fixtureRelPath.Replace('/', Path.DirectorySeparatorChar));
        Assert.True(File.Exists(path), $"fixture not found: {path}");

        byte[] svg = File.ReadAllBytes(path);

        // Pull intrinsic dimensions from the fixture — every authored
        // fixture sets explicit width/height plus a viewBox.
        var (iw, ih) = SvgIntrinsics.TryExtractIntrinsicSize(svg);
        Assert.True(iw > 0 && ih > 0, $"fixture {fixtureRelPath} is missing intrinsic w/h");
        int w = (int)Math.Round(iw);
        int h = (int)Math.Round(ih);
        Assert.True(w > 0 && h > 0, $"fixture {fixtureRelPath} has zero-sized intrinsic ({w}×{h})");

        // 1) Full provider pipeline: host preflight, isolated worker, shared
        // memory protocol, parsed-tree cache, and returned premultiplied pixels.
        using IMarkdownSvgDocument document = await Renderer.OpenAsync(
            new MarkdownSvgOpenRequest(svg, Locale: "en-US"));
        using MarkdownSvgRaster raster = await document.RenderAsync(
            new MarkdownSvgRenderRequest(w, h));
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
                ? Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "artifacts", "svg-pixel-diffs")
                : Path.GetFullPath(artifactRoot),
            fixtureRelPath.Replace('/', '_').Replace(".svg", ""));
        Directory.CreateDirectory(artifactsDir);
        PixelComparer.SaveRgbaAsPng(oursRgba, w, h, Path.Combine(artifactsDir, "ours.png"));

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

        string? browserPng = HeadlessBrowserRasterizer.Rasterize(svg, w, h);
        if (browserPng is null)
        {
            // A working browser was located but the screenshot pipeline
            // produced nothing — that's a real regression in our test
            // harness, not a "skip". Surface it as a test failure so the
            // suite can't silently degrade into rasterize-only checks.
            File.WriteAllText(Path.Combine(artifactsDir, "browser-failed.txt"),
                "Headless browser failed to produce a PNG for this fixture.\n" +
                "Browser executable: " + browserBin + "\n");
            Assert.Fail($"Headless browser produced no PNG for fixture '{fixtureRelPath}' " +
                        $"(executable: {browserBin}). Inspect {artifactsDir} for diagnostics.");
        }

        try
        {
            var (browserRgba, bw, bh) = PixelComparer.LoadPngAsRgba(browserPng);

            // Extra browser-frame allowance can make the capture taller, but a
            // capture smaller than the requested SVG is an invalid oracle.
            // Never pass by comparing only a cropped common rectangle.
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

            var diff = PixelComparer.Compare(oursCropped, browserCropped, cw, ch, channelTolerance: 2);
            bool textOrFilter = fixtureRelPath.StartsWith("08-filters/", StringComparison.OrdinalIgnoreCase) ||
                fixtureRelPath.StartsWith("13-text/", StringComparison.OrdinalIgnoreCase);
            double maxMeanChannelDelta = textOrFilter ? 5 : 2;
            double minimumSsim = textOrFilter ? 0.980 : 0.995;
            double minimumAlphaIou = textOrFilter ? 0.980 : 0.995;
            File.WriteAllText(Path.Combine(artifactsDir, "diff-stats.txt"),
                $"{cw}x{ch}\nmaxChannelDelta={diff.MaxChannelDelta}\nmeanChannelDelta={diff.MeanChannelDelta:F3}\n" +
                $"differingPixelFraction={diff.DifferingPixelFraction:F4}\nssim={diff.StructuralSimilarity:F6}\n" +
                $"alphaBoundaryTolerancePx=1\nalphaIoU={diff.AlphaIntersectionOverUnion:F6}\n");

            Assert.True(diff.MeanChannelDelta <= maxMeanChannelDelta,
                $"{fixtureRelPath}: mean channel delta {diff.MeanChannelDelta:F3} > {maxMeanChannelDelta}");
            Assert.True(diff.StructuralSimilarity >= minimumSsim,
                $"{fixtureRelPath}: SSIM {diff.StructuralSimilarity:F6} < {minimumSsim:F3}");
            Assert.True(diff.AlphaIntersectionOverUnion >= minimumAlphaIou,
                $"{fixtureRelPath}: boundary-tolerant alpha IoU " +
                $"{diff.AlphaIntersectionOverUnion:F6} < {minimumAlphaIou:F3}");
        }
        finally
        {
            try { File.Delete(browserPng); Directory.Delete(Path.GetDirectoryName(browserPng)!, recursive: true); } catch { }
        }
    }
}
