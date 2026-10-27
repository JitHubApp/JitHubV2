using MarkdownRenderer.Sample;
using Xunit;

namespace MarkdownRenderer.Tests;

public sealed class SampleLinkRoutingTests
{
    [Theory]
    [InlineData("https://example.invalid/touch-link")]
    [InlineData("https://example.invalid/mermaid-node")]
    [InlineData("https://example.invalid/suppression/unique-fixture-id")]
    [InlineData("https://example.invalid/touch-link?fixture=unique#selection")]
    [InlineData("HTTP://EXAMPLE.INVALID/other-fixture")]
    public void ReservedFixtureLinksCannotOpenAShellDialog(string source)
        => Assert.True(SampleLinkRouting.IsInertFixtureUri(new Uri(source)));

    [Theory]
    [InlineData("https://github.com/")]
    [InlineData("https://example.invalid.evil.test/touch-link")]
    [InlineData("https://example.invalid@github.com/touch-link")]
    [InlineData("file://example.invalid/touch-link")]
    [InlineData("mailto:test@example.invalid")]
    public void OrdinaryOrLookalikeLinksDoNotUseTheFixtureRoute(string source)
        => Assert.False(SampleLinkRouting.IsInertFixtureUri(new Uri(source)));

    [Fact]
    public void MissingOrRelativeUriIsNotAShellFixture()
    {
        Assert.False(SampleLinkRouting.IsInertFixtureUri(null));
        Assert.False(SampleLinkRouting.IsInertFixtureUri(new Uri("touch-link", UriKind.Relative)));
    }
}
