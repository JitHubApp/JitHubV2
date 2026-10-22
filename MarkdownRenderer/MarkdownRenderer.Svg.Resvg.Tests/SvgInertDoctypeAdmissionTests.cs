using System.Text;
using MarkdownRenderer.Images;
using Xunit;

namespace MarkdownRenderer.Svg.Resvg.Tests;

/// <summary>
/// P3-002: host source preparation admits the inert W3C SVG public DOCTYPE
/// that common exporters write (34 images in 22 frozen top-500 READMEs) by
/// neutralizing exactly that declaration. Every other DTD form stays rejected,
/// and the direct open path and the worker keep rejecting every DTD.
/// </summary>
public sealed class SvgInertDoctypeAdmissionTests
{
    private const string Body =
        "<svg xmlns='http://www.w3.org/2000/svg' width='8' height='8'><rect width='8' height='8' fill='green'/></svg>";

    public static TheoryData<string, string> W3cPairs => new()
    {
        { "-//W3C//DTD SVG 1.0//EN", "http://www.w3.org/TR/2001/REC-SVG-20010904/DTD/svg10.dtd" },
        { "-//W3C//DTD SVG 20010904//EN", "http://www.w3.org/TR/2001/REC-SVG-20010904/DTD/svg10.dtd" },
        { "-//W3C//DTD SVG 1.1//EN", "http://www.w3.org/Graphics/SVG/1.1/DTD/svg11.dtd" },
        { "-//W3C//DTD SVG 1.1 Basic//EN", "http://www.w3.org/Graphics/SVG/1.1/DTD/svg11-basic.dtd" },
        { "-//W3C//DTD SVG 1.1 Tiny//EN", "http://www.w3.org/Graphics/SVG/1.1/DTD/svg11-tiny.dtd" },
    };

    [Theory]
    [MemberData(nameof(W3cPairs))]
    public async Task PrepareSourceAdmitsInertW3cDoctypeAndTheWorkerRendersIt(string publicId, string systemId)
    {
        string doctype = $"<!DOCTYPE svg PUBLIC \"{publicId}\" \"{systemId}\">";
        byte[] source = Utf8("<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<!-- exporter comment -->\n" + doctype + "\n" + Body);
        byte[] original = (byte[])source.Clone();
        await using var renderer = new ResvgMarkdownSvgRenderer();

        MarkdownSvgSourcePreparation preparation = renderer.PrepareSource(source, CancellationToken.None);

        Assert.True(preparation.SanitizedBytes is not null, preparation.FailureDescription);
        Assert.Equal(original, source);
        string prepared = Encoding.UTF8.GetString(preparation.SanitizedBytes!);
        Assert.DoesNotContain("<!DOCTYPE", prepared, StringComparison.Ordinal);
        Assert.Contains("fill='green'", prepared, StringComparison.Ordinal);

        using IMarkdownSvgDocument document = await renderer.OpenAsync(
            new MarkdownSvgOpenRequest(preparation.SanitizedBytes!));
        Assert.Equal(8, document.Info.IntrinsicWidthDips);
        Assert.Equal(8, document.Info.IntrinsicHeightDips);
    }

    [Fact]
    public void PrepareSourceAdmitsTheExactCorpusShapeWithByteBom()
    {
        // Rank 1's badge: Illustrator prolog, comment, SVG 1.1 DOCTYPE, CRLF.
        byte[] source =
        [
            0xEF, 0xBB, 0xBF,
            .. Utf8("<?xml version=\"1.0\" encoding=\"utf-8\"?>\r\n" +
                "<!-- Generator: Adobe Illustrator 13.0.2, SVG Export Plug-In . SVG Version: 6.00 Build 14948)  -->\r\n" +
                "<!DOCTYPE svg PUBLIC \"-//W3C//DTD SVG 1.1//EN\" \"http://www.w3.org/Graphics/SVG/1.1/DTD/svg11.dtd\">\r\n" +
                Body),
        ];
        using var renderer = new ResvgMarkdownSvgRenderer();

        MarkdownSvgSourcePreparation preparation = renderer.PrepareSource(source, CancellationToken.None);

        Assert.True(preparation.SanitizedBytes is not null, preparation.FailureDescription);
        Assert.DoesNotContain("<!DOCTYPE", Encoding.UTF8.GetString(preparation.SanitizedBytes!), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("<!DOCTYPE svg PUBLIC '-//W3C//DTD SVG 1.1//EN' 'http://127.0.0.1:1/svg11.dtd'>")]
    [InlineData("<!DOCTYPE svg PUBLIC '-//W3C//DTD SVG 1.1//EN' 'https://www.w3.org/Graphics/SVG/1.1/DTD/svg11.dtd'>")]
    [InlineData("<!DOCTYPE svg PUBLIC '-//Example//DTD SVG 1.1//EN' 'http://www.w3.org/Graphics/SVG/1.1/DTD/svg11.dtd'>")]
    [InlineData("<!DOCTYPE svg SYSTEM 'http://www.w3.org/Graphics/SVG/1.1/DTD/svg11.dtd'>")]
    [InlineData("<!DOCTYPE html PUBLIC '-//W3C//DTD SVG 1.1//EN' 'http://www.w3.org/Graphics/SVG/1.1/DTD/svg11.dtd'>")]
    [InlineData("<!DOCTYPE svg PUBLIC '-//W3C//DTD SVG 1.1//EN' 'http://www.w3.org/Graphics/SVG/1.1/DTD/svg11.dtd' [<!ENTITY x 'y'>]>")]
    [InlineData("<!DOCTYPE svg PUBLIC '-//W3C//DTD SVG 1.1//EN' 'http://www.w3.org/Graphics/SVG/1.1/DTD/svg11.dtd' []>")]
    [InlineData("<!DOCTYPE svg [<!ENTITY x 'y'>]>")]
    public void PrepareSourceStillRejectsEveryOtherDoctype(string doctype)
    {
        byte[] source = Utf8(doctype + Body);
        byte[] original = (byte[])source.Clone();
        using var renderer = new ResvgMarkdownSvgRenderer();

        MarkdownSvgSourcePreparation preparation = renderer.PrepareSource(source, CancellationToken.None);

        Assert.Null(preparation.SanitizedBytes);
        Assert.Equal(MarkdownSvgFailureReason.UnsupportedContent, preparation.FailureReason);
        Assert.Contains("dtd-forbidden", preparation.FailureDescription, StringComparison.Ordinal);
        Assert.Equal(original, source);
        Assert.Equal(0, renderer.SourceAttachmentCount);
    }

    [Fact]
    public void PrepareSourceKeepsRejectingAnInertDoctypeOutsideUtf8()
    {
        byte[] source = Encoding.Unicode.GetPreamble()
            .Concat(Encoding.Unicode.GetBytes(
                "<?xml version=\"1.0\" encoding=\"utf-16\"?>" +
                "<!DOCTYPE svg PUBLIC \"-//W3C//DTD SVG 1.1//EN\" \"http://www.w3.org/Graphics/SVG/1.1/DTD/svg11.dtd\">" +
                Body))
            .ToArray();
        using var renderer = new ResvgMarkdownSvgRenderer();

        MarkdownSvgSourcePreparation preparation = renderer.PrepareSource(source, CancellationToken.None);

        Assert.Null(preparation.SanitizedBytes);
        Assert.Equal(MarkdownSvgFailureReason.UnsupportedContent, preparation.FailureReason);
    }

    [Fact]
    public void PrepareSourceKeepsRejectingAnInertDoctypeInsideNestedSvg()
    {
        byte[] nested = Utf8(
            "<!DOCTYPE svg PUBLIC \"-//W3C//DTD SVG 1.1//EN\" \"http://www.w3.org/Graphics/SVG/1.1/DTD/svg11.dtd\">" +
            Body);
        byte[] source = Utf8(
            "<svg xmlns='http://www.w3.org/2000/svg' width='8' height='8'>" +
            $"<image width='8' height='8' href='data:image/svg+xml;base64,{Convert.ToBase64String(nested)}'/></svg>");
        using var renderer = new ResvgMarkdownSvgRenderer();

        MarkdownSvgSourcePreparation preparation = renderer.PrepareSource(source, CancellationToken.None);

        Assert.Null(preparation.SanitizedBytes);
        Assert.Equal(MarkdownSvgFailureReason.UnsupportedContent, preparation.FailureReason);
    }

    [Fact]
    public void LiteralDoctypeMarkersAfterTheRootAreNotRewritten()
    {
        byte[] source = Utf8(
            "<svg xmlns='http://www.w3.org/2000/svg' width='8' height='8'><title><![CDATA[" +
            "<!DOCTYPE svg PUBLIC \"-//W3C//DTD SVG 1.1//EN\" \"http://www.w3.org/Graphics/SVG/1.1/DTD/svg11.dtd\">" +
            "]]></title><rect width='8' height='8'/></svg>");
        using var renderer = new ResvgMarkdownSvgRenderer();

        MarkdownSvgSourcePreparation preparation = renderer.PrepareSource(source, CancellationToken.None);

        Assert.True(preparation.SanitizedBytes is not null, preparation.FailureDescription);
        Assert.Contains("<!DOCTYPE svg PUBLIC", Encoding.UTF8.GetString(preparation.SanitizedBytes!), StringComparison.Ordinal);
    }

    private static byte[] Utf8(string value) => Encoding.UTF8.GetBytes(value);
}
