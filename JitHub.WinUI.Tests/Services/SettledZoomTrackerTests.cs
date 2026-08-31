using JitHub.Services.CodeViewer;
using Xunit;

namespace JitHub.WinUI.Tests.Services;

public sealed class SettledZoomTrackerTests
{
    [Fact]
    public void PanAtCurrentZoom_DoesNotProduceSettledAction()
    {
        SettledZoomTracker tracker = new();
        tracker.Reset(1.5f);

        Assert.False(tracker.Observe(1.5f));
        Assert.False(tracker.TrySettle(out _));
    }

    [Fact]
    public void IntermediateZoomFactors_CoalesceToLatestSettledAction()
    {
        SettledZoomTracker tracker = new();
        tracker.Reset(1);

        Assert.True(tracker.Observe(1.25f));
        Assert.True(tracker.Observe(1.75f));
        Assert.True(tracker.TrySettle(out float zoomFactor));
        Assert.Equal(1.75f, zoomFactor);
        Assert.False(tracker.TrySettle(out _));
    }

    [Fact]
    public void ReturningToReportedZoom_CancelsPendingAction()
    {
        SettledZoomTracker tracker = new();
        tracker.Reset(1);

        Assert.True(tracker.Observe(2));
        Assert.False(tracker.Observe(1));
        Assert.False(tracker.TrySettle(out _));
    }

    [Fact]
    public void InvalidZoomFactor_CannotProduceAction()
    {
        SettledZoomTracker tracker = new();
        tracker.Reset(2);

        Assert.False(tracker.Observe(float.NaN));
        Assert.False(tracker.Observe(float.PositiveInfinity));
        Assert.False(tracker.Observe(0));
        Assert.False(tracker.TrySettle(out _));
    }
}
