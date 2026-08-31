using JitHub.Services.CodeViewer;
using Xunit;

namespace JitHub.WinUI.Tests.Services;

public sealed class ViewportZoomAnchorTests
{
    [Fact]
    public void ZoomingIn_PreservesTheViewportCenter()
    {
        ViewportZoomTarget target = ViewportZoomAnchor.PreserveCenter(
            horizontalOffset: 0,
            verticalOffset: 0,
            viewportWidth: 1200,
            viewportHeight: 800,
            currentZoomFactor: 1,
            targetZoomFactor: 8);

        Assert.Equal(4200, target.HorizontalOffset);
        Assert.Equal(2800, target.VerticalOffset);
    }

    [Fact]
    public void ZoomingBackOut_FromThePreservedCenterReturnsToOrigin()
    {
        ViewportZoomTarget target = ViewportZoomAnchor.PreserveCenter(
            horizontalOffset: 4200,
            verticalOffset: 2800,
            viewportWidth: 1200,
            viewportHeight: 800,
            currentZoomFactor: 8,
            targetZoomFactor: 1);

        Assert.Equal(0, target.HorizontalOffset);
        Assert.Equal(0, target.VerticalOffset);
    }

    [Fact]
    public void ZoomingBelowFit_ClampsOffsetsToOrigin()
    {
        ViewportZoomTarget target = ViewportZoomAnchor.PreserveCenter(
            horizontalOffset: 0,
            verticalOffset: 0,
            viewportWidth: 1200,
            viewportHeight: 800,
            currentZoomFactor: 1,
            targetZoomFactor: 0.1);

        Assert.Equal(0, target.HorizontalOffset);
        Assert.Equal(0, target.VerticalOffset);
    }

    [Fact]
    public void PannedViewport_KeepsItsCurrentContentCenter()
    {
        ViewportZoomTarget target = ViewportZoomAnchor.PreserveCenter(
            horizontalOffset: 120,
            verticalOffset: 80,
            viewportWidth: 1000,
            viewportHeight: 600,
            currentZoomFactor: 2,
            targetZoomFactor: 4);

        Assert.Equal(740, target.HorizontalOffset);
        Assert.Equal(460, target.VerticalOffset);
    }
}
