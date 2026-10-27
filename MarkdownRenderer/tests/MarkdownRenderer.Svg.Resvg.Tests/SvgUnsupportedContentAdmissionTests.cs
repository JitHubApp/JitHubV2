using System.Text;
using MarkdownRenderer.Images;
using Xunit;

namespace MarkdownRenderer.Svg.Resvg.Tests;

public sealed class SvgUnsupportedContentAdmissionTests
{
    private const string SvgNamespace = "http://www.w3.org/2000/svg";

    [Theory]
    [InlineData("<script>alert(1)</script><rect width='180' height='60' fill='red'/>", "active-content")]
    [InlineData("<circle cx='30' cy='30' r='20'><animate attributeName='cx' to='150' dur='1s'/></circle>", "active-content")]
    [InlineData("<style>@keyframes pulse { from { opacity: 0 } to { opacity: 1 } }</style><rect width='180' height='60'/>", "active-content")]
    [InlineData("<style>.pulse { animation: pulse 1s; }</style><rect class='pulse' width='180' height='60'/>", "active-content")]
    [InlineData("<foreignObject width='180' height='60'><div xmlns='http://www.w3.org/1999/xhtml'>label</div></foreignObject><rect width='180' height='60' fill='red'/>", "active-content")]
    [InlineData("<rect width='180' height='60' fill='red' onclick='alert(1)'/>", "event-handler")]
    public void PrepareSourceRejectsOriginalUnsupportedContentBeforeSnapshotNormalization(
        string markup,
        string expectedPreflightReason)
    {
        byte[] source = Svg($"<svg xmlns='{SvgNamespace}' width='180' height='60'>{markup}</svg>");
        byte[] original = (byte[])source.Clone();
        using var renderer = new ResvgMarkdownSvgRenderer();

        MarkdownSvgSourcePreparation preparation = renderer.PrepareSource(
            source,
            CancellationToken.None);

        Assert.Null(preparation.SanitizedBytes);
        Assert.Equal(MarkdownSvgFailureReason.UnsupportedContent, preparation.FailureReason);
        Assert.Contains(expectedPreflightReason, preparation.FailureDescription, StringComparison.Ordinal);
        Assert.Equal(original, source);
        Assert.Equal(0, renderer.SourceAttachmentCount);
    }

    [Fact]
    public void PrepareSourceStillAdmitsAStaticNestedSvgDataUriClosureUnchanged()
    {
        byte[] nested = Svg($"<svg xmlns='{SvgNamespace}' width='1' height='1'><rect width='1' height='1' fill='blue'/></svg>");
        string dataUri = "data:image/svg+xml;base64," + Convert.ToBase64String(nested);
        byte[] source = Svg(
            $"<svg xmlns='{SvgNamespace}' width='1' height='1'><image width='1' height='1' href='{dataUri}'/></svg>");
        using var renderer = new ResvgMarkdownSvgRenderer();

        MarkdownSvgSourcePreparation preparation = renderer.PrepareSource(
            source,
            CancellationToken.None);

        Assert.Null(preparation.FailureReason);
        Assert.Same(source, preparation.SanitizedBytes);
    }

    [Fact]
    public void PrepareSourceRejectsForbiddenContentInsideNestedSvgBeforeNormalization()
    {
        byte[] nested = Svg(
            $"<svg xmlns='{SvgNamespace}' width='1' height='1'><script>alert(1)</script><rect width='1' height='1' fill='red'/></svg>");
        string dataUri = "data:image/svg+xml;base64," + Convert.ToBase64String(nested);
        byte[] source = Svg(
            $"<svg xmlns='{SvgNamespace}' width='1' height='1'><image width='1' height='1' href='{dataUri}'/></svg>");
        using var renderer = new ResvgMarkdownSvgRenderer();

        MarkdownSvgSourcePreparation preparation = renderer.PrepareSource(
            source,
            CancellationToken.None);

        Assert.Null(preparation.SanitizedBytes);
        Assert.Equal(MarkdownSvgFailureReason.UnsupportedContent, preparation.FailureReason);
        Assert.Contains("active-content", preparation.FailureDescription, StringComparison.Ordinal);
        Assert.Equal(0, renderer.SourceAttachmentCount);
    }

    [Fact]
    public async Task OpenAsyncRejectsTopLevelExternalDoctypeBeforeWorkerAttachment()
    {
        byte[] source = Svg(
            "<!DOCTYPE svg PUBLIC '-//W3C//DTD SVG 1.1//EN' " +
            "'http://www.w3.org/Graphics/SVG/1.1/DTD/svg11.dtd'>" +
            $"<svg xmlns='{SvgNamespace}' width='1' height='1'><rect width='1' height='1'/></svg>");
        byte[] original = (byte[])source.Clone();
        await using var renderer = new ResvgMarkdownSvgRenderer();

        MarkdownSvgException exception = await Assert.ThrowsAsync<MarkdownSvgException>(
            async () => await renderer.OpenAsync(new MarkdownSvgOpenRequest(source)));

        Assert.Equal(MarkdownSvgFailureReason.UnsupportedContent, exception.Reason);
        Assert.Contains("DTD", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(original, source);
        Assert.Equal(0, renderer.SourceAttachmentCount);
    }

    [Fact]
    public async Task OpenAsyncRejectsExternalDoctypeInsideNestedSvgBeforeWorkerAttachment()
    {
        byte[] nested = Svg(
            "<!DOCTYPE svg SYSTEM 'https://example.invalid/svg11.dtd'>" +
            $"<svg xmlns='{SvgNamespace}' width='1' height='1'><rect width='1' height='1'/></svg>");
        string dataUri = "data:image/svg+xml;base64," + Convert.ToBase64String(nested);
        byte[] source = Svg(
            $"<svg xmlns='{SvgNamespace}' width='1' height='1'><image width='1' height='1' href='{dataUri}'/></svg>");
        byte[] original = (byte[])source.Clone();
        await using var renderer = new ResvgMarkdownSvgRenderer();

        MarkdownSvgException exception = await Assert.ThrowsAsync<MarkdownSvgException>(
            async () => await renderer.OpenAsync(new MarkdownSvgOpenRequest(source)));

        Assert.Equal(MarkdownSvgFailureReason.UnsupportedContent, exception.Reason);
        Assert.Contains("DTD", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(original, source);
        Assert.Equal(0, renderer.SourceAttachmentCount);
    }

    [Theory]
    [InlineData("<script>alert(1)</script>", "static SVG subset")]
    [InlineData("<foreignObject width='1' height='1'><div xmlns='http://www.w3.org/1999/xhtml'>text</div></foreignObject>", "static SVG subset")]
    [InlineData("<animate attributeName='x' to='1' dur='1s'/>", "static SVG subset")]
    [InlineData("<rect width='1' height='1' onload='alert(1)'/>", "event handlers")]
    public async Task OpenAsyncRejectsActiveContentInsideNestedSvgBeforeWorkerAttachment(
        string nestedMarkup,
        string expectedDescription)
    {
        byte[] source = SvgWithNestedDataSvg(nestedMarkup);
        await using var renderer = new ResvgMarkdownSvgRenderer();

        MarkdownSvgException exception = await Assert.ThrowsAsync<MarkdownSvgException>(
            async () => await renderer.OpenAsync(new MarkdownSvgOpenRequest(source)));

        Assert.Equal(MarkdownSvgFailureReason.UnsupportedContent, exception.Reason);
        Assert.Contains(expectedDescription, exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, renderer.SourceAttachmentCount);
    }

    [Fact]
    public async Task OpenAsyncStillAdmitsAndRendersAStaticNestedSvgDataUri()
    {
        byte[] source = SvgWithNestedDataSvg("<rect width='1' height='1' fill='blue'/>");
        await using var renderer = new ResvgMarkdownSvgRenderer();

        using IMarkdownSvgDocument document = await renderer.OpenAsync(new MarkdownSvgOpenRequest(source));
        using MarkdownSvgRaster raster = await document.RenderAsync(new MarkdownSvgRenderRequest(1, 1));

        Assert.Equal(1, raster.WidthPixels);
        Assert.Equal(1, raster.HeightPixels);
        Assert.NotEqual(0, raster.Pixels.Span[3]);
    }

    private static byte[] SvgWithNestedDataSvg(string nestedMarkup)
    {
        byte[] nested = Svg(
            $"<svg xmlns='{SvgNamespace}' width='1' height='1'>{nestedMarkup}</svg>");
        string dataUri = "data:image/svg+xml;base64," + Convert.ToBase64String(nested);
        return Svg(
            $"<svg xmlns='{SvgNamespace}' width='1' height='1'><image width='1' height='1' href='{dataUri}'/></svg>");
    }

    private static byte[] Svg(string source) => Encoding.UTF8.GetBytes(source);
}
