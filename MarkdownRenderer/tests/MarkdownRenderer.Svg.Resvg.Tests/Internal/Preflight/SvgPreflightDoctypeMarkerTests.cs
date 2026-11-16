using System.Text;
using MarkdownRenderer.Images.Svg;
using MarkdownRenderer.Svg.Resvg.Internal.Preflight;
using MarkdownRenderer.Svg.Resvg.Tests.TestSupport;
using Xunit;

namespace MarkdownRenderer.Svg.Resvg.Tests.Internal.Preflight;

/// <summary>
/// Pins that preflight rejects an actual DOCTYPE declaration, even behind a
/// UTF-8 BOM and a marker comment, before publishing its private snapshot,
/// while literal DOCTYPE marker text in comments, CDATA or text is kept
/// byte for byte.
/// </summary>
public sealed class SvgPreflightDoctypeMarkerTests
{
    [Fact]
    public async Task ExternalDoctypeIsRejectedBeforePrivateSnapshotPublication()
    {
        const string sourceText =
            "<!DOCTYPE svg PUBLIC '-//W3C//DTD SVG 1.1//EN' " +
            "'http://www.w3.org/Graphics/SVG/1.1/DTD/svg11.dtd'>" +
            "<svg xmlns='http://www.w3.org/2000/svg' width='1' height='1'/>";
        byte[] source = Encoding.UTF8.GetBytes(sourceText);
        byte[] original = (byte[])source.Clone();

        MarkdownSvgException exception = await Assert.ThrowsAsync<MarkdownSvgException>(async () =>
            await SvgPreflight.InspectAsync(
                new MarkdownSvgOpenRequest(source),
                new ResvgMarkdownSvgRendererOptions(),
                CancellationToken.None));

        Assert.Equal(MarkdownSvgFailureReason.UnsupportedContent, exception.Reason);
        Assert.Contains("DTD", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(original, source);
    }

    [Theory]
    [InlineData("<svg xmlns='http://www.w3.org/2000/svg'><!-- literal <!DOCTYPE text> --></svg>")]
    [InlineData("<svg xmlns='http://www.w3.org/2000/svg'><![CDATA[<!DOCTYPE text>]]></svg>")]
    [InlineData("<svg xmlns='http://www.w3.org/2000/svg'><text>&lt;!DOCTYPE text&gt;</text></svg>")]
    public async Task DoctypeMarkerTextOutsideThePrologDeclarationIsPreserved(string sourceText)
    {
        byte[] source = Encoding.UTF8.GetBytes(sourceText);
        byte[] original = (byte[])source.Clone();

        SvgPreflightResult result = await SvgPreflight.InspectAsync(
            new MarkdownSvgOpenRequest(source),
            new ResvgMarkdownSvgRendererOptions(),
            CancellationToken.None);

        Assert.Equal(original, source);
        Assert.Equal(original, result.Source.ReadAllBytes());
    }

    [Fact]
    public async Task LiteralDoctypeMarkerInPrologCommentIsPreservedWhenNoDeclarationExists()
    {
        const string markerComment = "<!-- literal <!DOCTYPE svg> marker -->";
        byte[] source = Encoding.UTF8.GetBytes(
            markerComment + "<?audit keep?><svg xmlns='http://www.w3.org/2000/svg' width='1' height='1'/>");
        byte[] original = (byte[])source.Clone();

        SvgPreflightResult result = await SvgPreflight.InspectAsync(
            new MarkdownSvgOpenRequest(source),
            new ResvgMarkdownSvgRendererOptions(),
            CancellationToken.None);

        Assert.Equal(original, source);
        Assert.Contains(markerComment, Encoding.UTF8.GetString(result.Source.ReadAllBytes()));
    }

    [Fact]
    public async Task DoctypeMarkerInPrologCommentDoesNotHideActualUtf8BomDoctype()
    {
        const string markerComment = "<!-- literal <!DOCTYPE comment text> -->";
        const string doctype =
            "<!DOCTYPE svg PUBLIC '-//W3C//DTD SVG 1.1//EN' " +
            "'http://www.w3.org/Graphics/SVG/1.1/DTD/svg11.dtd'>";
        string document = markerComment + doctype +
            "<svg xmlns='http://www.w3.org/2000/svg' width='1' height='1'/>";
        byte[] documentBytes = Encoding.UTF8.GetBytes(document);
        byte[] source = new byte[Encoding.UTF8.GetPreamble().Length + documentBytes.Length];
        Encoding.UTF8.GetPreamble().CopyTo(source, 0);
        documentBytes.CopyTo(source, Encoding.UTF8.GetPreamble().Length);
        byte[] original = (byte[])source.Clone();

        MarkdownSvgException exception = await Assert.ThrowsAsync<MarkdownSvgException>(async () =>
            await SvgPreflight.InspectAsync(
                new MarkdownSvgOpenRequest(source),
                new ResvgMarkdownSvgRendererOptions(),
                CancellationToken.None));

        Assert.Equal(MarkdownSvgFailureReason.UnsupportedContent, exception.Reason);
        Assert.Contains("DTD", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(original, source);
        Assert.Contains(markerComment, Encoding.UTF8.GetString(source), StringComparison.Ordinal);
    }
}
