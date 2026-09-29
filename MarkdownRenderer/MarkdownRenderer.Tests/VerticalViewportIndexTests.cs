using MarkdownRenderer.Layout;
using Xunit;

namespace MarkdownRenderer.Tests;

public sealed class VerticalViewportIndexTests
{
    [Theory]
    [InlineData(-1, 0)]
    [InlineData(0, 0)]
    [InlineData(10, 0)]
    [InlineData(10.01, 1)]
    [InlineData(20, 1)]
    [InlineData(20.01, 3)]
    [InlineData(40, 3)]
    [InlineData(40.01, 4)]
    public void FindsFirstBottomEdgeThatCanIntersect(double viewportTop, int expected)
    {
        double[] bottoms = [10, 20, 20, 40];

        int actual = VerticalViewportIndex.FindFirstIntersecting(bottoms, viewportTop);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void EmptyIndexReturnsZero()
    {
        Assert.Equal(0, VerticalViewportIndex.FindFirstIntersecting([], 100));
    }

    [Fact]
    public void LookupCostIsLogarithmicForGiantDocuments()
    {
        const int count = 1_000_000;
        var bottoms = new double[count];
        for (int index = 0; index < count; index++)
            bottoms[index] = index + 1;

        Assert.Equal(999_999, VerticalViewportIndex.FindFirstIntersecting(bottoms, 999_999.5));
        Assert.Equal(count, VerticalViewportIndex.FindFirstIntersecting(bottoms, count + 1));
    }
}
