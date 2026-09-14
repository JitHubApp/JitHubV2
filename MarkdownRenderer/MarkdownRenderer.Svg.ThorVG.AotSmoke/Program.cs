using System.Text;
using MarkdownRenderer.Svg.ThorVG;

bool expectUnavailable = args.Contains("--expect-unavailable", StringComparer.Ordinal);
byte[] svg = Encoding.UTF8.GetBytes(
    "<svg xmlns='http://www.w3.org/2000/svg' width='16' height='16'><rect width='16' height='16' fill='#00ff00'/></svg>");
ThorVgRaster? raster = ThorVgFeature.Rasterize(svg, 16, 16);

if (expectUnavailable)
{
    if (raster is not null || ThorVgFeature.TryGetLoadedNativeVersion() is not null)
        return 20;

    Console.WriteLine("ThorVG unavailable fallback passed.");
    return 0;
}

if (raster is null || raster.WidthPixels != 16 || raster.HeightPixels != 16 ||
    raster.BgraPremultipliedPixels.Length != 16 * 16 * 4)
{
    return 10;
}

Version? version = ThorVgFeature.TryGetLoadedNativeVersion();
if (version?.ToString(3) != ThorVgFeature.NativeVersion)
    return 11;

ReadOnlySpan<byte> pixels = raster.BgraPremultipliedPixels.Span;
int center = ((8 * 16) + 8) * 4;
if (pixels[center] > 40 || pixels[center + 1] < 200 || pixels[center + 2] > 40 || pixels[center + 3] < 200)
    return 12;

Console.WriteLine($"ThorVG {version} deployment smoke passed.");
return 0;
