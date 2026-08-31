using JitHub.WinUI.Helpers;
using Xunit;

namespace JitHub.WinUI.Tests.Services;

public sealed class ShyHeaderScrollPolicyTests
{
    [Theory]
    [InlineData(249, 240, 8, true)]
    [InlineData(248, 240, 8, false)]
    [InlineData(145, 240, 8, false)]
    [InlineData(400, 80, 8, true)]
    public void CanCollapse_PreservesEnoughScrollRangeWhenTheCompactHeaderIsAnOverlay(
        double scrollableHeight,
        double expandedHeaderHeight,
        double restoreOffset,
        bool expected)
    {
        Assert.Equal(
            expected,
            ShyHeaderScrollPolicy.CanCollapse(
                scrollableHeight,
                expandedHeaderHeight,
                restoreOffset));
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void CanCollapse_RejectsInvalidMeasurements(double value)
    {
        Assert.False(ShyHeaderScrollPolicy.CanCollapse(value, 240, 8));
        Assert.False(ShyHeaderScrollPolicy.CanCollapse(400, value, 8));
        Assert.False(ShyHeaderScrollPolicy.CanCollapse(400, 240, value));
    }
}
