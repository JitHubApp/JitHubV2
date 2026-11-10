using MarkdownRenderer.Layout.Internal.Images;
using Windows.Foundation;
using Xunit;

namespace MarkdownRenderer.GitHub.Tests.Layout.Internal.Images.Svg;

/// <summary>
/// The accepted SVG metadata of an image box, captured before an operation that
/// must leave it untouched.
/// </summary>
internal sealed record AcceptedSvgMetadataSnapshot(
    Size IntrinsicSize,
    string? Title,
    string? Description,
    string? ContentHash,
    string? SecurityPartitionHash,
    object? Info,
    object? SourceIdentity,
    long SourceByteLength,
    long RendererIdentity,
    long ProviderCacheGeneration,
    int BitmapWidthPixels,
    int BitmapHeightPixels,
    int RenderGeneration)
{
    internal static AcceptedSvgMetadataSnapshot Capture(ImageBox image) => new(
        image.Svg.IntrinsicSize,
        image.Svg.Title,
        image.Svg.Description,
        image.Svg.ContentHash,
        image.Svg.SecurityPartitionHash,
        image.Svg.Info,
        image.Svg.SourceIdentity,
        image.Svg.SourceByteLength,
        image.Svg.RendererIdentity,
        image.Svg.ProviderCacheGeneration,
        image.Svg.BitmapWidthPixels,
        image.Svg.BitmapHeightPixels,
        image.Svg.RenderGeneration);

    internal void AssertUnchanged(ImageBox image)
    {
        Assert.Equal(IntrinsicSize, image.Svg.IntrinsicSize);
        Assert.Equal(Title, image.Svg.Title);
        Assert.Equal(Description, image.Svg.Description);
        Assert.Equal(ContentHash, image.Svg.ContentHash);
        Assert.Equal(SecurityPartitionHash, image.Svg.SecurityPartitionHash);
        Assert.Equal(Info, image.Svg.Info);
        Assert.Same(SourceIdentity, image.Svg.SourceIdentity);
        Assert.Equal(SourceByteLength, image.Svg.SourceByteLength);
        Assert.Equal(RendererIdentity, image.Svg.RendererIdentity);
        Assert.Equal(ProviderCacheGeneration, image.Svg.ProviderCacheGeneration);
        Assert.Equal(BitmapWidthPixels, image.Svg.BitmapWidthPixels);
        Assert.Equal(BitmapHeightPixels, image.Svg.BitmapHeightPixels);
        Assert.Equal(RenderGeneration, image.Svg.RenderGeneration);
    }
}
