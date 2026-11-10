using MarkdownRenderer.Accessibility.Internal.Peers;
using MarkdownRenderer.Accessibility.Internal.Semantics;
using MarkdownRenderer.Document;
using MarkdownRenderer.Extensions;
using MarkdownRenderer.Layout;
using MarkdownRenderer.Layout.Boxes;
using MarkdownRenderer.Layout.Internal.Images;
using MarkdownRenderer.Layout.Internal.Images.BitmapCache;
using MarkdownRenderer.Math;
using Microsoft.Graphics.Canvas;
using Windows.Foundation;
using Xunit;
using static MarkdownRenderer.GitHub.Tests.ContainerRelayoutTestTree;
using static MarkdownRenderer.GitHub.Tests.RootLocalContainerVectorRelayoutHarness;

namespace MarkdownRenderer.GitHub.Tests;

/// <summary>
/// The commit oracle for list, nested-list and blockquote roots that mix
/// completed images with measured math vectors: the transfer keeps every
/// source owner and matches a fresh fallback reflow.
/// </summary>
internal static class ContainerVectorParityAssertions
{
    internal static (StackBox List, StackBox Quote) AssertListAndQuoteShape(LayoutSnapshot snapshot)
    {
        StackBox listRoot = Assert.Single(snapshot.Blocks.OfType<StackBox>(),
            static root => root.Children.OfType<ListItemBox>().Any());
        StackBox quoteRoot = Assert.Single(snapshot.Blocks.OfType<StackBox>(),
            root => !ReferenceEquals(root, listRoot));
        Assert.Contains(EnumerateBoxes([listRoot]).OfType<StackBox>(),
            root => !ReferenceEquals(root, listRoot) && root.Children.OfType<ListItemBox>().Any());
        Assert.NotEmpty(quoteRoot.Children);
        return (listRoot, quoteRoot);
    }

    /// <summary>
    /// The general private COW clone stays closed to measured vectors; only the
    /// opted-in measured placement capture admits both container roots.
    /// </summary>
    internal static async Task AssertOnlyMeasuredPlacementCaptureAdmitsVectorsAsync(
        ContainerVectorDocument optimized,
        StackBox listRoot,
        StackBox quoteRoot)
    {
        MarkdownLayoutContext probeContext = optimized.Context.CreateRasterPlacementContext(
            CancellationToken.None,
            optimized.Context.ImagePreparationOwner!,
            retirementSink: null,
            imageUnavailable: null);
        try
        {
            Assert.False(PrivateRasterPlacementTree.TryCreateClone(
                listRoot,
                optimized.Context,
                probeContext,
                LayoutWidth,
                out BlockBox? genericClone,
                out RasterPlacementSourceFence[]? genericFences));
            Assert.Null(genericClone);
            Assert.Null(genericFences);

            var measuredPlacementCapture = new PrivateRasterPlacementTree.CaptureState(
                allowMeasuredVectorPlacement: true);
            Assert.True(measuredPlacementCapture.TryAddRoot(
                listRoot,
                optimized.Context,
                probeContext,
                LayoutWidth,
                out PrivateRasterPlacementTree.RootCounts placementCounts));
            Assert.True(placementCounts.RunCount > 0);
            Assert.Equal(2, placementCounts.ImageCount);
            Assert.True(measuredPlacementCapture.TryAddRoot(
                quoteRoot,
                optimized.Context,
                probeContext,
                LayoutWidth,
                out PrivateRasterPlacementTree.RootCounts quoteCounts));
            Assert.Equal(1, quoteCounts.ImageCount);
            Assert.Equal(3, measuredPlacementCapture.ImageCount);
        }
        finally
        {
            await probeContext.DrainOwnedRetirementsAsync().WaitAsync(AsyncDeadline);
        }
    }

    /// <summary>The source tree, vectors, leases and semantic projection before the transfer.</summary>
    internal sealed class SourceState
    {
        private BlockBox[] _tree = [];
        private int[] _blockIndices = [];
        private InlineContainerBox[] _inlines = [];
        private InlineRun[][] _runs = [];
        private InlineVectorSceneRun[] _vectors = [];
        private InlineVectorSceneRun[] _fallbackVectors = [];
        private CanvasBitmap[] _bitmaps = [];
        private SharedCanvasBitmapCache.Lease[] _leases = [];
        private int[] _leaseReferences = [];
        private VectorSceneDrawing[] _drawings = [];
        private MarkdownVectorScene[] _scenes = [];
        private Rect[][] _vectorSelection = [];
        private SemanticSpanGeometry[] _semanticSpans = [];
        private MarkdownSemanticDocument? _semanticDocument;
        private MarkdownSourceMap? _sourceMap;
        private string _semanticText = string.Empty;
        private MarkdownAutomationTextFingerprint _semanticProjection;
        private MarkdownAutomationTextFingerprint _semanticRanges;
        private long _revision;
        private long _automationGeneration;
        private int _resolveCount;

        internal static SourceState CaptureTree(LayoutSnapshot snapshot, LayoutSnapshot fallback)
        {
            var state = new SourceState();
            state._tree = EnumerateBoxes(snapshot.Blocks).ToArray();
            state._blockIndices = state._tree.Select(static box => box.BlockIndex).ToArray();
            state._inlines = state._tree.OfType<InlineContainerBox>().ToArray();
            state._runs = state._inlines.Select(static inline => inline.Runs.ToArray()).ToArray();
            state._vectors = state._inlines
                .SelectMany(static inline => inline.Runs.OfType<InlineVectorSceneRun>()).ToArray();
            state._fallbackVectors = EnumerateBoxes(fallback.Blocks)
                .OfType<InlineContainerBox>()
                .SelectMany(static inline => inline.Runs.OfType<InlineVectorSceneRun>()).ToArray();
            Assert.Equal(3, state._vectors.Length);
            Assert.Equal(state._vectors.Length, state._fallbackVectors.Length);
            Assert.All(state._vectors, static vector => Assert.True(vector.IsNativeResourcesPrepared));
            return state;
        }

        internal void CaptureResources(LayoutSnapshot snapshot, ImageBox[] images, ContainerVectorResolver resolver)
        {
            _bitmaps = images
                .Select(static image => Assert.IsType<CanvasBitmap>(image.Bitmap)).ToArray();
            _leases = images.Select(GetBitmapLease).ToArray();
            _leaseReferences = _leases.Select(GetLeaseReferenceCount).ToArray();
            _drawings = _vectors.Select(GetVectorDrawing).ToArray();
            _scenes = _vectors
                .Select(static vector => Assert.IsType<MarkdownVectorScene>(vector.Scene)).ToArray();
            _vectorSelection = CaptureVectorSelectionGeometry(snapshot, _inlines);
            Assert.Equal(_vectors.Length, _vectorSelection.Length);
            Assert.All(_vectorSelection, static rectangles => Assert.NotEmpty(rectangles));
            _semanticSpans = CaptureSemanticSpanGeometry(snapshot);
            _semanticDocument = snapshot.SemanticDocument;
            _sourceMap = snapshot.SourceMap;
            _semanticText = _semanticDocument.Text;
            _semanticProjection = _semanticDocument.TextProjectionFingerprint;
            _semanticRanges = _semanticDocument.SemanticRangeFingerprint;
            _revision = snapshot.LayoutRevision;
            _automationGeneration = snapshot.AutomationDocumentGeneration;
            _resolveCount = resolver.ResolveCount;
        }

        /// <summary>
        /// Checks a committed transfer in order: document identity, tree and
        /// inline owners, vector runs, vector selection and semantic geometry,
        /// then image leases and placement.
        /// </summary>
        internal void AssertCommitMatchesFallback(
            LayoutSnapshot snapshot,
            LayoutSnapshot fallback,
            ImageBox[] images,
            ImageBox[] fallbackImages,
            ContainerVectorResolver resolver,
            float rasterizationScale)
        {
            AssertDocumentIdentityPreserved(snapshot, fallback, resolver);
            InlineContainerBox[] inlineAfter = AssertTreeAndInlinesMatchFallback(
                snapshot, fallback, out InlineContainerBox[] fallbackInlines);
            AssertVectorsPreservedAndMatchFallback(inlineAfter);
            AssertVectorSelectionAndSemanticsMatchFallback(snapshot, fallback, inlineAfter, fallbackInlines);
            AssertImagesPreservedAndMatchFallback(snapshot, fallback, images, fallbackImages, rasterizationScale);
        }

        private void AssertDocumentIdentityPreserved(
            LayoutSnapshot snapshot,
            LayoutSnapshot fallback,
            ContainerVectorResolver resolver)
        {
            Assert.Equal(_revision + 1, snapshot.LayoutRevision);
            Assert.Equal(_automationGeneration, snapshot.AutomationDocumentGeneration);
            Assert.Same(_semanticDocument, snapshot.SemanticDocument);
            Assert.Same(_sourceMap, snapshot.SourceMap);
            Assert.Equal(_semanticText, snapshot.SemanticDocument.Text);
            Assert.Equal(_semanticProjection, snapshot.SemanticDocument.TextProjectionFingerprint);
            Assert.Equal(_semanticRanges, snapshot.SemanticDocument.SemanticRangeFingerprint);
            Assert.Equal(_resolveCount, resolver.ResolveCount);
            Assert.Equal(fallback.Size, snapshot.Size);
        }

        private InlineContainerBox[] AssertTreeAndInlinesMatchFallback(
            LayoutSnapshot snapshot,
            LayoutSnapshot fallback,
            out InlineContainerBox[] fallbackInlines)
        {
            BlockBox[] sourceTreeAfter = EnumerateBoxes(snapshot.Blocks).ToArray();
            BlockBox[] fallbackTree = EnumerateBoxes(fallback.Blocks).ToArray();
            Assert.Equal(_tree.Length, sourceTreeAfter.Length);
            Assert.Equal(_tree.Length, fallbackTree.Length);
            Assert.Equal(_blockIndices, sourceTreeAfter.Select(static box => box.BlockIndex));
            for (int index = 0; index < _tree.Length; index++)
            {
                Assert.Same(_tree[index], sourceTreeAfter[index]);
                Assert.Equal(LayoutPlacementTestGeometry.ToLogical(fallback, fallbackTree[index]),
                    LayoutPlacementTestGeometry.ToLogical(snapshot, sourceTreeAfter[index]));
            }

            InlineContainerBox[] inlineAfter = sourceTreeAfter.OfType<InlineContainerBox>().ToArray();
            fallbackInlines = fallbackTree.OfType<InlineContainerBox>().ToArray();
            Assert.Equal(_inlines.Length, inlineAfter.Length);
            Assert.Equal(_inlines.Length, fallbackInlines.Length);
            for (int inlineIndex = 0; inlineIndex < _inlines.Length; inlineIndex++)
            {
                Assert.Same(_inlines[inlineIndex], inlineAfter[inlineIndex]);
                Assert.Equal(_runs[inlineIndex].Length, inlineAfter[inlineIndex].Runs.Count);
                for (int runIndex = 0; runIndex < _runs[inlineIndex].Length; runIndex++)
                    Assert.Same(_runs[inlineIndex][runIndex], inlineAfter[inlineIndex].Runs[runIndex]);
            }
            return inlineAfter;
        }

        private void AssertVectorsPreservedAndMatchFallback(InlineContainerBox[] inlineAfter)
        {
            InlineVectorSceneRun[] vectorsAfter = inlineAfter
                .SelectMany(static inline => inline.Runs.OfType<InlineVectorSceneRun>()).ToArray();
            Assert.Equal(_vectors.Length, vectorsAfter.Length);
            for (int vectorIndex = 0; vectorIndex < _vectors.Length; vectorIndex++)
            {
                InlineVectorSceneRun vector = vectorsAfter[vectorIndex];
                InlineVectorSceneRun fallbackVector = _fallbackVectors[vectorIndex];
                Assert.Same(_vectors[vectorIndex], vector);
                Assert.Same(_scenes[vectorIndex], vector.Scene);
                Assert.Same(_drawings[vectorIndex], GetVectorDrawing(vector));
                Assert.True(vector.IsNativeResourcesPrepared);
                Assert.False(vector.IsMeasuredPlacementOnly);
                Assert.Equal(fallbackVector.DesiredWidth, vector.DesiredWidth);
                Assert.Equal(fallbackVector.DesiredHeight, vector.DesiredHeight);
                Assert.Equal(fallbackVector.DesiredBaseline, vector.DesiredBaseline);
                Assert.Equal(fallbackVector.LocalBounds, vector.LocalBounds);
            }
        }

        private void AssertVectorSelectionAndSemanticsMatchFallback(
            LayoutSnapshot snapshot,
            LayoutSnapshot fallback,
            InlineContainerBox[] inlineAfter,
            InlineContainerBox[] fallbackInlines)
        {
            Rect[][] vectorSelectionAfter = CaptureVectorSelectionGeometry(snapshot, inlineAfter);
            Rect[][] fallbackVectorSelection = CaptureVectorSelectionGeometry(fallback, fallbackInlines);
            Assert.Equal(_vectorSelection.Length, vectorSelectionAfter.Length);
            Assert.Equal(fallbackVectorSelection.Length, vectorSelectionAfter.Length);
            for (int index = 0; index < vectorSelectionAfter.Length; index++)
                Assert.Equal(fallbackVectorSelection[index], vectorSelectionAfter[index]);
            SemanticSpanGeometry[] semanticAfter = CaptureSemanticSpanGeometry(snapshot);
            // Reflow changes geometry, not the semantic span projection.
            Assert.Equal(_semanticSpans.Select(static span =>
                (span.Start, span.End, span.RunType, span.Text)),
                semanticAfter.Select(static span =>
                    (span.Start, span.End, span.RunType, span.Text)));
            Assert.True(_vectorSelection.Where((rectangles, index) =>
                !rectangles.SequenceEqual(vectorSelectionAfter[index])).Any(),
                "Completed image placement must move at least one vector selection range.");
            AssertSemanticSpanGeometryEqual(
                CaptureSemanticSpanGeometry(fallback),
                semanticAfter);
        }

        private void AssertImagesPreservedAndMatchFallback(
            LayoutSnapshot snapshot,
            LayoutSnapshot fallback,
            ImageBox[] images,
            ImageBox[] fallbackImages,
            float rasterizationScale)
        {
            ImageBox[] imagesAfter = EnumerateImages(snapshot.Blocks).ToArray();
            Assert.Equal(images.Length, imagesAfter.Length);
            for (int index = 0; index < images.Length; index++)
            {
                Assert.Same(images[index], imagesAfter[index]);
                Assert.Same(_bitmaps[index], imagesAfter[index].Bitmap);
                Assert.Same(_leases[index], GetBitmapLease(imagesAfter[index]));
                Assert.Equal(_leaseReferences[index], GetLeaseReferenceCount(_leases[index]));
                InlineImagePlacementTrace expectedPlacement = CaptureInlineImagePlacement(
                    fallback, fallbackImages[index]);
                InlineImagePlacementTrace actualPlacement = CaptureInlineImagePlacement(
                    snapshot, imagesAfter[index]);
                Assert.True(expectedPlacement.LogicalBounds.Equals(actualPlacement.LogicalBounds),
                    $"rasterScale={rasterizationScale}; expected={expectedPlacement}; actual={actualPlacement}");
            }
        }
    }
}
