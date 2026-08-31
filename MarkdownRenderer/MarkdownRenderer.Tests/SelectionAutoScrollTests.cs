using MarkdownRenderer.Controls;
using Xunit;

namespace MarkdownRenderer.Tests;

public class SelectionAutoScrollTests
{
    [Fact]
    public void ComputeDelta_InsideSafeBand_ReturnsZero()
    {
        Assert.Equal(0, SelectionAutoScroll.ComputeDelta(300, viewportTop: 100, viewportHeight: 500));
    }

    [Fact]
    public void ComputeDelta_NearTop_ReturnsNegativeStep()
    {
        double delta = SelectionAutoScroll.ComputeDelta(110, viewportTop: 100, viewportHeight: 500);
        Assert.True(delta < 0);
        Assert.True(delta >= -SelectionAutoScroll.MaxStepPx);
    }

    [Fact]
    public void ComputeDelta_NearBottom_ReturnsPositiveStep()
    {
        double delta = SelectionAutoScroll.ComputeDelta(590, viewportTop: 100, viewportHeight: 500);
        Assert.True(delta > 0);
        Assert.True(delta <= SelectionAutoScroll.MaxStepPx);
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
