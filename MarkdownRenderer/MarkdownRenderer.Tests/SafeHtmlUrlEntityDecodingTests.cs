using MarkdownRenderer.Parsing;
using Xunit;

namespace MarkdownRenderer.Tests;

public sealed class SafeHtmlUrlEntityDecodingTests
{
    [Fact]
    public void ElementLinkDecodesNestedNamedReferenceExactlyOnceAndKeepsSourceSpan()
    {
        const string prefix = "before ";
        const string openingTag = "<a href=\"https://example.test/?q=&amp;amp;v=1\">";
        string source = prefix + openingTag + "label</a> after";
        SafeHtmlDocument document = SafeHtmlParser.Parse(source);
        SafeHtmlElement anchor = Assert.IsType<SafeHtmlElement>(
            Assert.Single(document.Root.Children.OfType<SafeHtmlElement>()));

        Assert.Equal(prefix.Length, anchor.SourceStart);
        Assert.Equal(openingTag.Length, anchor.SourceLength);
        Assert.Equal(openingTag, anchor.RawOpeningTag);
        Assert.True(SafeHtmlParser.TryGetSafeLink(anchor, out string actual));
        Assert.Equal("https://example.test/?q=&amp;v=1", actual);
    }

    [Fact]
    public void TagLinkDecodesNestedNumericReferenceExactlyOnceAndKeepsSourceSpan()
    {
        const string tagSource = "<a href=\"https://example.test/?q=&#38;amp;v=1\">";
        Assert.True(SafeHtmlParser.TryParseSingleTag(tagSource, out SafeHtmlTag tag));

        Assert.Equal(0, tag.SourceStart);
        Assert.Equal(tagSource.Length, tag.SourceLength);
        Assert.True(SafeHtmlParser.TryGetSafeLink(tag, out string actual));
        Assert.Equal("https://example.test/?q=&amp;v=1", actual);
    }

    [Fact]
    public void ElementImageDecodesNestedHexReferenceExactlyOnce()
    {
        const string source = "<img src=\"https://example.test/image?q=&#x26;amp;v=1\" alt=\"sample\">";
        SafeHtmlDocument document = SafeHtmlParser.Parse(source);
        SafeHtmlElement image = Assert.IsType<SafeHtmlElement>(
            Assert.Single(document.Root.Children.OfType<SafeHtmlElement>()));

        Assert.True(SafeHtmlParser.TryGetSafeImageSource(image, out string actual));
        Assert.Equal("https://example.test/image?q=&amp;v=1", actual);
    }

    [Fact]
    public void TagImageDecodesNestedNamedReferenceExactlyOnce()
    {
        const string tagSource = "<img src=\"https://example.test/image?q=&amp;amp;v=1\">";
        Assert.True(SafeHtmlParser.TryParseSingleTag(tagSource, out SafeHtmlTag tag));

        Assert.True(SafeHtmlParser.TryGetSafeImageSource(tag, out string actual));
        Assert.Equal("https://example.test/image?q=&amp;v=1", actual);
    }

    [Fact]
    public void OrdinarySingleEntityInElementLinkIsDecodedOnce()
    {
        const string source = "<a href=\"https://example.test/?q=1&amp;v=2\">label</a>";
        SafeHtmlElement anchor = Assert.IsType<SafeHtmlElement>(
            Assert.Single(SafeHtmlParser.Parse(source).Root.Children.OfType<SafeHtmlElement>()));

        Assert.True(SafeHtmlParser.TryGetSafeLink(anchor, out string actual));
        Assert.Equal("https://example.test/?q=1&v=2", actual);
    }

    [Fact]
    public void DirectImageSourceNormalizationPreservesAlreadyDecodedAttributeValue()
    {
        // Media poster/srcset processing passes the attribute value returned by
        // TryGetAttribute, which has already received HTML's one entity decode.
        const string tagSource = "<video poster=\"https://example.test/poster?q=&amp;amp;v=1\">";
        Assert.True(SafeHtmlParser.TryParseSingleTag(tagSource, out SafeHtmlTag tag));
        Assert.True(tag.TryGetAttribute("poster", out string alreadyDecoded));
        Assert.Equal("https://example.test/poster?q=&amp;v=1", alreadyDecoded);

        Assert.True(SafeHtmlParser.TryNormalizeImageSource(alreadyDecoded, out string actual));
        Assert.Equal(alreadyDecoded, actual);
    }

    [Theory]
    [InlineData(
        "<a href=\"https://example.test/a&amp;amp;b?q=&amp;amp;v=1#frag&amp;amp;tail\">x</a>",
        "https://example.test/a&amp;b?q=&amp;v=1#frag&amp;tail")]
    [InlineData(
        "<a href=\"https://example.test/a&amp;unknown;?q=%26amp%3B#frag%26amp%3B\">x</a>",
        "https://example.test/a&unknown;?q=%26amp%3B#frag%26amp%3B")]
    [InlineData(
        "<a href=\"mailto:dev@example.test?subject=R%26D&amp;amp;body=x%2526\">x</a>",
        "mailto:dev@example.test?subject=R%26D&amp;body=x%2526")]
    public void LinkPreservesLiteralNestedUnknownAndPercentEncodedUrlContent(
        string source,
        string expected)
    {
        SafeHtmlElement anchor = Assert.IsType<SafeHtmlElement>(
            Assert.Single(SafeHtmlParser.Parse(source).Root.Children.OfType<SafeHtmlElement>()));

        Assert.True(SafeHtmlParser.TryGetSafeLink(anchor, out string actual));
        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData(
        "<img src=\"assets/my&amp;amp;logo%20icon.svg?download=%26amp%3B\">",
        "assets/my&amp;logo%20icon.svg?download=%26amp%3B")]
    [InlineData(
        "<img src=\"assets/icon&amp;unknown;.svg\">",
        "assets/icon&unknown;.svg")]
    public void ElementImagePreservesLiteralEntityAndPercentEncodedFilenameContent(
        string source,
        string expected)
    {
        SafeHtmlElement image = Assert.IsType<SafeHtmlElement>(
            Assert.Single(SafeHtmlParser.Parse(source).Root.Children.OfType<SafeHtmlElement>()));

        Assert.True(SafeHtmlParser.TryGetSafeImageSource(image, out string actual));
        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData("<img src=\"java&#x73;cript:alert(1)\">")]
    [InlineData("<img src=\"file&#58;//example.test/private.png\">")]
    [InlineData("<img src=\"data&#58;text/html,%3Cscript%3Ebad%3C/script%3E\">")]
    [InlineData("<img src=\"https://example.test/a&#10;b.png\">")]
    public void ElementImageRejectsEntityDecodedUnsafeSchemesAndControls(string source)
    {
        SafeHtmlElement image = Assert.IsType<SafeHtmlElement>(
            Assert.Single(SafeHtmlParser.Parse(source).Root.Children.OfType<SafeHtmlElement>()));

        Assert.False(SafeHtmlParser.TryGetSafeImageSource(image, out _));
    }

    [Theory]
    [InlineData("<img src=\"java&#x73;cript:alert(1)\">")]
    [InlineData("<img src=\"file&#58;//example.test/private.png\">")]
    [InlineData("<img src=\"data&#58;text/html,%3Cscript%3Ebad%3C/script%3E\">")]
    [InlineData("<img src=\"https://example.test/a&#10;b.png\">")]
    public void TagImageRejectsEntityDecodedUnsafeSchemesAndControls(string source)
    {
        Assert.True(SafeHtmlParser.TryParseSingleTag(source, out SafeHtmlTag tag));

        Assert.False(SafeHtmlParser.TryGetSafeImageSource(tag, out _));
    }

    [Theory]
    [InlineData("<a href=\"java&#x73;cript:alert(1)\">x</a>")]
    [InlineData("<a href=\"https://example.test/a&#10;b\">x</a>")]
    public void ElementLinkStillRejectsOnceDecodedUnsafeSchemeOrControlCharacter(string source)
    {
        SafeHtmlElement anchor = Assert.IsType<SafeHtmlElement>(
            Assert.Single(SafeHtmlParser.Parse(source).Root.Children.OfType<SafeHtmlElement>()));

        Assert.False(SafeHtmlParser.TryGetSafeLink(anchor, out _));
    }

    [Theory]
    [InlineData("<a href=\"java&#x73;cript:alert(1)\">")]
    [InlineData("<a href=\"https://example.test/a&#10;b\">")]
    public void TagLinkStillRejectsOnceDecodedUnsafeSchemeOrControlCharacter(string source)
    {
        Assert.True(SafeHtmlParser.TryParseSingleTag(source, out SafeHtmlTag tag));

        Assert.False(SafeHtmlParser.TryGetSafeLink(tag, out _));
    }

    [Fact]
    public void ElementImageStillRejectsOnceDecodedControlCharacter()
    {
        const string source = "<img src=\"https://example.test/a&#10;b\">";
        SafeHtmlElement image = Assert.IsType<SafeHtmlElement>(
            Assert.Single(SafeHtmlParser.Parse(source).Root.Children.OfType<SafeHtmlElement>()));

        Assert.False(SafeHtmlParser.TryGetSafeImageSource(image, out _));
    }
}
