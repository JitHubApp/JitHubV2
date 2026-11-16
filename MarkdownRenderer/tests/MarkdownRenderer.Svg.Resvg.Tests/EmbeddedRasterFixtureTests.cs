using System.Text;
using MarkdownRenderer.Images.Svg;
using MarkdownRenderer.Svg.Resvg.Tests.TestSupport;
using Xunit;

namespace MarkdownRenderer.Svg.Resvg.Tests;

/// <summary>
/// Opens and renders README-shaped documents with many embedded PNG data
/// URIs (KaTeX- and sponsor-grid-shaped fixtures, repeated payloads, and a
/// screenful of uncached documents) under the content deadlines.
/// </summary>
[Collection(WorkerDeadlineSensitiveCollection.Name)]
public sealed class EmbeddedRasterFixtureTests
{
    [Fact]
    public async Task KaTeXShapedFixture_Renders182UniqueDecodableEmbeddedPngs()
    {
        var body = new StringBuilder();
        for (int index = 0; index < 182; index++)
        {
            byte[] png = ProviderTestRasters.CreatePng(
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
        Assert.True(ProviderTestRasters.HasVisiblePixel(raster.Pixels.Span));
    }

    [Fact]
    public async Task SponsorShapedFixture_Opens576EmbeddedImagesUnderTheHardDeadline()
    {
        // Mirrors the element count, text/clip complexity, and source-byte
        // scale of an image-heavy sponsor grid without depending on live or
        // private README assets. A prior image-only shape missed its open cost.
        string[] payloads = Enumerable.Range(0, 380)
            .Select(index => Convert.ToBase64String(ProviderTestRasters.CreatePng(
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
        Assert.True(ProviderTestRasters.HasVisiblePixel(raster.Pixels.Span));
    }

    [Fact]
    public async Task RepeatedEmbeddedPayload_IsDeduplicatedAndRendersWithinUniqueBudget()
    {
        byte[] png = ProviderTestRasters.CreatePng(20, 100, 220, byte.MaxValue);
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

        Assert.True(ProviderTestRasters.HasVisiblePixel(raster.Pixels.Span));
    }

    [Fact]
    public async Task TwelveVisibleUncachedDocuments_CanRemainInFlightTogether()
    {
        await using var renderer = new ResvgMarkdownSvgRenderer();
        Task<IMarkdownSvgDocument>[] opens = Enumerable.Range(0, 12).Select(async index =>
            await renderer.OpenAsync(new MarkdownSvgOpenRequest(Svg(
                $"<svg xmlns='http://www.w3.org/2000/svg' width='64' height='64'><rect width='64' height='64' fill='rgb({index * 17},40,180)'/></svg>")))).ToArray();
        try
        {
            IMarkdownSvgDocument[] documents = await Task.WhenAll(opens);
            Task<MarkdownSvgRaster>[] renders = documents.Select(async document =>
                await document.RenderAsync(new MarkdownSvgRenderRequest(64, 64))).ToArray();
            try
            {
                MarkdownSvgRaster[] rasters = await Task.WhenAll(renders);
                Assert.All(rasters, raster => Assert.True(ProviderTestRasters.HasVisiblePixel(raster.Pixels.Span)));
            }
            finally
            {
                foreach (Task<MarkdownSvgRaster> render in renders)
                    if (render.IsCompletedSuccessfully)
                        (await render).Dispose();
            }
        }
        finally
        {
            foreach (Task<IMarkdownSvgDocument> open in opens)
                if (open.IsCompletedSuccessfully)
                    (await open).Dispose();
        }

        await renderer.TrimCachesAsync();
    }

    private static byte[] Svg(string value) => Encoding.UTF8.GetBytes(value);
}
