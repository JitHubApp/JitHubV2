using System.Linq;
using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using Xunit;

namespace MarkdownRenderer.Tests;

/// <summary>
/// Verifies that Markdig delivers data: URI image destinations to the renderer
/// intact across the parsing forms that real users (and our sample app) write.
///
/// Critical because raw <c>![alt](data:image/svg+xml;utf8,&lt;svg ...&gt;)</c>
/// markdown contains spaces inside the destination — CommonMark truncates that
/// URL at the first space unless the author percent-encodes, base64-encodes,
/// or wraps the destination in angle brackets. The renderer's data-URI code
/// path can only fix what Markdig actually hands it; if Markdig truncates, no
/// downstream fix is possible.
/// </summary>
public class ImageDataUriParsingTests
{
    private static MarkdownDocument Parse(string md)
    {
        var pipeline = new MarkdownPipelineBuilder().Build();
        return Markdig.Markdown.Parse(md, pipeline);
    }

    private static string? FirstImageUrl(MarkdownDocument doc)
    {
        return doc.Descendants<LinkInline>().FirstOrDefault(l => l.IsImage)?.Url;
    }

    [Fact]
    public void Base64DataUri_ReachesRendererIntact()
    {
        // SVG payload base64-encoded — no spaces, no special characters, the
        // canonical form for inline SVG in markdown.
        const string url = "data:image/svg+xml;base64,PHN2ZyB4bWxucz0naHR0cDovL3d3dy53My5vcmcvMjAwMC9zdmcnIHdpZHRoPSc2NCcgaGVpZ2h0PSc2NCcvPg==";
        var doc = Parse($"![alt]({url})");
        Assert.Equal(url, FirstImageUrl(doc));
    }

    [Fact]
    public void PercentEncodedDataUri_ReachesRendererIntact()
    {
        // Same SVG but percent-encoded so the URL contains no literal spaces
        // or angle brackets.
        const string url = "data:image/svg+xml;utf8,%3Csvg%20xmlns%3D%27http%3A%2F%2Fwww.w3.org%2F2000%2Fsvg%27%20width%3D%2748%27%20height%3D%2748%27%2F%3E";
        var doc = Parse($"![alt]({url})");
        Assert.Equal(url, FirstImageUrl(doc));
    }

    [Fact]
    public void RawSpaceContainingDataUri_IsRejectedByMarkdig()
    {
        // Regression sentinel: this is the form the original Images sample used.
        // CommonMark requires destination URLs to either be wrapped in <...>
        // (which can't contain raw '<') or contain no spaces. Markdig sees the
        // raw '<svg' inside the destination, treats the whole construct as not
        // a valid image link, and yields NO image link at all — the markdown
        // round-trips to literal text. This pins the breaking behavior so we
        // don't accidentally regress the sample back to it.
        const string svg = "<svg xmlns='http://www.w3.org/2000/svg' width='64' height='64'/>";
        var doc = Parse($"![alt](data:image/svg+xml;utf8,{svg})");
        var url = FirstImageUrl(doc);
        Assert.Null(url);
    }

    [Fact]
    public void NoMediaTypeBase64Variant_ReachesRendererIntact()
    {
        // Some authors omit the media type. The downstream loader still has to
        // sniff this and accept it; here we only verify Markdig hands it over.
        const string url = "data:;base64,SGVsbG8sIFdvcmxkIQ==";
        var doc = Parse($"![alt]({url})");
        Assert.Equal(url, FirstImageUrl(doc));
    }

    [Fact]
    public void CharsetUtf8Variant_ReachesRendererIntact()
    {
        // ;charset=utf-8 (RFC-compliant) instead of the ;utf8 shorthand.
        const string url = "data:image/svg+xml;charset=utf-8,%3Csvg%2F%3E";
        var doc = Parse($"![alt]({url})");
        Assert.Equal(url, FirstImageUrl(doc));
    }
}
