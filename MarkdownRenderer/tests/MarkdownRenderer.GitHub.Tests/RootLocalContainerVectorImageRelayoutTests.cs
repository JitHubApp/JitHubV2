using System.Numerics;
using System.Reflection;
using MarkdownRenderer.Accessibility;
using MarkdownRenderer.Document;
using MarkdownRenderer.Extensions;
using MarkdownRenderer.Images;
using MarkdownRenderer.Layout;
using MarkdownRenderer.Layout.Boxes;
using MarkdownRenderer.Math;
using MarkdownRenderer.Performance;
using MarkdownRenderer.Parsing;
using MarkdownRenderer.Selection;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.UI.Xaml;
using Windows.Foundation;
using Windows.UI;
using Xunit;

namespace MarkdownRenderer.GitHub.Tests;

[Collection(SharedCanvasBitmapCacheTestCollection.Name)]
public sealed class RootLocalContainerVectorImageRelayoutTests
{
    private const float LayoutWidth = 640f;
    private static readonly TimeSpan AsyncDeadline = TimeSpan.FromSeconds(10);

    [Theory]
    [InlineData(1f, false, false)]
    [InlineData(1f, false, true)]
    [InlineData(1f, true, false)]
    [InlineData(1f, true, true)]
    [InlineData(1.25f, false, false)]
    [InlineData(1.25f, false, true)]
    [InlineData(1.25f, true, false)]
    [InlineData(1.25f, true, true)]
    [InlineData(1.5f, false, false)]
    [InlineData(1.5f, false, true)]
    [InlineData(1.5f, true, false)]
    [InlineData(1.5f, true, true)]
    [InlineData(1.75f, false, false)]
    [InlineData(1.75f, false, true)]
    [InlineData(1.75f, true, false)]
    [InlineData(1.75f, true, true)]
    [InlineData(2f, false, false)]
    [InlineData(2f, false, true)]
    [InlineData(2f, true, false)]
    [InlineData(2f, true, true)]
    [InlineData(2.25f, false, false)]
    [InlineData(2.25f, false, true)]
    [InlineData(2.25f, true, false)]
    [InlineData(2.25f, true, true)]
    [InlineData(3f, false, false)]
    [InlineData(3f, false, true)]
    [InlineData(3f, true, false)]
    [InlineData(3f, true, true)]
    [InlineData(4f, false, false)]
    [InlineData(4f, false, true)]
    [InlineData(4f, true, false)]
    [InlineData(4f, true, true)]
    public async Task ListNestedListAndBlockquoteVectorImagesMatchFreshReflow(
        float rasterizationScale,
        bool rightToLeft,
        bool forceSoftwareRenderer)
    {
        await using var seed = new CompletedRasterPlacementCloneTests.PlacementFixture(
            forceSoftwareRenderer: forceSoftwareRenderer);
        Assert.Equal(forceSoftwareRenderer, seed.ForceSoftwareRenderer);
        using var session = new MarkdownPerformanceSession(MarkdownPerformanceOptions.Progressive);
        var optimizedResolver = new ContainerVectorResolver(imageCount: 3);
        var fallbackResolver = new ContainerVectorResolver(imageCount: 3);
        FlowDirection direction = rightToLeft ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;

        await using DocumentFixture optimized = await BuildDocumentAsync(
            seed, session, optimizedResolver, CreateListAndQuoteSource(optimizedResolver), direction,
            rasterizationScale);
        await using DocumentFixture fallback = await BuildDocumentAsync(
            seed, session, fallbackResolver, CreateListAndQuoteSource(fallbackResolver), direction,
            rasterizationScale);

        optimizedResolver.ReleaseImages();
        fallbackResolver.ReleaseImages();
        ImageBox[] images = EnumerateImages(optimized.Snapshot.Blocks).ToArray();
        ImageBox[] fallbackImages = EnumerateImages(fallback.Snapshot.Blocks).ToArray();
        Assert.Equal(3, images.Length);
        Assert.Equal(images.Length, fallbackImages.Length);
        foreach (ImageBox image in images.Concat(fallbackImages))
            await LoadImageAsync(image);

        LayoutSnapshot snapshot = optimized.Snapshot;
        StackBox listRoot = Assert.Single(snapshot.Blocks.OfType<StackBox>(),
            static root => root.Children.OfType<ListItemBox>().Any());
        StackBox quoteRoot = Assert.Single(snapshot.Blocks.OfType<StackBox>(),
            root => !ReferenceEquals(root, listRoot));
        Assert.Contains(EnumerateBoxes([listRoot]).OfType<StackBox>(),
            root => !ReferenceEquals(root, listRoot) && root.Children.OfType<ListItemBox>().Any());
        Assert.NotEmpty(quoteRoot.Children);

        BlockBox[] treeBefore = EnumerateBoxes(snapshot.Blocks).ToArray();
        int[] blockIndicesBefore = treeBefore.Select(static box => box.BlockIndex).ToArray();
        InlineContainerBox[] inlineBefore = treeBefore.OfType<InlineContainerBox>().ToArray();
        InlineRun[][] runsBefore = inlineBefore.Select(static inline => inline.Runs.ToArray()).ToArray();
        InlineVectorSceneRun[] vectorsBefore = inlineBefore
            .SelectMany(static inline => inline.Runs.OfType<InlineVectorSceneRun>()).ToArray();
        InlineVectorSceneRun[] fallbackVectors = EnumerateBoxes(fallback.Snapshot.Blocks)
            .OfType<InlineContainerBox>()
            .SelectMany(static inline => inline.Runs.OfType<InlineVectorSceneRun>()).ToArray();
        Assert.Equal(3, vectorsBefore.Length);
        Assert.Equal(vectorsBefore.Length, fallbackVectors.Length);
        Assert.All(vectorsBefore, static vector => Assert.True(vector.IsNativeResourcesPrepared));

        // The general private COW clone remains closed to measured vectors.
        // The image-completion root path below is the only opted-in measured
        // placement recipe; its measured clone is never published.
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

        CanvasBitmap[] bitmapsBefore = images
            .Select(static image => Assert.IsType<CanvasBitmap>(image.Bitmap)).ToArray();
        SharedCanvasBitmapCache.Lease[] leasesBefore = images.Select(GetBitmapLease).ToArray();
        int[] leaseReferencesBefore = leasesBefore.Select(GetLeaseReferenceCount).ToArray();
        VectorSceneDrawing[] drawingsBefore = vectorsBefore.Select(GetVectorDrawing).ToArray();
        MarkdownVectorScene[] scenesBefore = vectorsBefore
            .Select(static vector => Assert.IsType<MarkdownVectorScene>(vector.Scene)).ToArray();
        Rect[][] vectorSelectionBefore = CaptureVectorSelectionGeometry(snapshot, inlineBefore);
        Assert.Equal(vectorsBefore.Length, vectorSelectionBefore.Length);
        Assert.All(vectorSelectionBefore, static rectangles => Assert.NotEmpty(rectangles));
        SemanticSpanGeometry[] semanticBefore = CaptureSemanticSpanGeometry(snapshot);
        MarkdownSemanticDocument semanticDocumentBefore = snapshot.SemanticDocument;
        MarkdownSourceMap sourceMapBefore = snapshot.SourceMap;
        string semanticTextBefore = semanticDocumentBefore.Text;
        MarkdownAutomationTextFingerprint semanticProjectionBefore = semanticDocumentBefore.TextProjectionFingerprint;
        MarkdownAutomationTextFingerprint semanticRangesBefore = semanticDocumentBefore.SemanticRangeFingerprint;
        long revisionBefore = snapshot.LayoutRevision;
        long automationGenerationBefore = snapshot.AutomationDocumentGeneration;
        int resolveCountBefore = optimizedResolver.ResolveCount;

        int[] imageIndices = images.Select(static image => image.BlockIndex).Distinct().ToArray();
        fallback.Snapshot.RelayoutChangedBlocks(imageIndices, LayoutWidth, CancellationToken.None);

        MarkdownLayoutContext? constructionContext = null;
        try
        {
            PreparedImageRelayoutResult result = snapshot.TryRelayoutCompletedImageRoots(
                imageIndices,
                LayoutWidth,
                CancellationToken.None,
                out constructionContext,
                expectedPointerInputEpoch: snapshot.PointerInputEpoch);

            Assert.Equal(PreparedImageRelayoutResult.Committed, result);
            AssertExactPublishedContainerOrigin(snapshot, listRoot);
            AssertExactPublishedContainerOrigin(snapshot, quoteRoot);
            Assert.NotEqual(0f, GetPublishedPlacement(snapshot, quoteRoot).BaseTop);
            Assert.NotNull(constructionContext);
            await constructionContext.DrainOwnedRetirementsAsync().WaitAsync(AsyncDeadline);

            Assert.Equal(revisionBefore + 1, snapshot.LayoutRevision);
            Assert.Equal(automationGenerationBefore, snapshot.AutomationDocumentGeneration);
            Assert.Same(semanticDocumentBefore, snapshot.SemanticDocument);
            Assert.Same(sourceMapBefore, snapshot.SourceMap);
            Assert.Equal(semanticTextBefore, snapshot.SemanticDocument.Text);
            Assert.Equal(semanticProjectionBefore, snapshot.SemanticDocument.TextProjectionFingerprint);
            Assert.Equal(semanticRangesBefore, snapshot.SemanticDocument.SemanticRangeFingerprint);
            Assert.Equal(resolveCountBefore, optimizedResolver.ResolveCount);
            Assert.Equal(fallback.Snapshot.Size, snapshot.Size);

            BlockBox[] sourceTreeAfter = EnumerateBoxes(snapshot.Blocks).ToArray();
            BlockBox[] fallbackTree = EnumerateBoxes(fallback.Snapshot.Blocks).ToArray();
            Assert.Equal(treeBefore.Length, sourceTreeAfter.Length);
            Assert.Equal(treeBefore.Length, fallbackTree.Length);
            Assert.Equal(blockIndicesBefore, sourceTreeAfter.Select(static box => box.BlockIndex));
            for (int index = 0; index < treeBefore.Length; index++)
            {
                Assert.Same(treeBefore[index], sourceTreeAfter[index]);
                Assert.Equal(LayoutPlacementTestGeometry.ToLogical(fallback.Snapshot, fallbackTree[index]),
                    LayoutPlacementTestGeometry.ToLogical(snapshot, sourceTreeAfter[index]));
            }

            InlineContainerBox[] inlineAfter = sourceTreeAfter.OfType<InlineContainerBox>().ToArray();
            InlineContainerBox[] fallbackInlines = fallbackTree.OfType<InlineContainerBox>().ToArray();
            Assert.Equal(inlineBefore.Length, inlineAfter.Length);
            Assert.Equal(inlineBefore.Length, fallbackInlines.Length);
            for (int inlineIndex = 0; inlineIndex < inlineBefore.Length; inlineIndex++)
            {
                Assert.Same(inlineBefore[inlineIndex], inlineAfter[inlineIndex]);
                Assert.Equal(runsBefore[inlineIndex].Length, inlineAfter[inlineIndex].Runs.Count);
                for (int runIndex = 0; runIndex < runsBefore[inlineIndex].Length; runIndex++)
                    Assert.Same(runsBefore[inlineIndex][runIndex], inlineAfter[inlineIndex].Runs[runIndex]);
            }

            InlineVectorSceneRun[] vectorsAfter = inlineAfter
                .SelectMany(static inline => inline.Runs.OfType<InlineVectorSceneRun>()).ToArray();
            Assert.Equal(vectorsBefore.Length, vectorsAfter.Length);
            for (int vectorIndex = 0; vectorIndex < vectorsBefore.Length; vectorIndex++)
            {
                InlineVectorSceneRun vector = vectorsAfter[vectorIndex];
                InlineVectorSceneRun fallbackVector = fallbackVectors[vectorIndex];
                Assert.Same(vectorsBefore[vectorIndex], vector);
                Assert.Same(scenesBefore[vectorIndex], vector.Scene);
                Assert.Same(drawingsBefore[vectorIndex], GetVectorDrawing(vector));
                Assert.True(vector.IsNativeResourcesPrepared);
                Assert.False(vector.IsMeasuredPlacementOnly);
                Assert.Equal(fallbackVector.DesiredWidth, vector.DesiredWidth);
                Assert.Equal(fallbackVector.DesiredHeight, vector.DesiredHeight);
                Assert.Equal(fallbackVector.DesiredBaseline, vector.DesiredBaseline);
                Assert.Equal(fallbackVector.LocalBounds, vector.LocalBounds);
            }

            Rect[][] vectorSelectionAfter = CaptureVectorSelectionGeometry(snapshot, inlineAfter);
            Rect[][] fallbackVectorSelection = CaptureVectorSelectionGeometry(fallback.Snapshot, fallbackInlines);
            Assert.Equal(vectorSelectionBefore.Length, vectorSelectionAfter.Length);
            Assert.Equal(fallbackVectorSelection.Length, vectorSelectionAfter.Length);
            for (int index = 0; index < vectorSelectionAfter.Length; index++)
                Assert.Equal(fallbackVectorSelection[index], vectorSelectionAfter[index]);
            SemanticSpanGeometry[] semanticAfter = CaptureSemanticSpanGeometry(snapshot);
            // Reflow changes geometry, not the semantic span projection.
            Assert.Equal(semanticBefore.Select(static span =>
                (span.Start, span.End, span.RunType, span.Text)),
                semanticAfter.Select(static span =>
                    (span.Start, span.End, span.RunType, span.Text)));
            Assert.True(vectorSelectionBefore.Where((rectangles, index) =>
                !rectangles.SequenceEqual(vectorSelectionAfter[index])).Any(),
                "Completed image placement must move at least one vector selection range.");
            AssertSemanticSpanGeometryEqual(
                CaptureSemanticSpanGeometry(fallback.Snapshot),
                semanticAfter);

            ImageBox[] imagesAfter = EnumerateImages(snapshot.Blocks).ToArray();
            Assert.Equal(images.Length, imagesAfter.Length);
            for (int index = 0; index < images.Length; index++)
            {
                Assert.Same(images[index], imagesAfter[index]);
                Assert.Same(bitmapsBefore[index], imagesAfter[index].Bitmap);
                Assert.Same(leasesBefore[index], GetBitmapLease(imagesAfter[index]));
                Assert.Equal(leaseReferencesBefore[index], GetLeaseReferenceCount(leasesBefore[index]));
                InlineImagePlacementTrace expectedPlacement = CaptureInlineImagePlacement(
                    fallback.Snapshot, fallbackImages[index]);
                InlineImagePlacementTrace actualPlacement = CaptureInlineImagePlacement(
                    snapshot, imagesAfter[index]);
                Assert.True(expectedPlacement.LogicalBounds.Equals(actualPlacement.LogicalBounds),
                    $"rasterScale={rasterizationScale}; expected={expectedPlacement}; actual={actualPlacement}");
            }

            Color[] actualPixels = Paint(snapshot, seed.Device, rasterizationScale);
            Color[] expectedPixels = Paint(fallback.Snapshot, seed.Device, rasterizationScale);
            int firstMismatch = -1;
            for (int index = 0; index < expectedPixels.Length; index++)
            {
                if (actualPixels[index] == expectedPixels[index])
                    continue;
                firstMismatch = index;
                break;
            }
            Assert.True(firstMismatch < 0, firstMismatch < 0
                ? null
                : DescribeFirstPixelMismatch(
                    snapshot, fallback.Snapshot, expectedPixels, actualPixels, firstMismatch, rasterizationScale));

            if (rasterizationScale == 1.5f && !rightToLeft && forceSoftwareRenderer)
            {
                Matrix3x2 callerTransform =
                    Matrix3x2.CreateScale(0.93f, 0.88f) *
                    Matrix3x2.CreateRotation(0.023f) *
                    Matrix3x2.CreateTranslation(12.25f, 7.75f);
                Color[] transformedActual = Paint(snapshot, seed.Device, rasterizationScale, callerTransform);
                Color[] transformedExpected = Paint(fallback.Snapshot, seed.Device, rasterizationScale, callerTransform);
                int transformedMismatch = -1;
                for (int index = 0; index < transformedExpected.Length; index++)
                {
                    if (transformedActual[index] == transformedExpected[index])
                        continue;
                    transformedMismatch = index;
                    break;
                }
                Assert.True(transformedMismatch < 0, transformedMismatch < 0
                    ? null
                    : DescribeFirstPixelMismatch(
                        snapshot,
                        fallback.Snapshot,
                        transformedExpected,
                        transformedActual,
                        transformedMismatch,
                        rasterizationScale));
            }
        }
        finally
        {
            if (constructionContext is not null)
                await constructionContext.DrainOwnedRetirementsAsync().WaitAsync(AsyncDeadline);
        }
    }

    [Fact]
    public async Task CancellationDuringContainerVectorMeasurementDoesNotPublishCandidate()
    {
        await using var seed = new CompletedRasterPlacementCloneTests.PlacementFixture();
        using var session = new MarkdownPerformanceSession(MarkdownPerformanceOptions.Progressive);
        var resolver = new ContainerVectorResolver(imageCount: 1);
        await using DocumentFixture document = await BuildDocumentAsync(
            seed, session, resolver, $"- ![badge]({resolver.GetUrl(0)}) with $x^2$\n",
            FlowDirection.LeftToRight, 1.5f);
        resolver.ReleaseImages();
        ImageBox image = Assert.Single(EnumerateImages(document.Snapshot.Blocks));
        await LoadImageAsync(image);
        InlineContainerBox inline = EnumerateBoxes(document.Snapshot.Blocks)
            .OfType<InlineContainerBox>()
            .Single(candidate => candidate.Runs.OfType<InlineVectorSceneRun>().Any());
        InlineVectorSceneRun vector = Assert.Single(inline.Runs.OfType<InlineVectorSceneRun>());
        StackBox listRoot = Assert.Single(document.Snapshot.Blocks.OfType<StackBox>(),
            static root => root.Children.OfType<ListItemBox>().Any());
        BlockBox[] treeBefore = EnumerateBoxes(document.Snapshot.Blocks).ToArray();
        Rect[] boundsBefore = treeBefore.Select(static box => box.Bounds).ToArray();
        InlineRun[] runsBefore = inline.Runs.ToArray();
        CanvasTextLayout layoutBefore = GetInlineLayout(inline);
        CanvasBitmap bitmapBefore = Assert.IsType<CanvasBitmap>(image.Bitmap);
        SharedCanvasBitmapCache.Lease leaseBefore = GetBitmapLease(image);
        VectorSceneDrawing drawingBefore = GetVectorDrawing(vector);
        Rect vectorBoundsBefore = vector.LocalBounds;
        long revisionBefore = document.Snapshot.LayoutRevision;
        long generationBefore = document.Snapshot.AutomationDocumentGeneration;
        using var cancellation = new CancellationTokenSource();
        MarkdownLayoutContext? constructionContext = null;

        try
        {
            Assert.ThrowsAny<OperationCanceledException>(() => document.Snapshot.TryRelayoutCompletedImageRoots(
                [image.BlockIndex],
                LayoutWidth,
                cancellation.Token,
                out constructionContext,
                beforeMeasureOutsideGateForTests: cancellation.Cancel,
                expectedPointerInputEpoch: document.Snapshot.PointerInputEpoch));
            Assert.NotNull(constructionContext);
            await constructionContext.DrainOwnedRetirementsAsync().WaitAsync(AsyncDeadline);
            Assert.Equal(revisionBefore, document.Snapshot.LayoutRevision);
            Assert.Equal(generationBefore, document.Snapshot.AutomationDocumentGeneration);
            Assert.Same(listRoot, document.Snapshot.Blocks.OfType<StackBox>().Single(
                static root => root.Children.OfType<ListItemBox>().Any()));
            Assert.Same(layoutBefore, GetInlineLayout(inline));
            Assert.Same(drawingBefore, GetVectorDrawing(vector));
            Assert.Equal(vectorBoundsBefore, vector.LocalBounds);
            Assert.Equal(treeBefore.Length, EnumerateBoxes(document.Snapshot.Blocks).Count());
            for (int index = 0; index < treeBefore.Length; index++)
            {
                Assert.Same(treeBefore[index], EnumerateBoxes(document.Snapshot.Blocks).ElementAt(index));
                Assert.Equal(boundsBefore[index], treeBefore[index].Bounds);
            }
            Assert.Equal(runsBefore.Length, inline.Runs.Count);
            for (int index = 0; index < runsBefore.Length; index++)
                Assert.Same(runsBefore[index], inline.Runs[index]);
            Assert.Same(bitmapBefore, image.Bitmap);
            Assert.Same(leaseBefore, GetBitmapLease(image));
        }
        finally
        {
            if (constructionContext is not null)
                await constructionContext.DrainOwnedRetirementsAsync().WaitAsync(AsyncDeadline);
        }
    }

    [Fact]
    public async Task StaleAndMalformedContainerVectorFencesRejectWithoutPublishing()
    {
        await using var seed = new CompletedRasterPlacementCloneTests.PlacementFixture();
        using var session = new MarkdownPerformanceSession(MarkdownPerformanceOptions.Progressive);
        var staleResolver = new ContainerVectorResolver(imageCount: 1);
        await using DocumentFixture stale = await BuildDocumentAsync(
            seed, session, staleResolver, $"- ![badge]({staleResolver.GetUrl(0)}) with $x^2$\n",
            FlowDirection.LeftToRight, 1f);
        staleResolver.ReleaseImages();
        ImageBox staleImage = Assert.Single(EnumerateImages(stale.Snapshot.Blocks));
        await LoadImageAsync(staleImage);
        InlineContainerBox staleInline = Assert.Single(EnumerateBoxes(stale.Snapshot.Blocks)
            .OfType<InlineContainerBox>(), static inline =>
                inline.Runs.OfType<InlineVectorSceneRun>().Any());
        InlineVectorSceneRun staleVector = Assert.Single(staleInline.Runs.OfType<InlineVectorSceneRun>());
        CanvasTextLayout staleLayout = GetInlineLayout(staleInline);
        VectorSceneDrawing staleDrawing = GetVectorDrawing(staleVector);
        BlockBox[] staleTree = EnumerateBoxes(stale.Snapshot.Blocks).ToArray();
        Rect[] staleBounds = staleTree.Select(static box => box.Bounds).ToArray();
        long staleRevision = stale.Snapshot.LayoutRevision;
        Rect injectedBounds = staleVector.LocalBounds with { Y = staleVector.LocalBounds.Y + 3 };
        bool injected = false;
        MarkdownLayoutContext? staleContext = null;
        try
        {
            PreparedImageRelayoutResult staleResult = stale.Snapshot.TryRelayoutCompletedImageRoots(
                [staleImage.BlockIndex], LayoutWidth, CancellationToken.None, out staleContext,
                beforeMeasureOutsideGateForTests: () =>
                {
                    injected = true;
                    staleVector.RestoreLayoutState(staleVector.DesiredWidth, staleVector.DesiredHeight,
                        staleVector.DesiredBaseline, injectedBounds);
                },
                expectedPointerInputEpoch: stale.Snapshot.PointerInputEpoch);
            Assert.True(injected);
            Assert.Equal(PreparedImageRelayoutResult.Retry, staleResult);
            Assert.NotNull(staleContext);
            await staleContext.DrainOwnedRetirementsAsync().WaitAsync(AsyncDeadline);
            Assert.Equal(staleRevision, stale.Snapshot.LayoutRevision);
            Assert.Same(staleLayout, GetInlineLayout(staleInline));
            Assert.Same(staleDrawing, GetVectorDrawing(staleVector));
            Assert.Equal(injectedBounds, staleVector.LocalBounds);
            Assert.Equal(staleTree.Length, EnumerateBoxes(stale.Snapshot.Blocks).Count());
            for (int index = 0; index < staleTree.Length; index++)
            {
                Assert.Same(staleTree[index], EnumerateBoxes(stale.Snapshot.Blocks).ElementAt(index));
                Assert.Equal(staleBounds[index], staleTree[index].Bounds);
            }
        }
        finally
        {
            if (staleContext is not null)
                await staleContext.DrainOwnedRetirementsAsync().WaitAsync(AsyncDeadline);
        }

        var malformedResolver = new ContainerVectorResolver(imageCount: 1);
        await using DocumentFixture malformed = await BuildDocumentAsync(
            seed, session, malformedResolver,
            $"- ![badge]({malformedResolver.GetUrl(0)}) with $y^2$\n",
            FlowDirection.LeftToRight, 1f);
        malformedResolver.ReleaseImages();
        ImageBox malformedImage = Assert.Single(EnumerateImages(malformed.Snapshot.Blocks));
        await LoadImageAsync(malformedImage);
        InlineContainerBox malformedInline = Assert.Single(EnumerateBoxes(malformed.Snapshot.Blocks)
            .OfType<InlineContainerBox>(), static inline =>
                inline.Runs.OfType<InlineVectorSceneRun>().Any());
        InlineVectorSceneRun malformedVector = Assert.Single(
            malformedInline.Runs.OfType<InlineVectorSceneRun>());
        CanvasTextLayout malformedLayout = GetInlineLayout(malformedInline);
        VectorSceneDrawing malformedDrawing = GetVectorDrawing(malformedVector);
        Rect[] malformedBounds = EnumerateBoxes(malformed.Snapshot.Blocks)
            .Select(static box => box.Bounds).ToArray();
        long malformedRevision = malformed.Snapshot.LayoutRevision;
        Rect invalidBounds = malformedVector.LocalBounds with { X = double.NaN };
        malformedVector.RestoreLayoutState(malformedVector.DesiredWidth, malformedVector.DesiredHeight,
            malformedVector.DesiredBaseline, invalidBounds);
        MarkdownLayoutContext? malformedContext = null;

        PreparedImageRelayoutResult malformedResult = malformed.Snapshot.TryRelayoutCompletedImageRoots(
            [malformedImage.BlockIndex], LayoutWidth, CancellationToken.None, out malformedContext,
            expectedPointerInputEpoch: malformed.Snapshot.PointerInputEpoch);
        Assert.Equal(PreparedImageRelayoutResult.Unavailable, malformedResult);
        Assert.Null(malformedContext);
        Assert.Equal(malformedRevision, malformed.Snapshot.LayoutRevision);
        Assert.Same(malformedInline, Assert.Single(EnumerateBoxes(malformed.Snapshot.Blocks)
            .OfType<InlineContainerBox>(), static inline =>
                inline.Runs.OfType<InlineVectorSceneRun>().Any()));
        Assert.Same(malformedLayout, GetInlineLayout(malformedInline));
        Assert.Same(malformedDrawing, GetVectorDrawing(malformedVector));
        Assert.True(double.IsNaN(malformedVector.LocalBounds.X));
        Assert.Equal(malformedBounds, EnumerateBoxes(malformed.Snapshot.Blocks)
            .Select(static box => box.Bounds));
    }

    [Fact]
    public async Task ContainerAggregateImageBudgetRejectsBeforeChangingVectorOrImagePlacements()
    {
        await using var seed = new CompletedRasterPlacementCloneTests.PlacementFixture();
        using var session = new MarkdownPerformanceSession(MarkdownPerformanceOptions.Progressive);
        var resolver = new ContainerVectorResolver(imageCount: 33);
        string source = "- $x^2$ " + string.Join(" ", Enumerable.Range(0, 33)
            .Select(index => $"![badge {index}]({resolver.GetUrl(index)})")) + "\n";
        await using DocumentFixture document = await BuildDocumentAsync(
            seed, session, resolver, source, FlowDirection.LeftToRight, 1.5f);
        resolver.ReleaseImages();
        ImageBox[] images = EnumerateImages(document.Snapshot.Blocks).ToArray();
        Assert.Equal(33, images.Length);
        foreach (ImageBox image in images)
            await LoadImageAsync(image);
        InlineContainerBox inline = Assert.Single(EnumerateBoxes(document.Snapshot.Blocks)
            .OfType<InlineContainerBox>(), static candidate =>
                candidate.Runs.OfType<InlineVectorSceneRun>().Any());
        InlineVectorSceneRun vector = Assert.Single(inline.Runs.OfType<InlineVectorSceneRun>());
        VectorSceneDrawing drawing = GetVectorDrawing(vector);
        BlockBox[] treeBefore = EnumerateBoxes(document.Snapshot.Blocks).ToArray();
        Rect[] boundsBefore = treeBefore.Select(static box => box.Bounds).ToArray();
        CanvasBitmap[] bitmapsBefore = images.Select(static image => Assert.IsType<CanvasBitmap>(image.Bitmap)).ToArray();
        SharedCanvasBitmapCache.Lease[] leasesBefore = images.Select(GetBitmapLease).ToArray();
        long revisionBefore = document.Snapshot.LayoutRevision;
        int resolveCountBefore = resolver.ResolveCount;
        MarkdownLayoutContext? constructionContext = null;

        try
        {
            PreparedImageRelayoutResult result = document.Snapshot.TryRelayoutCompletedImageRoots(
                images.Select(static image => image.BlockIndex).Distinct().ToArray(),
                LayoutWidth,
                CancellationToken.None,
                out constructionContext,
                expectedPointerInputEpoch: document.Snapshot.PointerInputEpoch);

            Assert.Equal(PreparedImageRelayoutResult.Unavailable, result);
            Assert.Null(constructionContext);
            Assert.Equal(revisionBefore, document.Snapshot.LayoutRevision);
            Assert.Equal(resolveCountBefore, resolver.ResolveCount);
            Assert.Equal(treeBefore.Length, EnumerateBoxes(document.Snapshot.Blocks).Count());
            for (int index = 0; index < treeBefore.Length; index++)
            {
                Assert.Same(treeBefore[index], EnumerateBoxes(document.Snapshot.Blocks).ElementAt(index));
                Assert.Equal(boundsBefore[index], treeBefore[index].Bounds);
            }
            Assert.Same(drawing, GetVectorDrawing(vector));
            for (int index = 0; index < images.Length; index++)
            {
                Assert.Same(bitmapsBefore[index], images[index].Bitmap);
                Assert.Same(leasesBefore[index], GetBitmapLease(images[index]));
            }
        }
        finally
        {
            if (constructionContext is not null)
                await constructionContext.DrainOwnedRetirementsAsync().WaitAsync(AsyncDeadline);
        }
    }

    [Fact]
    public async Task TableNestedInListRemainsFailClosedForVectorImageRelayout()
    {
        await using var seed = new CompletedRasterPlacementCloneTests.PlacementFixture();
        using var session = new MarkdownPerformanceSession(MarkdownPerformanceOptions.Progressive);
        var resolver = new ContainerVectorResolver(imageCount: 2);
        string source = $"- lead ![outside]({resolver.GetUrl(0)}) with $x^2$\n\n" +
            "  | math | image |\n  | --- | --- |\n" +
            $"  | $y^2$ | ![nested]({resolver.GetUrl(1)}) |\n";
        await using DocumentFixture document = await BuildDocumentAsync(
            seed, session, resolver, source, FlowDirection.LeftToRight, 1.5f);
        resolver.ReleaseImages();
        ImageBox[] images = EnumerateImages(document.Snapshot.Blocks).ToArray();
        Assert.Equal(2, images.Length);
        foreach (ImageBox image in images)
            await LoadImageAsync(image);
        StackBox listRoot = Assert.Single(document.Snapshot.Blocks.OfType<StackBox>(),
            static root => root.Children.OfType<ListItemBox>().Any());
        Assert.NotEmpty(EnumerateBoxes([listRoot]).OfType<TableBox>());
        Assert.NotEmpty(EnumerateBoxes([listRoot]).OfType<InlineContainerBox>()
            .SelectMany(static inline => inline.Runs.OfType<InlineVectorSceneRun>()));
        BlockBox[] rootsBefore = document.Snapshot.Blocks.ToArray();
        BlockBox[] treeBefore = EnumerateBoxes(rootsBefore).ToArray();
        Rect[] rootBoundsBefore = rootsBefore.Select(static root => root.Bounds).ToArray();
        Rect[] boundsBefore = treeBefore.Select(static box => box.Bounds).ToArray();
        CanvasBitmap[] bitmapsBefore = images.Select(static image => Assert.IsType<CanvasBitmap>(image.Bitmap)).ToArray();
        SharedCanvasBitmapCache.Lease[] leasesBefore = images.Select(GetBitmapLease).ToArray();
        MarkdownSemanticDocument semanticBefore = document.Snapshot.SemanticDocument;
        MarkdownSourceMap sourceMapBefore = document.Snapshot.SourceMap;
        long revisionBefore = document.Snapshot.LayoutRevision;
        int resolveCountBefore = resolver.ResolveCount;
        MarkdownLayoutContext? constructionContext = null;

        try
        {
            PreparedImageRelayoutResult result = document.Snapshot.TryRelayoutCompletedImageRoots(
                [images[1].BlockIndex], LayoutWidth, CancellationToken.None, out constructionContext,
                expectedPointerInputEpoch: document.Snapshot.PointerInputEpoch);
            Assert.Equal(PreparedImageRelayoutResult.Unavailable, result);
            Assert.Null(constructionContext);
            Assert.Equal(revisionBefore, document.Snapshot.LayoutRevision);
            Assert.Same(semanticBefore, document.Snapshot.SemanticDocument);
            Assert.Same(sourceMapBefore, document.Snapshot.SourceMap);
            Assert.Equal(resolveCountBefore, resolver.ResolveCount);
            for (int index = 0; index < rootsBefore.Length; index++)
            {
                Assert.Same(rootsBefore[index], document.Snapshot.Blocks[index]);
                Assert.Equal(rootBoundsBefore[index], document.Snapshot.Blocks[index].Bounds);
            }
            for (int index = 0; index < treeBefore.Length; index++)
            {
                Assert.Same(treeBefore[index], EnumerateBoxes(document.Snapshot.Blocks).ElementAt(index));
                Assert.Equal(boundsBefore[index], treeBefore[index].Bounds);
            }
            for (int index = 0; index < images.Length; index++)
            {
                Assert.Same(bitmapsBefore[index], images[index].Bitmap);
                Assert.Same(leasesBefore[index], GetBitmapLease(images[index]));
            }
        }
        finally
        {
            if (constructionContext is not null)
                await constructionContext.DrainOwnedRetirementsAsync().WaitAsync(AsyncDeadline);
        }
    }

    private static string CreateListAndQuoteSource(ContainerVectorResolver resolver) =>
        $"- outer ![outer]({resolver.GetUrl(0)}) with $x^2$\n" +
        $"  - nested ![nested]({resolver.GetUrl(1)}) with $\\frac{{1}}{{2}}$\n\n" +
        $"> quote ![quote]({resolver.GetUrl(2)}) with $\\alpha + 1$\n\n" +
        "Trailing text stays outside the changed container roots.\n";

    private static void AssertExactPublishedContainerOrigin(LayoutSnapshot snapshot, BlockBox root)
    {
        LayoutRootPlacement placement = GetPublishedPlacement(snapshot, root);
        Assert.Equal(placement.BaseX, placement.X);
        Assert.Equal(placement.BaseTop, placement.Top);
        Assert.Equal(placement.BaseX, (float)root.Bounds.X);
        Assert.Equal(placement.BaseTop, (float)root.Bounds.Y);
    }

    private static LayoutRootPlacement GetPublishedPlacement(LayoutSnapshot snapshot, BlockBox root)
    {
        int ordinal = -1;
        for (int index = 0; index < snapshot.Blocks.Count; index++)
        {
            if (ReferenceEquals(snapshot.Blocks[index], root))
            {
                ordinal = index;
                break;
            }
        }
        Assert.InRange(ordinal, 0, snapshot.Blocks.Count - 1);
        return snapshot.PublishedPlacementGeneration.GetPlacement(ordinal);
    }

    private static async Task<DocumentFixture> BuildDocumentAsync(
        CompletedRasterPlacementCloneTests.PlacementFixture seed,
        MarkdownPerformanceSession session,
        ContainerVectorResolver resolver,
        string source,
        FlowDirection direction,
        float rasterizationScale)
    {
        MarkdownEngine engine = new MarkdownEngineBuilder()
            .UseProfile(MarkdownProfiles.GfmStrict)
            .UseMathematics()
            .WithParseCacheBudgetBytes(0)
            .Build();
        try
        {
            MarkdownDocument markdown = await engine.ParseAsync(source);
            var registry = new MarkdownExtensionRegistry().ConfigureGitHubReadmeRegistry();
            var sourceMap = new MarkdownSourceMap(markdown.Source);
            var context = new MarkdownLayoutContext(
                seed.Device,
                seed.SourceContext.ThemeSnapshot,
                sourceMap,
                registry,
                direction,
                language: "en-US")
            {
                CancellationToken = CancellationToken.None,
                ImageCancellationToken = CancellationToken.None,
                ImagePreparationOwner = seed.ImagePreparationOwnerToken,
                PerformanceDocumentOwner = new object(),
                PerformanceSession = session,
                ImageResolver = resolver,
                AllowThirdPartyRemoteImages = true,
                RasterizationScale = rasterizationScale,
            };

            LayoutSnapshot? snapshot = null;
            try
            {
                snapshot = new LayoutBuilder(context, semanticDocument: markdown)
                    .Build(markdown.ParsedDocument!, LayoutWidth, CancellationToken.None);
                snapshot.AssignAutomationDocumentGeneration(92_146);
                var fixture = new DocumentFixture(context, snapshot, engine);
                snapshot = null;
                engine = null!;
                return fixture;
            }
            catch
            {
                if (snapshot is not null)
                    await snapshot.Retire();
                await context.DrainOwnedRetirementsAsync();
                throw;
            }
        }
        catch
        {
            engine?.Dispose();
            throw;
        }
    }

    private static IEnumerable<BlockBox> EnumerateBoxes(IEnumerable<BlockBox> roots)
    {
        foreach (BlockBox box in roots)
        {
            yield return box;
            switch (box)
            {
                case StackBox stack:
                    foreach (BlockBox descendant in EnumerateBoxes(stack.Children))
                        yield return descendant;
                    break;
                case ListItemBox item:
                    foreach (BlockBox descendant in EnumerateBoxes([item.Marker, item.Content]))
                        yield return descendant;
                    break;
                case TableBox table:
                    foreach (InlineContainerBox cell in table.GetCellBoxes())
                        yield return cell;
                    break;
            }
        }
    }

    private static IEnumerable<ImageBox> EnumerateImages(IEnumerable<BlockBox> roots)
    {
        var seen = new HashSet<ImageBox>(ReferenceEqualityComparer.Instance);
        foreach (BlockBox box in EnumerateBoxes(roots))
        {
            if (box is ImageBox image && seen.Add(image))
                yield return image;
            if (box is InlineContainerBox inline)
            {
                foreach (var placement in inline.InlineImageRuns)
                    if (seen.Add(placement.Run.Image))
                        yield return placement.Run.Image;
            }
        }
    }

    private static Rect[][] CaptureVectorSelectionGeometry(
        LayoutSnapshot snapshot,
        IReadOnlyList<InlineContainerBox> inlines) =>
        inlines.SelectMany(inline => inline.Runs
            .Select((run, index) => (run, index))
            .Where(static item => item.run is InlineVectorSceneRun)
            .Select(item => SelectionFor(snapshot, inline, item.index, item.run)))
            .ToArray();

    private static Rect[] SelectionFor(
        LayoutSnapshot snapshot,
        InlineContainerBox inline,
        int runIndex,
        InlineRun run)
    {
        var range = new DocumentRange(
            new DocumentPosition(inline.BlockIndex, runIndex, 0),
            new DocumentPosition(inline.BlockIndex, runIndex, run.RenderedLength));
        var selection = new SelectionController();
        selection.SetAnchor(range.Start);
        selection.ExtendTo(range.End);
        return selection.GetHighlightRects(snapshot).ToArray();
    }

    private static InlineImagePlacementTrace CaptureInlineImagePlacement(LayoutSnapshot snapshot, ImageBox image)
    {
        InlineContainerBox? inlineOwner = EnumerateBoxes(snapshot.Blocks)
            .OfType<InlineContainerBox>()
            .FirstOrDefault(inline => inline.InlineImageRuns.Any(entry => ReferenceEquals(entry.Run.Image, image)));
        if (inlineOwner is null)
        {
            BlockBox topLevelOwner = Assert.Single(snapshot.Blocks, block => ReferenceEquals(block, image));
            if (!snapshot.TryBeginInteraction())
                throw new InvalidOperationException("Could not acquire a layout read lease.");
            try
            {
                using LayoutPlacementTransformContext.Scope placementScope =
                    snapshot.EnterPlacementTransformScopeForTopLevelBlockUnderPaint(topLevelOwner);
                LayoutPlacementTransformContext.GetCurrentTranslation(out _, out float translationY);
                Rect logicalBounds = snapshot.ToLogicalRectForTopLevelBlockUnderPaint(topLevelOwner, image.Bounds);
                return new InlineImagePlacementTrace(
                    logicalBounds, image.Bounds, topLevelOwner.Bounds, translationY, "standalone");
            }
            finally
            {
                snapshot.EndInteraction();
            }
        }

        if (!snapshot.TryBeginInteraction())
            throw new InvalidOperationException("Could not acquire a layout read lease.");
        try
        {
            using LayoutPlacementTransformContext.Scope placementScope =
                snapshot.EnterPlacementTransformScopeForBlockIndexUnderPaint(inlineOwner.BlockIndex);
            LayoutPlacementTransformContext.GetCurrentTranslation(out _, out float translationY);
            (InlineImageRun Run, Rect Rect) placement = Assert.Single(
                inlineOwner.EnumerateInlineImageRects(), entry => ReferenceEquals(entry.Run.Image, image));
            Rect logicalBounds = snapshot.ToLogicalRectForBlockIndexUnderPaint(
                inlineOwner.BlockIndex, placement.Rect);
            return new InlineImagePlacementTrace(
                logicalBounds, image.Bounds, inlineOwner.Bounds, translationY, "inline");
        }
        finally
        {
            snapshot.EndInteraction();
        }
    }

    private readonly record struct InlineImagePlacementTrace(
        Rect LogicalBounds,
        Rect ImageBoxBounds,
        Rect OwnerBounds,
        float PlacementTranslationY,
        string Kind);

    private static SemanticSpanGeometry[] CaptureSemanticSpanGeometry(LayoutSnapshot snapshot)
    {
        if (!snapshot.TryBeginInteraction())
            return [];
        try
        {
            MarkdownSemanticDocument semanticDocument = snapshot.SemanticDocument;
            Func<int, Rect, Rect> placementTransform = snapshot.CaptureLogicalRectMapper();
            return semanticDocument.TextSpans
                .Select(span => new SemanticSpanGeometry(
                    span.TextStart,
                    span.TextEnd,
                    span.InlineRun?.GetType(),
                    semanticDocument.Text.Substring(span.TextStart, span.TextEnd - span.TextStart),
                    semanticDocument.GetDocumentRects(
                        span.TextStart,
                        span.TextEnd,
                        expandDegenerate: false,
                        placementTransform: placementTransform,
                        placementScope: snapshot.EnterPlacementTransformScopeForBlockIndexUnderPaint).ToArray()))
                .ToArray();
        }
        finally
        {
            snapshot.EndInteraction();
        }
    }

    private static void AssertSemanticSpanGeometryEqual(
        SemanticSpanGeometry[] expected,
        SemanticSpanGeometry[] actual)
    {
        Assert.Equal(expected.Length, actual.Length);
        for (int index = 0; index < expected.Length; index++)
        {
            Assert.Equal(expected[index].Start, actual[index].Start);
            Assert.Equal(expected[index].End, actual[index].End);
            Assert.Equal(expected[index].RunType, actual[index].RunType);
            Assert.Equal(expected[index].Text, actual[index].Text);
            Assert.Equal(expected[index].DocumentRects, actual[index].DocumentRects);
        }
    }

    private static async Task LoadImageAsync(ImageBox image)
    {
        await DisplaySizedRasterDecodeTests.WaitForLoadAsync(image).WaitAsync(AsyncDeadline);
        await DisplaySizedRasterDecodeTests.WaitUntilAsync(
            () => image.ActiveImageWorkCountForTests == 0).WaitAsync(AsyncDeadline);
        Assert.NotNull(image.Bitmap);
    }

    private static CanvasTextLayout GetInlineLayout(InlineContainerBox inline) =>
        Assert.IsType<CanvasTextLayout>(ReadRawField(inline, "_layout"));

    private static SharedCanvasBitmapCache.Lease GetBitmapLease(ImageBox image) =>
        Assert.IsType<SharedCanvasBitmapCache.Lease>(ReadRawField(image, "_bitmapLease"));

    private static int GetLeaseReferenceCount(SharedCanvasBitmapCache.Lease lease)
    {
        object entry = Assert.IsAssignableFrom<SharedCanvasBitmapCache.Entry>(
            ReadRawField(lease, "_entry"));
        return Assert.IsType<int>(ReadRawField(entry, "_referenceCount"));
    }

    private static VectorSceneDrawing GetVectorDrawing(InlineVectorSceneRun run) =>
        Assert.IsType<VectorSceneDrawing>(ReadRawField(run, "_drawing"));

    private static object? ReadRawField(object instance, string name)
    {
        FieldInfo field = instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"Missing private test field '{name}'.");
        return field.GetValue(instance);
    }

    private static Color[] Paint(
        LayoutSnapshot snapshot,
        CanvasDevice device,
        float rasterizationScale,
        Matrix3x2? callerTransform = null)
    {
        float height = (float)System.Math.Max(1, System.Math.Ceiling(snapshot.Size.Height + 1));
        using var target = new CanvasRenderTarget(device, LayoutWidth, height, 96 * rasterizationScale);
        using (CanvasDrawingSession drawing = target.CreateDrawingSession())
        {
            drawing.Clear(Color.FromArgb(0, 0, 0, 0));
            if (callerTransform is Matrix3x2 transform)
                drawing.Transform = transform;
            snapshot.Paint(drawing, new Rect(0, 0, LayoutWidth, height));
        }
        return target.GetPixelColors();
    }

    private static string DescribeFirstPixelMismatch(
        LayoutSnapshot snapshot,
        LayoutSnapshot fallback,
        Color[] expected,
        Color[] actual,
        int pixelIndex,
        float rasterizationScale)
    {
        int pixelWidth = (int)System.Math.Ceiling(LayoutWidth * rasterizationScale);
        int pixelX = pixelIndex % pixelWidth;
        int pixelY = pixelIndex / pixelWidth;
        float dipX = pixelX / rasterizationScale;
        float dipY = pixelY / rasterizationScale;
        LayoutPlacementGeneration placements = snapshot.PublishedPlacementGeneration;
        string owners = string.Join("; ", Enumerable.Range(0, snapshot.Blocks.Count)
            .Select(ordinal =>
            {
                LayoutRootPlacement placement = placements.GetPlacement(ordinal);
                return $"{ordinal}:{snapshot.Blocks[ordinal].GetType().Name} " +
                    $"base={placement.BaseX},{placement.BaseTop},{placement.Width},{placement.Height} " +
                    $"logical={placement.X},{placement.Top},{placement.Width},{placement.Height}";
            }));
        string actualInline = DescribeInlineBoxesAt(snapshot, new Point(dipX, dipY));
        string fallbackInline = DescribeInlineBoxesAt(fallback, new Point(dipX, dipY));
        return $"rasterScale={rasterizationScale}; pixel=({pixelX},{pixelY}); dip=({dipX},{dipY}); " +
            $"expected={expected[pixelIndex]}; actual={actual[pixelIndex]}; roots=[{owners}]; " +
            $"actualInline=[{actualInline}]; fallbackInline=[{fallbackInline}]";
    }

    private static string DescribeInlineBoxesAt(LayoutSnapshot snapshot, Point point)
    {
        if (!snapshot.TryBeginInteraction())
            return "unavailable";
        try
        {
            return string.Join("; ", EnumerateBoxes(snapshot.Blocks)
                .OfType<InlineContainerBox>()
                .Where(inline => LayoutPlacementTestGeometry.ToLogical(snapshot, inline).Contains(point))
                .Select(inline =>
                {
                    using LayoutPlacementTransformContext.Scope placementScope =
                        snapshot.EnterPlacementTransformScopeForBlockIndexUnderPaint(inline.BlockIndex);
                    inline.GetSceneGeometryOrigin(out double originX, out double originY);
                    LayoutPlacementTransformContext.GetCurrentTranslation(out float translationX, out float translationY);
                    float paintOriginX = (float)originX;
                    float paintOriginY = (float)originY;
                    float finalPaintOriginX = paintOriginX + translationX;
                    float finalPaintOriginY = paintOriginY + translationY;
                    string runs = string.Join(",", inline.Runs.Select(run => run switch
                    {
                        InlineVectorSceneRun vector =>
                            $"vector:{vector.LocalBounds.X},{vector.LocalBounds.Y},{vector.LocalBounds.Width},{vector.LocalBounds.Height}",
                        InlineImageRun image => $"image:{image.DesiredWidth},{image.DesiredHeight}",
                        _ => run.GetType().Name,
                    }));
                    return $"block={inline.BlockIndex}; bounds={LayoutPlacementTestGeometry.ToLogical(snapshot, inline)}; " +
                        $"origin={originX},{originY}; translation={FormatFloat(translationX)},{FormatFloat(translationY)}; " +
                        $"localPaintOrigin={FormatFloat(paintOriginX)},{FormatFloat(paintOriginY)}; " +
                        $"finalPaintOrigin={FormatFloat(finalPaintOriginX)},{FormatFloat(finalPaintOriginY)}; runs=[{runs}]";
                }));
        }
        finally
        {
            snapshot.EndInteraction();
        }
    }

    private static string FormatFloat(float value) =>
        $"{value:R}/0x{BitConverter.SingleToInt32Bits(value):X8}";

    private readonly record struct SemanticSpanGeometry(
        int Start,
        int End,
        Type? RunType,
        string Text,
        Rect[] DocumentRects);

    private sealed class DocumentFixture(
        MarkdownLayoutContext context,
        LayoutSnapshot snapshot,
        MarkdownEngine engine) : IAsyncDisposable
    {
        internal MarkdownLayoutContext Context { get; } = context;
        internal LayoutSnapshot Snapshot { get; } = snapshot;
        private MarkdownEngine Engine { get; } = engine;

        public async ValueTask DisposeAsync()
        {
            try
            {
                await Snapshot.Retire().WaitAsync(AsyncDeadline);
            }
            finally
            {
                try
                {
                    await Context.DrainOwnedRetirementsAsync().WaitAsync(AsyncDeadline);
                }
                finally
                {
                    Engine.Dispose();
                }
            }
        }
    }

    private sealed class ContainerVectorResolver(int imageCount) : IMarkdownImageResolver
    {
        private readonly byte[] _png = DisplaySizedRasterDecodeTests.CreatePng(96, 192);
        private readonly string _id = Guid.NewGuid().ToString("N");
        private readonly string _cacheKey = "container-vector-relayout:" + Guid.NewGuid().ToString("N");
        private readonly TaskCompletionSource _imagesReleased =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _resolveCount;

        internal int ResolveCount => Volatile.Read(ref _resolveCount);
        internal string GetUrl(int index)
        {
            if ((uint)index >= (uint)imageCount)
                throw new ArgumentOutOfRangeException(nameof(index));
            return $"https://images.example.test/{_id}-{index}.png";
        }

        internal void ReleaseImages() => _imagesReleased.TrySetResult();

        public async ValueTask<MarkdownImageResolution> ResolveAsync(
            string source,
            MarkdownImageResolveContext context,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!Enumerable.Range(0, imageCount).Any(index =>
                    string.Equals(source, GetUrl(index), StringComparison.Ordinal)))
                throw new InvalidOperationException("Unexpected vector-container test image URL.");
            Interlocked.Increment(ref _resolveCount);
            await _imagesReleased.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            return MarkdownImageResolution.Resolved(new MarkdownImageAsset(
                _png,
                "image/png",
                new Uri(source),
                _cacheKey + ":" + source));
        }
    }
}
