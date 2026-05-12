using System;
using System.IO;
using MarkdownRenderer.Layout.Boxes;
using Xunit;

namespace MarkdownRenderer.PixelTests;

/// <summary>
/// Renders fixture SVGs through both <see cref="SvgSkiaRasterizer"/> and a
/// headless Chromium browser, then asserts the two outputs agree within a
/// per-pixel tolerance. The browser acts as the industry-standard
/// rasterizer ground truth — if our rasterizer drifts (e.g. wrong gamma,
/// missing filter, transform bug) the diff metrics surface it.
///
/// Tests skip themselves when no browser is installed so CI without a
/// Chromium binary stays green; locally where Edge ships with Windows
/// they always run.
/// </summary>
public class SvgSkiaVsBrowserTests
{
    private static string FixturePath(string name)
    {
        var dir = Path.GetDirectoryName(typeof(SvgSkiaVsBrowserTests).Assembly.Location)!;
        return Path.Combine(dir, "Fixtures", name);
    }

    [Theory]
    [InlineData("solid-square.svg", 64, 64, 0.005, 4)]
    [InlineData("circle.svg", 64, 64, 0.06, 24)]
    [InlineData("blurred-diamond.svg", 80, 80, 0.20, 64)]
    [InlineData("linear-gradient.svg", 100, 60, 0.05, 16)]
    public void SkiaRaster_AgreesWithBrowser_WithinTolerance(
        string fixture, int width, int height,
        double maxDifferingFraction, int channelTolerance)
    {
        var browser = HeadlessBrowserRasterizer.TryFindBrowser();
        Skip.If(browser is null, "No Chromium browser found; pixel-compare skipped.");

        var svgBytes = File.ReadAllBytes(FixturePath(fixture));

        SvgSkiaRasterizer.Raster? raster;
        try
        {
            // Validate Skia path with raw API to surface native-load issues.
            using (var probe = new Svg.Skia.SKSvg())
            {
                using var ms = new MemoryStream(svgBytes);
                var pic = probe.Load(ms);
                Assert.True(pic is not null, "SKSvg.Load returned null in test runner");
            }
            raster = SvgSkiaRasterizer.Rasterize(svgBytes, width, height);
        }
        catch (Exception ex)
        {
            throw new Xunit.Sdk.XunitException($"Skia rasterize threw: {ex}");
        }
        Assert.True(raster is not null, $"Skia rasterize returned null for {fixture}");
        var ours = PixelComparer.BgraPremulToRgba(raster!.Value.Bgra, width, height);

        var pngPath = HeadlessBrowserRasterizer.Rasterize(svgBytes, width, height);
        Assert.NotNull(pngPath);
        try
        {
            var (browserRgba, bw, bh) = PixelComparer.LoadPngAsRgba(pngPath!);
            // The browser may have padded the screenshot to the requested
            // window size; crop it back to width × height starting at 0,0
            // (the SVG image is anchored to the top-left of the body).
            byte[] cropped = (bw == width && bh == height)
                ? browserRgba
                : PixelComparer.Crop(browserRgba, bw, bh, 0, 0, width, height);

            var diff = PixelComparer.Compare(ours, cropped, width, height, channelTolerance);
            // Emit diff PNG for human inspection on failure.
            string artifactDir = Path.Combine(Path.GetDirectoryName(typeof(SvgSkiaVsBrowserTests).Assembly.Location)!, "Diffs");
            Directory.CreateDirectory(artifactDir);
            PixelComparer.SaveRgbaAsPng(ours, width, height, Path.Combine(artifactDir, $"{Path.GetFileNameWithoutExtension(fixture)}.skia.png"));
            PixelComparer.SaveRgbaAsPng(cropped, width, height, Path.Combine(artifactDir, $"{Path.GetFileNameWithoutExtension(fixture)}.browser.png"));

            Assert.True(diff.DifferingPixelFraction <= maxDifferingFraction,
                $"{fixture}: {diff.DifferingPixelFraction:P2} of pixels differ (limit {maxDifferingFraction:P2}). " +
                $"max channel delta = {diff.MaxChannelDelta}, mean = {diff.MeanChannelDelta:F2}. " +
                $"Diffs written to {artifactDir}.");
        }
        finally
        {
            try { File.Delete(pngPath!); } catch { }
            try { Directory.Delete(Path.GetDirectoryName(pngPath!)!, recursive: true); } catch { }
        }
    }
}

/// <summary>
/// Minimal Skip.If shim — xunit v2 doesn't have a built-in Skip but we
/// don't want to add another dep. Throws an exception that xunit reports
/// as a failure; for genuine "not available" cases prefer to bypass with
/// a Theory data filter or guard the test entry. We use it here because
/// the alternative (no test) hides the gap entirely.
/// </summary>
internal static class Skip
{
    public static void If(bool condition, string reason)
    {
        if (condition) throw new SkipException(reason);
    }
}

internal sealed class SkipException : Exception
{
    public SkipException(string reason) : base(reason) { }
}
