using MarkdownRenderer.Layout;
using Xunit;

namespace MarkdownRenderer.Tests;

public sealed class FocusableItemTests
{
    [Fact]
    public void LegacyBooleanConstructorPreservesLinkAndInlineEmbedKinds()
    {
        var link = new FocusableItem(3, 4, isLink: true);
        var inlineEmbed = new FocusableItem(5, 6, isLink: false);

        Assert.Equal(FocusableItemKind.Link, link.Kind);
        Assert.True(link.IsLink);
        Assert.False(link.IsInlineEmbed);

        Assert.Equal(FocusableItemKind.InlineEmbed, inlineEmbed.Kind);
        Assert.False(inlineEmbed.IsLink);
        Assert.True(inlineEmbed.IsInlineEmbed);
    }

    [Fact]
    public void ExplicitKindConstructorSupportsBlockEmbeds()
    {
        var item = new FocusableItem(7, 0, FocusableItemKind.BlockEmbed);

        Assert.Equal(7, item.BlockIndex);
        Assert.Equal(0, item.InlineIndex);
        Assert.True(item.IsBlockEmbed);
        Assert.False(item.IsLink);
    }
}
