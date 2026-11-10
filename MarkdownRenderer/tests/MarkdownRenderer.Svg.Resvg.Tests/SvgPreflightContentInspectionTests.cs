using System.Text;
using MarkdownRenderer.Images;
using MarkdownRenderer.Svg.Resvg.Internal.Preflight;
using Xunit;

namespace MarkdownRenderer.Svg.Resvg.Tests;

/// <summary>
/// Pins what preflight inspection accepts, normalizes or rejects: raster
/// media types, UTF-16 and Latin-1 normalization, DTDs across encodings,
/// nested SVG roots, CSS data references, and the nodes that follow a
/// consumed style or description element.
/// </summary>
public sealed class SvgPreflightContentInspectionTests
{
    [Fact]
    public async Task CorrectRasterMediaTypeDoesNotRewriteTheSvgSource()
    {
        byte[] png = CreatePngHeader();
        string payload = Convert.ToBase64String(png);
        string sourceText = CreateImageSvg("image/png", payload);
        byte[] source = Encoding.UTF8.GetBytes(sourceText);
        byte[] original = (byte[])source.Clone();

        SvgPreflightResult result = await SvgPreflight.InspectAsync(
            new MarkdownSvgOpenRequest(source),
            new ResvgMarkdownSvgRendererOptions(),
            CancellationToken.None);

        Assert.Equal(original, source);
        Assert.Equal(sourceText, Encoding.UTF8.GetString(result.Source.ReadAllBytes()));
    }

    [Fact]
    public async Task MismatchedRasterMediaTypeIsValidatedWithoutRewritingTheXml()
    {
        byte[] png = CreatePngHeader();
        string payload = Convert.ToBase64String(png);
        string sourceText =
            $"<svg xmlns='http://www.w3.org/2000/svg'><svg><image href='data:image/gif;base64,{payload}'/></svg></svg>";
        byte[] source = Encoding.UTF8.GetBytes(sourceText);
        byte[] original = (byte[])source.Clone();

        SvgPreflightResult result = await SvgPreflight.InspectAsync(
            new MarkdownSvgOpenRequest(source),
            new ResvgMarkdownSvgRendererOptions(),
            CancellationToken.None);

        Assert.Equal(original, source);
        Assert.Equal(original, result.Source.ReadAllBytes());
        Assert.Equal(png.Length, result.EmbeddedBytes);
        Assert.Equal(1, result.EmbeddedPixels);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Utf16SvgWithoutDoctypeIsNormalizedToUtf8(
        bool bigEndian)
    {
        string sourceText =
            "<?xml version='1.0' encoding='utf-16' standalone='yes'?>" +
            "<!--preserved comment--><?audit keep?><svg xmlns='http://www.w3.org/2000/svg' width='3' height='2'>" +
            "<desc>café</desc></svg>";
        Encoding encoding = new UnicodeEncoding(bigEndian, byteOrderMark: true);
        byte[] source = EncodeWithPreamble(encoding, sourceText);
        byte[] original = (byte[])source.Clone();

        SvgPreflightResult result = await SvgPreflight.InspectAsync(
            new MarkdownSvgOpenRequest(source),
            new ResvgMarkdownSvgRendererOptions(),
            CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));

        string normalized = Encoding.UTF8.GetString(result.Source.ReadAllBytes());
        Assert.Equal(original, source);
        Assert.False(result.Source.Length >= 3 &&
                     result.Source.GetByte(0) == 0xef && result.Source.GetByte(1) == 0xbb && result.Source.GetByte(2) == 0xbf);
        Assert.Contains("encoding=\"utf-8\"", normalized, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("standalone=\"yes\"", normalized, StringComparison.Ordinal);
        Assert.Contains("<!--preserved comment-->", normalized, StringComparison.Ordinal);
        Assert.Contains("<?audit keep?>", normalized, StringComparison.Ordinal);
        Assert.DoesNotContain("<!DOCTYPE", normalized, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("café", result.Info.Description);
        Assert.Equal(3, result.Info.IntrinsicWidthDips);
        Assert.Equal(2, result.Info.IntrinsicHeightDips);
    }

    [Theory]
    [InlineData("utf8-no-bom")]
    [InlineData("utf8-bom")]
    [InlineData("utf16-le-bom")]
    [InlineData("utf16-be-bom")]
    [InlineData("utf16-le-no-bom")]
    [InlineData("utf16-be-no-bom")]
    public async Task InspectRejectsDocumentTypeAcrossXmlEncodingsAndPrologVariants(string encodingKind)
    {
        Encoding encoding;
        string declaredEncoding;
        switch (encodingKind)
        {
            case "utf8-no-bom":
                encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
                declaredEncoding = "utf-8";
                break;
            case "utf8-bom":
                encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true);
                declaredEncoding = "utf-8";
                break;
            case "utf16-le-bom":
                encoding = new UnicodeEncoding(bigEndian: false, byteOrderMark: true);
                declaredEncoding = "utf-16";
                break;
            case "utf16-be-bom":
                encoding = new UnicodeEncoding(bigEndian: true, byteOrderMark: true);
                declaredEncoding = "utf-16";
                break;
            case "utf16-le-no-bom":
                encoding = new UnicodeEncoding(bigEndian: false, byteOrderMark: false);
                declaredEncoding = "utf-16";
                break;
            case "utf16-be-no-bom":
                encoding = new UnicodeEncoding(bigEndian: true, byteOrderMark: false);
                declaredEncoding = "utf-16";
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(encodingKind));
        }
        string sourceText =
            $"<?xml version='1.0' encoding='{declaredEncoding}' standalone='yes'?>\n" +
            "<!-- literal <!DOCTYPE marker --><?audit keep?>\n" +
            "<!DOCTYPE svg SYSTEM 'https://example.invalid/svg11.dtd'>" +
            "<svg xmlns='http://www.w3.org/2000/svg'><desc>Rejected</desc></svg>";
        byte[] source = EncodeWithPreamble(encoding, sourceText);
        byte[] original = (byte[])source.Clone();

        MarkdownSvgException exception = await Assert.ThrowsAsync<MarkdownSvgException>(async () =>
            await SvgPreflight.InspectAsync(
                new MarkdownSvgOpenRequest(source),
                new ResvgMarkdownSvgRendererOptions(),
                CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5)));

        Assert.Equal(MarkdownSvgFailureReason.UnsupportedContent, exception.Reason);
        Assert.Contains("DTD", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(original, source);
    }

    [Fact]
    public async Task DeclaredLatin1SvgIsNormalizedAndPreservesDecodedMetadata()
    {
        string sourceText =
            "<?xml version='1.0' encoding='iso-8859-1'?>" +
            "<svg xmlns='http://www.w3.org/2000/svg'><desc>café</desc></svg>";
        byte[] source = Encoding.Latin1.GetBytes(sourceText);
        byte[] original = (byte[])source.Clone();

        SvgPreflightResult result = await SvgPreflight.InspectAsync(
            new MarkdownSvgOpenRequest(source),
            new ResvgMarkdownSvgRendererOptions(),
            CancellationToken.None);

        string normalized = Encoding.UTF8.GetString(result.Source.ReadAllBytes());
        Assert.Equal(original, source);
        Assert.Contains("encoding=\"utf-8\"", normalized, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("café", normalized, StringComparison.Ordinal);
        Assert.Equal("café", result.Info.Description);
    }

    [Fact]
    public async Task Utf8NormalizationStopsAtLoweredSourceOutputCeiling()
    {
        string sourceText =
            "<?xml version='1.0' encoding='iso-8859-1'?>" +
            "<svg xmlns='http://www.w3.org/2000/svg'><desc>" + new string('é', 128) +
            "</desc></svg>";
        byte[] source = Encoding.Latin1.GetBytes(sourceText);

        MarkdownSvgException failure = await Assert.ThrowsAsync<MarkdownSvgException>(async () =>
            await SvgPreflight.InspectAsync(
                new MarkdownSvgOpenRequest(source),
                new ResvgMarkdownSvgRendererOptions(maxSourceBytes: source.Length),
                CancellationToken.None));

        Assert.Equal(MarkdownSvgFailureReason.ResourceLimitExceeded, failure.Reason);
    }

    [Fact]
    public async Task CanceledUtf16InspectionAndNormalizationObserveCancellation()
    {
        string sourceText = "<?xml version='1.0' encoding='utf-16'?><svg xmlns='http://www.w3.org/2000/svg'/>";
        byte[] source = EncodeWithPreamble(new UnicodeEncoding(false, true), sourceText);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await SvgPreflight.InspectAsync(
                new MarkdownSvgOpenRequest(source),
                new ResvgMarkdownSvgRendererOptions(),
                cancellation.Token));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await SvgUtf8Normalization.NormalizeToUtf8SegmentsAsync(
                source,
                new ResvgMarkdownSvgRendererOptions(),
                cancellation.Token));
    }

    [Fact]
    public async Task InertSvgStylesheetProcessingInstructionIsRejected()
    {
        const string sourceText =
            "<?xml-stylesheet type='text/css' href='https://example.invalid/style.css'?>" +
            "<svg xmlns='http://www.w3.org/2000/svg'/>";

        MarkdownSvgException failure = await Assert.ThrowsAsync<MarkdownSvgException>(async () =>
            await SvgPreflight.InspectAsync(
                new MarkdownSvgOpenRequest(Encoding.UTF8.GetBytes(sourceText)),
                new ResvgMarkdownSvgRendererOptions(),
                CancellationToken.None));

        Assert.Equal(MarkdownSvgFailureReason.UnsupportedContent, failure.Reason);
    }

    [Fact]
    public async Task RootWithoutSvgNamespaceIsRejected()
    {
        const string sourceText = "<svg xmlns='urn:not-svg'/>";

        MarkdownSvgException failure = await Assert.ThrowsAsync<MarkdownSvgException>(async () =>
            await SvgPreflight.InspectAsync(
                new MarkdownSvgOpenRequest(Encoding.UTF8.GetBytes(sourceText)),
                new ResvgMarkdownSvgRendererOptions(),
                CancellationToken.None));

        Assert.Equal(MarkdownSvgFailureReason.UnsupportedContent, failure.Reason);
    }

    [Fact]
    public async Task NestedSvgBeyondFormerPrefixLimitIsRecognizedAfterLongPrologComment()
    {
        string nestedText = "<!--" + new string('x', 600) + "--><?audit keep?>" +
            "<svg xmlns='http://www.w3.org/2000/svg' width='1' height='1'/>";
        byte[] nested = Encoding.UTF8.GetBytes(nestedText);
        string sourceText = CreateImageSvg("image/svg+xml", Convert.ToBase64String(nested));

        SvgPreflightResult result = await SvgPreflight.InspectAsync(
            new MarkdownSvgOpenRequest(Encoding.UTF8.GetBytes(sourceText)),
            new ResvgMarkdownSvgRendererOptions(),
            CancellationToken.None);

        Assert.Equal(nested.Length, result.EmbeddedBytes);
    }

    [Theory]
    [InlineData("<html xmlns='http://www.w3.org/1999/xhtml'/>")]
    [InlineData("<svg xmlns='urn:not-svg'/>")]
    [InlineData("<svg xmlns='http://www.w3.org/2000/svg'")]
    [InlineData("<?xml-stylesheet href='https://example.invalid/style.css'?><svg xmlns='http://www.w3.org/2000/svg'/>")]
    [InlineData("<!DOCTYPE svg [<!ENTITY x 'x'>]><svg xmlns='http://www.w3.org/2000/svg'/>")]
    [InlineData("<!DOCTYPE svg SYSTEM 'https://example.invalid/svg.dtd'><svg xmlns='http://www.w3.org/2000/svg'/>")]
    public async Task NestedSvgRootIdentificationRejectsMalformedNonSvgAndActiveProlog(
        string nestedText)
    {
        string sourceText = CreateImageSvg(
            "image/svg+xml",
            Convert.ToBase64String(Encoding.UTF8.GetBytes(nestedText)));

        MarkdownSvgException failure = await Assert.ThrowsAsync<MarkdownSvgException>(async () =>
            await SvgPreflight.InspectAsync(
                new MarkdownSvgOpenRequest(Encoding.UTF8.GetBytes(sourceText)),
                new ResvgMarkdownSvgRendererOptions(),
                CancellationToken.None));

        Assert.Equal(MarkdownSvgFailureReason.UnsupportedContent, failure.Reason);
    }

    [Fact]
    public async Task NestedSvgWithNonUtf8EncodingIsRejectedBeforeWorkerOpen()
    {
        const string nestedText = "<?xml version='1.0' encoding='utf-16'?>" +
            "<svg xmlns='http://www.w3.org/2000/svg'/>";
        byte[] nested = EncodeWithPreamble(new UnicodeEncoding(false, true), nestedText);
        string sourceText = CreateImageSvg("image/svg+xml", Convert.ToBase64String(nested));

        MarkdownSvgException failure = await Assert.ThrowsAsync<MarkdownSvgException>(async () =>
            await SvgPreflight.InspectAsync(
                new MarkdownSvgOpenRequest(Encoding.UTF8.GetBytes(sourceText)),
                new ResvgMarkdownSvgRendererOptions(),
                CancellationToken.None));

        Assert.Equal(MarkdownSvgFailureReason.UnsupportedContent, failure.Reason);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CssDataReferencesAcceptQuotedAndUnquotedTargetsAndNormalizeMimeHeaders(
        bool quoted)
    {
        byte[] png = CreatePngHeader();
        string payload = Convert.ToBase64String(png);
        string sourceText = CreateCssImageSvg(
            quoted,
            $"data:IMAGE/PNG;BaSe64;charset=utf-8,{payload}");

        SvgPreflightResult result = await SvgPreflight.InspectAsync(
            new MarkdownSvgOpenRequest(Encoding.UTF8.GetBytes(sourceText)),
            new ResvgMarkdownSvgRendererOptions(),
            CancellationToken.None);

        Assert.Equal(png.Length, result.EmbeddedBytes);
        Assert.Equal(1, result.EmbeddedPixels);
    }

    [Fact]
    public async Task CssDataReferenceEnforcesDecodedByteBudgetBeforeAcceptingPayload()
    {
        string payload = Convert.ToBase64String(CreatePngHeader());
        string sourceText = CreateCssImageSvg(
            quoted: true,
            dataUri: $"data:image/png;base64,{payload}");

        MarkdownSvgException failure = await Assert.ThrowsAsync<MarkdownSvgException>(async () =>
            await SvgPreflight.InspectAsync(
                new MarkdownSvgOpenRequest(Encoding.UTF8.GetBytes(sourceText)),
                new ResvgMarkdownSvgRendererOptions(maxEmbeddedImageBytes: 16),
                CancellationToken.None));

        Assert.Equal(MarkdownSvgFailureReason.ResourceLimitExceeded, failure.Reason);
    }

    [Fact]
    public async Task MalformedCssDataReferenceRemainsUnsupported()
    {
        string sourceText = CreateCssImageSvg(quoted: true, dataUri: "data:image/png;base64,%%%=");

        MarkdownSvgException failure = await Assert.ThrowsAsync<MarkdownSvgException>(async () =>
            await SvgPreflight.InspectAsync(
                new MarkdownSvgOpenRequest(Encoding.UTF8.GetBytes(sourceText)),
                new ResvgMarkdownSvgRendererOptions(),
                CancellationToken.None));

        Assert.Equal(MarkdownSvgFailureReason.UnsupportedContent, failure.Reason);
    }

    [Theory]
    [InlineData("<style>.x{fill:red}</style><script>alert(1)</script>")]
    [InlineData("<style>.x{fill:red}</style><rect onload='alert(1)'/>")]
    [InlineData("<desc>chart</desc><image href='https://example.invalid/image.png'/>")]
    public async Task ConsumedStyleOrDescriptionDoesNotSkipFollowingUnsafeNode(string content)
    {
        string sourceText = $"<svg xmlns='http://www.w3.org/2000/svg'>{content}</svg>";

        MarkdownSvgException failure = await Assert.ThrowsAsync<MarkdownSvgException>(async () =>
            await SvgPreflight.InspectAsync(
                new MarkdownSvgOpenRequest(Encoding.UTF8.GetBytes(sourceText)),
                new ResvgMarkdownSvgRendererOptions(),
                CancellationToken.None));

        Assert.Equal(MarkdownSvgFailureReason.UnsupportedContent, failure.Reason);
    }

    [Fact]
    public async Task NodesAfterAdjacentStyleAndDescriptionContributeMetadataAndBudget()
    {
        const string sourceText =
            "<svg xmlns='http://www.w3.org/2000/svg'><style>svg{color:currentColor}</style>" +
            "<desc>accessible graph</desc><text>label</text>" +
            "<filter id='f'><feGaussianBlur stdDeviation='1'/></filter></svg>";

        SvgPreflightResult result = await SvgPreflight.InspectAsync(
            new MarkdownSvgOpenRequest(Encoding.UTF8.GetBytes(sourceText)),
            new ResvgMarkdownSvgRendererOptions(),
            CancellationToken.None);

        Assert.Equal("accessible graph", result.Info.Description);
        Assert.True(result.Info.HasText);
        Assert.True(result.Info.UsesCurrentColor);
        Assert.Equal(1, result.FilterPrimitives);
        Assert.Equal(37, result.StructuralCost);
    }

    [Fact]
    public async Task FilterBudgetImmediatelyAfterConsumedDescriptionIsEnforced()
    {
        const string sourceText =
            "<svg xmlns='http://www.w3.org/2000/svg'><style>svg{color:currentColor}</style>" +
            "<desc>accessible graph</desc><text>label</text>" +
            "<filter id='f'><feGaussianBlur stdDeviation='1'/></filter></svg>";

        MarkdownSvgException failure = await Assert.ThrowsAsync<MarkdownSvgException>(async () =>
            await SvgPreflight.InspectAsync(
                new MarkdownSvgOpenRequest(Encoding.UTF8.GetBytes(sourceText)),
                new ResvgMarkdownSvgRendererOptions(maxStructuralCost: 36),
                CancellationToken.None));

        Assert.Equal(MarkdownSvgFailureReason.ResourceLimitExceeded, failure.Reason);
    }

    private static byte[] CreatePngHeader() =>
    [
        137, 80, 78, 71, 13, 10, 26, 10,
        0, 0, 0, 13, 73, 72, 68, 82,
        0, 0, 0, 1, 0, 0, 0, 1,
    ];

    private static string CreateImageSvg(string mediaType, string payload) =>
        $"<svg xmlns='http://www.w3.org/2000/svg'><image href='data:{mediaType};base64,{payload}'/></svg>";

    private static byte[] EncodeWithPreamble(Encoding encoding, string value)
    {
        byte[] preamble = encoding.GetPreamble();
        byte[] contents = encoding.GetBytes(value);
        byte[] result = new byte[preamble.Length + contents.Length];
        preamble.CopyTo(result, 0);
        contents.CopyTo(result, preamble.Length);
        return result;
    }

    private static string CreateCssImageSvg(bool quoted, string dataUri)
    {
        string url = quoted
            ? $"url(\"  {dataUri}  \")"
            : $"url(  {dataUri}  )";
        return $"<svg xmlns='http://www.w3.org/2000/svg'><rect style='fill:{url}'/></svg>";
    }
}
