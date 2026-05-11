using MarkdownRenderer.Layout;
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
    public void FocusableItem_LargeValues_Preserved()
    {
        var item = new FocusableItem(int.MaxValue, int.MaxValue, isLink: false);
        Assert.Equal(int.MaxValue, item.BlockIndex);
        Assert.Equal(int.MaxValue, item.InlineIndex);
        Assert.False(item.IsLink);
    }
}
