using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MarkdownRenderer.Layout.Boxes;
using MarkdownRenderer.Svg.ThorVG;
using Xunit;

namespace MarkdownRenderer.PixelTests;

/// <summary>
/// Auto-discovered SVG fixture suite. Each <c>Fixtures\svg\**\*.svg</c>
/// file becomes one test case. The fixture is rasterized twice:
///   1. ThorVG (our renderer) into an RGBA buffer at the SVG's intrinsic size.
///   2. Headless Edge/Chrome at the same pixel dimensions (ground truth).
/// Buffers are compared with a tolerant per-pixel diff. Per-fixture artifacts
/// (ours.png, browser.png, diff-stats.txt) land in artifacts\svg-pixel-diffs\
/// so manual inspection is possible after the run.
///
/// Tolerance is intentionally relaxed: ThorVG and Chromium/Skia use different
/// antialiasing kernels and gradient interpolation, so byte-perfect parity is
/// not expected. The thresholds (mean delta ≤ 12, differing fraction ≤ 0.20)
/// are tuned to catch gross rendering bugs (missing strokes, wrong colors,
/// blank output) while letting subtle pixel-level disagreements through.
///
/// Tests self-skip when no headless browser is installed (CI machines without
/// Chrome/Edge) — the ThorVG render still runs end-to-end so we at least
/// catch DllNotFound / crashes / null returns from the rasterizer.
/// </summary>
public sealed class SvgComplianceTests
{
    private const double MaxMeanChannelDelta = 12.0;
    private const double MaxDifferingPixelFraction = 0.20;

    // ThorVG 1.1.1 silently ignores unsupported filter primitives. The public
    // renderer deliberately rejects these inputs so callers get the atomic,
    // accessible image fallback instead of a plausible but incorrect bitmap.
    private static readonly HashSet<string> ExpectedAtomicFallbacks = new(StringComparer.OrdinalIgnoreCase)
    {
        "08-filters/drop-shadow.svg",
        "08-filters/color-matrix.svg",
        "15-flying-pig/gradient-shadow.svg",
    };

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
    public void Rasterize_Fixture_MatchesBrowser(string fixtureRelPath)
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

        if (ExpectedAtomicFallbacks.Contains(fixtureRelPath))
        {
            Assert.Null(ThorVgFeature.Rasterize(svg, w, h));
            return;
        }

        // 1) ThorVG render. Every fixture in the explicitly supported subset
        // must succeed; unsupported primitives are handled above.
        var raster = ThorVgFeature.Rasterize(svg, w, h);
        Assert.NotNull(raster);
        Assert.Equal(w * h * 4, raster!.BgraPremultipliedPixels.Length);

        byte[] oursRgba = PixelComparer.BgraPremulToRgba(
            raster.BgraPremultipliedPixels.ToArray(),
            w,
            h);

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
        if (browserBin is null)
        {
            File.WriteAllText(Path.Combine(artifactsDir, "skipped.txt"),
                "No headless Edge/Chrome found; browser comparison skipped.\n" +
                "ThorVG render produced " + oursRgba.Length + " bytes.\n");
            // Use xunit.skippablefact Skip.If so CI dashboards show "skipped"
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

            var diff = PixelComparer.Compare(oursCropped, browserCropped, cw, ch, channelTolerance: 12);
            File.WriteAllText(Path.Combine(artifactsDir, "diff-stats.txt"),
                $"{cw}x{ch}\nmaxChannelDelta={diff.MaxChannelDelta}\nmeanChannelDelta={diff.MeanChannelDelta:F3}\ndifferingPixelFraction={diff.DifferingPixelFraction:F4}\n");

            Assert.True(diff.MeanChannelDelta <= MaxMeanChannelDelta,
                $"{fixtureRelPath}: mean channel delta {diff.MeanChannelDelta:F2} > {MaxMeanChannelDelta}");
            Assert.True(diff.DifferingPixelFraction <= MaxDifferingPixelFraction,
                $"{fixtureRelPath}: differing pixel fraction {diff.DifferingPixelFraction:F3} > {MaxDifferingPixelFraction}");
        }
        finally
        {
            try { File.Delete(browserPng); Directory.Delete(Path.GetDirectoryName(browserPng)!, recursive: true); } catch { }
        }
    }
}
