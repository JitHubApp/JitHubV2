using MarkdownRenderer.Controls;
using Windows.Foundation;
using Xunit;

namespace MarkdownRenderer.Tests;

public sealed class MarkdownCanvasInvalidationPolicyTests
{
    [Fact]
    public void TryClipToCanvas_PreservesContainedRectangle()
    {
        bool accepted = MarkdownCanvasInvalidationPolicy.TryClipToCanvas(
            new Rect(10, 20, 30, 40),
            canvasWidth: 100,
            canvasHeight: 100,
            out Rect clipped);

        Assert.True(accepted);
        Assert.Equal(new Rect(10, 20, 30, 40), clipped);
    }

    [Fact]
    public void TryClipToCanvas_ClipsEveryCanvasEdge()
    {
        bool accepted = MarkdownCanvasInvalidationPolicy.TryClipToCanvas(
            new Rect(-5, -10, 120, 130),
            canvasWidth: 100,
            canvasHeight: 90,
            out Rect clipped);

        Assert.True(accepted);
        Assert.Equal(new Rect(0, 0, 100, 90), clipped);
    }

    [Theory]
    [InlineData(100, 10, 20, 20)]
    [InlineData(10, 100, 20, 20)]
    [InlineData(10, 10, 0, 20)]
    [InlineData(10, 10, 20, 0)]
    [InlineData(double.NaN, 10, 20, 20)]
    [InlineData(10, 10, double.PositiveInfinity, 20)]
    public void TryClipToCanvas_RejectsOffCanvasOrNonFiniteRectangle(
        double x,
        double y,
        double width,
        double height)
    {
        bool accepted = MarkdownCanvasInvalidationPolicy.TryClipToCanvas(
            new Rect(x, y, width, height),
            canvasWidth: 100,
            canvasHeight: 90,
            out Rect clipped);

        Assert.False(accepted);
        Assert.Equal(default, clipped);
    }
}
