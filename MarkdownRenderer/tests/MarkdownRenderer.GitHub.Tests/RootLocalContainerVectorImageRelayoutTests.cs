using System.Numerics;
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
using static MarkdownRenderer.GitHub.Tests.ContainerRelayoutTestTree;
using static MarkdownRenderer.GitHub.Tests.ContainerVectorPaintComparison;
using static MarkdownRenderer.GitHub.Tests.ContainerVectorParityAssertions;
using static MarkdownRenderer.GitHub.Tests.RootLocalContainerVectorRelayoutHarness;

namespace MarkdownRenderer.GitHub.Tests;

[Collection(SharedCanvasBitmapCacheTestCollection.Name)]
public sealed class RootLocalContainerVectorImageRelayoutTests
{
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

        await using ContainerVectorDocument optimized = await BuildDocumentAsync(
            seed, session, optimizedResolver, CreateListAndQuoteSource(optimizedResolver), direction,
            rasterizationScale);
        await using ContainerVectorDocument fallback = await BuildDocumentAsync(
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
        (StackBox listRoot, StackBox quoteRoot) = AssertListAndQuoteShape(snapshot);
        var before = ContainerVectorParityAssertions.SourceState.CaptureTree(snapshot, fallback.Snapshot);

        // The general private COW clone remains closed to measured vectors.
        // The image-completion root path below is the only opted-in measured
        // placement recipe; its measured clone is never published.
        await AssertOnlyMeasuredPlacementCaptureAdmitsVectorsAsync(optimized, listRoot, quoteRoot);
        before.CaptureResources(snapshot, images, optimizedResolver);

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

            before.AssertCommitMatchesFallback(snapshot, fallback.Snapshot, images, fallbackImages,
                optimizedResolver, rasterizationScale);
            AssertPaintMatchesFallback(snapshot, fallback.Snapshot, seed.Device, rasterizationScale);

            if (rasterizationScale == 1.5f && !rightToLeft && forceSoftwareRenderer)
            {
                Matrix3x2 callerTransform =
                    Matrix3x2.CreateScale(0.93f, 0.88f) *
                    Matrix3x2.CreateRotation(0.023f) *
                    Matrix3x2.CreateTranslation(12.25f, 7.75f);
                AssertPaintMatchesFallback(
                    snapshot, fallback.Snapshot, seed.Device, rasterizationScale, callerTransform);
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
        await using ContainerVectorDocument document = await BuildDocumentAsync(
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
                phaseHooks: CompletedImageRelayoutPhaseHooks.BeforeMeasure(cancellation.Cancel),
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
        await using ContainerVectorDocument stale = await BuildDocumentAsync(
            seed, session, staleResolver, $"- ![badge]({staleResolver.GetUrl(0)}) with $x^2$\n",
            FlowDirection.LeftToRight, 1f);
        staleResolver.ReleaseImages();
        await AssertStaleVectorFenceRetriesWithoutPublishingAsync(stale);

        var malformedResolver = new ContainerVectorResolver(imageCount: 1);
        await using ContainerVectorDocument malformed = await BuildDocumentAsync(
            seed, session, malformedResolver,
            $"- ![badge]({malformedResolver.GetUrl(0)}) with $y^2$\n",
            FlowDirection.LeftToRight, 1f);
        malformedResolver.ReleaseImages();
        await AssertMalformedVectorFenceIsUnavailableAsync(malformed);
    }

    private static async Task AssertStaleVectorFenceRetriesWithoutPublishingAsync(ContainerVectorDocument stale)
    {
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
                phaseHooks: CompletedImageRelayoutPhaseHooks.BeforeMeasure(() =>
                {
                    injected = true;
                    staleVector.RestoreLayoutState(staleVector.DesiredWidth, staleVector.DesiredHeight,
                        staleVector.DesiredBaseline, injectedBounds);
                }),
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
    }

    private static async Task AssertMalformedVectorFenceIsUnavailableAsync(ContainerVectorDocument malformed)
    {
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
        await using ContainerVectorDocument document = await BuildDocumentAsync(
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
        await using ContainerVectorDocument document = await BuildDocumentAsync(
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
}
