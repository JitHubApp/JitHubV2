using MarkdownRenderer.Controls;
using Xunit;

namespace MarkdownRenderer.Tests;

public class SelectionAutoScrollTests
{
    [Fact]
    public void ComputeVelocity_InsideSafeBand_ReturnsZero()
    {
        Assert.Equal(0, SelectionAutoScroll.ComputeVelocity(300, viewportTop: 100, viewportHeight: 500));
    }

    [Fact]
    public void ComputeFrameDelta_NearTop_ReturnsTimeScaledNegativeStep()
    {
        double delta = SelectionAutoScroll.ComputeFrameDelta(
            110, viewportTop: 100, viewportHeight: 500, elapsedSeconds: 1.0 / 60.0);
        Assert.True(delta < 0);
        Assert.True(delta >= -SelectionAutoScroll.MaximumVelocityDipPerSecond / 60.0);
    }

    [Fact]
    public void ComputeFrameDelta_NearBottom_ReturnsTimeScaledPositiveStep()
    {
        double delta = SelectionAutoScroll.ComputeFrameDelta(
            590, viewportTop: 100, viewportHeight: 500, elapsedSeconds: 1.0 / 60.0);
        Assert.True(delta > 0);
        Assert.True(delta <= SelectionAutoScroll.MaximumVelocityDipPerSecond / 60.0);
    }

    [Fact]
    public void ComputeFrameDelta_IsRefreshRateIndependent()
    {
        double at60Hz = 60 * SelectionAutoScroll.ComputeFrameDelta(
            590, viewportTop: 100, viewportHeight: 500, elapsedSeconds: 1.0 / 60.0);
        double at144Hz = 144 * SelectionAutoScroll.ComputeFrameDelta(
            590, viewportTop: 100, viewportHeight: 500, elapsedSeconds: 1.0 / 144.0);

        Assert.Equal(at60Hz, at144Hz, precision: 8);
    }

    [Fact]
    public void ComputeFrameDelta_ClampsLongUiThreadStall()
    {
        double stalled = SelectionAutoScroll.ComputeFrameDelta(
            590, viewportTop: 100, viewportHeight: 500, elapsedSeconds: 2);
        double bounded = SelectionAutoScroll.ComputeFrameDelta(
            590,
            viewportTop: 100,
            viewportHeight: 500,
            elapsedSeconds: SelectionAutoScroll.MaximumFrameDurationSeconds);

        Assert.Equal(bounded, stalled);
    }

    [Fact]
    public void ComputeVelocity_OverlappingBandsUsesNearestEdgeAndStopsAtCenter()
    {
        Assert.True(SelectionAutoScroll.ComputeVelocity(105, viewportTop: 100, viewportHeight: 60) < 0);
        Assert.Equal(0, SelectionAutoScroll.ComputeVelocity(130, viewportTop: 100, viewportHeight: 60));
        Assert.True(SelectionAutoScroll.ComputeVelocity(155, viewportTop: 100, viewportHeight: 60) > 0);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void ComputeVelocity_InvalidPointerCoordinateIsInert(double pointerY)
    {
        Assert.Equal(0, SelectionAutoScroll.ComputeVelocity(pointerY, 100, 500));
    }

    [Fact]
    public void ComputeDirectionalDelta_ShortViewport_DownwardDragNearTopDoesNotScrollBackward()
    {
        double delta = SelectionAutoScroll.ComputeDirectionalDelta(
            pointerY: 236,
            viewportTop: 225,
            viewportHeight: 94,
            previousPointerViewportY: 9);

        Assert.Equal(0, delta);
    }

    [Fact]
    public void ComputeDirectionalDelta_ShortViewport_UpwardDragNearTopScrollsBackward()
    {
        double delta = SelectionAutoScroll.ComputeDirectionalDelta(
            pointerY: 232,
            viewportTop: 225,
            viewportHeight: 94,
            previousPointerViewportY: 11);

        Assert.True(delta < 0);
    }

    [Fact]
    public void ComputeDirectionalDelta_ShortViewport_UpwardDragNearBottomDoesNotScrollForward()
    {
        double delta = SelectionAutoScroll.ComputeDirectionalDelta(
            pointerY: 304,
            viewportTop: 225,
            viewportHeight: 94,
            previousPointerViewportY: 82);

        Assert.Equal(0, delta);
    }

    [Fact]
    public void ComputeDirectionalDelta_ShortViewport_DownwardDragNearBottomScrollsForward()
    {
        double delta = SelectionAutoScroll.ComputeDirectionalDelta(
            pointerY: 308,
            viewportTop: 225,
            viewportHeight: 94,
            previousPointerViewportY: 79);

        Assert.True(delta > 0);
    }

    [Fact]
    public void ClampPointToViewport_ClampsToVisibleDocumentBand()
    {
        Assert.Equal(100, SelectionAutoScroll.ClampPointToViewport(50, 100, 500));
        Assert.Equal(599, SelectionAutoScroll.ClampPointToViewport(700, 100, 500));
    }
}
