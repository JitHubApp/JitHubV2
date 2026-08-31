using JitHub.Services.Layout;
using Xunit;

namespace JitHub.WinUI.Tests.Services;

public sealed class KeyedViewportAnchorPolicyTests
{
    [Fact]
    public void FindByKey_PreservesVisibleAnchorAcrossReordering()
    {
        ViewportRow anchor = new("issue-42");
        ViewportRow[] refreshedRows = [new("issue-99"), new("issue-7"), anchor, new("issue-1")];

        ViewportRow? restored = KeyedViewportAnchorPolicy.FindByKey(
            refreshedRows,
            "issue-42",
            static row => row.Key);

        Assert.Same(anchor, restored);
    }

    [Theory]
    [InlineData(400, 0, -18, 1000, 418)]
    [InlineData(20, -12, -12, 1000, 20)]
    [InlineData(990, 40, -10, 1000, 1000)]
    public void ResolveTargetVerticalOffset_KeepsAnchorAtCapturedViewportPosition(
        double currentOffset,
        double currentAnchorOffset,
        double capturedAnchorOffset,
        double scrollableHeight,
        double expected)
    {
        double result = KeyedViewportAnchorPolicy.ResolveTargetVerticalOffset(
            currentOffset,
            currentAnchorOffset,
            capturedAnchorOffset,
            scrollableHeight);

        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData(0, 0, false)]
    [InlineData(0, 250, false)]
    [InlineData(500, 499, true)]
    [InlineData(500, 400, false)]
    public void IsAtScrollableBottom_RequiresAPreviouslyScrollableList(
        double scrollableHeight,
        double verticalOffset,
        bool expected)
    {
        Assert.Equal(expected, ListViewScrollAnchorPolicy.IsAtScrollableBottom(scrollableHeight, verticalOffset));
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void ShouldRestore_CancelsAfterUserInteraction(bool userInteracted, bool expected)
    {
        Assert.Equal(expected, ListViewScrollAnchorPolicy.ShouldRestore(userInteracted));
    }

    private sealed record ViewportRow(string Key);
}
