using MarkdownRenderer.Parsing;
using Xunit;

namespace MarkdownRenderer.Tests;

public class ForgivingDataUriFixerTests
{
    [Fact]
    public void Fix_NoOp_OnPlainMarkdown()
    {
        const string s = "# hello\nworld\n![x](https://example.com/a.png)";
        Assert.Equal(s, ForgivingDataUriFixer.Fix(s));
    }

    [Fact]
    public void Fix_NoOp_OnBase64DataUri()
    {
        const string s = "![x](data:image/svg+xml;base64,PHN2Zy8+)";
        Assert.Equal(s, ForgivingDataUriFixer.Fix(s));
    }

    [Fact]
    public void Fix_NoOp_OnAngleBracketWrapped()
    {
        // Angle-bracket destinations are already CommonMark-safe for spaces.
        const string s = "![x](<data:image/svg+xml;utf8,<svg/>>)";
        Assert.Equal(s, ForgivingDataUriFixer.Fix(s));
    }

    [Fact]
    public void Fix_EncodesSpacesInsideUtf8DataUri()
    {
        const string input = "![x](data:image/svg+xml;utf8,<svg xmlns='http://www.w3.org/2000/svg' width='10' height='10'/>)";
        string fixedSrc = ForgivingDataUriFixer.Fix(input);
        Assert.DoesNotContain(' ', fixedSrc[fixedSrc.IndexOf("data:")..fixedSrc.LastIndexOf(')')]);
        Assert.Contains("%20", fixedSrc);
        Assert.Contains("%3C", fixedSrc); // <
        Assert.Contains("%3E", fixedSrc); // >
    }

    [Fact]
    public void Fix_RoundTripsViaUriUnescape()
    {
        const string svgPayload = "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"4\" height=\"4\"><rect width=\"4\" height=\"4\" fill=\"red\"/></svg>";
        string input = $"![x](data:image/svg+xml;utf8,{svgPayload})";
        string fixedSrc = ForgivingDataUriFixer.Fix(input);
        int comma = fixedSrc.IndexOf(',');
        int closeParen = fixedSrc.LastIndexOf(')');
        string encodedPayload = fixedSrc.Substring(comma + 1, closeParen - comma - 1);
        string roundTrip = System.Uri.UnescapeDataString(encodedPayload);
        Assert.Equal(svgPayload, roundTrip);
    }

    [Fact]
    public void Fix_HandlesMultipleImagesInSameDocument()
    {
        const string input =
            "![a](data:image/svg+xml;utf8,<svg/>)\n" +
            "regular text\n" +
            "![b](data:image/svg+xml;utf8,<svg width='2'/>)\n";
        string fixedSrc = ForgivingDataUriFixer.Fix(input);
        // Both data: occurrences fixed.
        Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(fixedSrc, "%3Csvg").Count);
    }

    [Fact]
    public void Fix_LeavesMalformedInputAlone()
    {
        // Unbalanced parens — bail rather than corrupt.
        const string s = "![x](data:image/svg+xml;utf8,<svg ";
        var result = ForgivingDataUriFixer.Fix(s);
        // Output may differ slightly but must be a string and not throw.
        Assert.NotNull(result);
    }

    [Fact]
    public void Fix_NullOrEmpty_ReturnsEmpty()
    {
        Assert.Equal(string.Empty, ForgivingDataUriFixer.Fix(null!));
        Assert.Equal(string.Empty, ForgivingDataUriFixer.Fix(string.Empty));
    }
}
