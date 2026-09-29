using MarkdownRenderer.Images;
using Xunit;

namespace MarkdownRenderer.Tests;

public sealed class MarkdownBuiltInImageSourcePolicyTests
{
    [Fact]
    public void RelativeSourcesResolveAgainstTheDocumentBase()
    {
        Assert.True(MarkdownBuiltInImageSourcePolicy.TryResolve(
            "assets/icon.svg",
            new Uri("file:///C:/docs/first/readme.md"),
            out MarkdownBuiltInImageSource first));
        Assert.True(MarkdownBuiltInImageSourcePolicy.TryResolve(
            "assets/icon.svg",
            new Uri("file:///C:/docs/second/readme.md"),
            out MarkdownBuiltInImageSource second));

        Assert.Equal("file:///C:/docs/first/assets/icon.svg", first.CacheKey);
        Assert.Equal("file:///C:/docs/second/assets/icon.svg", second.CacheKey);
        Assert.NotEqual(first.CacheKey, second.CacheKey);
        Assert.True(first.CanLoadWithoutResolver);
        Assert.True(second.CanLoadWithoutResolver);
    }

    [Theory]
    [InlineData("ms-appx:///docs/readme.md", "images/logo.svg", "ms-appx:///docs/images/logo.svg")]
    [InlineData("ms-appdata:///local/docs/readme.md", "images/logo.svg", "ms-appdata:///local/docs/images/logo.svg")]
    public void WinRtLocalSchemesRemainBuiltInSources(
        string baseUri,
        string source,
        string expected)
    {
        Assert.True(MarkdownBuiltInImageSourcePolicy.TryResolve(
            source,
            new Uri(baseUri),
            out MarkdownBuiltInImageSource resolved));

        Assert.Equal(expected, resolved.CacheKey);
        Assert.Equal(MarkdownBuiltInImageSourceKind.Local, resolved.Kind);
        Assert.True(resolved.CanLoadWithoutResolver);
    }

    [Theory]
    [InlineData("https://images.example.test/a.png", "RemoteHttps")]
    [InlineData("http://images.example.test/a.png", "InsecureHttp")]
    [InlineData("ftp://images.example.test/a.png", "Unsupported")]
    [InlineData("file://server/share/a.png", "Unsupported")]
    public void NetworkAndUnsupportedSchemesNeverGainBuiltInLoadPermission(
        string source,
        string expectedKind)
    {
        Assert.True(MarkdownBuiltInImageSourcePolicy.TryResolve(
            source,
            baseUri: null,
            out MarkdownBuiltInImageSource resolved));

        Assert.Equal(expectedKind, resolved.Kind.ToString());
        Assert.False(resolved.CanLoadWithoutResolver);
    }

    [Fact]
    public void RelativeHttpsSourceIsResolvedForIdentityButRemainsBlocked()
    {
        Assert.True(MarkdownBuiltInImageSourcePolicy.TryResolve(
            "images/logo.png",
            new Uri("https://docs.example.test/guides/readme.md"),
            out MarkdownBuiltInImageSource resolved));

        Assert.Equal("https://docs.example.test/guides/images/logo.png", resolved.CacheKey);
        Assert.Equal(MarkdownBuiltInImageSourceKind.RemoteHttps, resolved.Kind);
        Assert.False(resolved.CanLoadWithoutResolver);
    }

    [Fact]
    public void SelfContainedDataUriRemainsAResolverIndependentSource()
    {
        const string source = "data:image/png;base64,iVBORw0KGgo=";

        Assert.True(MarkdownBuiltInImageSourcePolicy.TryResolve(
            source,
            baseUri: null,
            out MarkdownBuiltInImageSource resolved));

        Assert.Equal(source, resolved.CacheKey);
        Assert.Equal(MarkdownBuiltInImageSourceKind.Data, resolved.Kind);
        Assert.True(resolved.CanLoadWithoutResolver);
    }

    [Fact]
    public void RelativeSourceWithoutAnAbsoluteBaseCannotBeResolved()
    {
        Assert.False(MarkdownBuiltInImageSourcePolicy.TryResolve(
            "images/logo.png",
            baseUri: null,
            out _));
        Assert.False(MarkdownBuiltInImageSourcePolicy.TryResolve(
            "images/logo.png",
            new Uri("relative/path/", UriKind.Relative),
            out _));
    }
}
