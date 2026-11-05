using System.Text;
using MarkdownRenderer.Images;
using MarkdownRenderer.Svg.Resvg.Internal;
using Xunit;

namespace MarkdownRenderer.Svg.Resvg.Tests;

/// <summary>
/// Pins when an open document's worker tree is kept or reattached (font
/// generation rotation, sequential tiles, cache trims) and that disposal
/// never lets a render read a retired source.
/// </summary>
[Collection(WorkerDeadlineSensitiveCollection.Name)]
public sealed class ResvgDocumentReattachmentTests
{
    [Fact]
    public void FontGeneration_IsPublishedBeforeAllInvalidationSubscribersRun()
    {
        using var renderer = new ResvgMarkdownSvgRenderer();
        long initial = renderer.CacheGeneration;
        long observed = -1;
        renderer.CacheInvalidated += (_, _) => throw new InvalidOperationException("subscriber isolation");
        renderer.CacheInvalidated += (_, _) => observed = renderer.CacheGeneration;

        renderer.NotifyFontsChanged();

        Assert.Equal(initial + 1, renderer.CacheGeneration);
        Assert.Equal(renderer.CacheGeneration, observed);
    }

    [Fact]
    public async Task FontGenerationRotation_NeverQuarantinesValidContent()
    {
        string text = string.Concat(Enumerable.Range(0, 1_000).Select(index =>
            $"<text x='{index % 100}' y='{1 + (index / 100)}' font-size='1'>A</text>"));
        byte[] source = Svg($"<svg xmlns='http://www.w3.org/2000/svg' width='100' height='12'>{text}</svg>");
        await using var renderer = new ResvgMarkdownSvgRenderer();
        using IMarkdownSvgDocument document = await renderer.OpenAsync(new MarkdownSvgOpenRequest(source));
        Task<MarkdownSvgRaster> active = document.RenderAsync(new MarkdownSvgRenderRequest(2_000, 240)).AsTask();
        await Task.Yield();
        renderer.NotifyFontsChanged();
        try
        {
            using MarkdownSvgRaster completed = await active;
        }
        catch (MarkdownSvgException failure)
        {
            Assert.Equal(MarkdownSvgFailureReason.Canceled, failure.Reason);
        }

        using MarkdownSvgRaster replacement = await document.RenderAsync(new MarkdownSvgRenderRequest(200, 24));
        Assert.True(ProviderTestRasters.HasVisiblePixel(replacement.Pixels.Span));
    }

    [Fact]
    public async Task FontGenerationRotationReattachesTextTreeButKeepsGeometryTreeAttached()
    {
        var options = new ResvgMarkdownSvgRendererOptions();
        var launcher = new RecordingWorkerLauncher(options);
        await using var renderer = new ResvgMarkdownSvgRenderer(options, SvgSnapshotAdmission.CreateDefault(), launcher);
        using IMarkdownSvgDocument textDocument = await renderer.OpenAsync(new MarkdownSvgOpenRequest(Svg(
            "<svg xmlns='http://www.w3.org/2000/svg' width='96' height='24'><text x='2' y='18' font-size='16'>Font generation</text></svg>")));
        using IMarkdownSvgDocument geometryDocument = await renderer.OpenAsync(new MarkdownSvgOpenRequest(Svg(
            "<svg xmlns='http://www.w3.org/2000/svg' width='24' height='24'><circle cx='12' cy='12' r='10' fill='#5288e8'/></svg>")));
        long attachmentsBeforeRotation = renderer.SourceAttachmentCount;
        Assert.Equal(2, attachmentsBeforeRotation);
        Assert.Equal(renderer.CacheGeneration, PrimaryPublishedFontGeneration());

        renderer.NotifyFontsChanged();
        Assert.True(PrimaryPublishedFontGeneration() < renderer.CacheGeneration);

        using MarkdownSvgRaster textRaster = await textDocument.RenderAsync(new MarkdownSvgRenderRequest(96, 24));
        Assert.True(ProviderTestRasters.HasVisiblePixel(textRaster.Pixels.Span));
        Assert.Equal(attachmentsBeforeRotation + 1, renderer.SourceAttachmentCount);
        Assert.Equal(renderer.CacheGeneration, PrimaryPublishedFontGeneration());

        using MarkdownSvgRaster geometryRaster = await geometryDocument.RenderAsync(new MarkdownSvgRenderRequest(24, 24));
        Assert.True(ProviderTestRasters.HasVisiblePixel(geometryRaster.Pixels.Span));
        Assert.Equal(attachmentsBeforeRotation + 1, renderer.SourceAttachmentCount);

        // The primary slot always starts the first worker; nothing here
        // invalidates it, so it is still the primary.
        long PrimaryPublishedFontGeneration() => launcher.Started.First().ReadyFontGeneration;
    }

    [Fact]
    public async Task TiledFilterUsesParameterDerivedGutterAndRejectsUnboundedFilter()
    {
        const string bounded = "<svg xmlns='http://www.w3.org/2000/svg' width='128' height='64'><defs><filter id='f' filterUnits='userSpaceOnUse' x='0' y='0' width='128' height='64'><feGaussianBlur stdDeviation='6'/></filter></defs><rect x='42' y='12' width='40' height='40' fill='#2864dc' filter='url(#f)'/></svg>";
        await using var renderer = new ResvgMarkdownSvgRenderer();
        using IMarkdownSvgDocument document = await renderer.OpenAsync(new MarkdownSvgOpenRequest(Svg(bounded)));
        using MarkdownSvgRaster full = await document.RenderAsync(new MarkdownSvgRenderRequest(128, 64));
        using MarkdownSvgRaster tile = await document.RenderAsync(new MarkdownSvgRenderRequest(
            128,
            64,
            new MarkdownSvgTileRegion(48, 0, 32, 64)));
        ProviderTestRasters.AssertTileMatchesCrop(full.Pixels.Span, tile.Pixels.Span, 128, 48, 0, 32, 64);

        const string unbounded = "<svg xmlns='http://www.w3.org/2000/svg' width='16' height='16'><filter id='f'><feTurbulence/><feTile/></filter><rect width='16' height='16' filter='url(#f)'/></svg>";
        using IMarkdownSvgDocument unboundedDocument = await renderer.OpenAsync(new MarkdownSvgOpenRequest(Svg(unbounded)));
        MarkdownSvgException failure = await Assert.ThrowsAsync<MarkdownSvgException>(async () =>
            await unboundedDocument.RenderAsync(new MarkdownSvgRenderRequest(
                16,
                16,
                new MarkdownSvgTileRegion(0, 0, 8, 8))));
        Assert.Equal(MarkdownSvgFailureReason.UnsupportedContent, failure.Reason);
    }

    [Fact]
    public async Task SequentialTilesReuseAttachedDocumentWithoutRepeatedSourceCopies()
    {
        await using var renderer = new ResvgMarkdownSvgRenderer();
        using IMarkdownSvgDocument document = await renderer.OpenAsync(new MarkdownSvgOpenRequest(Svg(
            "<svg xmlns='http://www.w3.org/2000/svg' width='64' height='64'><circle cx='32' cy='32' r='30' fill='#5288e8'/></svg>")));
        long attachmentsAfterOpen = renderer.SourceAttachmentCount;
        Assert.Equal(1, attachmentsAfterOpen);

        for (int index = 0; index < 8; index++)
        {
            using MarkdownSvgRaster tile = await document.RenderAsync(new MarkdownSvgRenderRequest(
                64,
                64,
                new MarkdownSvgTileRegion((index % 4) * 16, (index / 4) * 16, 16, 16)));
            Assert.Equal(16, tile.WidthPixels);
        }

        Assert.Equal(attachmentsAfterOpen, renderer.SourceAttachmentCount);
    }

    [Fact]
    public async Task TrimForcesDocumentReattachAndConcurrentTrimsCannotBreakRenderTransactions()
    {
        await using var renderer = new ResvgMarkdownSvgRenderer();
        string shapes = string.Concat(Enumerable.Range(0, 128).Select(index =>
            $"<rect x='{index % 16}' y='{index / 16}' width='1' height='1' fill='rgb({index % 255},80,180)'/>"));
        using IMarkdownSvgDocument document = await renderer.OpenAsync(new MarkdownSvgOpenRequest(Svg(
            $"<svg xmlns='http://www.w3.org/2000/svg' width='16' height='8'>{shapes}</svg>")));
        long attachmentsAfterOpen = renderer.SourceAttachmentCount;

        // Trim removes worker-side document bindings; the next render must
        // take the real DocumentMissing -> OPEN -> retry path.
        await renderer.TrimCachesAsync();
        using (MarkdownSvgRaster reattached = await document.RenderAsync(new MarkdownSvgRenderRequest(16, 8)))
            Assert.True(ProviderTestRasters.HasVisiblePixel(reattached.Pixels.Span));
        Assert.Equal(attachmentsAfterOpen + 1, renderer.SourceAttachmentCount);

        // Start render transactions and trims together. The worker-local gate
        // must keep TrimCache from landing between a missing-document probe,
        // source reattachment, and its retry render.
        await renderer.TrimCachesAsync();
        Task<MarkdownSvgRaster>[] renders = Enumerable.Range(0, 6)
            .Select(_ => document.RenderAsync(new MarkdownSvgRenderRequest(16, 8)).AsTask())
            .ToArray();
        Task[] trims = Enumerable.Range(0, 4)
            .Select(_ => renderer.TrimCachesAsync())
            .ToArray();

        try
        {
            await Task.WhenAll(renders.Cast<Task>().Concat(trims));
            foreach (Task<MarkdownSvgRaster> render in renders)
                Assert.True(ProviderTestRasters.HasVisiblePixel((await render).Pixels.Span));
        }
        finally
        {
            foreach (Task<MarkdownSvgRaster> render in renders)
                if (render.Status == TaskStatus.RanToCompletion)
                    (await render).Dispose();
        }

        Assert.True(renderer.SourceAttachmentCount >= attachmentsAfterOpen + 2);
    }

    [Fact]
    public async Task DisposeBeforeAndDuringRenderNeverReadsDisposedLifetimeSource()
    {
        await using var renderer = new ResvgMarkdownSvgRenderer();
        IMarkdownSvgDocument disposed = await renderer.OpenAsync(new MarkdownSvgOpenRequest(Svg(
            "<svg xmlns='http://www.w3.org/2000/svg' width='4' height='4'><rect width='4' height='4'/></svg>")));
        disposed.Dispose();
        MarkdownSvgException disposedFailure = await Assert.ThrowsAsync<MarkdownSvgException>(async () =>
            await disposed.RenderAsync(new MarkdownSvgRenderRequest(4, 4)));
        Assert.Equal(MarkdownSvgFailureReason.Canceled, disposedFailure.Reason);

        string shapes = string.Concat(Enumerable.Range(0, 5_000).Select(index =>
            $"<circle cx='{index % 128}' cy='{index / 40}' r='2'/>") );
        IMarkdownSvgDocument activeDocument = await renderer.OpenAsync(new MarkdownSvgOpenRequest(Svg(
            $"<svg xmlns='http://www.w3.org/2000/svg' width='128' height='128'>{shapes}</svg>")));
        Task<MarkdownSvgRaster>[] active = Enumerable.Range(0, 4)
            .Select(_ => activeDocument.RenderAsync(new MarkdownSvgRenderRequest(1024, 1024)).AsTask())
            .ToArray();
        activeDocument.Dispose();
        foreach (Task<MarkdownSvgRaster> render in active)
        {
            try
            {
                using MarkdownSvgRaster raster = await render;
            }
            catch (MarkdownSvgException failure)
            {
                Assert.Equal(MarkdownSvgFailureReason.Canceled, failure.Reason);
            }
        }
    }

    private static byte[] Svg(string value) => Encoding.UTF8.GetBytes(value);
}
