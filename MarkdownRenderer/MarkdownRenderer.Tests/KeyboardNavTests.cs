using MarkdownRenderer.Layout;
using Windows.Foundation;
using Xunit;

namespace MarkdownRenderer.Tests;

/// <summary>
/// Unit tests for keyboard navigation infrastructure:
/// the <see cref="FocusableItem"/> struct value semantics.
/// End-to-end Tab/Enter/Escape navigation is verified in UI automation tests.
/// </summary>
public class KeyboardNavTests
{
    [Fact]
    public void FocusableItem_Link_IsLink_True()
    {
        var item = new FocusableItem(1, 2, isLink: true);
        Assert.True(item.IsLink);
        Assert.Equal(1, item.BlockIndex);
        Assert.Equal(2, item.InlineIndex);
    }

    [Fact]
    public void FocusableItem_Embed_IsLink_False()
    {
        var item = new FocusableItem(0, 0, isLink: false);
        Assert.False(item.IsLink);
    }

    [Fact]
    public void FocusableItem_Values_RoundTrip()
    {
        for (int b = 0; b < 5; b++)
        {
            for (int i = 0; i < 5; i++)
            {
                var link  = new FocusableItem(b, i, isLink: true);
                var embed = new FocusableItem(b, i, isLink: false);
                Assert.Equal(b, link.BlockIndex);
                Assert.Equal(i, link.InlineIndex);
                Assert.True(link.IsLink);
                Assert.False(embed.IsLink);
            }
        }
    }

    [Fact]
    public void FocusableItem_ZeroValues_Valid()
    {
        var item = new FocusableItem(0, 0, isLink: true);
        Assert.Equal(0, item.BlockIndex);
        Assert.Equal(0, item.InlineIndex);
    }

    [Fact]
    public void FocusableItem_CodeBlockCopy_RoundTripsKind()
    {
        var item = new FocusableItem(12, 0, FocusableItemKind.CodeBlockCopy);

        Assert.Equal(12, item.BlockIndex);
        Assert.False(item.IsLink);
        Assert.False(item.IsInlineEmbed);
        Assert.False(item.IsBlockEmbed);
        Assert.True(item.IsCodeBlockCopy);
        Assert.True(item.IsCodeBlockAction);
    }

    [Fact]
    public void FocusableItem_LargeValues_Preserved()
    {
        var item = new FocusableItem(int.MaxValue, int.MaxValue, isLink: false);
        Assert.Equal(int.MaxValue, item.BlockIndex);
        Assert.Equal(int.MaxValue, item.InlineIndex);
        Assert.False(item.IsLink);
    }

    [Theory]
    [InlineData(false, 0)]
    [InlineData(true, 3)]
    public void MoveTab_EntersAtBoundary_WhenNoResume(bool reverse, int expected)
    {
        Assert.Equal(expected, FocusNavigationHelper.MoveTab(4, currentIndex: -1, resumeIndex: -1, reverse));
    }

    [Fact]
    public void MoveTab_UsesPointerResumeIndex()
    {
        Assert.Equal(2, FocusNavigationHelper.MoveTab(4, currentIndex: -1, resumeIndex: 2, reverse: false));
        Assert.Equal(1, FocusNavigationHelper.MoveTab(4, currentIndex: -1, resumeIndex: 2, reverse: true));
    }

    [Fact]
    public void MoveTab_ReturnsMinusOne_WhenTraversalShouldExitControl()
    {
        Assert.Equal(-1, FocusNavigationHelper.MoveTab(3, currentIndex: 2, resumeIndex: -1, reverse: false));
        Assert.Equal(-1, FocusNavigationHelper.MoveTab(3, currentIndex: 0, resumeIndex: -1, reverse: true));
    }

    [Fact]
    public void FindNearestIndex_ChoosesContainingOrClosestRect()
    {
        var rects = new[]
        {
            new Rect(10, 10, 20, 20),
            new Rect(90, 10, 20, 20),
            new Rect(10, 90, 20, 20),
        };

        Assert.Equal(0, FocusNavigationHelper.FindNearestIndex(rects, new Point(15, 15)));
        Assert.Equal(1, FocusNavigationHelper.FindNearestIndex(rects, new Point(75, 20)));
        Assert.Equal(2, FocusNavigationHelper.FindNearestIndex(rects, new Point(20, 75)));
    }

    [Fact]
    public void MoveSpatial_UsesVisualDirection()
    {
        var rects = new[]
        {
            new Rect(50, 50, 20, 20),
            new Rect(100, 52, 20, 20),
            new Rect(52, 110, 20, 20),
            new Rect(100, 110, 20, 20),
        };

        Assert.Equal(1, FocusNavigationHelper.MoveSpatial(rects, 0, FocusNavigationDirection.Right));
        Assert.Equal(2, FocusNavigationHelper.MoveSpatial(rects, 0, FocusNavigationDirection.Down));
        Assert.Equal(-1, FocusNavigationHelper.MoveSpatial(rects, 0, FocusNavigationDirection.Left));
        Assert.Equal(3, FocusNavigationHelper.MoveSpatial(rects, 1, FocusNavigationDirection.Down));
    }
}
