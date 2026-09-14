using System;
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
    [InlineData("<!DOCTYPE svg [<!ENTITY x 'boom'>]><svg><text>&x;</text></svg>", "invalid-xml")]
    [InlineData("<html><body>not svg</body></html>", "missing-root")]
    [InlineData("<svg><filter><feImage href='https://example.test/image.png'/></filter></svg>", "unsupported-filter-primitive")]
    [InlineData("<svg><filter><feDisplacementMap scale='20'/></filter></svg>", "unsupported-filter-primitive")]
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
    public void Validate_RejectsUnboundedEmbeddedImagesBeforeNativeRasterization()
    {
        string images = string.Concat(System.Linq.Enumerable.Repeat(
            "<image href='data:image/png;base64,AAAA'/>",
            SvgResourceBudget.MaxImageElements + 1));

        SvgResourceBudgetResult result = SvgResourceBudget.Validate(
            Bytes($"<svg>{images}</svg>"),
            CancellationToken.None);

        Assert.False(result.Accepted);
        Assert.Equal("image-count", result.Reason);
    }

    [Fact]
    public void Validate_AcceptsBoundedEmbeddedImageData()
    {
        SvgResourceBudgetResult result = SvgResourceBudget.Validate(
            Bytes("<svg><image href='data:image/png;base64,AAAA'/></svg>"),
            CancellationToken.None);

        Assert.True(result.Accepted, result.Reason);
    }

    [Fact]
    public void Validate_RejectsOversizedEmbeddedImageDataBeforeNativeRasterization()
    {
        string data = new('A', SvgResourceBudget.MaxEmbeddedImageDataUriCharacters);

        SvgResourceBudgetResult result = SvgResourceBudget.Validate(
            Bytes($"<svg><image href='data:image/png;base64,{data}'/></svg>"),
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

    [Fact]
    public void Validate_ObservesCancellation()
    {
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(() =>
            SvgResourceBudget.Validate(Bytes("<svg><rect width='1'/></svg>"), cancellation.Token));
    }

    private static byte[] Bytes(string value) => Encoding.UTF8.GetBytes(value);
}
