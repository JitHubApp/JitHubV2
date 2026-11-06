using MarkdownRenderer.Accessibility;
using MarkdownRenderer.Document;
using MarkdownRenderer.Layout;
using MarkdownRenderer.Layout.Boxes;
using Microsoft.Graphics.Canvas;
using Windows.Foundation;
using Xunit;
using static MarkdownRenderer.GitHub.Tests.ContainerRelayoutTestTree;

namespace MarkdownRenderer.GitHub.Tests;

/// <summary>
/// The source roots, images, leases and counters before a container relayout
/// attempt that must retry, and the oracle that the retry left them untouched.
/// </summary>
internal sealed class ContainerRetrySourceState
{
    private readonly BlockBox[] _roots;
    private readonly Rect[] _bounds;
    private readonly int _treeLength;
    private readonly CanvasBitmap[] _bitmaps;
    private readonly SharedCanvasBitmapCache.Lease[] _leases;
    private readonly int[] _leaseCounts;
    private readonly long _revision;
    private readonly MarkdownSourceMap _sourceMap;
    private readonly MarkdownSemanticDocument _semantic;
    private readonly int[] _resolveCounts;
    private readonly int _svgOpenCount;
    private readonly int _svgRenderCount;

    internal ContainerRetrySourceState(
        LayoutSnapshot snapshot,
        ImageBox[] images,
        ContainerResolver resolver,
        ContainerSvgRenderer svgRenderer)
    {
        _roots = snapshot.Blocks.ToArray();
        _bounds = _roots.Select(static root => root.Bounds).ToArray();
        _treeLength = EnumerateBoxes(_roots).Count();
        _bitmaps = images.Select(static image => Assert.IsType<CanvasBitmap>(image.Bitmap)).ToArray();
        _leases = images.Select(GetBitmapLease).ToArray();
        _leaseCounts = _leases.Select(GetLeaseReferenceCount).ToArray();
        _revision = snapshot.LayoutRevision;
        _sourceMap = snapshot.SourceMap;
        _semantic = snapshot.SemanticDocument;
        _resolveCounts = resolver.CaptureResolveCounts();
        _svgOpenCount = svgRenderer.OpenCount;
        _svgRenderCount = svgRenderer.FullRenderCount;
    }

    internal void AssertUnchanged(
        LayoutSnapshot snapshot,
        ImageBox[] images,
        ContainerResolver resolver,
        ContainerSvgRenderer svgRenderer)
    {
        Assert.Equal(_revision, snapshot.LayoutRevision);
        Assert.Same(_sourceMap, snapshot.SourceMap);
        Assert.Same(_semantic, snapshot.SemanticDocument);
        Assert.Equal(_resolveCounts, resolver.CaptureResolveCounts());
        Assert.Equal(_svgOpenCount, svgRenderer.OpenCount);
        Assert.Equal(_svgRenderCount, svgRenderer.FullRenderCount);
        Assert.Equal(_roots.Length, snapshot.Blocks.Count);
        for (int index = 0; index < _roots.Length; index++)
        {
            Assert.Same(_roots[index], snapshot.Blocks[index]);
            Assert.Equal(_bounds[index], snapshot.Blocks[index].Bounds);
        }
        BlockBox[] treeAfterRace = EnumerateBoxes(snapshot.Blocks).ToArray();
        Assert.Equal(_treeLength, treeAfterRace.Length);
        Assert.All(images, image => Assert.Contains(image, EnumerateImages(snapshot.Blocks)));
        for (int index = 0; index < images.Length; index++)
        {
            Assert.Same(_bitmaps[index], images[index].Bitmap);
            Assert.Same(_leases[index], GetBitmapLease(images[index]));
            Assert.Equal(_leaseCounts[index], GetLeaseReferenceCount(_leases[index]));
        }
    }
}
