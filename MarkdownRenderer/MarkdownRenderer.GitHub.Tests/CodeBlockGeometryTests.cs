using MarkdownRenderer.Layout.Boxes;
using Windows.Foundation;
using Xunit;

namespace MarkdownRenderer.GitHub.Tests;

public sealed class CodeBlockGeometryTests
{
    [Theory]
    [InlineData(0, 0, 1, 1, true)]
    [InlineData(0, 0, 0, 1, false)]
    [InlineData(0, 0, 1, 0, false)]
    [InlineData(double.NaN, 0, 1, 1, false)]
    [InlineData(0, double.PositiveInfinity, 1, 1, false)]
    [InlineData(0, 0, double.PositiveInfinity, 1, false)]
    [InlineData(0, 0, 1, double.NaN, false)]
    [InlineData(double.MaxValue, 0, 1, 1, false)]
    [InlineData(0, 0, double.MaxValue, 1, false)]
    public void DrawableRectangleRequiresFinitePositiveGeometry(
        double x,
        double y,
        double width,
        double height,
        bool expected)
    {
        Assert.Equal(expected, CodeBlockBox.IsDrawableRectangle(new Rect(x, y, width, height)));
    }
}
