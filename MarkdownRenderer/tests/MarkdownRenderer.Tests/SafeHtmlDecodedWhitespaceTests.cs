using MarkdownRenderer.Parsing;
using Xunit;

namespace MarkdownRenderer.Tests;

public sealed class SafeHtmlDecodedWhitespaceTests
{
    [Theory]
    [InlineData(null, "")]
    [InlineData("", "")]
    [InlineData(" \t\r\n \t", "")]
    public void DecodedWhitespaceCollapsesNullAndEmptyInput(string? input, string expected)
    {
        Assert.Equal(expected, SafeHtmlParser.CollapseDecodedWhitespace(input));
    }

    [Fact]
    public void DecodedWhitespaceDoesNotDecodeLiteralEntityLikeTextAgain()
    {
        const string decodedText = "Keep &amp; and &lt;script&gt; literal";

        Assert.Equal(decodedText, SafeHtmlParser.CollapseDecodedWhitespace(decodedText));
    }

    [Theory]
    [InlineData("&amp;", "&")]
    [InlineData("&amp;amp;", "&amp;")]
    [InlineData("&amp;lt;script&amp;gt;", "&lt;script&gt;")]
    [InlineData("&#38;lt;script&#38;gt;", "&lt;script&gt;")]
    public void RawWhitespaceEntryDecodesEntitiesExactlyOnce(string rawText, string expected)
    {
        Assert.Equal(expected, SafeHtmlParser.CollapseWhitespace(rawText));
    }

    [Fact]
    public void DecodedWhitespacePreservesUnicodeCombiningMarksAndEmojiSequences()
    {
        const string decodedText = "  Cafe\u0301\t\t👩‍💻  and  🧑🏽‍🚀  ";
        const string expected = "Cafe\u0301 👩‍💻 and 🧑🏽‍🚀";

        Assert.Equal(expected, SafeHtmlParser.CollapseDecodedWhitespace(decodedText));
    }

    [Fact]
    public void DecodedWhitespacePreservesNonBreakingSpacesAndNormalizesTheirBoundaries()
    {
        const string decodedText = "  alpha \t\u00A0  beta\u00A0  ";
        const string expected = "alpha \u00A0 beta\u00A0";

        Assert.Equal(expected, SafeHtmlParser.CollapseDecodedWhitespace(decodedText));
    }

    [Fact]
    public void DecodedWhitespaceKeepsLeadingAndTrailingNonBreakingSpaces()
    {
        const string decodedText = "\u00A0  alpha  \u00A0";
        const string expected = "\u00A0 alpha \u00A0";

        Assert.Equal(expected, SafeHtmlParser.CollapseDecodedWhitespace(decodedText));
    }

    [Fact]
    public void DecodedWhitespaceCollapsesRepeatedOrdinaryWhitespaceAndTrimsEdges()
    {
        const string decodedText = " \t alpha\r\n\t beta  \n gamma \t ";

        Assert.Equal("alpha beta gamma", SafeHtmlParser.CollapseDecodedWhitespace(decodedText));
    }
}
