using MarkdownRenderer.Document;
using MarkdownRenderer.Images;
using MarkdownRenderer.Layout;
using MarkdownRenderer.Layout.Boxes;
using MarkdownRenderer.Parsing;
using MarkdownRenderer.Theming;
using System.Numerics;
using Microsoft.Graphics.Canvas;
using Microsoft.UI.Xaml;
using Windows.Foundation;
using Windows.UI;
using Xunit;

namespace MarkdownRenderer.PixelTests;

/// <summary>
/// Exercises the document pipeline rather than treating provider pixels as
/// proof of what the user sees. No browser or replacement image painter is used
/// here: MarkdownEngine, the host resolver, ImageBox and LayoutSnapshot.Paint
/// feed the actual Win2D software device. All fixture bytes remain offline.
/// </summary>
internal static class SvgMarkdownRasterizer
{
    internal static async Task<byte[]> RasterizeAsync(
        byte[] source, int width, int height, IMarkdownSvgRenderer provider, double scale = 1)
    {
        int pixelWidth = checked((int)Math.Ceiling(width * scale));
        int pixelHeight = checked((int)Math.Ceiling(height * scale));
        using var engine = new MarkdownEngineBuilder().Build();
        const string markdown = "![](fixture.svg)";
        var document = await engine.ParseAsync(markdown);
        Assert.False(document.HasErrors);
        Assert.Single(document.GetImages());
        Assert.NotNull(document.ParsedDocument);

        // Every fixture has a separate resolver partition; the second build
        // deliberately shares it to test the real device-cache hit path.
        var resolver = new FixtureResolver(source);
        var renderer = new CountingRenderer(provider);
        CanvasDevice device = CanvasDevice.GetSharedDevice(forceSoftwareRenderer: true);
        Color transparent = Color.FromArgb(0, 0, 0, 0);
        var style = new ElementStyle { Foreground = Color.FromArgb(255, 0, 0, 0) };
        var theme = new ThemeSnapshot(
            new Dictionary<string, ElementStyle>
            {
                [MarkdownElementKeys.Body] = style,
                [MarkdownElementKeys.ImageCaption] = style,
            },
            new Dictionary<string, ElementStyleOverride>(),
            transparent, transparent, transparent, transparent,
            isDark: false, isHighContrast: false, textScaleFactor: 1);

        async Task<byte[]> PaintDocumentAsync()
        {
            var context = new MarkdownLayoutContext(
                device, theme, new MarkdownSourceMap(markdown),
                new MarkdownExtensionRegistry(), FlowDirection.LeftToRight,
                language: "en-US")
            {
                ImageResolver = resolver,
                SvgRenderer = renderer,
                RasterizationScale = (float)scale,
            };
            var snapshot = new LayoutBuilder(context, semanticDocument: document)
                .Build(document.ParsedDocument!, width);
            ImageBox? image = null;
            try
            {
                image = Assert.IsType<ImageBox>(Assert.Single(snapshot.Blocks));
                var loaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                EventHandler<LoadCompletedEventArgs> handler = (_, _) => loaded.TrySetResult();
                image.LoadCompleted += handler;
                try
                {
                    image.EnsureLoading();
                    await loaded.Task.WaitAsync(TimeSpan.FromSeconds(15));
                }
                finally
                {
                    image.LoadCompleted -= handler;
                }
                Assert.True(image.Bitmap is not null, $"{image.SvgFailureReason}: {image.SvgDesc}");
                Assert.Null(image.SvgFailureReason);
                snapshot.RelayoutMeasuredBlocks(width, CancellationToken.None);
                Assert.Equal(width, image.MeasuredImageWidth);
                Assert.Equal(height, image.MeasuredImageHeight);
                Assert.Equal((pixelWidth, pixelHeight), image.SvgRasterPixelSize);
                Rect imageViewport = image.AutomationRenderDestination;
                Assert.Equal(width, imageViewport.Width);
                Assert.Equal(height, imageViewport.Height);
                Assert.True(imageViewport.Left >= image.Bounds.Left &&
                    imageViewport.Top >= image.Bounds.Top &&
                    imageViewport.Right <= image.Bounds.Right &&
                    imageViewport.Bottom <= image.Bounds.Bottom,
                    "The complete image must fit inside its document block.");

                // CanvasRenderTarget rounds DIPs to the nearest pixel. Allocate
                // the complete ceil-sized image rectangle explicitly; document
                // layout and paint coordinates remain unchanged.
                using var target = new CanvasRenderTarget(device,
                    (float)(pixelWidth / scale), (float)(pixelHeight / scale), (float)(96 * scale));
                Assert.Equal((uint)pixelWidth, target.SizeInPixels.Width);
                Assert.Equal((uint)pixelHeight, target.SizeInPixels.Height);
                using (var drawing = target.CreateDrawingSession())
                {
                    drawing.Clear(transparent);
                    // Compare the whole image rectangle on both paths, not
                    // paragraph leading above a small image against SVG ink.
                    // Keep real document paint, sizing and clipping; never crop
                    // to only the overlapping nontransparent region.
                    drawing.Transform = Matrix3x2.CreateTranslation(
                        -(float)imageViewport.X, -(float)imageViewport.Y);
                    snapshot.Paint(drawing, imageViewport);
                }
                return PixelComparer.BgraPremulToRgba(target.GetPixelBytes(), pixelWidth, pixelHeight);
            }
            finally
            {
                snapshot.Dispose();
                if (image is not null)
                    await image.RetirementTask.WaitAsync(TimeSpan.FromSeconds(15));
            }
        }

        byte[] first = await PaintDocumentAsync();
        int opens = renderer.OpenCount;
        int renders = renderer.RenderCount;
        Assert.True(opens > 0 && renders > 0, "The uncached document must use the real provider.");
        byte[] cached = await PaintDocumentAsync();
        Assert.Equal(opens, renderer.OpenCount);
        Assert.Equal(renders, renderer.RenderCount);
        Assert.Equal(first, cached);
        return first;
    }

    private sealed class FixtureResolver(byte[] bytes) : IMarkdownImageResolver
    {
        private readonly string _partition = "svg-pixel-fixture:" + Guid.NewGuid().ToString("N");

        public ValueTask<MarkdownImageResolution> ResolveAsync(
            string source, MarkdownImageResolveContext context, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.Equal("fixture.svg", source);
            return ValueTask.FromResult(MarkdownImageResolution.Resolved(
                new MarkdownImageAsset(bytes, "image/svg+xml", CacheKey: _partition)));
        }
    }

    private sealed class CountingRenderer(IMarkdownSvgRenderer renderer)
        : IMarkdownSvgRenderer, IMarkdownSvgHostPreparationAdmission
    {
        // Keep the same progressive admission and priority-promotion path used
        // by the real provider. Instrumentation must not hide its interfaces
        // and silently exercise only the compatibility path.
        private readonly IMarkdownSvgHostPreparationAdmission _admission =
            Assert.IsAssignableFrom<IMarkdownSvgHostPreparationAdmission>(renderer);
        private int _opens;
        private int _renders;
        internal int OpenCount => Volatile.Read(ref _opens);
        internal int RenderCount => Volatile.Read(ref _renders);
        public long CacheGeneration => renderer.CacheGeneration;
        public object AdmissionDomain => _admission.AdmissionDomain;
        public long CapacityVersion => _admission.CapacityVersion;

        public Task WaitForCapacityAsync(long requiredFreeBytes,
            MarkdownSvgRenderPriority priority, CancellationToken cancellationToken)
            => _admission.WaitForCapacityAsync(requiredFreeBytes, priority, cancellationToken);

        public bool TryReserveHostPreparationScratch(MarkdownSvgDemandTicket demand,
            out IMarkdownSvgHostPreparationLease? lease, out long observedCapacityVersion,
            out long requiredFreeBytes)
            => _admission.TryReserveHostPreparationScratch(demand, out lease,
                out observedCapacityVersion, out requiredFreeBytes);

        public async ValueTask<MarkdownSvgHostOpenResult> OpenForHostAsync(
            MarkdownSvgOpenRequest request, MarkdownSvgDemandTicket demand,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _opens);
            MarkdownSvgHostOpenResult result = await _admission.OpenForHostAsync(
                request, demand, cancellationToken);
            return result.Status == MarkdownSvgHostOpenStatus.Opened
                ? MarkdownSvgHostOpenResult.Opened(new CountingDocument(result.Document!, this),
                    result.SourceReservationBytes, result.AdmissionDomain!)
                : result;
        }

        public MarkdownSvgSourcePreparation PrepareSource(byte[] source, CancellationToken cancellationToken)
            => renderer.PrepareSource(source, cancellationToken);

        public async ValueTask<IMarkdownSvgDocument> OpenAsync(
            MarkdownSvgOpenRequest request, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _opens);
            return new CountingDocument(await renderer.OpenAsync(request, cancellationToken), this);
        }

        private sealed class CountingDocument(IMarkdownSvgDocument document, CountingRenderer owner)
            : IMarkdownSvgDocument, IMarkdownSvgHostDocument
        {
            public MarkdownSvgDocumentInfo Info => document.Info;
            public ValueTask<MarkdownSvgRaster> RenderAsync(
                MarkdownSvgRenderRequest request, CancellationToken cancellationToken = default)
            {
                AssertPhysicalViewport(request);
                Interlocked.Increment(ref owner._renders);
                return document.RenderAsync(request, cancellationToken);
            }
            public ValueTask<MarkdownSvgRaster> RenderForHostAsync(
                MarkdownSvgRenderRequest request, MarkdownSvgDemandTicket demand,
                CancellationToken cancellationToken)
            {
                AssertPhysicalViewport(request);
                Interlocked.Increment(ref owner._renders);
                return Assert.IsAssignableFrom<IMarkdownSvgHostDocument>(document)
                    .RenderForHostAsync(request, demand, cancellationToken);
            }

            private static void AssertPhysicalViewport(MarkdownSvgRenderRequest request)
            {
                var viewport = Assert.IsType<MarkdownSvgPhysicalViewport>(request.PhysicalViewport);
                Assert.Equal(request.TargetWidthPixels, Math.Ceiling(viewport.Width));
                Assert.Equal(request.TargetHeightPixels, Math.Ceiling(viewport.Height));
            }
            public void Dispose() => document.Dispose();
        }
    }
}
