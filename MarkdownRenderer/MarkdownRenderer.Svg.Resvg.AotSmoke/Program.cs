using System.Text;
using MarkdownRenderer.Images;
using MarkdownRenderer.Svg.Resvg;

bool expectUnavailable = args.Contains("--expect-unavailable", StringComparer.Ordinal);
byte[] svg = Encoding.UTF8.GetBytes(
    "<svg xmlns='http://www.w3.org/2000/svg' width='16' height='16'><rect width='16' height='16' fill='#00ff00'/></svg>");

await using var renderer = new ResvgMarkdownSvgRenderer();
try
{
    await renderer.WarmUpAsync();
    using IMarkdownSvgDocument document = await renderer.OpenAsync(new MarkdownSvgOpenRequest(svg));
    using MarkdownSvgRaster raster = await document.RenderAsync(new MarkdownSvgRenderRequest(16, 16));

    if (expectUnavailable)
        return 20;
    if (raster.WidthPixels != 16 || raster.HeightPixels != 16 || raster.LengthBytes != 16 * 16 * 4)
        return 10;

    ReadOnlySpan<byte> pixels = raster.Pixels.Span;
    int center = ((8 * 16) + 8) * 4;
    if (pixels[center] > 40 || pixels[center + 1] < 200 || pixels[center + 2] > 40 || pixels[center + 3] < 200)
        return 11;

    Console.WriteLine($"resvg {ResvgMarkdownSvgRenderer.ResvgVersion} isolated-worker deployment smoke passed.");
    return 0;
}
catch (MarkdownSvgException exception) when (expectUnavailable &&
    exception.Reason is MarkdownSvgFailureReason.ArchitectureMismatch or MarkdownSvgFailureReason.WorkerFailure)
{
    Console.WriteLine("resvg unavailable fallback passed.");
    return 0;
}
