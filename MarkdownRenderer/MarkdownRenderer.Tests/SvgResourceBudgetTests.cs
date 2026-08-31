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
    public void Validate_RejectsHostileXml(string svg, string expectedReason)
    {
        SvgResourceBudgetResult result = SvgResourceBudget.Validate(Bytes(svg), CancellationToken.None);

        Assert.False(result.Accepted);
        Assert.Equal(expectedReason, result.Reason);
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
