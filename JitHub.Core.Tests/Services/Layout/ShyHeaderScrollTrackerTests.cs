using JitHub.Core.Services.Layout;
using Xunit;

namespace JitHub.Core.Tests.Services.Layout;

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

    [Fact]
    public void AHeldGestureHidesOnceEvenIfTheOffsetJumpsBack()
    {
        ShyHeaderScrollTracker tracker = CreateTracker();
        tracker.Reset(20, 2000, isShy: false);
        tracker.BeginManipulation();

        Assert.Equal(ShyHeaderScrollAction.Hide, tracker.Observe(60, 2000, canHide: true, isIntermediate: true));
        Assert.Equal(ShyHeaderScrollAction.None, tracker.Observe(300, 2000, canHide: true, isIntermediate: true));
        Assert.Equal(ShyHeaderScrollAction.None, tracker.Observe(200, 2000, canHide: true, isIntermediate: true));
        Assert.Equal(ShyHeaderScrollAction.None, tracker.Observe(400, 2000, canHide: true, isIntermediate: true));
        Assert.True(tracker.IsShy);
    }

    [Fact]
    public void AFinalViewChangeDuringAHeldGestureDoesNotReleaseTheHeader()
    {
        ShyHeaderScrollTracker tracker = CreateTracker();
        tracker.Reset(20, 2000, isShy: false);
        tracker.BeginManipulation();
        tracker.Observe(60, 2000, canHide: true, isIntermediate: true);

        Assert.Equal(ShyHeaderScrollAction.None, tracker.Observe(200, 2000, canHide: true, isIntermediate: false));
        Assert.Equal(ShyHeaderScrollAction.None, tracker.Observe(100, 2000, canHide: true, isIntermediate: true));
        Assert.True(tracker.IsShy);
    }

    [Fact]
    public void EndingTheGestureAllowsTheNextScrollToRevealTheHeader()
    {
        ShyHeaderScrollTracker tracker = CreateTracker();
        tracker.Reset(20, 2000, isShy: false);
        tracker.BeginManipulation();
        tracker.Observe(300, 2000, canHide: true, isIntermediate: true);
        tracker.EndManipulation();

        tracker.BeginManipulation();
        Assert.Equal(ShyHeaderScrollAction.None, tracker.Observe(260, 2000, canHide: true, isIntermediate: true));
        Assert.Equal(ShyHeaderScrollAction.Reveal, tracker.Observe(220, 2000, canHide: true, isIntermediate: true));
    }

    [Fact]
    public void AQuickWheelReversalAppliesWhenTheScrollSettles()
    {
        ShyHeaderScrollTracker tracker = CreateTracker();
        tracker.Reset(20, 2000, isShy: false);

        Assert.Equal(ShyHeaderScrollAction.Hide, tracker.Observe(300, 2000, canHide: true, isIntermediate: true));
        Assert.Equal(ShyHeaderScrollAction.None, tracker.Observe(200, 2000, canHide: true, isIntermediate: true));
        Assert.Equal(ShyHeaderScrollAction.Reveal, tracker.Observe(200, 2000, canHide: true, isIntermediate: false));
    }

    [Fact]
    public void AHeldReversalAppliesWhenTheGestureEnds()
    {
        ShyHeaderScrollTracker tracker = CreateTracker();
        tracker.Reset(20, 2000, isShy: false);
        tracker.BeginManipulation();
        tracker.Observe(300, 2000, canHide: true, isIntermediate: true);
        Assert.Equal(ShyHeaderScrollAction.None, tracker.Observe(200, 2000, canHide: true, isIntermediate: true));

        tracker.EndManipulation();

        Assert.Equal(ShyHeaderScrollAction.Reveal, tracker.Observe(200, 2000, canHide: true));
    }

    [Fact]
    public void ContinuingDownwardCancelsAnOffsetJumpBeforeTheGestureEnds()
    {
        ShyHeaderScrollTracker tracker = CreateTracker();
        tracker.Reset(20, 2000, isShy: false);
        tracker.BeginManipulation();
        tracker.Observe(300, 2000, canHide: true, isIntermediate: true);
        tracker.Observe(200, 2000, canHide: true, isIntermediate: true);
        tracker.Observe(320, 2000, canHide: true, isIntermediate: true);

        tracker.EndManipulation();

        Assert.Equal(ShyHeaderScrollAction.None, tracker.Observe(320, 2000, canHide: true));
        Assert.True(tracker.IsShy);
    }

    [Fact]
    public void ReachingTheTopDuringAHeldGestureRevealsAndCanHideAgain()
    {
        ShyHeaderScrollTracker tracker = CreateTracker();
        tracker.Reset(20, 2000, isShy: false);
        tracker.BeginManipulation();
        tracker.Observe(100, 2000, canHide: true, isIntermediate: true);

        Assert.Equal(ShyHeaderScrollAction.Reveal, tracker.Observe(4, 2000, canHide: true, isIntermediate: true));
        Assert.Equal(ShyHeaderScrollAction.Hide, tracker.Observe(70, 2000, canHide: true, isIntermediate: true));
    }

    [Fact]
    public void LayoutSyncIsNotCountedAsScrollTravel()
    {
        ShyHeaderScrollTracker tracker = CreateTracker();
        tracker.Reset(300, 2000, isShy: true);

        tracker.SyncLayout(200, 2000);

        Assert.Equal(ShyHeaderScrollAction.None, tracker.Observe(200, 2000, canHide: true));
        Assert.True(tracker.IsShy);
    }

    private static ShyHeaderScrollTracker CreateTracker() => new(
        startOffset: 56,
        restoreOffset: 8,
        revealTravel: 64,
        rehideTravel: 24,
        epsilon: 0.5);
}
