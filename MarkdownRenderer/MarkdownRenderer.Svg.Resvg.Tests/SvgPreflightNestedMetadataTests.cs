using System.Text;
using MarkdownRenderer.Images;
using MarkdownRenderer.Svg.Resvg.Internal;
using Xunit;

namespace MarkdownRenderer.Svg.Resvg.Tests;

public sealed class SvgPreflightNestedMetadataTests
{
    private const string SvgNamespace = "http://www.w3.org/2000/svg";

    [Theory]
    [InlineData("image")]
    [InlineData("css")]
    [InlineData("feImage")]
    [InlineData("multilevel")]
    public async Task InspectCore_PropagatesTextFromNestedSvgResources(string referenceKind)
    {
        string textSvg = Svg("<text x='1' y='8'>nested</text>");
        string nestedDataUri = DataUri(textSvg);
        string secondLevelSvg = Svg($"<image href='{nestedDataUri}'/>");
        string contents = referenceKind switch
        {
            "image" => $"<image href='{nestedDataUri}'/>",
            "css" => "<style>.nested{fill:url('" + nestedDataUri + "')}</style><rect class='nested'/>",
            "feImage" => $"<filter id='nested'><feImage href='{nestedDataUri}'/></filter><rect filter='url(#nested)'/>",
            "multilevel" => $"<image href='{DataUri(secondLevelSvg)}'/>",
            _ => throw new ArgumentOutOfRangeException(nameof(referenceKind)),
        };

        SvgPreflightResult result = await InspectAsync(contents);

        Assert.True(result.Info.HasText);
    }

    [Fact]
    public async Task InspectCore_LeavesNonTextNestedSvgOutOfFontCatalogPath()
    {
        string nonTextSvg = Svg("<rect width='2' height='2'/><path d='M0 0L2 2'/>");
        SvgPreflightResult result = await InspectAsync($"<image href='{DataUri(nonTextSvg)}'/>");

        Assert.False(result.Info.HasText);
    }

    [Fact]
    public async Task InspectCore_UsesLocalNamesForOuterAndNestedTextMetadata()
    {
        SvgPreflightResult outer = await InspectSourceAsync(
            $"<svg xmlns='{SvgNamespace}' xmlns:f='urn:foreign'><f:text>outer</f:text></svg>");
        string nestedSvg = Svg("<f:text xmlns:f='urn:foreign'>nested</f:text>");
        SvgPreflightResult nested = await InspectAsync($"<image href='{DataUri(nestedSvg)}'/>");

        Assert.True(outer.Info.HasText);
        Assert.True(nested.Info.HasText);
    }

    [Fact]
    public async Task InspectCore_TraversesForeignNamespaceNestedResourceWrappersButRejectsExternalReferences()
    {
        string textSvg = Svg("<text>nested through foreign wrapper</text>");
        string nestedSvg = Svg(
            $"<f:image xmlns:f='urn:foreign' href='{DataUri(textSvg)}'/>");
        SvgPreflightResult result = await InspectAsync($"<image href='{DataUri(nestedSvg)}'/>");
        Assert.True(result.Info.HasText);

        string externalSvg = Svg(
            "<f:image xmlns:f='urn:foreign' href='https://example.test/nested.svg'/>");
        MarkdownSvgException failure = await Assert.ThrowsAsync<MarkdownSvgException>(
            () => InspectAsync($"<image href='{DataUri(externalSvg)}'/>"));
        Assert.Equal(MarkdownSvgFailureReason.UnsupportedContent, failure.Reason);
    }

    [Fact]
    public async Task InspectCore_PropagatesNestedCurrentColorAndColorSchemeDependencies()
    {
        string nestedSvg = Svg(
            "<style>@media (prefers-color-scheme: dark) { .shape { fill: currentColor; } }</style>" +
            "<rect class='shape'/>");

        MarkdownSvgDocumentInfo info = (await InspectAsync($"<image href='{DataUri(nestedSvg)}'/>")).Info;

        Assert.True(info.UsesCurrentColor);
        Assert.True(info.UsesColorScheme);
    }

    [Fact]
    public async Task InspectCore_OnlyReportsSemanticallyUsedNestedThemeDependencies()
    {
        string nestedSvg = Svg(
            "<g aria-label='currentColor prefers-color-scheme'/>" +
            "<style>/* @media (prefers-color-scheme: dark) { fill: currentColor; } */" +
            ".currentColor { fill: red; }</style>");

        MarkdownSvgDocumentInfo info = (await InspectAsync($"<image href='{DataUri(nestedSvg)}'/>")).Info;

        Assert.False(info.UsesCurrentColor);
        Assert.False(info.UsesColorScheme);
    }

    [Fact]
    public async Task InspectCore_DeduplicatesNestedResourceAccountingAndStillKeepsItsMetadata()
    {
        byte[] nestedBytes = Encoding.UTF8.GetBytes(Svg("<text>once</text>"));
        string nestedDataUri = DataUri(nestedBytes);

        SvgPreflightResult result = await InspectAsync(
            $"<image href='{nestedDataUri}'/><feImage href='{nestedDataUri}'/>");

        Assert.True(result.Info.HasText);
        Assert.Equal(nestedBytes.Length, result.EmbeddedBytes);
    }

    [Fact]
    public async Task InspectCore_PreservesNestedSvgDepthAndExternalReferenceRejections()
    {
        var options = new ResvgMarkdownSvgRendererOptions();
        string nestedSvg = Svg("<text>within-limit</text>");
        // The input document is level one, so only max-1 embedded SVG documents
        // fit in the worker's nested-reference budget.
        for (int depth = 1; depth < options.MaxNestedSvgDepth - 1; depth++)
            nestedSvg = Svg($"<image href='{DataUri(nestedSvg)}'/>");

        SvgPreflightResult withinLimit = await InspectAsync($"<image href='{DataUri(nestedSvg)}'/>", options);
        Assert.True(withinLimit.Info.HasText);

        string overLimitSvg = Svg($"<image href='{DataUri(nestedSvg)}'/>");
        MarkdownSvgException depthFailure = await Assert.ThrowsAsync<MarkdownSvgException>(
            () => InspectAsync($"<image href='{DataUri(overLimitSvg)}'/>", options));
        Assert.Equal(MarkdownSvgFailureReason.ResourceLimitExceeded, depthFailure.Reason);

        string externalReferenceSvg = Svg("<image href='https://example.test/nested.svg'/>");
        MarkdownSvgException referenceFailure = await Assert.ThrowsAsync<MarkdownSvgException>(
            () => InspectAsync($"<image href='{DataUri(externalReferenceSvg)}'/>", options));
        Assert.Equal(MarkdownSvgFailureReason.UnsupportedContent, referenceFailure.Reason);
    }

    [Fact]
    public async Task InspectCore_RevalidatesSharedSvgAtItsDeeperContext()
    {
        var options = new ResvgMarkdownSvgRendererOptions();
        string leafSvg = Svg("<rect width='1' height='1'/>");
        string sharedSvg = Svg($"<image href='{DataUri(leafSvg)}'/>");
        string firstWrapper = Svg($"<image href='{DataUri(sharedSvg)}'/>");
        string secondWrapper = Svg($"<image href='{DataUri(firstWrapper)}'/>");

        // The direct path visits sharedSvg at embedded depth one. The second
        // path reaches it at depth three, where its child exceeds max depth four.
        MarkdownSvgException failure = await Assert.ThrowsAsync<MarkdownSvgException>(
            () => InspectAsync(
                $"<image href='{DataUri(sharedSvg)}'/><image href='{DataUri(secondWrapper)}'/>",
                options));

        Assert.Equal(MarkdownSvgFailureReason.ResourceLimitExceeded, failure.Reason);
    }

    private static Task<SvgPreflightResult> InspectAsync(
        string contents,
        ResvgMarkdownSvgRendererOptions? options = null) =>
        InspectSourceAsync(Svg(contents), options);

    private static Task<SvgPreflightResult> InspectSourceAsync(
        string source,
        ResvgMarkdownSvgRendererOptions? options = null) =>
        SvgPreflight.InspectCoreAsync(
            Encoding.UTF8.GetBytes(source),
            options ?? new ResvgMarkdownSvgRendererOptions(),
            CancellationToken.None);

    private static string Svg(string contents) =>
        $"<svg xmlns='{SvgNamespace}'>{contents}</svg>";

    private static string DataUri(string svg) => DataUri(Encoding.UTF8.GetBytes(svg));

    private static string DataUri(byte[] svgBytes) =>
        "data:image/svg+xml;base64," + Convert.ToBase64String(svgBytes);
}
