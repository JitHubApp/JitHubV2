using MarkdownRenderer.Accessibility;
using Xunit;

namespace MarkdownRenderer.Tests;

public sealed class MarkdownImageSemanticPolicyTests
{
    [Fact]
    public void EmptyAltUnlinkedImage_IsDecorative()
    {
        Assert.Null(MarkdownImageSemanticPolicy.GetInlineAccessibleName("", false, null, "Image"));
        Assert.True(MarkdownImageSemanticPolicy.IsDecorativeBlock(""));
    }

    [Fact]
    public void EmptyAltLinkedImage_RetainsInvokableName()
    {
        Assert.Equal(
            "Open dashboard",
            MarkdownImageSemanticPolicy.GetInlineAccessibleName("", true, "Open dashboard", "Image"));
        Assert.Equal(
            "Image",
            MarkdownImageSemanticPolicy.GetInlineAccessibleName("", true, null, "Image"));
    }

    [Fact]
    public void AuthoredAltText_IsPreservedExactly()
    {
        Assert.Equal(
            "Build status",
            MarkdownImageSemanticPolicy.GetInlineAccessibleName("Build status", false, null, "Image"));
        Assert.False(MarkdownImageSemanticPolicy.IsDecorativeBlock("Build status"));
    }
}
