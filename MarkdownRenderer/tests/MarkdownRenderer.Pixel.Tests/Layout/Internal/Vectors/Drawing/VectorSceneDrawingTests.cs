using MarkdownRenderer.Extensions.Vectors;
using MarkdownRenderer.Layout.Internal.Vectors.Drawing;
using Xunit;

namespace MarkdownRenderer.Pixel.Tests.Layout.Internal.Vectors.Drawing;

public sealed class VectorSceneDrawingTests
{
    [Fact]
    public void TextLayoutUsesFiniteBoundsForLongVectorLabels()
    {
        var style = new MarkdownVectorTextStyle("Segoe UI", 16);
        var scene = new MarkdownVectorScene(
            628,
            169,
            169,
            [],
            0,
            new MarkdownVectorRectangle(0, 0, 628, 169),
            [],
            []);

        (float width, float height) = VectorScenePaintResolver.ResolveTextLayoutSize(
            scene,
            new string('W', 16_384),
            style);

        Assert.True(float.IsFinite(width));
        Assert.True(float.IsFinite(height));
        Assert.InRange(width, 628, 1_000_000);
        Assert.InRange(height, 169, 1_000_000);
    }
}
