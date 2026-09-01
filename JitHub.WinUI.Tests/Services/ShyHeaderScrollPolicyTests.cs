using JitHub.WinUI.Helpers;
using Xunit;

namespace JitHub.WinUI.Tests.Services;

public sealed class ShyHeaderScrollPolicyTests
{
    [Theory]
    [InlineData(0, 300, 54, 8, 246, 238)]
    [InlineData(316, 300, 54, 8, 562, 554)]
    [InlineData(-20, 80, 40, 8, 20, 12)]
    public void TryGetOverlayOffsets_WaitsUntilTheExpandedBottomReachesTheOverlay(
        double sourceTop,
        double sourceHeight,
        double overlayHeight,
        double restoreInset,
        double expectedStart,
        double expectedRestore)
    {
        bool result = ShyHeaderScrollPolicy.TryGetOverlayOffsets(
            sourceTop,
            sourceHeight,
            overlayHeight,
            restoreInset,
            out double startOffset,
            out double restoreOffset);

        Assert.True(result);
        Assert.Equal(expectedStart, startOffset);
        Assert.Equal(expectedRestore, restoreOffset);
    }

    [Fact]
    public void OverlayOffsets_DoNotCollapseWhenATallViewportCannotReachTheCardBottom()
    {
        Assert.True(ShyHeaderScrollPolicy.TryGetOverlayOffsets(
            0,
            300,
            54,
            8,
            out double startOffset,
            out _));

        Assert.False(128 >= startOffset);
        Assert.True(428 >= startOffset);
    }

    [Theory]
    [InlineData(double.NaN, 300, 54, 8)]
    [InlineData(0, double.PositiveInfinity, 54, 8)]
    [InlineData(0, 300, double.NegativeInfinity, 8)]
    [InlineData(0, 300, 54, double.NaN)]
    [InlineData(0, 0, 54, 8)]
    [InlineData(0, -1, 54, 8)]
    [InlineData(0, 300, -1, 8)]
    [InlineData(0, 300, 54, -1)]
    public void TryGetOverlayOffsets_RejectsInvalidGeometry(
        double sourceTop,
        double sourceHeight,
        double overlayHeight,
        double restoreInset)
    {
        Assert.False(ShyHeaderScrollPolicy.TryGetOverlayOffsets(
            sourceTop,
            sourceHeight,
            overlayHeight,
            restoreInset,
            out double startOffset,
            out double restoreOffset));
        Assert.Equal(0, startOffset);
        Assert.Equal(0, restoreOffset);
    }

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
