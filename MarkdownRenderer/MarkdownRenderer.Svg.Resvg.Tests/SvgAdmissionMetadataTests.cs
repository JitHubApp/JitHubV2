using System.Text;
using MarkdownRenderer.Images;
using MarkdownRenderer.Layout.Boxes;
using Xunit;

namespace MarkdownRenderer.Svg.Resvg.Tests;

public sealed class SvgAdmissionMetadataTests
{
    [Fact]
    public void ProviderPreparationStillAdmitsStaticSvgAndRejectsExternalImage()
    {
        using var renderer = new ResvgMarkdownSvgRenderer();
        byte[] safeSource = Svg("<svg xmlns='http://www.w3.org/2000/svg' width='48' height='24'><title>Badge</title><rect width='48' height='24'/></svg>");

        MarkdownSvgSourcePreparation accepted = renderer.PrepareSource(safeSource, CancellationToken.None);

        Assert.NotNull(accepted.SanitizedBytes);
        Assert.Null(accepted.FailureReason);

        MarkdownSvgSourcePreparation rejected = renderer.PrepareSource(
            Svg("<svg xmlns='http://www.w3.org/2000/svg'><image href='https://example.invalid/a.png'/></svg>"),
            CancellationToken.None);

        Assert.Null(rejected.SanitizedBytes);
        Assert.Equal(MarkdownSvgFailureReason.UnsupportedContent, rejected.FailureReason);
        Assert.Contains("external-image-reference", rejected.FailureDescription, StringComparison.Ordinal);
    }

    [Fact]
    public void ProviderPreparationPropagatesCancellation()
    {
        using var renderer = new ResvgMarkdownSvgRenderer();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.ThrowsAny<OperationCanceledException>(() => renderer.PrepareSource(
            Svg("<svg xmlns='http://www.w3.org/2000/svg'><title>Cancelled</title></svg>"),
            cancellation.Token));
    }

    private static byte[] Svg(string source) => Encoding.UTF8.GetBytes(source);

#if MARKDOWN_LAYOUT_DIAGNOSTICS
    [Fact]
    public void CombinedValidationPreservesAcceptanceAndRejectionParity()
    {
        string nested = Convert.ToBase64String(Svg(
            "<svg xmlns='http://www.w3.org/2000/svg'><title>Nested</title></svg>"));
        string[] documents =
        [
            "<svg xmlns='http://www.w3.org/2000/svg' width='890' height='158'><title>Root</title><desc>Info</desc><rect width='1' height='1'/></svg>",
            "<svg xmlns='http://www.w3.org/2000/svg'><defs><g id='d'/></defs></svg>",
            $"<svg xmlns='http://www.w3.org/2000/svg'><image href='data:image/svg+xml;base64,{nested}'/></svg>",
            "<svg xmlns='http://www.w3.org/2000/svg'><image href='https://example.invalid/image.png'/></svg>",
            "<svg xmlns='http://www.w3.org/2000/svg'><script/></svg>",
            "<svg xmlns='http://www.w3.org/2000/svg'><title>partial</title></svg><trailing>",
            "<!DOCTYPE svg [<!ENTITY x 'expanded'>]><svg xmlns='http://www.w3.org/2000/svg'><title>&x;</title></svg>",
        ];

        foreach (string document in documents)
        {
            byte[] bytes = Svg(document);
            SvgResourceBudgetResult original = SvgResourceBudget.Validate(bytes, CancellationToken.None);
            SvgResourceBudgetResult combined = SvgResourceBudget.ValidateWithMetadata(
                bytes,
                CancellationToken.None,
                out SvgSourceMetadata metadata);

            Assert.Equal(original.Accepted, combined.Accepted);
            Assert.Equal(original.Reason, combined.Reason);
            if (!combined.Accepted)
                Assert.Equal(default, metadata);
        }
    }

    [Fact]
    public void RootMetadataIgnoresDefinitionsGroupsAndNestedSvgResources()
    {
        string nested = Convert.ToBase64String(Svg(
            "<svg xmlns='http://www.w3.org/2000/svg'><title>Nested title</title><desc>Nested desc</desc></svg>"));
        byte[] bytes = Svg(
            $"<svg xmlns='http://www.w3.org/2000/svg' width='890' height='158' viewBox='0 0 890 158'>" +
            "<defs><title>Definition title</title><desc>Definition desc</desc></defs>" +
            "<defs/>" +
            "<g><title>Group title</title><desc>Group desc</desc></g>" +
            "<title>  Direct &amp;   root title </title><desc> Direct\n root   description </desc>" +
            $"<image href='data:image/svg+xml;base64,{nested}'/></svg>");

        SvgResourceBudgetResult result = SvgResourceBudget.ValidateWithMetadata(
            bytes,
            CancellationToken.None,
            out SvgSourceMetadata metadata);

        Assert.True(result.Accepted, result.Reason);
        Assert.Equal("Direct & root title", metadata.Title);
        Assert.Equal("Direct root description", metadata.Desc);
        Assert.Equal(890d, metadata.Geometry.IntrinsicWidth);
        Assert.Equal(158d, metadata.Geometry.IntrinsicHeight);
        Assert.Equal(890d, metadata.Geometry.ViewBoxWidth);
        Assert.Equal(158d, metadata.Geometry.ViewBoxHeight);
    }

    [Fact]
    public void FirstDirectChildTitleWinsIncludingAnEmptySelfClosingTitle()
    {
        SvgResourceBudgetResult result = SvgResourceBudget.ValidateWithMetadata(
            Svg("<svg><defs/><title/><title>Must not replace first occurrence</title><desc>Root</desc></svg>"),
            CancellationToken.None,
            out SvgSourceMetadata metadata);

        Assert.True(result.Accepted, result.Reason);
        Assert.Null(metadata.Title);
        Assert.Equal("Root", metadata.Desc);
    }

    [Fact]
    public void ForeignNamespaceMetadataElementsDoNotBecomeRootMetadata()
    {
        SvgResourceBudgetResult result = SvgResourceBudget.ValidateWithMetadata(
            Svg("<svg xmlns='http://www.w3.org/2000/svg'><x:title xmlns:x='urn:foreign'>Foreign</x:title><title>Native</title></svg>"),
            CancellationToken.None,
            out SvgSourceMetadata metadata);

        Assert.True(result.Accepted, result.Reason);
        Assert.Equal("Native", metadata.Title);
    }

    [Fact]
    public void ExternalSvg11DoctypeIsInertButInternalSubsetFailsClosed()
    {
        byte[] externalDoctype = Svg(
            "<!DOCTYPE svg PUBLIC '-//W3C//DTD SVG 1.1//EN' 'http://127.0.0.1:1/svg11.dtd'>" +
            "<svg xmlns='http://www.w3.org/2000/svg'><title>Standalone</title></svg>");
        byte[] internalSubset = Svg(
            "<!DOCTYPE svg [<!ENTITY label 'unsafe'>]>" +
            "<svg xmlns='http://www.w3.org/2000/svg'><title>&label;</title></svg>");

        SvgResourceBudgetResult externalResult = SvgResourceBudget.ValidateWithMetadata(
            externalDoctype,
            CancellationToken.None,
            out SvgSourceMetadata externalMetadata);
        SvgResourceBudgetResult internalResult = SvgResourceBudget.ValidateWithMetadata(
            internalSubset,
            CancellationToken.None,
            out SvgSourceMetadata internalMetadata);

        Assert.True(externalResult.Accepted, externalResult.Reason);
        Assert.Equal("Standalone", externalMetadata.Title);
        Assert.False(internalResult.Accepted);
        Assert.Equal("dtd-internal-subset", internalResult.Reason);
        Assert.Equal(default, internalMetadata);
    }

    [Fact]
    public void MetadataFromEarlyNodesIsNotPublishedWhenLaterXmlIsMalformedOrUnsafe()
    {
        string[] rejectedDocuments =
        [
            "<svg xmlns='http://www.w3.org/2000/svg'><title>Early</title></svg><extra>",
            "<svg xmlns='http://www.w3.org/2000/svg'><title>Early</title><image href='https://example.invalid/a.png'/></svg>",
            "<svg xmlns='http://www.w3.org/2000/svg'><title>Early</title><script/></svg>",
        ];

        foreach (string document in rejectedDocuments)
        {
            SvgResourceBudgetResult result = SvgResourceBudget.ValidateWithMetadata(
                Svg(document),
                CancellationToken.None,
                out SvgSourceMetadata metadata);

            Assert.False(result.Accepted);
            Assert.Equal(default, metadata);
        }
    }

    [Fact]
    public void MetadataTruncationNeverSplitsAnAstralScalarOrKeepsPendingWhitespace()
    {
        string astralAtBoundary = new string('a', 4095) + "😀";
        string pendingSeparatorAtBoundary = new string('b', 4094) + " 😀";

        SvgResourceBudgetResult firstResult = SvgResourceBudget.ValidateWithMetadata(
            Svg($"<svg><title>{astralAtBoundary}</title></svg>"),
            CancellationToken.None,
            out SvgSourceMetadata first);
        SvgResourceBudgetResult secondResult = SvgResourceBudget.ValidateWithMetadata(
            Svg($"<svg><title>{pendingSeparatorAtBoundary}</title></svg>"),
            CancellationToken.None,
            out SvgSourceMetadata second);

        Assert.True(firstResult.Accepted, firstResult.Reason);
        Assert.True(secondResult.Accepted, secondResult.Reason);
        Assert.NotNull(first.Title);
        Assert.NotNull(second.Title);
        Assert.Equal(new string('a', 4095), first.Title);
        Assert.Equal(new string('b', 4094), second.Title);
    }

    [Fact]
    public void CombinedValidationPropagatesCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.ThrowsAny<OperationCanceledException>(() => SvgResourceBudget.ValidateWithMetadata(
            Svg("<svg><title>Cancelled</title></svg>"),
            cancellation.Token,
            out _));
    }

    [Fact]
    public void ProviderPreparationCarriesMetadataOnlyWithTheExactPreparedArray()
    {
        using var renderer = new ResvgMarkdownSvgRenderer();
        MarkdownSvgSourcePreparation prepared = renderer.PrepareSource(
            Svg("<svg xmlns='http://www.w3.org/2000/svg' width='96' height='24'><title>Prepared</title><desc>Accessible</desc></svg>"),
            CancellationToken.None);
        byte[] admittedBytes = Assert.IsType<byte[]>(prepared.SanitizedBytes);

        Assert.True(prepared.TryGetPreparedMetadata(admittedBytes, out SvgSourceMetadata trusted));
        Assert.Equal("Prepared", trusted.Title);
        Assert.Equal("Accessible", trusted.Desc);

        byte[] equalCopy = (byte[])admittedBytes.Clone();
        Assert.Equal(admittedBytes, equalCopy);
        Assert.False(prepared.TryGetPreparedMetadata(equalCopy, out SvgSourceMetadata copied));
        Assert.Equal(default, copied);

        MarkdownSvgSourcePreparation genericAdmission = MarkdownSvgSourcePreparation.Admit(admittedBytes);
        Assert.False(genericAdmission.TryGetPreparedMetadata(admittedBytes, out SvgSourceMetadata fallback));
        Assert.Equal(default, fallback);
        Assert.Equal("Prepared", SvgSourceMetadata.Extract(admittedBytes, CancellationToken.None).Title);
    }
#endif
}
