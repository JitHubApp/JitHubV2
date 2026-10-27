using System;
using System.Text;
using System.Threading;
using MarkdownRenderer.Layout.Boxes;
using Xunit;

namespace MarkdownRenderer.GitHub.Tests;

public sealed class SvgSourceMetadataTests
{
    private const string SvgNamespace = "http://www.w3.org/2000/svg";

    [Theory]
    [InlineData("broken='unterminated")]
    [InlineData("bad='&unknown;'")]
    [InlineData("bad='\u0001'")]
    [InlineData("duplicate='1' duplicate='2'")]
    [InlineData("undeclared:attribute='value'")]
    public void DescendantAttributesAreFullyValidatedWithoutMetadataAttributeEnumeration(string attributes)
    {
        byte[] source = Utf8($"<svg xmlns='{SvgNamespace}' width='48' height='24'><title>early label</title><g {attributes}/></svg>");

        Assert.Equal(default, SvgSourceMetadata.Extract(source, CancellationToken.None));
    }

    [Theory]
    [InlineData(1_999, true)]
    [InlineData(2_000, false)]
    public void CountsAttributesCumulativelyWithoutExceedingTheSourceByteLimit(int elementCount, bool admitted)
    {
        var element = new StringBuilder("<g");
        for (int attribute = 0; attribute < 400; attribute++)
            element.Append(" a").Append(attribute).Append("='0'");
        element.Append("/>");

        var document = new StringBuilder();
        document.Append("<svg xmlns='").Append(SvgNamespace).Append("' width='48' height='24'><title>early label</title>");
        for (int index = 0; index < elementCount; index++)
            document.Append(element);
        document.Append("</svg>");
        byte[] source = Utf8(document.ToString());
        Assert.True(source.Length < 8 * 1024 * 1024, "This fixture must exercise the attribute ceiling, not the source-byte ceiling.");

        SvgSourceMetadata metadata = SvgSourceMetadata.Extract(source, CancellationToken.None);

        if (admitted)
        {
            Assert.Equal("early label", metadata.Title);
            Assert.Equal(48d, metadata.Geometry.IntrinsicWidth);
        }
        else
        {
            Assert.Equal(default, metadata);
        }
    }

    [Fact]
    public void ExtractUsesFirstDirectRootMetadataAndDecodesAndNormalizesText()
    {
        byte[] source = Utf8($"""
            <svg xmlns="{SvgNamespace}" width="890" height="158" viewBox="-2 3 890 158">
              <defs><title>defs title</title><desc>defs description</desc></defs>
              <g><title>group title</title><desc>group description</desc></g>
              <title>  KaTeX
                <tspan>λ</tspan> &amp; &lt; &gt; &quot; &apos; &#x1F600;  </title>
              <title>second title is ignored</title>
              <desc>  First
                description &amp; detail  </desc>
              <desc>second description is ignored</desc>
            </svg>
            """);

        SvgSourceMetadata actual = SvgSourceMetadata.Extract(source, CancellationToken.None);

        Assert.Equal("KaTeX λ & < > \" ' 😀", actual.Title);
        Assert.Equal("First description & detail", actual.Desc);
        Assert.Equal(890d, actual.Geometry.IntrinsicWidth);
        Assert.Equal(158d, actual.Geometry.IntrinsicHeight);
        Assert.Equal(-2d, actual.Geometry.ViewBoxX);
        Assert.Equal(3d, actual.Geometry.ViewBoxY);
        Assert.Equal(890d, actual.Geometry.ViewBoxWidth);
        Assert.Equal(158d, actual.Geometry.ViewBoxHeight);
    }

    [Fact]
    public void ExtractSupportsPrefixedSvgNamespaceAndSelfClosingDefinitions()
    {
        byte[] source = Utf8($"""
            <s:svg xmlns:s="{SvgNamespace}" width="24" height="12">
              <s:defs/><s:g/>
              <s:title>  Badge   label </s:title>
              <s:desc>  Accessible   detail </s:desc>
            </s:svg>
            """);

        SvgSourceMetadata actual = SvgSourceMetadata.Extract(source, CancellationToken.None);

        Assert.Equal("Badge label", actual.Title);
        Assert.Equal("Accessible detail", actual.Desc);
        Assert.Equal(24d, actual.Geometry.IntrinsicWidth);
        Assert.Equal(12d, actual.Geometry.IntrinsicHeight);
    }

    [Fact]
    public void ExtractIgnoresTitlesAndDescriptionsBelowAnyNonRootElement()
    {
        byte[] source = Utf8($"""
            <svg xmlns="{SvgNamespace}" width="20" height="10">
              <g><title>group title</title><desc>group description</desc></g>
              <defs><g><title>nested defs title</title></g></defs>
              <symbol><desc>symbol description</desc></symbol>
            </svg>
            """);

        SvgSourceMetadata actual = SvgSourceMetadata.Extract(source, CancellationToken.None);

        Assert.Null(actual.Title);
        Assert.Null(actual.Desc);
        Assert.Equal(20d, actual.Geometry.IntrinsicWidth);
        Assert.Equal(10d, actual.Geometry.IntrinsicHeight);
    }

    [Fact]
    public void ExtractTreatsFirstSelfClosingMetadataElementAsTheFirstOccurrence()
    {
        byte[] source = Utf8($"""
            <svg xmlns="{SvgNamespace}" width="20" height="10">
              <title/><title>later title</title>
              <desc/><desc>later description</desc>
            </svg>
            """);

        SvgSourceMetadata actual = SvgSourceMetadata.Extract(source, CancellationToken.None);

        Assert.Null(actual.Title);
        Assert.Null(actual.Desc);
    }

    [Fact]
    public void ExtractPreservesExistingExplicitUnitAndPercentViewBoxGeometrySemantics()
    {
        AssertGeometryMatchesLegacy(
            Utf8($"<svg xmlns='{SvgNamespace}' width='12.5px' height='3.5em' viewBox='-1.5, 2e1, 24.5, 7.5'/>"),
            expectedWidth: 12.5,
            expectedHeight: 3.5,
            expectedViewBoxX: -1.5,
            expectedViewBoxY: 20,
            expectedViewBoxWidth: 24.5,
            expectedViewBoxHeight: 7.5);

        AssertGeometryMatchesLegacy(
            Utf8($"<svg xmlns='{SvgNamespace}' width='50%' height='25%' viewBox='4 5 40 20'/>"),
            expectedWidth: 40,
            expectedHeight: 20,
            expectedViewBoxX: 4,
            expectedViewBoxY: 5,
            expectedViewBoxWidth: 40,
            expectedViewBoxHeight: 20);

        AssertGeometryMatchesLegacy(
            Utf8($"<svg xmlns='{SvgNamespace}' width='25.4mm' height='7.5pt'/>"),
            expectedWidth: 25.4,
            expectedHeight: 7.5,
            expectedViewBoxX: 0,
            expectedViewBoxY: 0,
            expectedViewBoxWidth: 0,
            expectedViewBoxHeight: 0);

        AssertGeometryMatchesLegacy(
            Utf8($"<svg xmlns='{SvgNamespace}' width='16' height='0' viewBox='1 2 8 6'/>"),
            expectedWidth: 8,
            expectedHeight: 6,
            expectedViewBoxX: 1,
            expectedViewBoxY: 2,
            expectedViewBoxWidth: 8,
            expectedViewBoxHeight: 6);
    }

    [Fact]
    public void ExtractSupportsNamespaceLessSvgForCompatibility()
    {
        SvgSourceMetadata actual = SvgSourceMetadata.Extract(
            Utf8("<svg width='18' height='9'><title>plain SVG</title></svg>"),
            CancellationToken.None);

        Assert.Equal("plain SVG", actual.Title);
        Assert.Equal(18d, actual.Geometry.IntrinsicWidth);
        Assert.Equal(9d, actual.Geometry.IntrinsicHeight);
    }

    [Fact]
    public void ExtractSupportsUtf8AndUtf16XmlPreambles()
    {
        string utf8Xml = $"<?xml version='1.0' encoding='utf-8'?><svg xmlns='{SvgNamespace}' width='21' height='11'><title>Unicode stream</title></svg>";
        string utf16Xml = $"<?xml version='1.0' encoding='utf-16'?><svg xmlns='{SvgNamespace}' width='21' height='11'><title>Unicode stream</title></svg>";

        SvgSourceMetadata utf8 = SvgSourceMetadata.Extract(
            WithPreamble(Encoding.UTF8.GetPreamble(), Encoding.UTF8.GetBytes(utf8Xml)),
            CancellationToken.None);
        SvgSourceMetadata utf16 = SvgSourceMetadata.Extract(
            WithPreamble(Encoding.Unicode.GetPreamble(), Encoding.Unicode.GetBytes(utf16Xml)),
            CancellationToken.None);

        Assert.Equal("Unicode stream", utf8.Title);
        Assert.Equal("Unicode stream", utf16.Title);
        Assert.Equal(21d, utf8.Geometry.IntrinsicWidth);
        Assert.Equal(11d, utf8.Geometry.IntrinsicHeight);
        Assert.Equal(21d, utf16.Geometry.IntrinsicWidth);
        Assert.Equal(11d, utf16.Geometry.IntrinsicHeight);
    }

    [Fact]
    public void ExternalSvg11DoctypeIsInertWhenNoResolverIsConfigured()
    {
        byte[] source = Utf8($"""
            <!DOCTYPE svg SYSTEM "http://127.0.0.1:9/should-not-be-fetched.dtd">
            <svg xmlns="{SvgNamespace}" width="30" height="15"><title>safe external declaration</title></svg>
            """);

        SvgSourceMetadata actual = SvgSourceMetadata.Extract(source, CancellationToken.None);

        Assert.Equal("safe external declaration", actual.Title);
        Assert.Equal(30d, actual.Geometry.IntrinsicWidth);
    }

    [Fact]
    public void InternalDtdAndCustomEntityExpansionFailClosed()
    {
        byte[] source = Utf8($"""
            <!DOCTYPE svg [<!ENTITY injected "must not expand">]>
            <svg xmlns="{SvgNamespace}" width="30" height="15"><title>&injected;</title></svg>
            """);

        Assert.Equal(default(SvgSourceMetadata), SvgSourceMetadata.Extract(source, CancellationToken.None));
    }

    [Theory]
    [InlineData("<svg xmlns='http://www.w3.org/2000/svg'><title>missing close")]
    [InlineData("<not-svg xmlns='http://www.w3.org/2000/svg'><title>wrong root</title></not-svg>")]
    [InlineData("not XML at all")]
    public void MalformedOrNonSvgXmlReturnsEmptyMetadata(string text)
    {
        Assert.Equal(
            default(SvgSourceMetadata),
            SvgSourceMetadata.Extract(Utf8(text), CancellationToken.None));
    }

    [Fact]
    public void ExcessiveXmlDepthFailsClosed()
    {
        var source = new StringBuilder($"<svg xmlns='{SvgNamespace}' width='20' height='10'>");
        const int nestedDepth = 140;
        for (int index = 0; index < nestedDepth; index++)
            source.Append("<g>");
        source.Append("<title>too deep</title>");
        for (int index = 0; index < nestedDepth; index++)
            source.Append("</g>");
        source.Append("</svg>");

        Assert.Equal(
            default(SvgSourceMetadata),
            SvgSourceMetadata.Extract(Utf8(source.ToString()), CancellationToken.None));
    }

    [Fact]
    public void OversizedSourceFailsClosedBeforeXmlParsing()
    {
        byte[] overBudget = new byte[(8 * 1024 * 1024) + 1];

        Assert.Equal(
            default(SvgSourceMetadata),
            SvgSourceMetadata.Extract(overBudget, CancellationToken.None));
    }

    [Fact]
    public void LargeIgnoredNestedBodyDoesNotReplaceRootMetadata()
    {
        var source = new StringBuilder($"<svg xmlns='{SvgNamespace}' width='40' height='20'><g><title>hidden</title><text>");
        source.Append('x', 512 * 1024);
        source.Append($"</text></g><title>visible root title</title></svg>");

        SvgSourceMetadata actual = SvgSourceMetadata.Extract(Utf8(source.ToString()), CancellationToken.None);

        Assert.Equal("visible root title", actual.Title);
        Assert.Equal(40d, actual.Geometry.IntrinsicWidth);
        Assert.Equal(20d, actual.Geometry.IntrinsicHeight);
    }

    [Fact]
    public void MetadataTextIsBoundedPerField()
    {
        string description = new('d', 5_000);
        SvgSourceMetadata actual = SvgSourceMetadata.Extract(
            Utf8($"<svg xmlns='{SvgNamespace}'><desc>{description}</desc></svg>"),
            CancellationToken.None);

        Assert.NotNull(actual.Desc);
        Assert.Equal(4_096, actual.Desc!.Length);
        Assert.Equal(description[..4_096], actual.Desc);
    }

    [Fact]
    public void MetadataTruncationNeverSplitsAnAstralScalarOrLeavesItsSeparator()
    {
        string scalar = char.ConvertFromUtf32(0x1F600);
        string completePrefix = new('x', 4_095);
        string separatorPrefix = new('x', 4_094);

        SvgSourceMetadata splitPair = SvgSourceMetadata.Extract(
            Utf8($"<svg xmlns='{SvgNamespace}'><desc>{completePrefix}{scalar}</desc></svg>"),
            CancellationToken.None);
        SvgSourceMetadata separatorAtCap = SvgSourceMetadata.Extract(
            Utf8($"<svg xmlns='{SvgNamespace}'><desc>{separatorPrefix} {scalar}</desc></svg>"),
            CancellationToken.None);

        Assert.Equal(completePrefix, splitPair.Desc);
        Assert.Equal(separatorPrefix, separatorAtCap.Desc);
        Assert.All(splitPair.Desc!, character => Assert.False(char.IsSurrogate(character)));
        Assert.All(separatorAtCap.Desc!, character => Assert.False(char.IsSurrogate(character)));
    }

    [Fact]
    public void CancellationIsPropagated()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.ThrowsAny<OperationCanceledException>(() =>
            SvgSourceMetadata.Extract(Utf8($"<svg xmlns='{SvgNamespace}'/>"), cancellation.Token));
    }

    private static byte[] Utf8(string text) => Encoding.UTF8.GetBytes(text);

    private static byte[] WithPreamble(byte[] preamble, byte[] content)
    {
        var result = new byte[preamble.Length + content.Length];
        Buffer.BlockCopy(preamble, 0, result, 0, preamble.Length);
        Buffer.BlockCopy(content, 0, result, preamble.Length, content.Length);
        return result;
    }

    private static void AssertGeometryMatchesLegacy(
        byte[] source,
        double expectedWidth,
        double expectedHeight,
        double expectedViewBoxX,
        double expectedViewBoxY,
        double expectedViewBoxWidth,
        double expectedViewBoxHeight)
    {
        SvgSourceMetadata actual = SvgSourceMetadata.Extract(source, CancellationToken.None);
        SvgIntrinsics.SvgRootGeometry legacy = SvgIntrinsics.TryExtractRootGeometry(source);

        Assert.Equal(expectedWidth, actual.Geometry.IntrinsicWidth);
        Assert.Equal(expectedHeight, actual.Geometry.IntrinsicHeight);
        Assert.Equal(expectedViewBoxX, actual.Geometry.ViewBoxX);
        Assert.Equal(expectedViewBoxY, actual.Geometry.ViewBoxY);
        Assert.Equal(expectedViewBoxWidth, actual.Geometry.ViewBoxWidth);
        Assert.Equal(expectedViewBoxHeight, actual.Geometry.ViewBoxHeight);
        Assert.Equal(legacy.IntrinsicWidth, actual.Geometry.IntrinsicWidth);
        Assert.Equal(legacy.IntrinsicHeight, actual.Geometry.IntrinsicHeight);
        Assert.Equal(legacy.ViewBoxX, actual.Geometry.ViewBoxX);
        Assert.Equal(legacy.ViewBoxY, actual.Geometry.ViewBoxY);
        Assert.Equal(legacy.ViewBoxWidth, actual.Geometry.ViewBoxWidth);
        Assert.Equal(legacy.ViewBoxHeight, actual.Geometry.ViewBoxHeight);
    }
}
