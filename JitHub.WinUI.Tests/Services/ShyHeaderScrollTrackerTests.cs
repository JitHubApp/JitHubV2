using JitHub.WinUI.Helpers;
using Xunit;

namespace JitHub.WinUI.Tests.Services;

public sealed class ShyHeaderScrollTrackerTests
{
    // Issue #51 at 1360x900: the conversation scrolls 369 DIP with the header
    // shown and 252 DIP once hiding it gives the viewport 117 DIP more.
    private const double ExpandedRange = 369;
    private const double CollapsedRange = 252;

    [Fact]
    public void ScrollingPastTheStartOffsetHidesTheHeader()
    {
        ShyHeaderScrollTracker tracker = CreateTracker();

        Assert.Equal(ShyHeaderScrollAction.None, tracker.Observe(40, ExpandedRange, canHide: true));
        Assert.Equal(ShyHeaderScrollAction.Hide, tracker.Observe(60, ExpandedRange, canHide: true));
        Assert.True(tracker.IsShy);
    }

    [Fact]
    public void AHeaderThatCannotCollapseStaysVisible()
    {
        ShyHeaderScrollTracker tracker = CreateTracker();

        Assert.Equal(ShyHeaderScrollAction.None, tracker.Observe(60, ExpandedRange, canHide: false));
        Assert.False(tracker.IsShy);
    }

    [Fact]
    public void UpwardTravelRevealsAndDownwardTravelHidesAgain()
    {
        ShyHeaderScrollTracker tracker = CreateTracker();
        tracker.Reset(200, CollapsedRange, isShy: true);

        Assert.Equal(ShyHeaderScrollAction.None, tracker.Observe(170, CollapsedRange, canHide: true));
        Assert.Equal(ShyHeaderScrollAction.Reveal, tracker.Observe(130, CollapsedRange, canHide: true));
        Assert.False(tracker.IsShy);
        Assert.Equal(ShyHeaderScrollAction.None, tracker.Observe(140, ExpandedRange, canHide: true));
        Assert.Equal(ShyHeaderScrollAction.Hide, tracker.Observe(160, ExpandedRange, canHide: true));
        Assert.True(tracker.IsShy);
    }

    [Fact]
    public void ReachingTheTopRevealsTheHeader()
    {
        ShyHeaderScrollTracker tracker = CreateTracker();
        tracker.Reset(30, CollapsedRange, isShy: true);

        Assert.Equal(ShyHeaderScrollAction.Reveal, tracker.Observe(4, CollapsedRange, canHide: true));
    }

    [Fact]
    public void HidingAtTheEndDoesNotTreatTheClampedOffsetAsUpwardScroll()
    {
        // The header was revealed and the user flung to the end of the conversation.
        ShyHeaderScrollTracker tracker = CreateTracker();
        tracker.Reset(250, ExpandedRange, isShy: false);
        Assert.Equal(ShyHeaderScrollAction.Hide, tracker.Observe(368.75, ExpandedRange, canHide: true));

        // Hiding grew the viewport, so the scroll viewer clamped the offset to the
        // smaller range. Revealing here restores the range and the offset, which
        // hides the header again: the layout cycle that crashed issue #51.
        Assert.Equal(ShyHeaderScrollAction.None, tracker.Observe(CollapsedRange, CollapsedRange, canHide: true));
        Assert.True(tracker.IsShy);
        Assert.Equal(ShyHeaderScrollAction.None, tracker.Observe(CollapsedRange, CollapsedRange, canHide: true));
        Assert.True(tracker.IsShy);
    }

    [Fact]
    public void RevealingAtTheEndDoesNotTreatTheRestoredOffsetAsDownwardScroll()
    {
        ShyHeaderScrollTracker tracker = CreateTracker();
        tracker.Reset(CollapsedRange, CollapsedRange, isShy: true);
        Assert.Equal(ShyHeaderScrollAction.Reveal, tracker.Observe(180, CollapsedRange, canHide: true));

        // Showing the header shrank the viewport; a scroll viewer that kept its
        // requested offset moves back down into the larger range.
        Assert.Equal(ShyHeaderScrollAction.None, tracker.Observe(ExpandedRange, ExpandedRange, canHide: true));
        Assert.False(tracker.IsShy);
    }

    [Fact]
    public void ScrollingAfterTheRangeSettlesStillCountsTravel()
    {
        ShyHeaderScrollTracker tracker = CreateTracker();
        tracker.Reset(250, ExpandedRange, isShy: false);
        tracker.Observe(368.75, ExpandedRange, canHide: true);
        tracker.Observe(CollapsedRange, CollapsedRange, canHide: true);

        Assert.Equal(ShyHeaderScrollAction.None, tracker.Observe(220, CollapsedRange, canHide: true));
        Assert.Equal(ShyHeaderScrollAction.Reveal, tracker.Observe(180, CollapsedRange, canHide: true));
    }

    [Fact]
    public void ContentGrowingAwayFromTheEndKeepsCountingTravel()
    {
        // An image loading below the viewport changes the range without moving the offset.
        ShyHeaderScrollTracker tracker = CreateTracker();
        tracker.Reset(20, ExpandedRange, isShy: false);

        Assert.Equal(ShyHeaderScrollAction.Hide, tracker.Observe(60, ExpandedRange + 300, canHide: true));
    }

    private static ShyHeaderScrollTracker CreateTracker() => new(
        startOffset: 56,
        restoreOffset: 8,
        revealTravel: 64,
        rehideTravel: 24,
        epsilon: 0.5);
}
