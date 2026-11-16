using Xunit;
using MarkdownRenderer.Accessibility.Internal.Semantics;

namespace MarkdownRenderer.Tests.Accessibility.Internal.Semantics;

public sealed class MarkdownImageSemanticPolicyTests
{
    [Fact]
    public void EmptyAltUnlinkedImage_IsDecorative()
    {
        Assert.Null(MarkdownImageSemanticPolicy.GetInlineAccessibleName("", false, null, null, "Image"));
        Assert.True(MarkdownImageSemanticPolicy.IsDecorativeBlock(""));
    }

    [Fact]
    public void EmptyAltLinkedImage_RetainsInvokableName()
    {
        Assert.Equal(
            "Open dashboard",
            MarkdownImageSemanticPolicy.GetInlineAccessibleName(
                "", true, "Open dashboard", "https://example.test/dashboard", "Image"));
        Assert.Equal(
            "Image",
            MarkdownImageSemanticPolicy.GetInlineAccessibleName("", true, null, "#", "Image"));
    }

    [Fact]
    public void EmptyAltLinkedImage_WithoutTitle_IsNamedByItsLinkTarget()
    {
        // deno's README opens with [![](crates.io badge)](https://crates.io/crates/deno).
        Assert.Equal(
            "crates.io/crates/deno",
            MarkdownImageSemanticPolicy.GetInlineAccessibleName(
                "", true, null, "https://crates.io/crates/deno", "Image"));
        Assert.Equal(
            "example.test/a b",
            MarkdownImageSemanticPolicy.GetLinkedImageFallbackName(
                null, " https://example.test/a%20b/?tab=readme#top ", "Image"));
        Assert.Equal(
            "example.test",
            MarkdownImageSemanticPolicy.GetLinkedImageFallbackName(null, "https://example.test/", "Image"));
        Assert.Equal(
            "docs/install.md",
            MarkdownImageSemanticPolicy.GetLinkedImageFallbackName(null, "docs/install.md", "Image"));
        Assert.Equal(
            "mailto:team@example.test",
            MarkdownImageSemanticPolicy.GetLinkedImageFallbackName(null, "mailto:team@example.test", "Image"));
        Assert.Equal(
            "Image",
            MarkdownImageSemanticPolicy.GetLinkedImageFallbackName(null, "  ", "Image"));
    }

    [Fact]
    public void AuthoredAltText_IsPreservedExactly()
    {
        Assert.Equal(
            "Build status",
            MarkdownImageSemanticPolicy.GetInlineAccessibleName(
                "Build status", false, null, null, "Image"));
        Assert.Equal(
            "Build status",
            MarkdownImageSemanticPolicy.GetInlineAccessibleName(
                "Build status", true, "Open CI", "https://example.test/ci", "Image"));
        Assert.False(MarkdownImageSemanticPolicy.IsDecorativeBlock("Build status"));
    }
}
