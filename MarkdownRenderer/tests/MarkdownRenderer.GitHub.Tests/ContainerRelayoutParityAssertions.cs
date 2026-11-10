using MarkdownRenderer.Accessibility.Internal.Semantics;
using MarkdownRenderer.Document;
using MarkdownRenderer.GitHub.Tests.TestSupport.Fixtures;
using MarkdownRenderer.Images;
using MarkdownRenderer.Layout.Internal.Boxes;
using MarkdownRenderer.Layout.Internal.Images;
using MarkdownRenderer.Layout.Internal.Images.BitmapCache;
using MarkdownRenderer.Layout.Internal.Inlines;
using MarkdownRenderer.Layout.Internal.Inlines.Runs;
using MarkdownRenderer.Layout.Internal.PrivatePlacement;
using MarkdownRenderer.Layout.Internal.Snapshots;
using MarkdownRenderer.Theming;
using Microsoft.Graphics.Canvas;
using Windows.Foundation;
using Windows.UI;
using Xunit;
using static MarkdownRenderer.GitHub.Tests.TestSupport.Fixtures.ContainerRelayoutTestTree;
using static MarkdownRenderer.GitHub.Tests.ContainerTextLayoutCapture;
using static MarkdownRenderer.GitHub.Tests.RootLocalContainerImageRelayoutHarness;

namespace MarkdownRenderer.GitHub.Tests;

/// <summary>
/// The commit oracle for a completed nested-list and blockquote image
/// relayout: the optimized transfer keeps every source owner and matches a
/// fresh fallback reflow in geometry, selection, text layout and pixels.
/// </summary>
internal static class ContainerRelayoutParityAssertions
{
    internal static (StackBox List, StackBox Quote) AssertRichListAndQuoteShape(
        LayoutSnapshot optimized,
        LayoutSnapshot fallback)
    {
        StackBox optimizedList = Assert.Single(optimized.Blocks.OfType<StackBox>(),
            stack => stack.Children.OfType<ListItemBox>().Any());
        StackBox fallbackList = Assert.Single(fallback.Blocks.OfType<StackBox>(),
            stack => stack.Children.OfType<ListItemBox>().Any());
        Assert.Equal(0, optimizedList.InterChildSpacing); // The blank line makes this list loose.
        Assert.Equal(0, fallbackList.InterChildSpacing);
        ListItemBox[] listItems = optimizedList.Children.OfType<ListItemBox>().ToArray();
        Assert.Equal(2, listItems.Length);
        StackBox nestedList = Assert.Single(listItems[0].Content.Children.OfType<StackBox>());
        Assert.True(nestedList.InterChildSpacing > 0, "Nested children must exercise tight-list spacing.");
        Assert.Equal(3, nestedList.Children.Count);
        StackBox optimizedQuote = Assert.Single(optimized.Blocks.OfType<StackBox>(),
            stack => !ReferenceEquals(stack, optimizedList));
        _ = Assert.Single(fallback.Blocks.OfType<StackBox>(),
            stack => !ReferenceEquals(stack, fallbackList));
        Assert.NotEmpty(optimizedQuote.Children);
        return (optimizedList, optimizedQuote);
    }

    internal static void AssertPublicationPlanBeforePaint(LayoutSnapshot snapshot, ImageBox[] images)
    {
        var imagePlanBeforePaint = new RasterPlacementImagePlan(snapshot.Blocks, expectedCount: images.Length);
        Assert.Equal(images.Length, imagePlanBeforePaint.PublicationImages.Length);
        for (int index = 0; index < images.Length; index++)
            Assert.Same(images[index], imagePlanBeforePaint.PublicationImages[index]);
    }

    private static void AssertCommittedImagePlanMatchesFallback(
        LayoutSnapshot snapshot,
        LayoutSnapshot fallback,
        ImageBox[] images,
        ImageBox[] fallbackImages)
    {
        var committedImagePlan = new RasterPlacementImagePlan(
            snapshot, snapshot.Blocks, expectedCount: images.Length);
        Assert.Equal(images.Length, committedImagePlan.PublicationImages.Length);
        for (int index = 0; index < images.Length; index++)
        {
            Assert.Same(images[index], committedImagePlan.PublicationImages[index]);
            Rect logicalImageBounds = LayoutPlacementTestGeometry.ToLogical(snapshot, images[index]);
            Rect fallbackImageBounds = LayoutPlacementTestGeometry.ToLogical(fallback, fallbackImages[index]);
            Assert.Equal((logicalImageBounds.Top, logicalImageBounds.Bottom),
                committedImagePlan.Index.GetBounds(index));
            Assert.Equal((fallbackImageBounds.Top, fallbackImageBounds.Bottom),
                committedImagePlan.Index.GetBounds(index));
        }
    }

    private static void AssertTextLayoutMatchesFallback(LayoutSnapshot snapshot, LayoutSnapshot fallback)
    {
        Assert.Equal(CaptureFinalTextOrigins(fallback), CaptureFinalTextOrigins(snapshot));
        Assert.Equal(CaptureCanvasTextLayoutMetrics(fallback), CaptureCanvasTextLayoutMetrics(snapshot));
        Assert.Equal(CaptureTextLayoutRegions(fallback), CaptureTextLayoutRegions(snapshot));
    }

    private static void AssertRootPixelsMatchFallback(
        LayoutSnapshot snapshot,
        LayoutSnapshot fallback,
        CanvasDevice device,
        float rasterizationScale)
    {
        foreach (BlockBox fallbackRoot in fallback.Blocks)
        {
            BlockBox optimizedRoot = Assert.Single(snapshot.Blocks,
                root => root.BlockIndex == fallbackRoot.BlockIndex);
            Rect logicalRootBounds = LayoutPlacementTestGeometry.ToLogical(fallback, fallbackRoot);
            Rect rootViewport = new(0, logicalRootBounds.Top, LayoutWidth,
                System.Math.Max(1, logicalRootBounds.Height));
            Color[] optimizedPixels = Paint(snapshot, device, rasterizationScale, rootViewport);
            Color[] fallbackPixels = Paint(fallback, device, rasterizationScale, rootViewport);
            int mismatch = -1;
            for (int pixel = 0; pixel < optimizedPixels.Length; pixel++)
            {
                if (optimizedPixels[pixel] == fallbackPixels[pixel])
                    continue;
                mismatch = pixel;
                break;
            }
            Assert.True(mismatch < 0,
                mismatch < 0 ? string.Empty : $"Root block {fallbackRoot.BlockIndex} differs at pixel {mismatch}: " +
                $"optimized={optimizedPixels[mismatch]}, fallback={fallbackPixels[mismatch]}.\n" +
                $"Optimized inlines: {CaptureInlinePaintDetails(snapshot, optimizedRoot)}\n" +
                $"Fallback inlines: {CaptureInlinePaintDetails(fallback, fallbackRoot)}");
        }
    }

    /// <summary>The source owners, leases and counters captured before the optimized relayout.</summary>
    internal sealed class SourceState
    {
        private BlockBox[] _roots = [];
        private BlockBox[] _tree = [];
        private int[] _blockIndices = [];
        private InlineContainerBox[] _inlines = [];
        private InlineRun[][] _runs = [];
        private CanvasBitmap[] _bitmaps = [];
        private SharedCanvasBitmapCache.Lease[] _leases = [];
        private int[] _leaseReferenceCounts = [];
        private ImageBox? _svgImage;
        private MarkdownSvgDocumentInfo? _svgInfo;
        private long _revision;
        private MarkdownSourceMap? _sourceMap;
        private MarkdownSemanticDocument? _semantic;
        private long _automationGeneration;
        private int[] _resolveCounts = [];
        private int _svgOpenCount;
        private int _svgRenderCount;

        internal Size Size { get; private set; }

        internal static SourceState Capture(
            LayoutSnapshot snapshot,
            ImageBox[] images,
            ContainerResolver resolver,
            ContainerSvgRenderer svgRenderer)
        {
            var state = new SourceState();
            state._roots = snapshot.Blocks.ToArray();
            state._tree = EnumerateBoxes(state._roots).ToArray();
            state._blockIndices = state._tree.Select(static box => box.BlockIndex).ToArray();
            state._inlines = state._tree.OfType<InlineContainerBox>().ToArray();
            state._runs = state._inlines.Select(static inline => inline.Runs.ToArray()).ToArray();
            InlineRun[] allRunsBefore = state._runs.SelectMany(static runs => runs).ToArray();
            Assert.Contains(allRunsBefore, static run => run is LinkRun);
            Assert.Contains(allRunsBefore, static run => run is StrongRun ||
                run.HasStyleModifier(MarkdownElementKeys.Strong));
            state._bitmaps = images.Select(static image => Assert.IsType<CanvasBitmap>(image.Bitmap)).ToArray();
            state._leases = images.Select(GetBitmapLease).ToArray();
            state._leaseReferenceCounts = state._leases.Select(GetLeaseReferenceCount).ToArray();
            state._svgImage = Assert.Single(images, image => image.Url == resolver.SvgUrl);
            state._svgInfo = Assert.IsType<MarkdownSvgDocumentInfo>(state._svgImage.Svg.Info);
            state.Size = snapshot.Size;
            state._revision = snapshot.LayoutRevision;
            state._sourceMap = snapshot.SourceMap;
            state._semantic = snapshot.SemanticDocument;
            state._automationGeneration = snapshot.AutomationDocumentGeneration;
            state._resolveCounts = resolver.CaptureResolveCounts();
            state._svgOpenCount = svgRenderer.OpenCount;
            state._svgRenderCount = svgRenderer.FullRenderCount;
            return state;
        }

        /// <summary>
        /// Checks a committed transfer in order: document identity, root and tree
        /// owners, inline runs and selection, image leases and counters, the
        /// publication plan, native text layout, and per-root pixels.
        /// </summary>
        internal void AssertCommitMatchesFallback(
            LayoutSnapshot snapshot,
            LayoutSnapshot fallback,
            ImageBox[] images,
            ImageBox[] fallbackImages,
            ContainerResolver resolver,
            ContainerSvgRenderer svgRenderer,
            CanvasDevice device,
            float rasterizationScale)
        {
            AssertDocumentIdentityPreserved(snapshot, fallback);
            (BlockBox[] treeAfter, BlockBox[] fallbackTree) = AssertRootsAndTreeMatchFallback(snapshot, fallback);
            AssertInlinesMatchFallback(snapshot, fallback, treeAfter, fallbackTree);
            AssertImagesPreservedAndMatchFallback(snapshot, fallback, images, fallbackImages, resolver, svgRenderer);
            AssertCommittedImagePlanMatchesFallback(snapshot, fallback, images, fallbackImages);
            AssertTextLayoutMatchesFallback(snapshot, fallback);
            AssertRootPixelsMatchFallback(snapshot, fallback, device, rasterizationScale);
        }

        private void AssertDocumentIdentityPreserved(LayoutSnapshot snapshot, LayoutSnapshot fallback)
        {
            Assert.Equal(_revision + 1, snapshot.LayoutRevision);
            Assert.Equal(_automationGeneration, snapshot.AutomationDocumentGeneration);
            Assert.Same(_sourceMap, snapshot.SourceMap);
            Assert.Same(_semantic, snapshot.SemanticDocument);
            Assert.Equal(fallback.Size, snapshot.Size);
        }

        private (BlockBox[] Tree, BlockBox[] FallbackTree) AssertRootsAndTreeMatchFallback(
            LayoutSnapshot snapshot,
            LayoutSnapshot fallback)
        {
            Assert.Equal(_roots.Length, snapshot.Blocks.Count);
            for (int index = 0; index < _roots.Length; index++)
            {
                Assert.Same(_roots[index], snapshot.Blocks[index]);
                Assert.Equal(LayoutPlacementTestGeometry.ToLogical(fallback, fallback.Blocks[index]),
                    LayoutPlacementTestGeometry.ToLogical(snapshot, snapshot.Blocks[index]));
            }

            BlockBox[] treeAfter = EnumerateBoxes(snapshot.Blocks).ToArray();
            BlockBox[] fallbackTree = EnumerateBoxes(fallback.Blocks).ToArray();
            Assert.Equal(_tree.Length, treeAfter.Length);
            Assert.Equal(_tree.Length, fallbackTree.Length);
            Assert.Equal(_blockIndices, treeAfter.Select(static box => box.BlockIndex));
            for (int index = 0; index < _tree.Length; index++)
            {
                Assert.Same(_tree[index], treeAfter[index]);
                Assert.Equal(LayoutPlacementTestGeometry.ToLogical(fallback, fallbackTree[index]),
                    LayoutPlacementTestGeometry.ToLogical(snapshot, treeAfter[index]));
            }
            return (treeAfter, fallbackTree);
        }

        private void AssertInlinesMatchFallback(
            LayoutSnapshot snapshot,
            LayoutSnapshot fallback,
            BlockBox[] treeAfter,
            BlockBox[] fallbackTree)
        {
            InlineContainerBox[] inlinesAfter = treeAfter.OfType<InlineContainerBox>().ToArray();
            InlineContainerBox[] fallbackInlines = fallbackTree.OfType<InlineContainerBox>().ToArray();
            Rect[][] uiGeometryAfter = CaptureSelectionGeometry(snapshot, inlinesAfter);
            Rect[][] fallbackUiGeometry = CaptureSelectionGeometry(fallback, fallbackInlines);
            Assert.Contains(uiGeometryAfter, static rectangles => rectangles.Length > 0);
            Assert.Equal(_inlines.Length, inlinesAfter.Length);
            Assert.Equal(_inlines.Length, fallbackInlines.Length);
            for (int inlineIndex = 0; inlineIndex < _inlines.Length; inlineIndex++)
            {
                Assert.Same(_inlines[inlineIndex], inlinesAfter[inlineIndex]);
                Assert.Equal(_runs[inlineIndex].Length, inlinesAfter[inlineIndex].Runs.Count);
                for (int runIndex = 0; runIndex < _runs[inlineIndex].Length; runIndex++)
                    Assert.Same(_runs[inlineIndex][runIndex], inlinesAfter[inlineIndex].Runs[runIndex]);
                Assert.Equal(fallbackUiGeometry[inlineIndex], uiGeometryAfter[inlineIndex]);
            }
        }

        private void AssertImagesPreservedAndMatchFallback(
            LayoutSnapshot snapshot,
            LayoutSnapshot fallback,
            ImageBox[] images,
            ImageBox[] fallbackImages,
            ContainerResolver resolver,
            ContainerSvgRenderer svgRenderer)
        {
            ImageBox[] imagesAfter = EnumerateImages(snapshot.Blocks).ToArray();
            Assert.Equal(images.Length, imagesAfter.Length);
            for (int index = 0; index < images.Length; index++)
            {
                Assert.Same(images[index], imagesAfter[index]);
                Assert.Same(_bitmaps[index], imagesAfter[index].Bitmap);
                Assert.Same(_leases[index], GetBitmapLease(imagesAfter[index]));
                Assert.Equal(_leaseReferenceCounts[index], GetLeaseReferenceCount(_leases[index]));
                Assert.Equal(LayoutPlacementTestGeometry.ToLogical(fallback, fallbackImages[index]),
                    LayoutPlacementTestGeometry.ToLogical(snapshot, imagesAfter[index]));
            }
            Assert.Same(_svgInfo, _svgImage!.Svg.Info);

            Assert.Equal(_resolveCounts, resolver.CaptureResolveCounts());
            Assert.Equal(_svgOpenCount, svgRenderer.OpenCount);
            Assert.Equal(_svgRenderCount, svgRenderer.FullRenderCount);
            Assert.Equal(0, svgRenderer.TileRenderCount);
        }
    }
}
