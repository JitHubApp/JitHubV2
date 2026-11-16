using System;
using JitHub.Markdown.Images.Internal;
using Xunit;

namespace JitHub.Markdown.Tests.Images.Internal;

/// <summary>Which unowned Markdown image sources the shared HTTPS path accepts.</summary>
public sealed class MarkdownImageSourcePolicyTests
{
    [Theory]
    [InlineData("http://example.test/image.png", "SharedHttps")]
    [InlineData("https://example.test/image.png", "SharedHttps")]
    [InlineData("images/local.png", "NotHandled")]
    [InlineData("ms-appx:///Assets/image.png", "NotHandled")]
    public void MarkdownImageSourcePolicy_UsesHttpsOnly(
        string source,
        string expectedName)
    {
        MarkdownImageSourceDisposition expected = Enum.Parse<MarkdownImageSourceDisposition>(expectedName);
        MarkdownImageSourceDisposition actual =
            MarkdownImageSourcePolicy.ClassifyUnownedSource(source, out Uri? absoluteUri);

        Assert.Equal(expected, actual);
        Assert.Equal(Uri.TryCreate(source, UriKind.Absolute, out _), absoluteUri is not null);
        if (source.StartsWith("http://", StringComparison.Ordinal))
        {
            Assert.Equal(Uri.UriSchemeHttps, absoluteUri!.Scheme);
            Assert.Equal("https://example.test/image.png", absoluteUri.AbsoluteUri);
        }
    }
}
