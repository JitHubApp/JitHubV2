using System.Text;
using MarkdownRenderer.Layout.Boxes;
using Xunit;

namespace MarkdownRenderer.Tests;

public class SvgTitleExtractorTests
{
    [Fact]
    public void Extract_RetrievesTitle()
    {
        var bytes = Encoding.UTF8.GetBytes("<svg><title>Github logo</title><path/></svg>");
        var meta = SvgTitleExtractor.Extract(bytes);
        Assert.Equal("Github logo", meta.Title);
        Assert.Null(meta.Desc);
    }

    [Fact]
    public void Extract_RetrievesTitleAndDesc()
    {
        var bytes = Encoding.UTF8.GetBytes("<svg><title>Logo</title><desc>An octocat</desc></svg>");
        var meta = SvgTitleExtractor.Extract(bytes);
        Assert.Equal("Logo", meta.Title);
        Assert.Equal("An octocat", meta.Desc);
    }

    [Fact]
    public void Extract_CollapsesWhitespace()
    {
        var bytes = Encoding.UTF8.GetBytes("<svg><title>\n   Hello\n   world  \n</title></svg>");
        var meta = SvgTitleExtractor.Extract(bytes);
        Assert.Equal("Hello world", meta.Title);
    }

    [Fact]
    public void Extract_ResolvesXmlEntities()
    {
        var bytes = Encoding.UTF8.GetBytes("<svg><title>A &amp; B &lt;tag&gt;</title></svg>");
        var meta = SvgTitleExtractor.Extract(bytes);
        Assert.Equal("A & B <tag>", meta.Title);
    }

    [Fact]
    public void Extract_NoMetadata_ReturnsNullFields()
    {
        var bytes = Encoding.UTF8.GetBytes("<svg><path/></svg>");
        var meta = SvgTitleExtractor.Extract(bytes);
        Assert.Null(meta.Title);
        Assert.Null(meta.Desc);
    }

    [Fact]
    public void Extract_HandlesAttributesOnTitle()
    {
        var bytes = Encoding.UTF8.GetBytes("<svg><title id=\"t1\" lang=\"en\">Accessible name</title></svg>");
        var meta = SvgTitleExtractor.Extract(bytes);
        Assert.Equal("Accessible name", meta.Title);
    }

    [Fact]
    public void Extract_EmptyBytes_ReturnsDefault()
    {
        var meta = SvgTitleExtractor.Extract(System.Array.Empty<byte>());
        Assert.Null(meta.Title);
        Assert.Null(meta.Desc);
    }

    [Fact]
    public void Extract_EmptyTitleElement_ReturnsNull()
    {
        var bytes = Encoding.UTF8.GetBytes("<svg><title>   </title></svg>");
        var meta = SvgTitleExtractor.Extract(bytes);
        Assert.Null(meta.Title);
    }
}
