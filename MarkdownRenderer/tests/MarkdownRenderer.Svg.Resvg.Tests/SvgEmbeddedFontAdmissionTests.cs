using System.Text;
using MarkdownRenderer.Images;
using Xunit;

namespace MarkdownRenderer.Svg.Resvg.Tests;

/// <summary>
/// Star-history-style charts embed their lettering font as a <c>data:</c>
/// <c>@font-face</c>. By design those charts render with a system fallback font: the self-contained block is
/// set aside during admission and removed by the static snapshot. External font
/// sources and animation stay rejected.
/// </summary>
public sealed class SvgEmbeddedFontAdmissionTests
{
    private const string Chart =
        "<svg xmlns='http://www.w3.org/2000/svg' width='40' height='20' style='font-family:xkcd'>" +
        "<defs><style>{0}.label{{font-family:xkcd;fill:#123456}}</style></defs>" +
        "<rect width='40' height='20' fill='white'/><text class='label' x='2' y='14'>Stars</text></svg>";

    [Theory]
    [InlineData("@font-face{font-family:\"xkcd\";src:url(data:application/font-woff;charset=utf-8;base64,d09GRk9UVE8AAA==) format(\"woff\")}")]
    [InlineData("@font-face { font-family: 'xkcd'; src: url('data:font/woff2;base64,AAAA'), local('xkcd'); }")]
    [InlineData("@font-face{font-family:xkcd;src:local(xkcd)}")]
    public async Task PrepareSourceAdmitsSelfContainedFontFaceAndTheWorkerRendersTheChart(string fontFace)
    {
        byte[] source = Utf8(string.Format(Chart, fontFace));
        byte[] original = (byte[])source.Clone();
        await using var renderer = new ResvgMarkdownSvgRenderer();

        MarkdownSvgSourcePreparation preparation = renderer.PrepareSource(source, CancellationToken.None);

        Assert.True(preparation.SanitizedBytes is not null, preparation.FailureDescription);
        Assert.Equal(original, source);
        string prepared = Encoding.UTF8.GetString(preparation.SanitizedBytes!);
        Assert.DoesNotContain("@font-face", prepared, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(".label{font-family:xkcd;fill:#123456}", prepared, StringComparison.Ordinal);
        Assert.Contains("Stars", prepared, StringComparison.Ordinal);

        using IMarkdownSvgDocument document = await renderer.OpenAsync(
            new MarkdownSvgOpenRequest(preparation.SanitizedBytes!));
        Assert.Equal(40, document.Info.IntrinsicWidthDips);
        Assert.Equal(20, document.Info.IntrinsicHeightDips);
    }

    [Theory]
    [InlineData("@font-face{font-family:xkcd;src:url(https://example.test/xkcd.woff)}", "external-resource")]
    [InlineData("@font-face{font-family:xkcd;src:url(data:font/woff;base64,AA==),url(//example.test/x.woff)}", "external-resource")]
    [InlineData("@font-face{font-family:xkcd;src:url(data:font/woff;base64,AA==)}@keyframes pulse{from{opacity:0}to{opacity:1}}", "active-content")]
    [InlineData("@font-face{font-family:xkcd;src:url(data:font/woff;base64,AA==)}.label{animation:pulse 1s}", "active-content")]
    [InlineData("@font-face{font-family:xkcd", "active-content")]
    public void PrepareSourceStillRejectsExternalFontsAnimationAndMalformedRules(string css, string reason)
    {
        byte[] source = Utf8(
            "<svg xmlns='http://www.w3.org/2000/svg' width='40' height='20'><style>" + css +
            "</style><rect width='40' height='20'/></svg>");
        using var renderer = new ResvgMarkdownSvgRenderer();

        MarkdownSvgSourcePreparation preparation = renderer.PrepareSource(source, CancellationToken.None);

        Assert.Null(preparation.SanitizedBytes);
        Assert.Contains(reason, preparation.FailureDescription, StringComparison.Ordinal);
        Assert.Equal(0, renderer.SourceAttachmentCount);
    }

    [Fact]
    public async Task OpenAsyncStillRejectsAnUnpreparedFontFace()
    {
        byte[] source = Utf8(string.Format(Chart, "@font-face{font-family:xkcd;src:url(data:font/woff;base64,AA==)}"));
        await using var renderer = new ResvgMarkdownSvgRenderer();

        MarkdownSvgException failure = await Assert.ThrowsAsync<MarkdownSvgException>(async () =>
            await renderer.OpenAsync(new MarkdownSvgOpenRequest(source)));

        Assert.Equal(MarkdownSvgFailureReason.UnsupportedContent, failure.Reason);
        Assert.Equal(0, renderer.SourceAttachmentCount);
    }

    private static byte[] Utf8(string value) => Encoding.UTF8.GetBytes(value);
}
