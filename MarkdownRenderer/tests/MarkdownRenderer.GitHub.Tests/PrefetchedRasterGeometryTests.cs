using MarkdownRenderer.Images;
using MarkdownRenderer.Layout.Boxes;
using MarkdownRenderer.Parsing;
using MarkdownRenderer.Performance;
using Windows.Foundation;
using Xunit;

namespace MarkdownRenderer.GitHub.Tests;

[Collection(SharedCanvasBitmapCacheTestCollection.Name)]
public sealed class PrefetchedRasterGeometryTests
{
    private const string Source = "https://images.example/prefetched-geometry.png";

    [Theory]
    [InlineData(false, 1.0)]
    [InlineData(false, 1.25)]
    [InlineData(false, 1.5)]
    [InlineData(false, 1.75)]
    [InlineData(false, 2.0)]
    [InlineData(false, 2.25)]
    [InlineData(false, 3.0)]
    [InlineData(false, 4.0)]
    [InlineData(true, 1.0)]
    [InlineData(true, 1.5)]
    [InlineData(true, 2.0)]
    [InlineData(true, 4.0)]
    public async Task PrefetchedGeometrySurvivesAuthoritativeDecodeWithoutReflow(bool inline, double scale)
    {
        using var session = new MarkdownPerformanceSession(MarkdownPerformanceOptions.Progressive);
        var resolver = new ByteResolver(DisplaySizedRasterDecodeTests.CreatePng(1024, 512));
        using IMarkdownPerformanceDocumentScope scope = OpenScope(session, resolver);
        await scope.PrefetchAsync([Source], CancellationToken.None);
        int resolvesAfterPrefetch = resolver.Calls;
        using var images = new ImageCustody();
        ImageBox image = images.Own(CreateImage(scope, session, scale));
        if (inline)
            image.MeasureInline(200, 20);
        else
            image.Measure(200);
        Rect reserved = image.Bounds;
        Assert.Equal(200f, image.MeasuredImageWidth);
        Assert.Equal(100f, image.MeasuredImageHeight);
        Assert.Null(image.Bitmap);
        Assert.Equal(resolvesAfterPrefetch, resolver.Calls);
        Assert.True(image.HasExactReservedRasterGeometry(new Size(1024, 512)));

        bool invalidated = true;
        image.LoadCompleted += (_, args) => invalidated = args.LayoutInvalidated;
        await DisplaySizedRasterDecodeTests.WaitForLoadAsync(image);

        Assert.NotNull(image.Bitmap);
        Assert.Equal(reserved, image.Bounds);
        Assert.False(invalidated);
        Assert.Equal((uint)(200 * scale), image.Bitmap.SizeInPixels.Width);
        Assert.Equal((uint)(100 * scale), image.Bitmap.SizeInPixels.Height);
        Assert.Equal(resolvesAfterPrefetch, resolver.Calls);
    }

    [Theory]
    [InlineData((ushort)1, 100f)]
    [InlineData((ushort)5, 400f)]
    [InlineData((ushort)6, 400f)]
    [InlineData((ushort)8, 400f)]
    public async Task OrientedJpegReservesTheSameAxesAsWic(ushort orientation, float expectedHeight)
    {
        byte[] bytes = await ExifRasterCachePolicyTests.CreateJpegAsync(1600, 800, orientation);
        using var session = new MarkdownPerformanceSession(MarkdownPerformanceOptions.Progressive);
        using IMarkdownPerformanceDocumentScope scope = OpenScope(session, new ByteResolver(bytes));
        await scope.PrefetchAsync([Source], CancellationToken.None);
        using var images = new ImageCustody();
        ImageBox image = images.Own(CreateImage(scope, session, scale: 1));
        image.Measure(200);
        Assert.Equal(expectedHeight, image.MeasuredImageHeight);
        bool invalidated = true;
        image.LoadCompleted += (_, args) => invalidated = args.LayoutInvalidated;
        await DisplaySizedRasterDecodeTests.WaitForLoadAsync(image);
        Assert.NotNull(image.Bitmap);
        Assert.False(invalidated);
        Assert.Equal((uint)expectedHeight, image.Bitmap.SizeInPixels.Height);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task EitherMissingOptInKeepsTheColdPlaceholderAndReflow(bool prefetch, bool displayDecode)
    {
        using var session = new MarkdownPerformanceSession(MarkdownPerformanceOptions.Progressive with
        { PrefetchDocumentImages = prefetch, UseDisplaySizedRasterDecode = displayDecode });
        using IMarkdownPerformanceDocumentScope scope = OpenScope(session,
            new ByteResolver(DisplaySizedRasterDecodeTests.CreatePng(1024, 512)));
        await scope.PrefetchAsync([Source], CancellationToken.None);
        using var images = new ImageCustody();
        ImageBox image = images.Own(CreateImage(scope, session, scale: 1));
        image.Measure(200);
        Assert.Equal(32f, image.MeasuredImageHeight);
        bool invalidated = false;
        image.LoadCompleted += (_, args) => invalidated = args.LayoutInvalidated;
        await DisplaySizedRasterDecodeTests.WaitForLoadAsync(image);
        Assert.NotNull(image.Bitmap);
        Assert.True(invalidated);
    }

    [Fact]
    public async Task MissingPrefetchDoesNotResolveDuringMeasurement()
    {
        using var session = new MarkdownPerformanceSession(MarkdownPerformanceOptions.Progressive);
        var resolver = new ByteResolver(DisplaySizedRasterDecodeTests.CreatePng(1024, 512));
        using IMarkdownPerformanceDocumentScope scope = OpenScope(session, resolver);
        using var images = new ImageCustody();
        ImageBox image = images.Own(CreateImage(scope, session, scale: 1));
        image.Measure(200);
        Assert.Equal(0, resolver.Calls);
        Assert.Equal(32f, image.MeasuredImageHeight);
        bool invalidated = false;
        image.LoadCompleted += (_, args) => invalidated = args.LayoutInvalidated;
        await DisplaySizedRasterDecodeTests.WaitForLoadAsync(image);
        Assert.NotNull(image.Bitmap);
        Assert.True(invalidated);
    }

    [Fact]
    public async Task ReplacedSourceDimensionsStillRequestAuthoritativeReflow()
    {
        using var session = new MarkdownPerformanceSession(MarkdownPerformanceOptions.Progressive);
        var resolver = new ByteResolver(DisplaySizedRasterDecodeTests.CreatePng(1024, 512));
        using IMarkdownPerformanceDocumentScope scope = OpenScope(session, resolver);
        await scope.PrefetchAsync([Source], CancellationToken.None);
        using var images = new ImageCustody();
        ImageBox image = images.Own(CreateImage(scope, session, scale: 1));
        image.Measure(200);
        Assert.Equal(100f, image.MeasuredImageHeight);
        session.Trim();
        resolver.Bytes = DisplaySizedRasterDecodeTests.CreatePng(1024, 1024);
        await scope.PrefetchAsync([Source], CancellationToken.None);
        bool invalidated = false;
        image.LoadCompleted += (_, args) => invalidated = args.LayoutInvalidated;
        await DisplaySizedRasterDecodeTests.WaitForLoadAsync(image);
        Assert.NotNull(image.Bitmap);
        Assert.True(invalidated);
        image.Measure(200);
        Assert.Equal(200f, image.MeasuredImageHeight);
    }

    [Fact]
    public async Task GeometryProofRejectsActiveAndRolledBackInlineWorkAndClippedBounds()
    {
        using var session = new MarkdownPerformanceSession(MarkdownPerformanceOptions.Progressive);
        using IMarkdownPerformanceDocumentScope scope = OpenScope(session,
            new ByteResolver(DisplaySizedRasterDecodeTests.CreatePng(1024, 512)));
        using var images = new ImageCustody();
        ImageBox image = images.Own(CreateImage(scope, session, scale: 1));
        image.MeasureInline(200, 20);
        Rect original = image.Bounds;
        await scope.PrefetchAsync([Source], CancellationToken.None);
        ImageBox.InlineMeasureCheckpoint checkpoint = image.BeginInlineCheckpoint();
        image.MeasureInline(200, 20, checkpoint);
        Assert.False(image.HasExactReservedRasterGeometry(new Size(1024, 512)));
        checkpoint.Rollback();
        Assert.Equal(original, image.Bounds);
        Assert.False(image.HasExactReservedRasterGeometry(new Size(1024, 512)));
        image.MeasureInline(200, 20);
        Assert.True(image.HasExactReservedRasterGeometry(new Size(1024, 512)));
        image.SetInlineBounds(new Rect(50, 20, 180, 100));
        Assert.False(image.HasExactReservedRasterGeometry(new Size(1024, 512)));
        bool invalidated = false;
        image.LoadCompleted += (_, args) => invalidated = args.LayoutInvalidated;
        await DisplaySizedRasterDecodeTests.WaitForLoadAsync(image);
        Assert.True(invalidated);
    }

    [Theory]
    [InlineData(800f, 400f, 200f, 100f)]
    [InlineData(800f, 0f, 200f, 100f)]
    [InlineData(0f, 800f, 200f, 100f)]
    [InlineData(800f, 800f, 200f, 200f)]
    public async Task RequestedDimensionsAndCaptionKeepTheirExactReservedBounds(
        float authoredWidth, float authoredHeight, float expectedWidth, float expectedHeight)
    {
        using var session = new MarkdownPerformanceSession(MarkdownPerformanceOptions.Progressive);
        using IMarkdownPerformanceDocumentScope scope = OpenScope(session,
            new ByteResolver(DisplaySizedRasterDecodeTests.CreatePng(1024, 512)));
        await scope.PrefetchAsync([Source], CancellationToken.None);
        using var images = new ImageCustody();
        ImageBox image = images.Own(new ImageBox(DisplaySizedRasterDecodeTests.CreateContext(scope, session, 2),
            Source, "A real caption", new SafeHtmlLength(authoredWidth, false),
            new SafeHtmlLength(authoredHeight, false)));
        image.MinimumParagraphLineHeight = 24;
        image.Measure(200);
        Rect reserved = image.Bounds;
        Assert.Equal(expectedWidth, image.MeasuredImageWidth);
        Assert.Equal(expectedHeight, image.MeasuredImageHeight);
        Assert.True(image.HasExactReservedRasterGeometry(new Size(1024, 512)));
        Assert.False(image.HasExactReservedRasterGeometry(new Size(double.NaN, 0)));
        bool invalidated = true;
        image.LoadCompleted += (_, args) => invalidated = args.LayoutInvalidated;
        await DisplaySizedRasterDecodeTests.WaitForLoadAsync(image);
        Assert.False(invalidated);
        Assert.Equal(reserved, image.Bounds);
    }

    [Fact]
    public async Task NarrowCellAndPercentWidthRespectMarginsAndParagraphHeight()
    {
        using var session = new MarkdownPerformanceSession(MarkdownPerformanceOptions.Progressive);
        using IMarkdownPerformanceDocumentScope scope = OpenScope(session,
            new ByteResolver(DisplaySizedRasterDecodeTests.CreatePng(1024, 512)));
        await scope.PrefetchAsync([Source], CancellationToken.None);
        using var images = new ImageCustody();
        ImageBox image = images.Own(new ImageBox(DisplaySizedRasterDecodeTests.CreateContext(scope, session, 2),
            Source, string.Empty, new SafeHtmlLength(50, true), new SafeHtmlLength(80, true)));
        image.Margin = new Microsoft.UI.Xaml.Thickness(10, 4, 20, 8);
        image.MinimumParagraphLineHeight = 64;
        image.Measure(150);
        Assert.Equal(60f, image.MeasuredImageWidth);
        Assert.Equal(30f, image.MeasuredImageHeight);
        Assert.Equal(150d, image.Bounds.Width);
        Assert.Equal(76d, image.Bounds.Height);
        Assert.True(image.HasExactReservedRasterGeometry(new Size(1024, 512)));
        bool invalidated = true;
        image.LoadCompleted += (_, args) => invalidated = args.LayoutInvalidated;
        await DisplaySizedRasterDecodeTests.WaitForLoadAsync(image);
        Assert.False(invalidated);
    }

    [Fact]
    public async Task HintDoesNotTurnAnUnstartedPrivatePlacementIntoACompletedSource()
    {
        using var session = new MarkdownPerformanceSession(MarkdownPerformanceOptions.Progressive);
        using IMarkdownPerformanceDocumentScope scope = OpenScope(session,
            new ByteResolver(DisplaySizedRasterDecodeTests.CreatePng(1024, 512)));
        await scope.PrefetchAsync([Source], CancellationToken.None);
        var context = DisplaySizedRasterDecodeTests.CreateContext(scope, session, 1);
        using var images = new ImageCustody();
        ImageBox image = images.Own(new ImageBox(context, Source, string.Empty));
        image.Measure(200);
        Assert.True(image.TryCreatePrivateImagePlacementClone(context,
            out ImageBox? clone, out var lease, out var svgFence, out bool idle, out int generation));
        if (clone is not null)
            images.Own(clone);
        {
            Assert.True(idle);
            Assert.Equal(0, generation);
            Assert.Null(lease);
            Assert.Null(svgFence);
            Assert.NotNull(clone);
            clone.Measure(200);
            Assert.True(image.IsCurrentIdleImagePlacement(generation));
            Assert.True(clone.HasExactReservedRasterGeometry(new Size(1024, 512)));
            Assert.Null(clone.Bitmap);
        }
    }

    private static IMarkdownPerformanceDocumentScope OpenScope(
        MarkdownPerformanceSession session, IMarkdownImageResolver resolver) =>
        ((IMarkdownPerformanceSessionInternal)session).OpenDocument(resolver,
            new MarkdownImageResolveContext(BaseUri: null));

    private static ImageBox CreateImage(IMarkdownImageResolver resolver,
        MarkdownPerformanceSession session, double scale) =>
        new(DisplaySizedRasterDecodeTests.CreateContext(resolver, session, scale), Source, string.Empty);

    private sealed class ImageCustody : IDisposable
    {
        private readonly List<ImageBox> _images = [];
        internal ImageBox Own(ImageBox image)
        {
            _images.Add(image);
            return image;
        }

        public void Dispose()
        {
            foreach (ImageBox image in _images)
                image.Dispose();
        }
    }

    private sealed class ByteResolver(byte[] bytes) : IMarkdownImageResolver
    {
        public byte[] Bytes { get; set; } = bytes;
        private readonly string _cacheKey = $"geometry-{Guid.NewGuid():N}";
        private int _calls;
        public int Calls => Volatile.Read(ref _calls);
        public ValueTask<MarkdownImageResolution> ResolveAsync(string source,
            MarkdownImageResolveContext context, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _calls);
            return ValueTask.FromResult(MarkdownImageResolution.Resolved(
                new MarkdownImageAsset(Bytes, ResolvedUri: new Uri(source),
                    CacheKey: _cacheKey)));
        }
    }
}
