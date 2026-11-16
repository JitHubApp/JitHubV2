using MarkdownRenderer.GitHub.Tests.Performance;
using MarkdownRenderer.GitHub.Tests.TestSupport.Fakes;
using MarkdownRenderer.Images.Svg;
using MarkdownRenderer.Layout.Internal.Images;
using MarkdownRenderer.Layout.Internal.RenderResources;
using Xunit;
using static MarkdownRenderer.GitHub.Tests.TestSupport.Fixtures.ImageBoxSvgRendererFixture;

namespace MarkdownRenderer.GitHub.Tests.Layout.Internal.Images.Svg;

/// <summary>
/// Checks the SVG device bitmap cache: exact hits skip opening and
/// rendering, entries stay scoped to their renderer and provider, and
/// cache generations invalidate only text SVG rasters.
/// </summary>
[Collection(ProcessRasterDecodeCollection.CollectionName)]
public sealed class ImageBoxSvgDeviceCacheTests
{
    private readonly MarkdownRenderResources _renderResources = new();

    [Fact]
    public async Task ExactDeviceCacheHitDoesNotOpenOrRenderTheSvgAgain()
    {
        var renderer = new RecordingSvgRenderer(new MarkdownSvgDocumentInfo(
            IntrinsicWidthDips: 80,
            IntrinsicHeightDips: 40,
            IntrinsicAspectRatio: 2));
        string source = CreateSvgDataUri(Guid.NewGuid().ToString("N"));

        var first = new ImageBox(CreateContext(_renderResources, renderer), _renderResources.Images, source, "cached");
        first.Measure(80);
        await WaitForLoadAsync(first);
        first.Dispose();

        int opensAfterFirstRender = renderer.OpenCount;
        int rendersAfterFirstRender = renderer.RenderRequests.Count;
        var second = new ImageBox(CreateContext(_renderResources, renderer), _renderResources.Images, source, "cached");
        try
        {
            second.Measure(80);
            await WaitForLoadAsync(second);

            Assert.NotNull(second.Bitmap);
            Assert.Equal(opensAfterFirstRender, renderer.OpenCount);
            Assert.Equal(rendersAfterFirstRender, renderer.RenderRequests.Count);
        }
        finally
        {
            second.Dispose();
        }
    }

    [Fact]
    public async Task DeviceCacheDoesNotCrossRendererInstances()
    {
        var info = new MarkdownSvgDocumentInfo(
            IntrinsicWidthDips: 80,
            IntrinsicHeightDips: 40,
            IntrinsicAspectRatio: 2);
        var firstRenderer = new RecordingSvgRenderer(info);
        var replacementRenderer = new RecordingSvgRenderer(info);
        string source = CreateSvgDataUri(Guid.NewGuid().ToString("N"));

        var first = new ImageBox(CreateContext(_renderResources, firstRenderer), _renderResources.Images, source, "provider swap");
        first.Measure(80);
        await WaitForLoadAsync(first);
        first.Dispose();

        var replacement = new ImageBox(
            CreateContext(_renderResources, replacementRenderer), _renderResources.Images,
            source,
            "provider swap");
        try
        {
            replacement.Measure(80);
            await WaitForLoadAsync(replacement);

            Assert.Equal(1, replacementRenderer.OpenCount);
            Assert.Single(replacementRenderer.RenderRequests);
            Assert.NotNull(replacement.Bitmap);
        }
        finally
        {
            replacement.Dispose();
        }
    }

    [Fact]
    public async Task DeviceCacheIsNotUsedAfterProviderIsRemoved()
    {
        var renderer = new RecordingSvgRenderer(new MarkdownSvgDocumentInfo(
            IntrinsicWidthDips: 80,
            IntrinsicHeightDips: 40,
            IntrinsicAspectRatio: 2));
        string source = CreateSvgDataUri(Guid.NewGuid().ToString("N"));

        var first = new ImageBox(CreateContext(_renderResources, renderer), _renderResources.Images, source, "provider removed");
        first.Measure(80);
        await WaitForLoadAsync(first);
        first.Dispose();

        var withoutProvider = new ImageBox(CreateContext(_renderResources, renderer: null), _renderResources.Images, source, "provider removed");
        try
        {
            withoutProvider.Measure(80);
            await WaitForLoadAsync(withoutProvider);

            Assert.Null(withoutProvider.Bitmap);
            Assert.Equal(
                MarkdownSvgFailureReason.ArchitectureMismatch,
                withoutProvider.SvgFailureReason);
        }
        finally
        {
            withoutProvider.Dispose();
        }
    }

    [Fact]
    public async Task CacheGenerationDoesNotInvalidateNonTextSvgRaster()
    {
        var renderer = new RecordingSvgRenderer(new MarkdownSvgDocumentInfo(
            IntrinsicWidthDips: 80,
            IntrinsicHeightDips: 40,
            IntrinsicAspectRatio: 2,
            HasText: false));
        string source = CreateSvgDataUri(Guid.NewGuid().ToString("N"));

        var first = new ImageBox(CreateContext(_renderResources, renderer), _renderResources.Images, source, "generation");
        first.Measure(80);
        await WaitForLoadAsync(first);
        first.Dispose();

        int opensBeforeInvalidation = renderer.OpenCount;
        int rendersBeforeInvalidation = renderer.RenderRequests.Count;
        renderer.AdvanceGeneration();

        var replacement = new ImageBox(CreateContext(_renderResources, renderer), _renderResources.Images, source, "generation");
        try
        {
            replacement.Measure(80);
            await WaitForLoadAsync(replacement);

            Assert.Equal(opensBeforeInvalidation, renderer.OpenCount);
            Assert.Equal(rendersBeforeInvalidation, renderer.RenderRequests.Count);
            Assert.NotNull(replacement.Bitmap);
        }
        finally
        {
            replacement.Dispose();
        }
    }

    [Fact]
    public async Task CacheGenerationInvalidatesTextSvgRaster()
    {
        var renderer = new RecordingSvgRenderer(new MarkdownSvgDocumentInfo(
            IntrinsicWidthDips: 80,
            IntrinsicHeightDips: 40,
            IntrinsicAspectRatio: 2,
            HasText: true));
        string source = CreateSvgDataUri(Guid.NewGuid().ToString("N"));

        var first = new ImageBox(CreateContext(_renderResources, renderer), _renderResources.Images, source, "generation");
        first.Measure(80);
        await WaitForLoadAsync(first);
        first.Dispose();

        int opensBeforeInvalidation = renderer.OpenCount;
        int rendersBeforeInvalidation = renderer.RenderRequests.Count;
        renderer.AdvanceGeneration();

        var replacement = new ImageBox(CreateContext(_renderResources, renderer), _renderResources.Images, source, "generation");
        try
        {
            replacement.Measure(80);
            await WaitForLoadAsync(replacement);

            Assert.True(renderer.OpenCount > opensBeforeInvalidation);
            Assert.True(renderer.RenderRequests.Count > rendersBeforeInvalidation);
            Assert.NotNull(replacement.Bitmap);
        }
        finally
        {
            replacement.Dispose();
        }
    }
}
