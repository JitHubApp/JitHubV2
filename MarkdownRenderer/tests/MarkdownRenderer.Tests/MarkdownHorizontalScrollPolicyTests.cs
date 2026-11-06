using MarkdownRenderer.Accessibility;
using Xunit;

namespace MarkdownRenderer.Tests;

public sealed class MarkdownHorizontalScrollPolicyTests
{
    [Theory]
    [InlineData(false, 0, 0)]
    [InlineData(false, 200, 25)]
    [InlineData(false, 800, 100)]
    [InlineData(true, 0, 100)]
    [InlineData(true, 200, 75)]
    [InlineData(true, 800, 0)]
    public void PhysicalPercent_UsesPhysicalLeftToRightCoordinates(
        bool isRightToLeft,
        double logicalOffset,
        double expectedPercent)
    {
        double actual = MarkdownHorizontalScrollPolicy.GetPhysicalPercent(
            logicalOffset,
            extent: 1000,
            viewport: 200,
            isRightToLeft);

        Assert.Equal(expectedPercent, actual, precision: 8);
    }

    [Theory]
    [InlineData(false, 0, 0)]
    [InlineData(false, 25, 200)]
    [InlineData(false, 100, 800)]
    [InlineData(true, 0, 800)]
    [InlineData(true, 25, 600)]
    [InlineData(true, 100, 0)]
    public void PhysicalPercent_RoundTripsToLogicalOffset(
        bool isRightToLeft,
        double physicalPercent,
        double expectedLogicalOffset)
    {
        double logicalOffset = MarkdownHorizontalScrollPolicy.GetLogicalOffset(
            physicalPercent,
            extent: 1000,
            viewport: 200,
            isRightToLeft);

        Assert.Equal(expectedLogicalOffset, logicalOffset, precision: 8);
        Assert.Equal(
            physicalPercent,
            MarkdownHorizontalScrollPolicy.GetPhysicalPercent(
                logicalOffset,
                extent: 1000,
                viewport: 200,
                isRightToLeft),
            precision: 8);
    }

    [Theory]
    [InlineData(false, -48, -48)]
    [InlineData(false, 48, 48)]
    [InlineData(true, -48, 48)]
    [InlineData(true, 48, -48)]
    public void PhysicalScrollDelta_IsInvertedForRightToLeft(
        bool isRightToLeft,
        double physicalDelta,
        double expectedLogicalDelta)
    {
        Assert.Equal(
            expectedLogicalDelta,
            MarkdownHorizontalScrollPolicy.GetLogicalDelta(physicalDelta, isRightToLeft));
    }
}
