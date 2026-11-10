using MarkdownRenderer.Images;
using MarkdownRenderer.Layout.Internal.Images;
using Xunit;

namespace MarkdownRenderer.GitHub.Tests;

/// <summary>
/// The provider-owned SVG state of each table image (document info, cache
/// generation, renderer identity, raster size, title, description and alt
/// text), which a committed root-local table relayout must keep.
/// </summary>
internal sealed record RootLocalTableSvgImageOwners(
    MarkdownSvgDocumentInfo[] Info,
    long[] ProviderGenerations,
    long[] RendererIdentities,
    (int Width, int Height)[] RasterSizes,
    string?[] Titles,
    string?[] Descriptions,
    string[] Alt)
{
    /// <summary>Captures the wide and tall SVG fixture images and checks their provider metadata.</summary>
    internal static RootLocalTableSvgImageOwners CaptureTableFixture(ImageBox[] images)
    {
        var owners = new RootLocalTableSvgImageOwners(
            images.Select(image => Assert.IsType<MarkdownSvgDocumentInfo>(image.Svg.Info)).ToArray(),
            images.Select(image => image.Svg.ProviderCacheGeneration).ToArray(),
            images.Select(image => image.Svg.RendererIdentity).ToArray(),
            images.Select(static image => image.SvgRasterPixelSize).ToArray(),
            images.Select(static image => image.SvgTitle).ToArray(),
            images.Select(static image => image.SvgDesc).ToArray(),
            images.Select(static image => image.Alt).ToArray());
        Assert.Equal<double?>(480d, owners.Info[0].IntrinsicWidthDips);
        Assert.Equal<double?>(96d, owners.Info[0].IntrinsicHeightDips);
        Assert.Equal<double?>(96d, owners.Info[1].IntrinsicWidthDips);
        Assert.Equal<double?>(240d, owners.Info[1].IntrinsicHeightDips);
        Assert.Equal("Wide root title", owners.Titles[0]);
        Assert.Equal("Tall root title", owners.Titles[1]);
        Assert.Equal("Wide provider description", owners.Descriptions[0]);
        Assert.Equal("Tall provider description", owners.Descriptions[1]);
        return owners;
    }

    internal void AssertPreserved(ImageBox[] imagesAfter)
    {
        for (int imageIndex = 0; imageIndex < Info.Length; imageIndex++)
        {
            ImageBox image = imagesAfter[imageIndex];
            Assert.Same(Info[imageIndex], image.Svg.Info);
            Assert.Equal(ProviderGenerations[imageIndex], image.Svg.ProviderCacheGeneration);
            Assert.Equal(RendererIdentities[imageIndex], image.Svg.RendererIdentity);
            Assert.Equal(RasterSizes[imageIndex], image.SvgRasterPixelSize);
            Assert.Equal(Titles[imageIndex], image.SvgTitle);
            Assert.Equal(Descriptions[imageIndex], image.SvgDesc);
            Assert.Equal(Alt[imageIndex], image.Alt);
        }
    }
}
