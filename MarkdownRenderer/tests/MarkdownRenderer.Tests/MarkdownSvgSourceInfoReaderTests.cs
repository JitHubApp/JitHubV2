using System.Text;
using System.Xml;
using MarkdownRenderer.Images;
using MarkdownRenderer.Layout.Boxes;
using Xunit;

namespace MarkdownRenderer.Tests;

public sealed class MarkdownSvgSourceInfoReaderTests
{
    private const string Svg =
        "<svg xmlns='http://www.w3.org/2000/svg' width='96' height='32' viewBox='0 0 96 32'>" +
        "<title>Build <![CDATA[status]]></title><desc>Passing</desc>" +
        "<g><title>Nested title is ignored</title></g></svg>";

    [Fact]
    public void ReaderFedFromAnXmlPass_MatchesTheViewsOwnExtraction()
    {
        byte[] bytes = Encoding.UTF8.GetBytes(Svg);
        SvgSourceMetadata expected = SvgSourceMetadata.Extract(bytes, CancellationToken.None);

        MarkdownSvgSourceInfo info = Read(bytes);

        Assert.Equal("Build status", info.Title);
        Assert.Equal("Passing", info.Description);
        Assert.Equal(96d, info.IntrinsicWidth);
        Assert.Equal(32d, info.IntrinsicHeight);
        Assert.Equal(expected, info.Metadata);
    }

    [Fact]
    public void ReaderRejectsANonSvgRoot()
    {
        var reader = new MarkdownSvgSourceInfoReader();

        Assert.False(reader.ObserveElement(0, "html", namespaceUri: null, isEmptyElement: false));
        MarkdownSvgSourceInfo info = reader.GetResult();
        Assert.Null(info.Title);
        Assert.Equal(0d, info.IntrinsicWidth);
    }

    [Fact]
    public void AdmittedSourceInfoIsUsedOnlyForTheExactAdmittedBytes()
    {
        byte[] bytes = Encoding.UTF8.GetBytes(Svg);
        MarkdownSvgSourceInfo info = Read(bytes);

        MarkdownSvgSourcePreparation preparation = MarkdownSvgSourcePreparation.Admit(bytes, info);

        Assert.True(preparation.TryGetPreparedMetadata(bytes, out SvgSourceMetadata carried));
        Assert.Equal(info.Metadata, carried);
        Assert.False(preparation.TryGetPreparedMetadata((byte[])bytes.Clone(), out _));
    }

    private static MarkdownSvgSourceInfo Read(byte[] bytes)
    {
        var info = new MarkdownSvgSourceInfoReader();
        using var stream = new MemoryStream(bytes);
        using var xml = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit });
        while (xml.Read())
        {
            switch (xml.NodeType)
            {
                case XmlNodeType.Element:
                    int depth = xml.Depth;
                    Assert.True(info.ObserveElement(depth, xml.LocalName, xml.NamespaceURI, xml.IsEmptyElement));
                    if (xml.MoveToFirstAttribute())
                    {
                        do
                        {
                            info.ObserveAttribute(depth, xml.LocalName,
                                xml.NamespaceURI.Length == 0 ? null : xml.NamespaceURI, xml.Value);
                        }
                        while (xml.MoveToNextAttribute());
                        xml.MoveToElement();
                    }

                    break;
                case XmlNodeType.Text or XmlNodeType.CDATA or XmlNodeType.Whitespace or XmlNodeType.SignificantWhitespace:
                    info.ObserveText(xml.Depth, xml.Value.AsSpan());
                    break;
                case XmlNodeType.EndElement:
                    info.ObserveEndElement(xml.Depth);
                    break;
            }
        }

        return info.GetResult();
    }
}
