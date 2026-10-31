using MarkdownRenderer.Controls;
using Xunit;

namespace MarkdownRenderer.GitHub.Tests;

public sealed class SelectionAdornerScrollRefreshTests
{
    [Fact]
    public void IdleScrollSkipsButEveryAdornmentStateKeepsTheRefresh()
    {
        Assert.False(ShouldRefresh());

        // Active selection is intentionally sufficient even before any
        // selection rectangles are visible; scrolling can bring an offscreen
        // range back into the viewport.
        Assert.True(ShouldRefresh(hasActiveSelection: true));
        Assert.True(ShouldRefresh(hasHoveredLink: true));
        Assert.True(ShouldRefresh(hasFocusedLink: true));

        // A visible image or retained old rectangles must get one refresh so
        // stale pixels can be cleared after the underlying state disappears.
        Assert.True(ShouldRefresh(hasVisibleAdorner: true));
        Assert.True(ShouldRefresh(hasRetainedRectangles: true));
        Assert.True(ShouldRefresh(hasTouchHandles: true));
        Assert.True(ShouldRefresh(hasPendingRefresh: true));
    }

    [Fact]
    public void BusyOrReentrantRefreshRemainsEligibleForTheNextScrollFrame()
    {
        var retry = new SelectionAdornerRetryState();
        retry.Request();
        Assert.True(ShouldRefresh(hasPendingRefresh: retry.IsPending));

        Assert.True(retry.TryBeginFrame());
        retry.Retry();
        Assert.True(ShouldRefresh(hasPendingRefresh: retry.IsPending));
    }

    private static bool ShouldRefresh(
        bool hasActiveSelection = false,
        bool hasHoveredLink = false,
        bool hasFocusedLink = false,
        bool hasVisibleAdorner = false,
        bool hasRetainedRectangles = false,
        bool hasTouchHandles = false,
        bool hasPendingRefresh = false)
        => SelectionAdornerScrollRefreshPolicy.ShouldRefreshForScroll(
            hasActiveSelection,
            hasHoveredLink,
            hasFocusedLink,
            hasVisibleAdorner,
            hasRetainedRectangles,
            hasTouchHandles,
            hasPendingRefresh);
}
