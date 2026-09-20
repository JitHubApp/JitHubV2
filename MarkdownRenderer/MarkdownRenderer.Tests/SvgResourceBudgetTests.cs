using System;
using System.Buffers.Binary;
using System.Text;
using System.Threading;
using MarkdownRenderer.Layout.Boxes;
using Xunit;

namespace MarkdownRenderer.Tests;

public sealed class SvgResourceBudgetTests
{
    [Fact]
    public void Validate_AcceptsSmallSelfContainedSvg()
    {
        byte[] bytes = Bytes("<svg xmlns='http://www.w3.org/2000/svg' width='32' height='32'><path d='M0 0 L32 32'/><text x='2' y='12'>safe</text></svg>");

        SvgResourceBudgetResult result = SvgResourceBudget.Validate(bytes, CancellationToken.None);

        Assert.True(result.Accepted, result.Reason);
    }

    [Fact]
    public void Validate_RejectsOversizedInputBeforeParsing()
    {
        byte[] bytes = new byte[SvgResourceBudget.MaxInputBytes + 1];

        SvgResourceBudgetResult result = SvgResourceBudget.Validate(bytes, CancellationToken.None);

        Assert.False(result.Accepted);
        Assert.Equal("input-bytes", result.Reason);
    }

    [Fact]
    public void Validate_RejectsExcessiveNesting()
    {
        string svg = "<svg>" + new string(' ', 1) + string.Concat(
            System.Linq.Enumerable.Repeat("<g>", SvgResourceBudget.MaxDepth + 2)) +
            string.Concat(System.Linq.Enumerable.Repeat("</g>", SvgResourceBudget.MaxDepth + 2)) +
            "</svg>";

        SvgResourceBudgetResult result = SvgResourceBudget.Validate(Bytes(svg), CancellationToken.None);

        Assert.False(result.Accepted);
        Assert.Equal("element-depth", result.Reason);
    }

    [Fact]
    public void Validate_RejectsPathAndTextExhaustion()
    {
        string path = new('1', SvgResourceBudget.MaxPathCharacters + 1);
        SvgResourceBudgetResult pathResult = SvgResourceBudget.Validate(
            Bytes($"<svg><path d='{path}'/></svg>"),
            CancellationToken.None);
        string text = new('a', SvgResourceBudget.MaxTextCharacters + 1);
        SvgResourceBudgetResult textResult = SvgResourceBudget.Validate(
            Bytes($"<svg><text>{text}</text></svg>"),
            CancellationToken.None);

        Assert.False(pathResult.Accepted);
        Assert.Equal("path-complexity", pathResult.Reason);
        Assert.False(textResult.Accepted);
        Assert.Equal("text-length", textResult.Reason);
    }

    [Theory]
    [InlineData("<svg><text font-size='999999'>large</text></svg>", "font-size")]
    [InlineData("<!DOCTYPE svg [<!ENTITY x 'boom'>]><svg><text>&x;</text></svg>", "dtd-internal-subset")]
    [InlineData("<html><body>not svg</body></html>", "missing-root")]
    [InlineData("<svg><filter><feImage href='https://example.test/image.png'/></filter></svg>", "external-image-reference")]
    public void Validate_RejectsHostileXml(string svg, string expectedReason)
    {
        SvgResourceBudgetResult result = SvgResourceBudget.Validate(Bytes(svg), CancellationToken.None);

        Assert.False(result.Accepted);
        Assert.Equal(expectedReason, result.Reason);
    }

    [Fact]
    public void Validate_AcceptsSupportedGaussianBlur()
    {
        SvgResourceBudgetResult result = SvgResourceBudget.Validate(
            Bytes("<svg><filter><feGaussianBlur stdDeviation='2'/></filter></svg>"),
            CancellationToken.None);

        Assert.True(result.Accepted, result.Reason);
    }

    [Fact]
    public void Validate_AcceptsInertExternalSvgDoctypeWithoutResolvingIt()
    {
        byte[] bytes = Bytes(
            "<!DOCTYPE svg PUBLIC '-//W3C//DTD SVG 1.1//EN' " +
            "'http://www.w3.org/Graphics/SVG/1.1/DTD/svg11.dtd'>" +
            "<svg xmlns='http://www.w3.org/2000/svg'><rect width='1' height='1'/></svg>");

        SvgResourceBudgetResult result = SvgResourceBudget.Validate(bytes, CancellationToken.None);

        Assert.True(result.Accepted, result.Reason);
    }

    [Fact]
    public void Validate_AcceptsCssNamespaceIdentifierWithoutTreatingItAsAResource()
    {
        byte[] bytes = Bytes(
            "<svg xmlns='http://www.w3.org/2000/svg'><style>" +
            "@namespace svg url(http://www.w3.org/2000/svg);" +
            "svg|rect { fill: green; }</style><rect width='1' height='1'/></svg>");

        SvgResourceBudgetResult result = SvgResourceBudget.Validate(bytes, CancellationToken.None);

        Assert.True(result.Accepted, result.Reason);
    }

    [Fact]
    public void Validate_CssNamespaceDoesNotHideAFollowingExternalResource()
    {
        byte[] bytes = Bytes(
            "<svg xmlns='http://www.w3.org/2000/svg'><style>" +
            "@namespace svg url(http://www.w3.org/2000/svg);" +
            "rect { fill: url(https://example.test/paint.svg); }</style>" +
            "<rect width='1' height='1'/></svg>");

        SvgResourceBudgetResult result = SvgResourceBudget.Validate(bytes, CancellationToken.None);

        Assert.False(result.Accepted);
        Assert.Equal("external-resource", result.Reason);
    }

    [Fact]
    public void Validate_AcceptsBoundedStandardFilterPipeline()
    {
        SvgResourceBudgetResult result = SvgResourceBudget.Validate(
            Bytes("<svg><filter><feFlood/><feColorMatrix/><feOffset/><feGaussianBlur/><feBlend/><feDropShadow/></filter></svg>"),
            CancellationToken.None);

        Assert.True(result.Accepted, result.Reason);
    }

    [Fact]
    public void Validate_RejectsExcessiveFilterPipeline()
    {
        string primitives = string.Concat(
            System.Linq.Enumerable.Repeat("<feColorMatrix/>", SvgResourceBudget.MaxFilterPrimitives + 1));

        SvgResourceBudgetResult result = SvgResourceBudget.Validate(
            Bytes($"<svg><filter>{primitives}</filter></svg>"),
            CancellationToken.None);

        Assert.False(result.Accepted);
        Assert.Equal("filter-complexity", result.Reason);
    }

    [Fact]
    public void Validate_AcceptsKaTeXShapedEmbeddedImageMosaic()
    {
        var images = new StringBuilder();
        for (int index = 0; index < 182; index++)
        {
            string payload = Convert.ToBase64String(CreatePngHeader(48, 48, index));
            images.Append("<a href='https://github.com/example/")
                .Append(index)
                .Append("'><image xlink:href='data:image/png;base64,")
                .Append(payload)
                .Append("'/></a>");
        }

        SvgResourceBudgetResult result = SvgResourceBudget.Validate(
            Bytes($"<svg xmlns:xlink='http://www.w3.org/1999/xlink'>{images}</svg>"),
            CancellationToken.None);

        Assert.True(result.Accepted, result.Reason);
    }

    [Fact]
    public void Validate_AcceptsBoundedEmbeddedImageData()
    {
        SvgResourceBudgetResult result = SvgResourceBudget.Validate(
            Bytes($"<svg><image href='data:image/png;base64,{Convert.ToBase64String(CreatePngHeader(1, 1, 1))}'/></svg>"),
            CancellationToken.None);

        Assert.True(result.Accepted, result.Reason);
    }

    [Fact]
    public void Validate_AcceptsKnownRasterBytesWithBrowserToleratedInvalidMediaType()
    {
        SvgResourceBudgetResult result = SvgResourceBudget.Validate(
            Bytes($"<svg><image href='data:false;base64,{Convert.ToBase64String(CreatePngHeader(128, 128, 1))}'/></svg>"),
            CancellationToken.None);

        Assert.True(result.Accepted, result.Reason);
    }

    [Fact]
    public void Validate_DeduplicatesRepeatedEmbeddedImagePayloads()
    {
        string data = Convert.ToBase64String(CreatePngHeader(4096, 4096, 7));
        string images = string.Concat(System.Linq.Enumerable.Repeat(
            $"<image href='data:image/png;base64,{data}'/>",
            256));

        SvgResourceBudgetResult result = SvgResourceBudget.Validate(
            Bytes($"<svg>{images}</svg>"),
            CancellationToken.None);

        Assert.True(result.Accepted, result.Reason);
    }

    [Fact]
    public void Validate_RejectsAggregateDecodedEmbeddedImageBudget()
    {
        var images = new StringBuilder();
        for (int index = 0; index < 17; index++)
        {
            string data = Convert.ToBase64String(CreatePngHeader(1024, 1024, index));
            images.Append("<image href='data:image/png;base64,")
                .Append(data)
                .Append("'/>");
        }

        SvgResourceBudgetResult result = SvgResourceBudget.Validate(
            Bytes($"<svg>{images}</svg>"),
            CancellationToken.None);

        Assert.False(result.Accepted);
        Assert.Equal("embedded-image-data", result.Reason);
    }

    [Theory]
    [InlineData("https://example.test/avatar.png")]
    [InlineData("file:///C:/avatar.png")]
    public void Validate_RejectsExternalSvgImageReferences(string href)
    {
        SvgResourceBudgetResult result = SvgResourceBudget.Validate(
            Bytes($"<svg><image href='{href}'/></svg>"),
            CancellationToken.None);

        Assert.False(result.Accepted);
        Assert.Equal("external-image-reference", result.Reason);
    }

    [Theory]
    [InlineData("data:image/png;base64,not-valid-base64")]
    [InlineData("data:text/plain;base64,SGVsbG8=")]
    public void Validate_ReportsInvalidEmbeddedImagesAsEmbeddedContent(string href)
    {
        SvgResourceBudgetResult result = SvgResourceBudget.Validate(
            Bytes($"<svg><image href='{href}'/></svg>"),
            CancellationToken.None);

        Assert.False(result.Accepted);
        Assert.Equal("invalid-embedded-image", result.Reason);
    }

    [Theory]
    [InlineData("<svg><use href='https://example.test/symbol.svg#icon'/></svg>")]
    [InlineData("<svg><rect style='fill:url(https://example.test/paint.svg#p)'/></svg>")]
    [InlineData("<svg><style>@import url('https://example.test/theme.css');</style></svg>")]
    public void Validate_RejectsNestedExternalResources(string svg)
    {
        SvgResourceBudgetResult result = SvgResourceBudget.Validate(Bytes(svg), CancellationToken.None);

        Assert.False(result.Accepted);
        Assert.Contains(result.Reason, new[] { "external-resource", "active-content" });
    }

    [Theory]
    [InlineData("<svg><script>0</script></svg>")]
    [InlineData("<svg><foreignObject/></svg>")]
    [InlineData("<svg><animate attributeName='x' dur='1s'/></svg>")]
    [InlineData("<svg><rect onclick='alert(1)'/></svg>")]
    public void Validate_RejectsExecutableOrAnimatedContent(string svg)
    {
        SvgResourceBudgetResult result = SvgResourceBudget.Validate(Bytes(svg), CancellationToken.None);

        Assert.False(result.Accepted);
        Assert.Contains(result.Reason, new[] { "active-content", "event-handler" });
    }

    [Fact]
    public void StaticSnapshot_ConvertsSmilToDeterministicInitialFrame()
    {
        byte[] source = Bytes(
            "<svg xmlns='http://www.w3.org/2000/svg'>" +
            "<g opacity='0'><animate attributeName='opacity' values='0;0;1;1' keyTimes='0;0;0;.5'/></g>" +
            "<g transform='translate(1 1)'><animateTransform attributeName='transform' type='translate' values='0,48;0,48;0,0' keyTimes='0;0;0' additive='sum'/></g>" +
            "</svg>");

        byte[] snapshot = SvgStaticSnapshot.Create(source, CancellationToken.None);
        string text = Encoding.UTF8.GetString(snapshot);
        SvgResourceBudgetResult result = SvgResourceBudget.Validate(snapshot, CancellationToken.None);

        Assert.True(result.Accepted, result.Reason);
        Assert.DoesNotContain("<animate", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("opacity=\"1\"", text, StringComparison.Ordinal);
        Assert.Contains("transform=\"translate(1 1) translate(0,0)\"", text, StringComparison.Ordinal);
    }

    [Fact]
    public void StaticSnapshot_DoesNotRemoveOtherRejectedActiveContent()
    {
        byte[] source = Bytes(
            "<svg><script>alert(1)</script><rect><animate attributeName='opacity' from='0'/></rect></svg>");

        byte[] snapshot = SvgStaticSnapshot.Create(source, CancellationToken.None);
        SvgResourceBudgetResult result = SvgResourceBudget.Validate(snapshot, CancellationToken.None);

        Assert.False(result.Accepted);
        Assert.Equal("active-content", result.Reason);
    }

    [Fact]
    public void StaticSnapshot_ConvertsTextOnlyHtmlForeignObject()
    {
        byte[] source = Bytes(
            "<svg xmlns='http://www.w3.org/2000/svg'>" +
            "<foreignObject x='6' y='10' width='198' height='17' " +
            "selection='true' style='font-size:9px;color:rgb(67, 39, 135);font-family:Arial;font-weight:400;text-align:center;letter-spacing:0em;line-height:1.5'>" +
            "<div xmlns='http://www.w3.org/1999/xhtml'>GITHUB TRENDING</div>" +
            "</foreignObject></svg>");

        byte[] snapshot = SvgStaticSnapshot.Create(source, CancellationToken.None);
        string text = Encoding.UTF8.GetString(snapshot);
        SvgResourceBudgetResult result = SvgResourceBudget.Validate(snapshot, CancellationToken.None);

        Assert.True(result.Accepted, result.Reason);
        Assert.DoesNotContain("foreignObject", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("GITHUB TRENDING", text, StringComparison.Ordinal);
        Assert.Contains("text-anchor=\"middle\"", text, StringComparison.Ordinal);
        Assert.Contains("fill=\"rgb(67, 39, 135)\"", text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("<img xmlns='http://www.w3.org/1999/xhtml' src='https://example.test/a.png'/>")]
    [InlineData("<script xmlns='http://www.w3.org/1999/xhtml'>alert(1)</script>")]
    [InlineData("<div xmlns='http://www.w3.org/1999/xhtml' onclick='alert(1)'>unsafe</div>")]
    public void StaticSnapshot_DoesNotConvertInteractiveForeignObject(string content)
    {
        byte[] source = Bytes(
            $"<svg xmlns='http://www.w3.org/2000/svg'><foreignObject width='100' height='20'>{content}</foreignObject></svg>");

        byte[] snapshot = SvgStaticSnapshot.Create(source, CancellationToken.None);
        SvgResourceBudgetResult result = SvgResourceBudget.Validate(snapshot, CancellationToken.None);

        Assert.False(result.Accepted);
        Assert.Equal("active-content", result.Reason);
    }

    [Theory]
    [InlineData("onload='alert(1)'")]
    [InlineData("data-unknown='discarded styling'")]
    [InlineData("style='background:url(https://example.test/a.png)'")]
    public void StaticSnapshot_DoesNotHideUnsupportedForeignObjectAttributes(string attributes)
    {
        byte[] source = Bytes(
            $"<svg xmlns='http://www.w3.org/2000/svg'><foreignObject width='100' height='20' {attributes}>" +
            "<div xmlns='http://www.w3.org/1999/xhtml'>text</div></foreignObject></svg>");

        byte[] snapshot = SvgStaticSnapshot.Create(source, CancellationToken.None);
        SvgResourceBudgetResult result = SvgResourceBudget.Validate(snapshot, CancellationToken.None);

        Assert.False(result.Accepted);
        Assert.Equal("active-content", result.Reason);
    }

    [Fact]
    public void StaticSnapshot_RemovesEmbeddedFontFaceAndKeepsChartStyles()
    {
        byte[] source = Bytes(
            "<svg xmlns='http://www.w3.org/2000/svg'><style>" +
            "@font-face{font-family:xkcd;src:url(data:application/font-woff;base64,AAAA)}" +
            ".label{font-family:xkcd;fill:#123456}" +
            "</style><text class='label'>History</text></svg>");

        byte[] snapshot = SvgStaticSnapshot.Create(source, CancellationToken.None);
        string text = Encoding.UTF8.GetString(snapshot);
        SvgResourceBudgetResult result = SvgResourceBudget.Validate(snapshot, CancellationToken.None);

        Assert.True(result.Accepted, result.Reason);
        Assert.DoesNotContain("@font-face", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(".label{font-family:xkcd;fill:#123456}", text, StringComparison.Ordinal);
        Assert.Contains("History", text, StringComparison.Ordinal);
    }

    [Fact]
    public void StaticSnapshot_DoesNotHideMalformedFontFaceRule()
    {
        byte[] source = Bytes(
            "<svg xmlns='http://www.w3.org/2000/svg'><style>@font-face{font-family:xkcd</style></svg>");

        byte[] snapshot = SvgStaticSnapshot.Create(source, CancellationToken.None);
        SvgResourceBudgetResult result = SvgResourceBudget.Validate(snapshot, CancellationToken.None);

        Assert.False(result.Accepted);
        Assert.Equal("active-content", result.Reason);
    }

    [Fact]
    public void StaticSnapshot_ObservesCancellation()
    {
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(() => SvgStaticSnapshot.Create(
            Bytes("<svg><animate attributeName='opacity' from='0'/></svg>"),
            cancellation.Token));
    }

    [Fact]
    public void Validate_ObservesCancellation()
    {
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(() =>
            SvgResourceBudget.Validate(Bytes("<svg><rect width='1'/></svg>"), cancellation.Token));
    }

    private static byte[] Bytes(string value) => Encoding.UTF8.GetBytes(value);

    private static byte[] CreatePngHeader(int width, int height, int unique)
    {
        byte[] bytes = new byte[25];
        new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }.CopyTo(bytes, 0);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(8), 13);
        "IHDR"u8.CopyTo(bytes.AsSpan(12));
        BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(16), width);
        BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(20), height);
        bytes[24] = unchecked((byte)unique);
        return bytes;
    }
}
