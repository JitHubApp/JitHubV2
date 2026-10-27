using System.Buffers;
using System.Reflection;
using MarkdownRenderer.Accessibility;
using System.Text;
using MarkdownRenderer.Document;
using MarkdownRenderer.Extensions;
using MarkdownRenderer.Html;
using MarkdownRenderer.Images;
using MarkdownRenderer.Layout;
using MarkdownRenderer.Layout.Boxes;
using MarkdownRenderer.Parsing;
using MarkdownRenderer.Performance;
using MarkdownRenderer.Selection;
using MarkdownRenderer.Theming;
using Markdig;
using Markdig.Syntax;
using Microsoft.Graphics.Canvas;
using Microsoft.UI.Xaml;
using Windows.Foundation;
using Windows.UI;
using Xunit;

namespace MarkdownRenderer.GitHub.Tests;

[Collection(SharedCanvasBitmapCacheTestCollection.Name)]
public sealed class RootLocalContainerImageRelayoutTests
{
    private const float LayoutWidth = 640f;
    private const long AutomationGeneration = 8_452;
    private static readonly TimeSpan AsyncDeadline = TimeSpan.FromSeconds(10);

    [Theory]
    [InlineData(1f, false, false)]
    [InlineData(1f, false, true)]
    [InlineData(1f, true, false)]
    [InlineData(1f, true, true)]
    [InlineData(1.5f, false, false)]
    [InlineData(1.5f, false, true)]
    [InlineData(1.5f, true, false)]
    [InlineData(1.5f, true, true)]
    [InlineData(2f, false, false)]
    [InlineData(2f, false, true)]
    [InlineData(2f, true, false)]
    [InlineData(2f, true, true)]
    [InlineData(4f, false, false)]
    [InlineData(4f, false, true)]
    [InlineData(4f, true, false)]
    [InlineData(4f, true, true)]
    public async Task CompletedNestedListAndBlockquoteImagesMatchFallbackAcrossScaleAndDirection(
        float rasterizationScale,
        bool rightToLeft,
        bool forceSoftwareRenderer)
    {
        await using var seed = new CompletedRasterPlacementCloneTests.PlacementFixture(
            forceSoftwareRenderer: forceSoftwareRenderer);
        Assert.Equal(forceSoftwareRenderer, seed.ForceSoftwareRenderer);
        using var session = new MarkdownPerformanceSession(MarkdownPerformanceOptions.Progressive);
        var optimizedResolver = new ContainerResolver(holdImages: true);
        var fallbackResolver = new ContainerResolver(holdImages: true);
        var optimizedSvgRenderer = new ContainerSvgRenderer();
        var fallbackSvgRenderer = new ContainerSvgRenderer();
        string optimizedSource = CreateRichListAndQuoteSource(optimizedResolver);
        string fallbackSource = CreateRichListAndQuoteSource(fallbackResolver);
        FlowDirection direction = rightToLeft ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;

        await using DocumentFixture optimized = BuildDocument(seed, session, optimizedResolver,
            optimizedSource, direction, rasterizationScale, svgRenderer: optimizedSvgRenderer);
        await using DocumentFixture fallback = BuildDocument(seed, session, fallbackResolver,
            fallbackSource, direction, rasterizationScale, svgRenderer: fallbackSvgRenderer);

        ApplyNonzeroListItemMarginsAndRefresh(optimized.Snapshot);
        ApplyNonzeroListItemMarginsAndRefresh(fallback.Snapshot);
        optimizedResolver.ReleaseImages();
        fallbackResolver.ReleaseImages();

        StackBox optimizedList = Assert.Single(optimized.Snapshot.Blocks.OfType<StackBox>(),
            stack => stack.Children.OfType<ListItemBox>().Any());
        StackBox fallbackList = Assert.Single(fallback.Snapshot.Blocks.OfType<StackBox>(),
            stack => stack.Children.OfType<ListItemBox>().Any());
        Assert.Equal(0, optimizedList.InterChildSpacing); // The blank line makes this list loose.
        Assert.Equal(0, fallbackList.InterChildSpacing);
        ListItemBox[] listItems = optimizedList.Children.OfType<ListItemBox>().ToArray();
        Assert.Equal(2, listItems.Length);
        StackBox nestedList = Assert.Single(listItems[0].Content.Children.OfType<StackBox>());
        Assert.True(nestedList.InterChildSpacing > 0, "Nested children must exercise tight-list spacing.");
        Assert.Equal(3, nestedList.Children.Count);
        StackBox optimizedQuote = Assert.Single(optimized.Snapshot.Blocks.OfType<StackBox>(),
            stack => !ReferenceEquals(stack, optimizedList));
        StackBox fallbackQuote = Assert.Single(fallback.Snapshot.Blocks.OfType<StackBox>(),
            stack => !ReferenceEquals(stack, fallbackList));
        Assert.NotEmpty(optimizedQuote.Children);

        ImageBox[] images = EnumerateImages(optimized.Snapshot.Blocks).ToArray();
        ImageBox[] fallbackImages = EnumerateImages(fallback.Snapshot.Blocks).ToArray();
        Assert.Equal(4, images.Length);
        Assert.Equal(images.Length, fallbackImages.Length);
        foreach (ImageBox image in images.Concat(fallbackImages))
            await LoadImageAsync(image);

        LayoutSnapshot snapshot = optimized.Snapshot;
        BlockBox[] rootsBefore = snapshot.Blocks.ToArray();
        Rect[] rootBoundsBefore = rootsBefore.Select(static root => root.Bounds).ToArray();
        BlockBox[] treeBefore = EnumerateBoxes(rootsBefore).ToArray();
        int[] blockIndicesBefore = treeBefore.Select(static box => box.BlockIndex).ToArray();
        InlineContainerBox[] inlinesBefore = treeBefore.OfType<InlineContainerBox>().ToArray();
        InlineRun[][] runsBefore = inlinesBefore.Select(static inline => inline.Runs.ToArray()).ToArray();
        InlineRun[] allRunsBefore = runsBefore.SelectMany(static runs => runs).ToArray();
        Assert.Contains(allRunsBefore, static run => run is LinkRun);
        Assert.Contains(allRunsBefore, static run => run is StrongRun ||
            run.HasStyleModifier(MarkdownElementKeys.Strong));
        CanvasBitmap[] bitmapsBefore = images.Select(static image => Assert.IsType<CanvasBitmap>(image.Bitmap)).ToArray();
        SharedCanvasBitmapCache.Lease[] leasesBefore = images.Select(GetBitmapLease).ToArray();
        int[] leaseReferenceCountsBefore = leasesBefore.Select(GetLeaseReferenceCount).ToArray();
        ImageBox svgImage = Assert.Single(images, image => image.Url == optimizedResolver.SvgUrl);
        MarkdownSvgDocumentInfo svgInfoBefore = GetPrivateField<MarkdownSvgDocumentInfo>(svgImage, "_svgInfo");
        Size sizeBefore = snapshot.Size;
        long revisionBefore = snapshot.LayoutRevision;
        MarkdownSourceMap sourceMapBefore = snapshot.SourceMap;
        MarkdownSemanticDocument semanticBefore = snapshot.SemanticDocument;
        long automationGenerationBefore = snapshot.AutomationDocumentGeneration;
        int[] resolveCountsBefore = optimizedResolver.CaptureResolveCounts();
        int svgOpenCountBefore = optimizedSvgRenderer.OpenCount;
        int svgRenderCountBefore = optimizedSvgRenderer.FullRenderCount;

        // The ordinary fallback is intentionally exercised with child image identities too. The
        // optimized entry point additionally receives the root IDs, in reverse/mixed order, to
        // prove that descendants consolidate to exactly one list and one quote root.
        int[] imageIndices = images.Select(static image => image.BlockIndex).Distinct().ToArray();
        fallback.Snapshot.RelayoutChangedBlocks(imageIndices, LayoutWidth, CancellationToken.None);
        var changedIndices = new HashSet<int>(imageIndices);
        changedIndices.Add(optimizedQuote.BlockIndex);
        changedIndices.Add(optimizedList.BlockIndex);
        var imagePlanBeforePaint = new RasterPlacementImagePlan(snapshot.Blocks, expectedCount: images.Length);
        Assert.Equal(images.Length, imagePlanBeforePaint.PublicationImages.Length);
        for (int index = 0; index < images.Length; index++)
            Assert.Same(images[index], imagePlanBeforePaint.PublicationImages[index]);

        MarkdownLayoutContext? constructionContext = null;
        try
        {
            PreparedImageRelayoutResult result = snapshot.TryRelayoutCompletedImageRoots(
                changedIndices, LayoutWidth, CancellationToken.None, out constructionContext,
                beforeMeasureOutsideGateForTests: () => AssertSourceLayoutLockReleased(snapshot),
                expectedPointerInputEpoch: snapshot.PointerInputEpoch);

            Assert.Equal(PreparedImageRelayoutResult.Committed, result);
            AssertExactPublishedContainerOrigin(snapshot, optimizedList);
            AssertExactPublishedContainerOrigin(snapshot, optimizedQuote);
            Assert.NotEqual(0f, GetPublishedPlacement(snapshot, optimizedQuote).BaseTop);
            // Publication retires the detached candidate asynchronously, never
            // under the source gate. Join that owner before checking the exact
            // pre-transfer lease counts, without changing the timeout or counts.
            Assert.NotNull(constructionContext);
            await constructionContext.DrainOwnedRetirementsAsync().WaitAsync(AsyncDeadline);
            Assert.Same(snapshot, optimized.Snapshot);
            Assert.Equal(revisionBefore + 1, snapshot.LayoutRevision);
            Assert.Equal(automationGenerationBefore, snapshot.AutomationDocumentGeneration);
            Assert.Same(sourceMapBefore, snapshot.SourceMap);
            Assert.Same(semanticBefore, snapshot.SemanticDocument);
            Assert.Equal(fallback.Snapshot.Size, snapshot.Size);
            Assert.Equal(rootsBefore.Length, snapshot.Blocks.Count);
            for (int index = 0; index < rootsBefore.Length; index++)
            {
                Assert.Same(rootsBefore[index], snapshot.Blocks[index]);
                Assert.Equal(LayoutPlacementTestGeometry.ToLogical(fallback.Snapshot, fallback.Snapshot.Blocks[index]),
                    LayoutPlacementTestGeometry.ToLogical(snapshot, snapshot.Blocks[index]));
            }

            BlockBox[] treeAfter = EnumerateBoxes(snapshot.Blocks).ToArray();
            BlockBox[] fallbackTree = EnumerateBoxes(fallback.Snapshot.Blocks).ToArray();
            Assert.Equal(treeBefore.Length, treeAfter.Length);
            Assert.Equal(treeBefore.Length, fallbackTree.Length);
            Assert.Equal(blockIndicesBefore, treeAfter.Select(static box => box.BlockIndex));
            for (int index = 0; index < treeBefore.Length; index++)
            {
                Assert.Same(treeBefore[index], treeAfter[index]);
                Assert.Equal(LayoutPlacementTestGeometry.ToLogical(fallback.Snapshot, fallbackTree[index]),
                    LayoutPlacementTestGeometry.ToLogical(snapshot, treeAfter[index]));
            }

            InlineContainerBox[] inlinesAfter = treeAfter.OfType<InlineContainerBox>().ToArray();
            InlineContainerBox[] fallbackInlines = fallbackTree.OfType<InlineContainerBox>().ToArray();
            Rect[][] uiGeometryAfter = CaptureSelectionGeometry(snapshot, inlinesAfter);
            Rect[][] fallbackUiGeometry = CaptureSelectionGeometry(fallback.Snapshot, fallbackInlines);
            Assert.Contains(uiGeometryAfter, static rectangles => rectangles.Length > 0);
            Assert.Equal(inlinesBefore.Length, inlinesAfter.Length);
            Assert.Equal(inlinesBefore.Length, fallbackInlines.Length);
            for (int inlineIndex = 0; inlineIndex < inlinesBefore.Length; inlineIndex++)
            {
                Assert.Same(inlinesBefore[inlineIndex], inlinesAfter[inlineIndex]);
                Assert.Equal(runsBefore[inlineIndex].Length, inlinesAfter[inlineIndex].Runs.Count);
                for (int runIndex = 0; runIndex < runsBefore[inlineIndex].Length; runIndex++)
                    Assert.Same(runsBefore[inlineIndex][runIndex], inlinesAfter[inlineIndex].Runs[runIndex]);
                Assert.Equal(fallbackUiGeometry[inlineIndex], uiGeometryAfter[inlineIndex]);
            }

            ImageBox[] imagesAfter = EnumerateImages(snapshot.Blocks).ToArray();
            Assert.Equal(images.Length, imagesAfter.Length);
            for (int index = 0; index < images.Length; index++)
            {
                Assert.Same(images[index], imagesAfter[index]);
                Assert.Same(bitmapsBefore[index], imagesAfter[index].Bitmap);
                Assert.Same(leasesBefore[index], GetBitmapLease(imagesAfter[index]));
                Assert.Equal(leaseReferenceCountsBefore[index], GetLeaseReferenceCount(leasesBefore[index]));
                Assert.Equal(LayoutPlacementTestGeometry.ToLogical(fallback.Snapshot, fallbackImages[index]),
                    LayoutPlacementTestGeometry.ToLogical(snapshot, imagesAfter[index]));
            }
            Assert.Same(svgInfoBefore, GetPrivateField<MarkdownSvgDocumentInfo>(svgImage, "_svgInfo"));

            Assert.Equal(resolveCountsBefore, optimizedResolver.CaptureResolveCounts());
            Assert.Equal(svgOpenCountBefore, optimizedSvgRenderer.OpenCount);
            Assert.Equal(svgRenderCountBefore, optimizedSvgRenderer.FullRenderCount);
            Assert.Equal(0, optimizedSvgRenderer.TileRenderCount);
            var committedImagePlan = new RasterPlacementImagePlan(
                snapshot, snapshot.Blocks, expectedCount: images.Length);
            Assert.Equal(images.Length, committedImagePlan.PublicationImages.Length);
            for (int index = 0; index < images.Length; index++)
            {
                Assert.Same(images[index], committedImagePlan.PublicationImages[index]);
                Rect logicalImageBounds = LayoutPlacementTestGeometry.ToLogical(snapshot, images[index]);
                Rect fallbackImageBounds = LayoutPlacementTestGeometry.ToLogical(fallback.Snapshot, fallbackImages[index]);
                Assert.Equal((logicalImageBounds.Top, logicalImageBounds.Bottom),
                    committedImagePlan.Index.GetBounds(index));
                Assert.Equal((fallbackImageBounds.Top, fallbackImageBounds.Bottom),
                    committedImagePlan.Index.GetBounds(index));
            }
            Assert.Equal(CaptureFinalTextOrigins(fallback.Snapshot), CaptureFinalTextOrigins(snapshot));
            Assert.Equal(CaptureCanvasTextLayoutMetrics(fallback.Snapshot), CaptureCanvasTextLayoutMetrics(snapshot));
            Assert.Equal(CaptureTextLayoutRegions(fallback.Snapshot), CaptureTextLayoutRegions(snapshot));
            foreach (BlockBox fallbackRoot in fallback.Snapshot.Blocks)
            {
                BlockBox optimizedRoot = Assert.Single(snapshot.Blocks,
                    root => root.BlockIndex == fallbackRoot.BlockIndex);
                Rect logicalRootBounds = LayoutPlacementTestGeometry.ToLogical(fallback.Snapshot, fallbackRoot);
                Rect rootViewport = new(0, logicalRootBounds.Top, LayoutWidth,
                    System.Math.Max(1, logicalRootBounds.Height));
                Color[] optimizedPixels = Paint(snapshot, seed.Device, rasterizationScale, rootViewport);
                Color[] fallbackPixels = Paint(fallback.Snapshot, seed.Device, rasterizationScale, rootViewport);
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
                    $"Fallback inlines: {CaptureInlinePaintDetails(fallback.Snapshot, fallbackRoot)}");
            }
            Assert.Equal(Paint(snapshot, seed.Device, rasterizationScale),
                Paint(fallback.Snapshot, seed.Device, rasterizationScale));
            Assert.NotEqual(sizeBefore, snapshot.Size);
        }
        finally
        {
            if (constructionContext is not null)
                await constructionContext.DrainOwnedRetirementsAsync().WaitAsync(AsyncDeadline);
        }
    }

    [Fact]
    public async Task ParsedListItemArrangeWithMarginsIsIdempotentAndRoundTripsTranslation()
    {
        await using var seed = new CompletedRasterPlacementCloneTests.PlacementFixture(forceSoftwareRenderer: true);
        using var session = new MarkdownPerformanceSession(MarkdownPerformanceOptions.Progressive);
        var resolver = new ContainerResolver();
        await using DocumentFixture document = BuildDocument(seed, session, resolver,
            "- **first item** with [a link](https://example.test/list)\n- second text-only item\n",
            FlowDirection.LeftToRight, 1.5f);
        StackBox list = Assert.Single(document.Snapshot.Blocks.OfType<StackBox>(),
            stack => stack.Children.OfType<ListItemBox>().Any());
        ListItemBox item = Assert.IsType<ListItemBox>(list.Children[0]);
        item.Margin = new Thickness(5, 7, 11, 13);
        _ = list.Measure(LayoutWidth);
        list.Arrange((float)list.Bounds.X, (float)list.Bounds.Y, LayoutWidth);

        Rect markerBefore = item.Marker.Bounds;
        Rect contentBefore = item.Content.Bounds;
        Rect itemBefore = item.Bounds;
        item.Arrange((float)itemBefore.X, (float)itemBefore.Y, (float)itemBefore.Width);
        Assert.Equal(markerBefore, item.Marker.Bounds);
        Assert.Equal(contentBefore, item.Content.Bounds);

        item.Arrange((float)itemBefore.X + 17, (float)itemBefore.Y + 23, (float)itemBefore.Width);
        item.Arrange((float)itemBefore.X, (float)itemBefore.Y, (float)itemBefore.Width);
        Assert.Equal(markerBefore, item.Marker.Bounds);
        Assert.Equal(contentBefore, item.Content.Bounds);
        Assert.Equal(itemBefore, item.Bounds);
    }

    [Theory]
    [InlineData(ContainerMutation.Margin)]
    [InlineData(ContainerMutation.ImageGeometry)]
    [InlineData(ContainerMutation.LayoutConfiguration)]
    [InlineData(ContainerMutation.ChildOrder)]
    public async Task SourceMutationDuringPrivateMeasurementRetriesWithoutPublishingPlacement(
        ContainerMutation mutation)
    {
        await using var seed = new CompletedRasterPlacementCloneTests.PlacementFixture(forceSoftwareRenderer: true);
        using var session = new MarkdownPerformanceSession(MarkdownPerformanceOptions.Progressive);
        var resolver = new ContainerResolver();
        var svgRenderer = new ContainerSvgRenderer();
        await using DocumentFixture document = BuildDocument(seed, session, resolver,
            CreateRichListAndQuoteSource(resolver), FlowDirection.LeftToRight, 1.5f,
            svgRenderer: svgRenderer);
        ImageBox[] images = EnumerateImages(document.Snapshot.Blocks).ToArray();
        Assert.NotEmpty(images);
        if (mutation == ContainerMutation.ImageGeometry)
        {
            Assert.True(EnumerateBoxes(document.Snapshot.Blocks.OfType<StackBox>())
                .OfType<ListItemBox>()
                .Where(static item => item.Content.Children.OfType<StackBox>().Any())
                .Any(item => EnumerateImages([item]).Contains(images[0])),
                "The injected geometry race must target an image inside the nested list subtree.");
        }
        foreach (ImageBox image in images)
            await LoadImageAsync(image);

        LayoutSnapshot snapshot = document.Snapshot;
        StackBox list = Assert.Single(snapshot.Blocks.OfType<StackBox>(),
            stack => stack.Children.OfType<ListItemBox>().Any());
        InlineContainerBox suppressedMarginLeaf = EnumerateBoxes([list])
            .OfType<InlineContainerBox>()
            .First(inline => !inline.UseContainerMargin &&
                inline.Runs.OfType<TextRun>().Any(static run =>
                    run.Text.Contains("nested text-only leaf", StringComparison.Ordinal)));
        // Tight-list paragraphs use explicit margins. Inject a real hidden raw margin
        // into that leaf, then establish its measured source baseline before the race.
        Assert.False(suppressedMarginLeaf.SuppressTopMargin);
        Assert.False(suppressedMarginLeaf.SuppressBottomMargin);
        Thickness unsuppressedRawMargin = suppressedMarginLeaf.RawPlacementMargin;
        Thickness seededRawMargin = new(
            unsuppressedRawMargin.Left + 1,
            unsuppressedRawMargin.Top + 7,
            unsuppressedRawMargin.Right + 1,
            unsuppressedRawMargin.Bottom + 5);
        suppressedMarginLeaf.Margin = seededRawMargin;
        suppressedMarginLeaf.SuppressTopMargin = true;
        suppressedMarginLeaf.SuppressBottomMargin = true;
        snapshot.RelayoutMeasuredBlocks(LayoutWidth, CancellationToken.None);
        Assert.Equal(seededRawMargin, suppressedMarginLeaf.RawPlacementMargin);
        Assert.Equal(0, suppressedMarginLeaf.Margin.Top);
        Assert.Equal(0, suppressedMarginLeaf.Margin.Bottom);
        BlockBox[] rootsBefore = snapshot.Blocks.ToArray();
        Rect[] boundsBefore = rootsBefore.Select(static root => root.Bounds).ToArray();
        BlockBox[] treeBefore = EnumerateBoxes(rootsBefore).ToArray();
        CanvasBitmap[] bitmapsBefore = images.Select(static image => Assert.IsType<CanvasBitmap>(image.Bitmap)).ToArray();
        SharedCanvasBitmapCache.Lease[] leasesBefore = images.Select(GetBitmapLease).ToArray();
        int[] leaseCountsBefore = leasesBefore.Select(GetLeaseReferenceCount).ToArray();
        long revisionBefore = snapshot.LayoutRevision;
        MarkdownSourceMap sourceMapBefore = snapshot.SourceMap;
        MarkdownSemanticDocument semanticBefore = snapshot.SemanticDocument;
        int[] resolveCountsBefore = resolver.CaptureResolveCounts();
        int svgOpenCountBefore = svgRenderer.OpenCount;
        int svgRenderCountBefore = svgRenderer.FullRenderCount;
        Thickness originalRawMargin = suppressedMarginLeaf.RawPlacementMargin;
        Thickness originalEffectiveMargin = suppressedMarginLeaf.Margin;
        Thickness mutatedRawMargin = new(originalRawMargin.Left, originalRawMargin.Top + 7,
            originalRawMargin.Right, originalRawMargin.Bottom + 3);
        Rect[] originalImageBounds = images.Select(static image => image.Bounds).ToArray();
        Microsoft.UI.Xaml.FlowDirection originalDirection = list.FlowDirection;
        BlockBox[] originalChildren = list.Children.ToArray();
        bool mutated = false;
        MarkdownLayoutContext? constructionContext = null;
        try
        {
            PreparedImageRelayoutResult result = snapshot.TryRelayoutCompletedImageRoots(
                images.Select(static image => image.BlockIndex).Distinct().ToArray(),
                LayoutWidth, CancellationToken.None, out constructionContext,
                beforeMeasureOutsideGateForTests: () =>
                {
                    AssertSourceLayoutLockReleased(snapshot);
                    mutated = true;
                    switch (mutation)
                    {
                        case ContainerMutation.Margin:
                            suppressedMarginLeaf.Margin = mutatedRawMargin;
                            break;
                        case ContainerMutation.ImageGeometry:
                            images[0].Arrange(
                                (float)originalImageBounds[0].X + 5,
                                (float)originalImageBounds[0].Y + 7,
                                (float)originalImageBounds[0].Width);
                            break;
                        case ContainerMutation.LayoutConfiguration:
                            list.FlowDirection = originalDirection == FlowDirection.LeftToRight
                                ? FlowDirection.RightToLeft
                                : FlowDirection.LeftToRight;
                            list.InterChildSpacing += 11;
                            break;
                        case ContainerMutation.ChildOrder:
                            BlockBox[] detached = list.DetachChildrenForTransfer();
                            foreach (BlockBox child in detached.Reverse())
                                list.Add(child);
                            break;
                        default:
                            throw new ArgumentOutOfRangeException(nameof(mutation));
                    }
                },
                expectedPointerInputEpoch: snapshot.PointerInputEpoch);

            Assert.True(mutated);
            Assert.Equal(PreparedImageRelayoutResult.Retry, result);
            // Candidate retirement is intentionally asynchronous and outside the
            // source gate. Join it before asserting that all borrowed leases
            // have returned to their exact pre-attempt counts.
            if (constructionContext is not null)
                await constructionContext.DrainOwnedRetirementsAsync().WaitAsync(AsyncDeadline);
            Assert.Equal(revisionBefore, snapshot.LayoutRevision);
            Assert.Same(sourceMapBefore, snapshot.SourceMap);
            Assert.Same(semanticBefore, snapshot.SemanticDocument);
            Assert.Equal(resolveCountsBefore, resolver.CaptureResolveCounts());
            Assert.Equal(svgOpenCountBefore, svgRenderer.OpenCount);
            Assert.Equal(svgRenderCountBefore, svgRenderer.FullRenderCount);
            Assert.Equal(rootsBefore.Length, snapshot.Blocks.Count);
            for (int index = 0; index < rootsBefore.Length; index++)
            {
                Assert.Same(rootsBefore[index], snapshot.Blocks[index]);
                Assert.Equal(boundsBefore[index], snapshot.Blocks[index].Bounds);
            }
            BlockBox[] treeAfterRace = EnumerateBoxes(snapshot.Blocks).ToArray();
            Assert.Equal(treeBefore.Length, treeAfterRace.Length);
            Assert.All(images, image => Assert.Contains(image, EnumerateImages(snapshot.Blocks)));
            for (int index = 0; index < images.Length; index++)
            {
                Assert.Same(bitmapsBefore[index], images[index].Bitmap);
                Assert.Same(leasesBefore[index], GetBitmapLease(images[index]));
                Assert.Equal(leaseCountsBefore[index], GetLeaseReferenceCount(leasesBefore[index]));
            }

            if (mutation == ContainerMutation.Margin)
            {
                Assert.Equal(mutatedRawMargin, suppressedMarginLeaf.RawPlacementMargin);
                Assert.Equal(originalEffectiveMargin, suppressedMarginLeaf.Margin);
            }
            if (mutation == ContainerMutation.LayoutConfiguration)
                Assert.NotEqual(originalDirection, list.FlowDirection);
            if (mutation == ContainerMutation.ChildOrder)
                Assert.Equal(originalChildren.Reverse(), list.Children);
            if (mutation == ContainerMutation.ImageGeometry)
            {
                Assert.Equal(originalImageBounds[0].X + 5, images[0].Bounds.X);
                Assert.Equal(originalImageBounds[0].Y + 7, images[0].Bounds.Y);
            }
        }
        finally
        {
            // Restore the source only after the fence has observed the injected race. This keeps
            // teardown ownership valid even if an assertion below fails.
            if (mutated)
            {
                if (mutation == ContainerMutation.Margin)
                    suppressedMarginLeaf.Margin = originalRawMargin;
                else if (mutation == ContainerMutation.ImageGeometry)
                    images[0].Arrange((float)originalImageBounds[0].X,
                        (float)originalImageBounds[0].Y, (float)originalImageBounds[0].Width);
                else if (mutation == ContainerMutation.LayoutConfiguration)
                {
                    list.FlowDirection = originalDirection;
                    list.InterChildSpacing -= 11;
                }
                else if (mutation == ContainerMutation.ChildOrder)
                {
                    _ = list.DetachChildrenForTransfer();
                    foreach (BlockBox child in originalChildren)
                        list.Add(child);
                }
            }
            suppressedMarginLeaf.SuppressTopMargin = false;
            suppressedMarginLeaf.SuppressBottomMargin = false;
            suppressedMarginLeaf.Margin = unsuppressedRawMargin;
            snapshot.RelayoutMeasuredBlocks(LayoutWidth, CancellationToken.None);
            if (constructionContext is not null)
                await constructionContext.DrainOwnedRetirementsAsync().WaitAsync(AsyncDeadline);
        }
    }

    [Fact]
    public async Task CancellationAndPointerEpochChangesDoNotTransferSourcePlacement()
    {
        await using var seed = new CompletedRasterPlacementCloneTests.PlacementFixture(forceSoftwareRenderer: true);
        using var session = new MarkdownPerformanceSession(MarkdownPerformanceOptions.Progressive);
        var resolver = new ContainerResolver();
        var svgRenderer = new ContainerSvgRenderer();
        await using DocumentFixture document = BuildDocument(seed, session, resolver,
            CreateRichListAndQuoteSource(resolver), FlowDirection.RightToLeft, 1.5f,
            svgRenderer: svgRenderer);
        ImageBox[] images = EnumerateImages(document.Snapshot.Blocks).ToArray();
        foreach (ImageBox image in images)
            await LoadImageAsync(image);
        LayoutSnapshot snapshot = document.Snapshot;
        BlockBox[] rootsBefore = snapshot.Blocks.ToArray();
        Rect[] boundsBefore = rootsBefore.Select(static root => root.Bounds).ToArray();
        CanvasBitmap[] bitmapsBefore = images.Select(static image => Assert.IsType<CanvasBitmap>(image.Bitmap)).ToArray();
        SharedCanvasBitmapCache.Lease[] leasesBefore = images.Select(GetBitmapLease).ToArray();
        int[] leaseCountsBefore = leasesBefore.Select(GetLeaseReferenceCount).ToArray();
        long revisionBefore = snapshot.LayoutRevision;
        int[] resolveCountsBefore = resolver.CaptureResolveCounts();
        int[] changed = images.Select(static image => image.BlockIndex).Distinct().ToArray();

        using (var canceled = new CancellationTokenSource())
        {
            MarkdownLayoutContext? canceledContext = null;
            try
            {
                Assert.ThrowsAny<OperationCanceledException>(() => snapshot.TryRelayoutCompletedImageRoots(
                    changed, LayoutWidth, canceled.Token, out canceledContext,
                    beforeMeasureOutsideGateForTests: canceled.Cancel,
                    expectedPointerInputEpoch: snapshot.PointerInputEpoch));
            }
            finally
            {
                if (canceledContext is not null)
                    await canceledContext.DrainOwnedRetirementsAsync().WaitAsync(AsyncDeadline);
            }
        }

        PreparedImageRelayoutResult pointerResult = snapshot.TryRelayoutCompletedImageRoots(
            changed, LayoutWidth, CancellationToken.None, out MarkdownLayoutContext? pointerContext,
            expectedPointerInputEpoch: snapshot.PointerInputEpoch - 1);
        Assert.Null(pointerContext);
        Assert.Equal(PreparedImageRelayoutResult.Retry, pointerResult);
        Assert.Equal(revisionBefore, snapshot.LayoutRevision);
        for (int index = 0; index < rootsBefore.Length; index++)
        {
            Assert.Same(rootsBefore[index], snapshot.Blocks[index]);
            Assert.Equal(boundsBefore[index], snapshot.Blocks[index].Bounds);
        }
        for (int index = 0; index < images.Length; index++)
        {
            Assert.Same(bitmapsBefore[index], images[index].Bitmap);
            Assert.Same(leasesBefore[index], GetBitmapLease(images[index]));
            Assert.Equal(leaseCountsBefore[index], GetLeaseReferenceCount(leasesBefore[index]));
        }
        Assert.Equal(resolveCountsBefore, resolver.CaptureResolveCounts());
        Assert.Equal(1, svgRenderer.OpenCount);
        Assert.Equal(1, svgRenderer.FullRenderCount);
        Assert.Equal(0, svgRenderer.TileRenderCount);
    }

    [Fact]
    public async Task LargeLazyListTransferLeavesPendingSiblingAndDistantUnmeasuredImageUntouched()
    {
        await using var seed = new CompletedRasterPlacementCloneTests.PlacementFixture(forceSoftwareRenderer: true);
        using var session = new MarkdownPerformanceSession(MarkdownPerformanceOptions.Progressive);
        var resolver = new ContainerResolver(holdPending: true);
        string source = $"- ![target]({resolver.FirstUrl})\n\n" +
            $"![pending sibling]({resolver.PendingUrl})\n\n" +
            string.Join("\n\n", Enumerable.Range(0, 80).Select(static index =>
                $"Lazy sibling {index}: preserve the estimated block and do not realize distant content.")) +
            $"\n\n- ![distant unmeasured image]({resolver.FarUrl})\n";
        await using DocumentFixture document = BuildDocument(seed, session, resolver,
            source, FlowDirection.LeftToRight, 1.5f, lazy: true);
        LayoutSnapshot snapshot = document.Snapshot;
        Assert.True(snapshot.IsLazyLayoutEnabled);
        Assert.True(snapshot.TopLevelBlockCount > 64);
        ImageBox target = Assert.Single(EnumerateImages(snapshot.Blocks), image => image.Url == resolver.FirstUrl);
        ImageBox pending = Assert.Single(EnumerateImages(snapshot.Blocks), image => image.Url == resolver.PendingUrl);
        ImageBox far = Assert.Single(EnumerateImages(snapshot.Blocks), image => image.Url == resolver.FarUrl);
        Assert.False(target.IsDirty, "The initial lazy viewport must include the changed list root.");
        Assert.True(far.IsDirty);
        Assert.Null(far.Bitmap);
        Assert.Equal(0, resolver.ResolveCountFor(resolver.FarUrl));
        await LoadImageAsync(target);
        pending.EnsureLoading();
        await resolver.WaitForPendingResolveAsync().WaitAsync(AsyncDeadline);

        BlockBox farRoot = Assert.Single(snapshot.Blocks,
            root => EnumerateImages([root]).Any(image => ReferenceEquals(image, far)));
        Rect farBoundsBefore = farRoot.Bounds;
        bool farDirtyBefore = far.IsDirty;
        int measuredBefore = snapshot.MeasuredTopLevelBlockCount;
        int pendingResolveCountBefore = resolver.ResolveCountFor(resolver.PendingUrl);
        int targetResolveCountBefore = resolver.ResolveCountFor(resolver.FirstUrl);
        long revisionBefore = snapshot.LayoutRevision;
        BlockBox[] rootsBefore = snapshot.Blocks.ToArray();
        CanvasBitmap? pendingBitmapBefore = pending.Bitmap;
        int[] targetIdentity = [target.BlockIndex];

        MarkdownLayoutContext? constructionContext = null;
        try
        {
            PreparedImageRelayoutResult result = snapshot.TryRelayoutCompletedImageRoots(
                targetIdentity, LayoutWidth, CancellationToken.None, out constructionContext,
                expectedPointerInputEpoch: snapshot.PointerInputEpoch);
            Assert.Equal(PreparedImageRelayoutResult.Committed, result);
            Assert.Equal(revisionBefore + 1, snapshot.LayoutRevision);
            Assert.Equal(measuredBefore, snapshot.MeasuredTopLevelBlockCount);
            for (int index = 0; index < rootsBefore.Length; index++)
                Assert.Same(rootsBefore[index], snapshot.Blocks[index]);
            Assert.Same(far, Assert.Single(EnumerateImages(snapshot.Blocks), image => image.Url == resolver.FarUrl));
            Assert.True(far.IsDirty);
            Assert.Equal(farDirtyBefore, far.IsDirty);
            Assert.Null(far.Bitmap);
            BlockBox farRootAfter = Assert.Single(snapshot.Blocks,
                root => EnumerateImages([root]).Any(image => ReferenceEquals(image, far)));
            Assert.Equal(farBoundsBefore.X, farRootAfter.Bounds.X);
            Assert.Equal(farBoundsBefore.Width, farRootAfter.Bounds.Width);
            Assert.Equal(farBoundsBefore.Height, farRootAfter.Bounds.Height);
            Assert.Same(pending, Assert.Single(EnumerateImages(snapshot.Blocks), image => image.Url == resolver.PendingUrl));
            Assert.Same(pendingBitmapBefore, pending.Bitmap);
            Assert.True(pending.ActiveImageWorkCountForTests > 0,
                "The pending sibling's active resolver work must survive another root's transfer.");
            Assert.Equal(pendingResolveCountBefore, resolver.ResolveCountFor(resolver.PendingUrl));
            Assert.Equal(targetResolveCountBefore, resolver.ResolveCountFor(resolver.FirstUrl));
            Assert.Equal(0, resolver.ResolveCountFor(resolver.FarUrl));
        }
        finally
        {
            if (constructionContext is not null)
                await constructionContext.DrainOwnedRetirementsAsync().WaitAsync(AsyncDeadline);
        }
    }

    [Fact]
    public async Task ContainerTransferRejectsAggregateImageBudgetBeforeAcquiringAnyCloneLeases()
    {
        await using var seed = new CompletedRasterPlacementCloneTests.PlacementFixture(forceSoftwareRenderer: true);
        using var session = new MarkdownPerformanceSession(MarkdownPerformanceOptions.Progressive);
        var resolver = new ContainerResolver();
        await using DocumentFixture document = BuildDocument(seed, session, resolver,
            CreateBudgetListSource(resolver, itemCount: 33, imageCount: 33),
            FlowDirection.LeftToRight, 1.5f);

        StackBox root = Assert.Single(document.Snapshot.Blocks.OfType<StackBox>());
        Assert.All(root.Children, static child => Assert.IsType<ListItemBox>(child));
        Assert.True(EnumerateBoxes([root]).Count() < PrivateRasterPlacementTree.MaximumNodes,
            "The fixture must stay below the node cap so image count is the rejecting budget.");
        ImageBox[] images = EnumerateImages([root]).ToArray();
        Assert.Equal(ImageBox.MaximumMeasuredPlacementTransferBatchCount + 1, images.Length);
        await LoadAllImagesAsync(images);
        AssertContainerShapeIsSupportedAndMeasured([root]);

        await AssertContainerBudgetRejectedBeforeCloneAsync(
            document, [images[0].BlockIndex], resolver);
    }

    [Fact]
    public async Task ContainerTransferRejectsAggregateRunBudgetAcrossLeavesBeforeAcquiringAnyCloneLeases()
    {
        await using var seed = new CompletedRasterPlacementCloneTests.PlacementFixture(forceSoftwareRenderer: true);
        using var session = new MarkdownPerformanceSession(MarkdownPerformanceOptions.Progressive);
        var resolver = new ContainerResolver();
        const int formattedParagraphCount = 40;
        await using DocumentFixture document = BuildDocument(seed, session, resolver,
            CreateRunBudgetQuoteSource(resolver, formattedParagraphCount),
            FlowDirection.LeftToRight, 1.5f);

        StackBox root = Assert.Single(document.Snapshot.Blocks.OfType<StackBox>());
        InlineContainerBox[] leaves = EnumerateBoxes([root]).OfType<InlineContainerBox>().ToArray();
        int aggregateRuns = leaves.Sum(static inline =>
            inline.Runs.Count + inline.PrivateRasterPlacementForegroundSpanCount);
        Assert.True(aggregateRuns > InlineContainerBox.MaximumPrivateRasterPlacementRuns,
            $"Fixture has {aggregateRuns} aggregate runs; the cap is " +
            $"{InlineContainerBox.MaximumPrivateRasterPlacementRuns}.");
        Assert.All(leaves, inline =>
            Assert.True(inline.Runs.Count < InlineContainerBox.MaximumPrivateRasterPlacementRuns,
                "Each leaf must individually fit so aggregate run count is the rejecting budget."));
        Assert.True(EnumerateBoxes([root]).Count() < PrivateRasterPlacementTree.MaximumNodes,
            "The fixture must stay below the node cap so run count is the rejecting budget.");
        ImageBox[] images = EnumerateImages([root]).ToArray();
        Assert.Single(images);
        await LoadAllImagesAsync(images);
        AssertContainerShapeIsSupportedAndMeasured([root]);

        await AssertContainerBudgetRejectedBeforeCloneAsync(
            document, [images[0].BlockIndex], resolver);
    }

    [Fact]
    public async Task ContainerTransferRejectsSingleLeafRunBudgetBeforeAcquiringAnyCloneLeases()
    {
        await using var seed = new CompletedRasterPlacementCloneTests.PlacementFixture(forceSoftwareRenderer: true);
        using var session = new MarkdownPerformanceSession(MarkdownPerformanceOptions.Progressive);
        var resolver = new ContainerResolver();
        await using DocumentFixture document = BuildDocument(seed, session, resolver,
            CreateSingleLeafRunBudgetListSource(resolver, repeatedPairs: 150),
            FlowDirection.LeftToRight, 1.5f);

        StackBox root = Assert.Single(document.Snapshot.Blocks.OfType<StackBox>());
        InlineContainerBox leaf = Assert.Single(EnumerateBoxes([root]).OfType<InlineContainerBox>(),
            static inline => inline.ElementKey == MarkdownElementKeys.Body);
        Assert.True(leaf.Runs.Count > InlineContainerBox.MaximumPrivateRasterPlacementRuns,
            $"Fixture has {leaf.Runs.Count} runs in one leaf; the cap is " +
            $"{InlineContainerBox.MaximumPrivateRasterPlacementRuns}.");
        Assert.True(leaf.PrivateRasterPlacementTextLength < InlineContainerBox.MaximumPrivateRasterPlacementTextLength,
            "Text length must stay below its independent cap so run count is the rejecting budget.");
        Assert.True(EnumerateBoxes([root]).Count() < PrivateRasterPlacementTree.MaximumNodes,
            "The fixture must stay below the node cap so the per-leaf run count is the rejecting budget.");
        ImageBox[] images = EnumerateImages([root]).ToArray();
        Assert.Single(images);
        Assert.True(images.Length <= InlineContainerBox.MaximumPrivateRasterPlacementImages);
        foreach (ImageBox image in images)
            await LoadImageAsync(image);
        // The deliberately huge paragraph can finish image loading before its
        // inline layout has been re-realized. Re-run the normal measured layout
        // pass after load so the rejection is specifically the run cap, not an
        // incomplete source paragraph. Inline ImageBox.IsDirty is intentionally
        // not cleared by inline placement; its completed measured state is
        // checked separately below.
        document.Snapshot.RelayoutMeasuredBlocks(LayoutWidth, CancellationToken.None);
        Assert.True(leaf.HasMeasuredLayout);
        Assert.False(leaf.IsDirty);
        Assert.True(float.IsFinite((float)leaf.Bounds.Width) && leaf.Bounds.Width > 0);
        AssertContainerShapeIsSupportedAndMeasured([root]);

        await AssertContainerBudgetRejectedBeforeCloneAsync(
            document, [images[0].BlockIndex], resolver);
    }

    [Fact]
    public async Task ContainerTransferRejectsAggregateNodeBudgetBeforeAcquiringAnyCloneLeases()
    {
        await using var seed = new CompletedRasterPlacementCloneTests.PlacementFixture(forceSoftwareRenderer: true);
        using var session = new MarkdownPerformanceSession(MarkdownPerformanceOptions.Progressive);
        var resolver = new ContainerResolver();
        await using DocumentFixture document = BuildDocument(seed, session, resolver,
            CreateBudgetListSource(resolver, itemCount: 65, imageCount: 1),
            FlowDirection.LeftToRight, 1.5f);

        StackBox root = Assert.Single(document.Snapshot.Blocks.OfType<StackBox>());
        int nodeCount = EnumerateBoxes([root]).Count();
        Assert.True(nodeCount > PrivateRasterPlacementTree.MaximumNodes,
            $"Fixture has {nodeCount} structural nodes; the cap is {PrivateRasterPlacementTree.MaximumNodes}.");
        Assert.Equal(65, root.Children.Count);
        ImageBox[] images = EnumerateImages([root]).ToArray();
        Assert.Single(images);
        await LoadAllImagesAsync(images);
        AssertContainerShapeIsSupportedAndMeasured([root]);

        await AssertContainerBudgetRejectedBeforeCloneAsync(
            document, [images[0].BlockIndex], resolver);
    }

    [Fact]
    public async Task ContainerTransferRejectsDepthOverBudgetBeforeAcquiringAnyCloneLeases()
    {
        await using var seed = new CompletedRasterPlacementCloneTests.PlacementFixture(forceSoftwareRenderer: true);
        using var session = new MarkdownPerformanceSession(MarkdownPerformanceOptions.Progressive);
        var resolver = new ContainerResolver();
        await using DocumentFixture document = BuildDocument(seed, session, resolver,
            CreateDeepListSource(resolver, nestingDepth: 12),
            FlowDirection.LeftToRight, 1.5f);

        StackBox root = Assert.Single(document.Snapshot.Blocks.OfType<StackBox>());
        int nodeCount = EnumerateBoxes([root]).Count();
        int depth = GetMaximumBoxDepth([root]);
        Assert.True(depth > PrivateRasterPlacementTree.MaximumDepth,
            $"Fixture depth is {depth}; the cap is {PrivateRasterPlacementTree.MaximumDepth}.");
        Assert.True(nodeCount < PrivateRasterPlacementTree.MaximumNodes,
            "The fixture must stay below the node cap so depth is the rejecting budget.");
        ImageBox[] images = EnumerateImages([root]).ToArray();
        Assert.Single(images);
        await LoadAllImagesAsync(images);
        AssertContainerShapeIsSupportedAndMeasured([root]);

        await AssertContainerBudgetRejectedBeforeCloneAsync(
            document, [images[0].BlockIndex], resolver);
    }

    [Fact]
    public async Task ContainerTransferRejectsAggregateImageBudgetAcrossRootsBeforeAcquiringAnyCloneLeases()
    {
        await using var seed = new CompletedRasterPlacementCloneTests.PlacementFixture(forceSoftwareRenderer: true);
        using var session = new MarkdownPerformanceSession(MarkdownPerformanceOptions.Progressive);
        var resolver = new ContainerResolver();
        const int imagesPerRoot = 17;
        string source = CreateBudgetListSource(resolver, itemCount: imagesPerRoot, imageCount: imagesPerRoot) +
                        "\n\nUnchanged paragraph between eligible roots.\n\n" +
                        CreateBudgetListSource(resolver, itemCount: imagesPerRoot, imageCount: imagesPerRoot);
        await using DocumentFixture document = BuildDocument(seed, session, resolver,
            source, FlowDirection.LeftToRight, 1.5f);

        StackBox[] roots = document.Snapshot.Blocks.OfType<StackBox>()
            .Where(static stack => stack.Children.OfType<ListItemBox>().Any())
            .ToArray();
        Assert.Equal(2, roots.Length);
        ImageBox[][] rootImages = roots.Select(static root => EnumerateImages([root]).ToArray()).ToArray();
        Assert.All(rootImages, images => Assert.Equal(imagesPerRoot, images.Length));
        Assert.True(rootImages.Sum(static images => images.Length) >
                    ImageBox.MaximumMeasuredPlacementTransferBatchCount);
        Assert.True(roots.Sum(root => EnumerateBoxes([root]).Count()) <= PrivateRasterPlacementTree.MaximumNodes,
            "The roots must stay within the aggregate node cap so aggregate image count is the rejecting budget.");
        ImageBox[] images = rootImages.SelectMany(static group => group).ToArray();
        await LoadAllImagesAsync(images);
        AssertContainerShapeIsSupportedAndMeasured(roots);

        // One representative descendant per root keeps the input request under its
        // 32-root dispatch cap while exercising the cross-root aggregate image budget.
        await AssertContainerBudgetRejectedBeforeCloneAsync(
            document, [rootImages[0][0].BlockIndex, rootImages[1][0].BlockIndex], resolver);
    }

    [Theory]
    [InlineData(UnsupportedNestedShape.Code)]
    [InlineData(UnsupportedNestedShape.TaskList)]
    [InlineData(UnsupportedNestedShape.Html)]
    [InlineData(UnsupportedNestedShape.Table)]
    public async Task UnsupportedNestedContainerShapeRejectsBeforeChangingImages(
        UnsupportedNestedShape shape)
    {
        await using var seed = new CompletedRasterPlacementCloneTests.PlacementFixture(forceSoftwareRenderer: true);
        using var session = new MarkdownPerformanceSession(MarkdownPerformanceOptions.Progressive);
        var resolver = new ContainerResolver();
        await using DocumentFixture document = BuildDocument(seed, session, resolver,
            CreateUnsupportedSource(resolver, shape), FlowDirection.LeftToRight, 1.5f,
            enableSafeHtml: shape == UnsupportedNestedShape.Html);
        if (shape == UnsupportedNestedShape.Html)
        {
            Assert.NotNull(document.Context.Registry.SafeHtmlPolicy);
            Assert.True(document.Context.Registry.TryGetRenderer(typeof(HtmlBlock), out _));
            Markdig.Syntax.MarkdownDocument parsed = Markdown.Parse(
                CreateUnsupportedSource(resolver, shape), document.Context.Registry.BuildPipeline());
            Assert.Contains(parsed.Descendants(), static node => node is HtmlBlock);
            Assert.Contains(EnumerateBoxes(document.Snapshot.Blocks).OfType<StackBox>(),
                static stack => stack.HasResolvedSafeHtmlAlignment);
            Assert.Contains(EnumerateBoxes(document.Snapshot.Blocks).OfType<InlineContainerBox>(),
                static inline => inline.Runs.OfType<TextRun>().Any(static run =>
                    run.Text.Contains("raw inline html", StringComparison.Ordinal)));
        }
        ImageBox[] images = EnumerateImages(document.Snapshot.Blocks).ToArray();
        Assert.NotEmpty(images);
        foreach (ImageBox image in images)
            await LoadImageAsync(image);

        LayoutSnapshot snapshot = document.Snapshot;
        BlockBox[] rootsBefore = snapshot.Blocks.ToArray();
        Rect[] boundsBefore = rootsBefore.Select(static root => root.Bounds).ToArray();
        BlockBox[] boxesBefore = EnumerateBoxes(rootsBefore).ToArray();
        CanvasBitmap[] bitmapsBefore = images.Select(static image => Assert.IsType<CanvasBitmap>(image.Bitmap)).ToArray();
        SharedCanvasBitmapCache.Lease[] leasesBefore = images.Select(GetBitmapLease).ToArray();
        int[] leaseCountsBefore = leasesBefore.Select(GetLeaseReferenceCount).ToArray();
        int[] resolveCountsBefore = resolver.CaptureResolveCounts();
        long revisionBefore = snapshot.LayoutRevision;
        MarkdownSourceMap sourceMapBefore = snapshot.SourceMap;
        MarkdownSemanticDocument semanticBefore = snapshot.SemanticDocument;
        MarkdownLayoutContext? constructionContext = null;

        try
        {
            PreparedImageRelayoutResult result = snapshot.TryRelayoutCompletedImageRoots(
                images.Select(static image => image.BlockIndex).Distinct().ToArray(),
                LayoutWidth, CancellationToken.None, out constructionContext,
                expectedPointerInputEpoch: snapshot.PointerInputEpoch);
            Assert.Equal(PreparedImageRelayoutResult.Unavailable, result);
            Assert.Null(constructionContext);
            Assert.Equal(revisionBefore, snapshot.LayoutRevision);
            Assert.Same(sourceMapBefore, snapshot.SourceMap);
            Assert.Same(semanticBefore, snapshot.SemanticDocument);
            Assert.Equal(resolveCountsBefore, resolver.CaptureResolveCounts());
            for (int index = 0; index < rootsBefore.Length; index++)
            {
                Assert.Same(rootsBefore[index], snapshot.Blocks[index]);
                Assert.Equal(boundsBefore[index], snapshot.Blocks[index].Bounds);
            }
            Assert.Equal(boxesBefore, EnumerateBoxes(snapshot.Blocks));
            for (int index = 0; index < images.Length; index++)
            {
                Assert.Same(bitmapsBefore[index], images[index].Bitmap);
                Assert.Same(leasesBefore[index], GetBitmapLease(images[index]));
                Assert.Equal(leaseCountsBefore[index], GetLeaseReferenceCount(leasesBefore[index]));
            }
        }
        finally
        {
            if (constructionContext is not null)
                await constructionContext.DrainOwnedRetirementsAsync().WaitAsync(AsyncDeadline);
        }
    }

    private static string CreateBudgetListSource(ContainerResolver resolver, int itemCount, int imageCount)
    {
        Assert.InRange(imageCount, 0, itemCount);
        var source = new StringBuilder(itemCount * 48);
        for (int index = 0; index < itemCount; index++)
        {
            source.Append("- ");
            if (index < imageCount)
                source.Append("![budget image ").Append(index).Append("](").Append(resolver.FirstUrl).Append(')');
            else
                source.Append("ordinary supported list item ").Append(index);
            source.Append('\n');
        }
        return source.ToString();
    }

    private static string CreateRunBudgetQuoteSource(ContainerResolver resolver, int formattedParagraphCount)
    {
        var source = new StringBuilder(formattedParagraphCount * 96);
        source.Append("> ![run budget image](").Append(resolver.FirstUrl).Append(")\n>\n");
        for (int index = 0; index < formattedParagraphCount; index++)
        {
            if (index > 0)
                source.Append(">\n");
            source.Append("> alpha **strong** beta *emphasis* gamma ~~deleted~~ delta\n");
        }
        return source.ToString();
    }

    private static string CreateSingleLeafRunBudgetListSource(ContainerResolver resolver, int repeatedPairs)
    {
        var source = new StringBuilder(repeatedPairs * 32);
        source.Append("- ![single leaf run budget image](").Append(resolver.FirstUrl).Append(") ");
        for (int index = 0; index < repeatedPairs; index++)
            source.Append("plain").Append(index).Append(" **bold").Append(index).Append("** ");
        source.Append('\n');
        return source.ToString();
    }

    private static string CreateDeepListSource(ContainerResolver resolver, int nestingDepth)
    {
        var source = new StringBuilder(nestingDepth * 24);
        for (int depth = 0; depth < nestingDepth; depth++)
        {
            source.Append(' ', depth * 2).Append("- ");
            if (depth == nestingDepth - 1)
                source.Append("![deep budget image](").Append(resolver.FirstUrl).Append(')');
            else
                source.Append("nested level ").Append(depth).Append('\n');
        }
        return source.ToString();
    }

    private static void AssertContainerShapeIsSupportedAndMeasured(IEnumerable<BlockBox> roots)
    {
        BlockBox[] boxes = EnumerateBoxes(roots).ToArray();
        Assert.NotEmpty(boxes);
        foreach (BlockBox box in boxes)
        {
            Assert.False(box.IsDirty, $"Budget fixture unexpectedly contains a dirty {box.GetType().Name}.");
            Assert.True(double.IsFinite(box.Bounds.X) && double.IsFinite(box.Bounds.Y) &&
                        double.IsFinite(box.Bounds.Width) && box.Bounds.Width > 0 &&
                        double.IsFinite(box.Bounds.Height) && box.Bounds.Height >= 0,
                $"Budget fixture contains incomplete measured bounds for {box.GetType().Name}.");
            switch (box)
            {
                case StackBox stack:
                    Assert.Equal(typeof(StackBox), stack.GetType());
                    Assert.True(stack.IsPrivateRasterPlacementCloneEligible);
                    Assert.Null(stack.SafeHtmlInlineFlowScopeId);
                    Assert.False(stack.HasResolvedSafeHtmlAlignment);
                    break;
                case ListItemBox item:
                    Assert.Equal(typeof(ListItemBox), item.GetType());
                    Assert.Null(item.SafeHtmlInlineFlowScopeId);
                    break;
                case InlineContainerBox inline:
                    Assert.Equal(typeof(InlineContainerBox), inline.GetType());
                    Assert.Contains(inline.ElementKey, new[]
                    {
                        MarkdownElementKeys.Body,
                        MarkdownElementKeys.Heading1,
                        MarkdownElementKeys.Heading2,
                        MarkdownElementKeys.Heading3,
                        MarkdownElementKeys.Heading4,
                        MarkdownElementKeys.Heading5,
                        MarkdownElementKeys.Heading6,
                        MarkdownElementKeys.ListMarker,
                    });
                    Assert.Null(inline.SafeHtmlInlineFlowScopeId);
                    Assert.Null(inline.CodeLanguage);
                    Assert.Null(inline.StyleState);
                    Assert.False(inline.HasInlineEmbeds);
                    Assert.Equal(0, inline.PrivateRasterPlacementForegroundSpanCount);
                    break;
                case ImageBox image:
                    Assert.Equal(typeof(ImageBox), image.GetType());
                    Assert.True(image.BlockIndex > 0);
                    break;
                default:
                    throw new Xunit.Sdk.XunitException(
                        $"Budget fixture reached unsupported node {box.GetType().Name} instead of its intended cap.");
            }
        }

        var inlineImages = new HashSet<ImageBox>(ReferenceEqualityComparer.Instance);
        foreach (InlineContainerBox inline in boxes.OfType<InlineContainerBox>())
        {
            Assert.True(inline.HasMeasuredLayout,
                "Every supported source leaf must own its completed measured native layout.");
            Assert.False(inline.IsDirty,
                "Every supported source leaf must have completed, current layout before cap rejection.");
            foreach (var entry in inline.InlineImageRuns)
            {
                InlineImageRun run = entry.Run;
                Assert.True(run.Image.TryGetCompletedInlinePlacementState(
                        out float availableWidth, out float imageWidth, out float imageHeight),
                    "Inline image dirtiness is permitted, but completed bitmap placement must be coherent.");
                Assert.True(float.IsFinite(availableWidth) && availableWidth > 0);
                Assert.True(float.IsFinite(imageWidth) && imageWidth > 0 && imageWidth <= run.DesiredWidth);
                Assert.Equal(run.DesiredHeight, imageHeight);
                Assert.True(inlineImages.Add(run.Image),
                    "The fixture must not alias one image identity across multiple inline placements.");
            }
        }

        ImageBox[] images = EnumerateImages(roots).ToArray();
        Assert.NotEmpty(images);
        foreach (ImageBox image in images)
        {
            Assert.NotNull(image.Bitmap);
            Assert.Equal(0, image.ActiveImageWorkCountForTests);
            if (!inlineImages.Contains(image))
                Assert.False(image.IsDirty,
                    "Non-inline image boxes must have a completed measured source placement.");
            _ = GetBitmapLease(image);
        }
    }

    private static int GetMaximumBoxDepth(IEnumerable<BlockBox> roots)
    {
        int maximum = 0;
        foreach (BlockBox root in roots)
            maximum = System.Math.Max(maximum, GetMaximumBoxDepth(root, depth: 1));
        return maximum;
    }

    private static int GetMaximumBoxDepth(BlockBox box, int depth)
    {
        int maximum = depth;
        switch (box)
        {
            case StackBox stack:
                foreach (BlockBox child in stack.Children)
                    maximum = System.Math.Max(maximum, GetMaximumBoxDepth(child, depth + 1));
                break;
            case ListItemBox item:
                maximum = System.Math.Max(maximum, GetMaximumBoxDepth(item.Marker, depth + 1));
                maximum = System.Math.Max(maximum, GetMaximumBoxDepth(item.Content, depth + 1));
                break;
        }
        return maximum;
    }

    private static async Task AssertContainerBudgetRejectedBeforeCloneAsync(
        DocumentFixture document,
        IReadOnlyCollection<int> changedBlockIndices,
        ContainerResolver resolver)
    {
        LayoutSnapshot snapshot = document.Snapshot;
        BlockBox[] rootsBefore = snapshot.Blocks.ToArray();
        Rect[] rootBoundsBefore = rootsBefore.Select(static root => root.Bounds).ToArray();
        BlockBox[] boxesBefore = EnumerateBoxes(rootsBefore).ToArray();
        Rect[] boxBoundsBefore = boxesBefore.Select(static box => box.Bounds).ToArray();
        InlineContainerBox[] inlineBefore = boxesBefore.OfType<InlineContainerBox>().ToArray();
        InlineRun[][] runsBefore = inlineBefore.Select(static inline => inline.Runs.ToArray()).ToArray();
        ImageBox[] imagesBefore = EnumerateImages(rootsBefore).ToArray();
        CanvasBitmap[] bitmapsBefore = imagesBefore
            .Select(static image => Assert.IsType<CanvasBitmap>(image.Bitmap)).ToArray();
        Rect[] imageBoundsBefore = imagesBefore.Select(static image => image.Bounds).ToArray();
        SharedCanvasBitmapCache.Lease[] leasesBefore = imagesBefore.Select(GetBitmapLease).ToArray();
        int[] leaseCountsBefore = leasesBefore.Select(GetLeaseReferenceCount).ToArray();
        int[] resolveCountsBefore = resolver.CaptureResolveCounts();
        Size sizeBefore = snapshot.Size;
        long revisionBefore = snapshot.LayoutRevision;
        MarkdownSourceMap sourceMapBefore = snapshot.SourceMap;
        MarkdownSemanticDocument semanticBefore = snapshot.SemanticDocument;
        long automationGenerationBefore = snapshot.AutomationDocumentGeneration;
        bool privateMeasurementStarted = false;
        bool constructionContextCreated = false;
        MarkdownLayoutContext? constructionContext = null;
        PreparedImageRelayoutResult result = default;

        try
        {
            result = snapshot.TryRelayoutCompletedImageRoots(
                changedBlockIndices, LayoutWidth, CancellationToken.None, out constructionContext,
                beforeMeasureOutsideGateForTests: () => privateMeasurementStarted = true,
                expectedPointerInputEpoch: snapshot.PointerInputEpoch);
            constructionContextCreated = constructionContext is not null;
        }
        finally
        {
            if (constructionContext is not null)
                await constructionContext.DrainOwnedRetirementsAsync().WaitAsync(AsyncDeadline);
        }

        Assert.Equal(PreparedImageRelayoutResult.Unavailable, result);
        Assert.False(constructionContextCreated,
            "Budget rejection must not expose a candidate context or acquire any private bitmap leases.");
        Assert.False(privateMeasurementStarted,
            "Budget rejection must precede native/private measurement.");
        Assert.Equal(revisionBefore, snapshot.LayoutRevision);
        Assert.Equal(sizeBefore, snapshot.Size);
        Assert.Equal(automationGenerationBefore, snapshot.AutomationDocumentGeneration);
        Assert.Same(sourceMapBefore, snapshot.SourceMap);
        Assert.Same(semanticBefore, snapshot.SemanticDocument);
        Assert.Equal(resolveCountsBefore, resolver.CaptureResolveCounts());
        Assert.Equal(rootsBefore.Length, snapshot.Blocks.Count);
        for (int index = 0; index < rootsBefore.Length; index++)
        {
            Assert.Same(rootsBefore[index], snapshot.Blocks[index]);
            Assert.Equal(rootBoundsBefore[index], snapshot.Blocks[index].Bounds);
        }

        BlockBox[] boxesAfter = EnumerateBoxes(snapshot.Blocks).ToArray();
        Assert.Equal(boxesBefore.Length, boxesAfter.Length);
        for (int index = 0; index < boxesBefore.Length; index++)
        {
            Assert.Same(boxesBefore[index], boxesAfter[index]);
            Assert.Equal(boxBoundsBefore[index], boxesAfter[index].Bounds);
        }

        InlineContainerBox[] inlineAfter = boxesAfter.OfType<InlineContainerBox>().ToArray();
        Assert.Equal(inlineBefore.Length, inlineAfter.Length);
        for (int index = 0; index < inlineBefore.Length; index++)
        {
            Assert.Same(inlineBefore[index], inlineAfter[index]);
            Assert.Equal(runsBefore[index].Length, inlineAfter[index].Runs.Count);
            for (int run = 0; run < runsBefore[index].Length; run++)
                Assert.Same(runsBefore[index][run], inlineAfter[index].Runs[run]);
        }

        ImageBox[] imagesAfter = EnumerateImages(snapshot.Blocks).ToArray();
        Assert.Equal(imagesBefore.Length, imagesAfter.Length);
        for (int index = 0; index < imagesBefore.Length; index++)
        {
            Assert.Same(imagesBefore[index], imagesAfter[index]);
            Assert.Same(bitmapsBefore[index], imagesAfter[index].Bitmap);
            Assert.Same(leasesBefore[index], GetBitmapLease(imagesAfter[index]));
            Assert.Equal(imageBoundsBefore[index], imagesAfter[index].Bounds);
            Assert.Equal(leaseCountsBefore[index], GetLeaseReferenceCount(leasesBefore[index]));
        }
    }

    private static string CreateRichListAndQuoteSource(ContainerResolver resolver) =>
        "- **outer strong** and [outer link](https://example.test/list)\n" +
        "  - nested text-only leaf\n" +
        $"  - ![nested image]({resolver.FirstUrl})\n" +
        $"  - ![nested SVG]({resolver.SvgUrl})\n\n" +
        $"- ![outer loose image]({resolver.SecondUrl})\n\n" +
        "> quote text with **style** and [quote link](https://example.test/quote)\n>\n" +
        $"> ![quote image]({resolver.ThirdUrl})\n\n" +
        "Trailing text-only paragraph keeps the changed roots away from document end.\n";

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

    private static void ApplyNonzeroListItemMarginsAndRefresh(LayoutSnapshot snapshot)
    {
        StackBox list = Assert.Single(snapshot.Blocks.OfType<StackBox>(),
            stack => stack.Children.OfType<ListItemBox>().Any());
        int index = 0;
        foreach (ListItemBox item in list.Children.OfType<ListItemBox>())
        {
            float offset = index++ * 2;
            item.Margin = new Thickness(3 + offset, 2 + offset, 5 + offset, 7 + offset);
        }
        snapshot.RelayoutMeasuredBlocks(LayoutWidth, CancellationToken.None);
    }

    private static string CreateUnsupportedSource(ContainerResolver resolver, UnsupportedNestedShape shape) => shape switch
    {
        UnsupportedNestedShape.Code =>
            $"- ![candidate]({resolver.FirstUrl})\n\n  ```csharp\n  int value = 1;\n  ```\n",
        UnsupportedNestedShape.TaskList =>
            $"- [ ] task text ![candidate]({resolver.FirstUrl})\n- ordinary sibling\n",
        UnsupportedNestedShape.Html =>
            $"- ![candidate]({resolver.FirstUrl})\n\n  <div align=\"center\">\n  <p>raw inline html</p>\n  </div>\n",
        UnsupportedNestedShape.Table =>
            $"- lead text\n\n  | cell | other |\n  | --- | --- |\n  | ![candidate]({resolver.FirstUrl}) | value |\n",
        _ => throw new ArgumentOutOfRangeException(nameof(shape)),
    };

    private static DocumentFixture BuildDocument(
        CompletedRasterPlacementCloneTests.PlacementFixture seed,
        MarkdownPerformanceSession session,
        ContainerResolver resolver,
        string source,
        FlowDirection direction,
        float rasterizationScale,
        bool lazy = false,
        IMarkdownSvgRenderer? svgRenderer = null,
        bool enableSafeHtml = false)
    {
        var registry = new MarkdownExtensionRegistry().ConfigureGitHubReadmeRegistry(
            enableSafeHtml ? SafeHtmlOptions.Default : null);
        Markdig.Syntax.MarkdownDocument parsed = Markdown.Parse(source, registry.BuildPipeline());
        Document.MarkdownDocument semanticDocument = Document.MarkdownDocument.FromParsed(source, parsed);
        var sourceMap = new MarkdownSourceMap(source);
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
            SvgRenderer = svgRenderer,
            AllowThirdPartyRemoteImages = true,
            RasterizationScale = rasterizationScale,
        };

        LayoutSnapshot? snapshot = null;
        try
        {
            var builder = new LayoutBuilder(context, semanticDocument: semanticDocument);
            snapshot = lazy
                ? builder.BuildLazy(parsed, LayoutWidth, viewportTop: 0, viewportHeight: 100,
                    overscan: 0, CancellationToken.None)
                : builder.Build(parsed, LayoutWidth, CancellationToken.None);
            snapshot.AssignAutomationDocumentGeneration(AutomationGeneration);
            var document = new DocumentFixture(context, snapshot, sourceMap, resolver);
            snapshot = null;
            return document;
        }
        catch
        {
            try
            {
                snapshot?.Retire().GetAwaiter().GetResult();
            }
            finally
            {
                context.DrainOwnedRetirementsAsync().GetAwaiter().GetResult();
            }
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
                foreach (var entry in inline.InlineImageRuns)
                    if (seen.Add(entry.Run.Image))
                        yield return entry.Run.Image;
            }
        }
    }

    private static Rect[][] CaptureSelectionGeometry(
        LayoutSnapshot snapshot,
        IReadOnlyList<InlineContainerBox> inlines)
    {
        var result = new Rect[inlines.Count][];
        for (int index = 0; index < inlines.Count; index++)
        {
            InlineContainerBox inline = inlines[index];
            TextRun? firstText = inline.Runs.OfType<TextRun>().FirstOrDefault(static run => run.Text.Length > 0);
            if (firstText is null)
            {
                result[index] = [];
                continue;
            }

            int runIndex = Enumerable.Range(0, inline.Runs.Count)
                .First(index => ReferenceEquals(inline.Runs[index], firstText));
            var range = new DocumentRange(
                new DocumentPosition(inline.BlockIndex, runIndex, 0),
                new DocumentPosition(inline.BlockIndex, runIndex, 1));
            var selection = new SelectionController();
            selection.SetAnchor(range.Start);
            selection.ExtendTo(range.End);
            result[index] = selection.GetHighlightRects(snapshot).ToArray();
        }
        return result;
    }

    private static (int RootBlockIndex, int BlockIndex, float X, float Y)[] CaptureFinalTextOrigins(
        LayoutSnapshot snapshot)
    {
        MethodInfo getStyle = typeof(InlineContainerBox).GetMethod(
            "GetContainerStyle", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Inline style resolver is unavailable.");
        MethodInfo getOrigin = typeof(InlineContainerBox).GetMethods(
                BindingFlags.Instance | BindingFlags.NonPublic)
            .Single(method => method.Name == "GetSnappedOrigin" && method.GetParameters().Length == 3);
        var result = new List<(int RootBlockIndex, int BlockIndex, float X, float Y)>();
        Assert.True(snapshot.TryBeginInteraction());
        try
        {
            foreach (BlockBox root in snapshot.Blocks)
            {
                using LayoutPlacementTransformContext.Scope placementScope =
                    snapshot.EnterPlacementTransformScopeForTopLevelBlockUnderPaint(root);
                foreach (InlineContainerBox inline in EnumerateBoxes([root]).OfType<InlineContainerBox>())
                {
                    object style = getStyle.Invoke(inline, null)
                        ?? throw new InvalidOperationException("Inline style resolver returned null.");
                    object?[] arguments = [style, 0f, 0f];
                    _ = getOrigin.Invoke(inline, arguments);
                    result.Add((root.BlockIndex, inline.BlockIndex,
                        Assert.IsType<float>(arguments[1]), Assert.IsType<float>(arguments[2])));
                }
            }
        }
        finally
        {
            snapshot.EndInteraction();
        }
        return result.ToArray();
    }

    private static (int RootBlockIndex, int BlockIndex, string Metrics)[] CaptureCanvasTextLayoutMetrics(
        LayoutSnapshot snapshot)
    {
        FieldInfo layoutField = typeof(InlineContainerBox).GetField("_layout",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Inline text layout field is unavailable.");
        var result = new List<(int RootBlockIndex, int BlockIndex, string Metrics)>();
        Assert.True(snapshot.TryBeginInteraction());
        try
        {
            foreach (BlockBox root in snapshot.Blocks)
            {
                using LayoutPlacementTransformContext.Scope placementScope =
                    snapshot.EnterPlacementTransformScopeForTopLevelBlockUnderPaint(root);
                foreach (InlineContainerBox inline in EnumerateBoxes([root]).OfType<InlineContainerBox>())
                {
                    object? layout = layoutField.GetValue(inline);
                    if (layout is null)
                    {
                        result.Add((root.BlockIndex, inline.BlockIndex, "<no-layout>"));
                        continue;
                    }

                    string metrics = string.Join(" | ", layout.GetType()
                        .GetProperties(BindingFlags.Instance | BindingFlags.Public)
                        .Where(static property => property.GetIndexParameters().Length == 0)
                        .OrderBy(static property => property.Name, StringComparer.Ordinal)
                        .Select(property =>
                        {
                            try
                            {
                                object? value = property.GetValue(layout);
                                if (value is null)
                                    return $"{property.Name}=<null>";
                                if (value is string or bool or byte or sbyte or short or ushort or int or uint or long or ulong or float or double or decimal or Enum)
                                    return $"{property.Name}={value}";
                                if (value is System.Collections.IEnumerable items)
                                    return $"{property.Name}=[{string.Join(';', items.Cast<object?>().Select(static item => item?.ToString() ?? "<null>"))}]";
                                return $"{property.Name}={value.GetType().FullName}";
                            }
                            catch (Exception exception)
                            {
                                return $"{property.Name}=<{exception.GetType().Name}>";
                            }
                        }));
                    result.Add((root.BlockIndex, inline.BlockIndex, metrics));
                }
            }
        }
        finally
        {
            snapshot.EndInteraction();
        }
        return result.ToArray();
    }

    private static (int RootBlockIndex, int BlockIndex, string Buffer, string Regions)[] CaptureTextLayoutRegions(
        LayoutSnapshot snapshot)
    {
        FieldInfo layoutField = typeof(InlineContainerBox).GetField("_layout",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Inline text layout field is unavailable.");
        FieldInfo bufferField = typeof(InlineContainerBox).GetField("_buffer",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Inline text buffer field is unavailable.");
        var result = new List<(int RootBlockIndex, int BlockIndex, string Buffer, string Regions)>();
        Assert.True(snapshot.TryBeginInteraction());
        try
        {
            foreach (BlockBox root in snapshot.Blocks)
            {
                using LayoutPlacementTransformContext.Scope placementScope =
                    snapshot.EnterPlacementTransformScopeForTopLevelBlockUnderPaint(root);
                foreach (InlineContainerBox inline in EnumerateBoxes([root]).OfType<InlineContainerBox>())
                {
                    object? layout = layoutField.GetValue(inline);
                    string buffer = Assert.IsType<string>(bufferField.GetValue(inline));
                    if (layout is null || buffer.Length == 0)
                    {
                        result.Add((root.BlockIndex, inline.BlockIndex, buffer,
                            layout is null ? "<no-layout>" : "<empty>"));
                        continue;
                    }

                    MethodInfo getRegions = layout.GetType().GetMethod("GetCharacterRegions",
                        BindingFlags.Instance | BindingFlags.Public, binder: null,
                        [typeof(int), typeof(int)], modifiers: null)
                        ?? throw new InvalidOperationException("Canvas text region API is unavailable.");
                    object? regionValue = getRegions.Invoke(layout, [0, buffer.Length]);
                    string regions = regionValue is System.Collections.IEnumerable items
                        ? string.Join(" | ", items.Cast<object?>().Select(DescribeTextRegion))
                        : "<no-regions>";
                    result.Add((root.BlockIndex, inline.BlockIndex, buffer, regions));
                }
            }
        }
        finally
        {
            snapshot.EndInteraction();
        }
        return result.ToArray();
    }

    private static string DescribeTextRegion(object? region)
    {
        if (region is null)
            return "<null>";

        string[] values = region.GetType()
            .GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .Where(static property => property.GetIndexParameters().Length == 0)
            .OrderBy(static property => property.Name, StringComparer.Ordinal)
            .Select(property =>
            {
                object? value = property.GetValue(region);
                if (value is null)
                    return $"{property.Name}=<null>";
                if (value.GetType().FullName == "Windows.Foundation.Rect")
                {
                    Type type = value.GetType();
                    return $"{property.Name}=({type.GetProperty("X")?.GetValue(value)}," +
                        $"{type.GetProperty("Y")?.GetValue(value)}," +
                        $"{type.GetProperty("Width")?.GetValue(value)}," +
                        $"{type.GetProperty("Height")?.GetValue(value)})";
                }
                return $"{property.Name}={value}";
            })
            .ToArray();
        return string.Join(",", values);
    }

    private static string CaptureInlinePaintDetails(LayoutSnapshot snapshot, BlockBox root)
    {
        FieldInfo bufferField = typeof(InlineContainerBox).GetField("_buffer",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Inline text buffer field is unavailable.");
        MethodInfo getStyle = typeof(InlineContainerBox).GetMethod("GetContainerStyle",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Inline style resolver is unavailable.");
        MethodInfo getOrigin = typeof(InlineContainerBox).GetMethods(
                BindingFlags.Instance | BindingFlags.NonPublic)
            .Single(method => method.Name == "GetSnappedOrigin" && method.GetParameters().Length == 3);
        Assert.True(snapshot.TryBeginInteraction());
        try
        {
            using LayoutPlacementTransformContext.Scope placementScope =
                snapshot.EnterPlacementTransformScopeForTopLevelBlockUnderPaint(root);
            string boxes = string.Join("; ", EnumerateBoxes([root]).Select(box =>
                $"{box.GetType().Name}#{box.BlockIndex} bounds={box.Bounds}"));
            string images = string.Join("; ", EnumerateImages([root]).Select(image =>
                $"ImageBox#{image.BlockIndex} logical={LayoutPlacementTestGeometry.ToLogical(snapshot, image)}"));
            string inlines = string.Join("; ", EnumerateBoxes([root]).OfType<InlineContainerBox>().Select(inline =>
            {
                object style = getStyle.Invoke(inline, null)
                    ?? throw new InvalidOperationException("Inline style resolver returned null.");
                object?[] arguments = [style, 0f, 0f];
                _ = getOrigin.Invoke(inline, arguments);
                float originX = Assert.IsType<float>(arguments[1]);
                float originY = Assert.IsType<float>(arguments[2]);
                string text = Assert.IsType<string>(bufferField.GetValue(inline));
                object? foreground = style.GetType().GetProperty("Foreground")?.GetValue(style);
                return $"block={inline.BlockIndex}, bounds={inline.Bounds}, origin=({originX:R},{originY:R}), " +
                    $"foreground={foreground}, text=\"{text}\"";
            }));
            return $"boxes: {boxes}; images: {images}; inlines: {inlines}";
        }
        finally
        {
            snapshot.EndInteraction();
        }
    }

    private static void AssertSourceLayoutLockReleased(LayoutSnapshot snapshot)
    {
        FieldInfo gateField = typeof(LayoutSnapshot).GetField("_layoutLock",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Missing LayoutSnapshot source layout gate.");
        object gate = Assert.IsAssignableFrom<object>(gateField.GetValue(snapshot));
        Assert.False(System.Threading.Monitor.IsEntered(gate),
            "Native candidate measurement/provider access must run outside the source layout lock.");
        Assert.True(System.Threading.Monitor.TryEnter(gate));
        System.Threading.Monitor.Exit(gate);
    }

    private static async Task LoadImageAsync(ImageBox image)
    {
        // The wait helper subscribes before triggering the load, which is
        // essential when repeated URLs complete synchronously from the cache.
        await DisplaySizedRasterDecodeTests.WaitForLoadAsync(image).WaitAsync(AsyncDeadline);
        await DisplaySizedRasterDecodeTests.WaitUntilAsync(
            () => image.ActiveImageWorkCountForTests == 0).WaitAsync(AsyncDeadline);
        Assert.NotNull(image.Bitmap);
    }

    private static async Task LoadAllImagesAsync(IEnumerable<ImageBox> images)
    {
        foreach (ImageBox image in images)
        {
            await LoadImageAsync(image);
            Assert.Equal(0, image.ActiveImageWorkCountForTests);
        }
    }

    private static Color[] Paint(
        LayoutSnapshot snapshot,
        CanvasDevice device,
        float rasterizationScale,
        Rect? viewport = null)
    {
        float height = (float)System.Math.Max(1, System.Math.Ceiling(snapshot.Size.Height + 1));
        using var target = new CanvasRenderTarget(device, LayoutWidth, height, 96 * rasterizationScale);
        using (CanvasDrawingSession drawing = target.CreateDrawingSession())
        {
            drawing.Clear(Color.FromArgb(0, 0, 0, 0));
            snapshot.Paint(drawing, viewport ?? new Rect(0, 0, LayoutWidth, height));
        }
        return target.GetPixelColors();
    }

    private static SharedCanvasBitmapCache.Lease GetBitmapLease(ImageBox image)
    {
        FieldInfo field = typeof(ImageBox).GetField("_bitmapLease", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Missing ImageBox bitmap lease field.");
        return Assert.IsType<SharedCanvasBitmapCache.Lease>(field.GetValue(image));
    }

    private static int GetLeaseReferenceCount(SharedCanvasBitmapCache.Lease lease)
    {
        FieldInfo entryField = typeof(SharedCanvasBitmapCache.Lease).GetField("_entry",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Missing shared bitmap cache entry field.");
        object entry = Assert.IsAssignableFrom<SharedCanvasBitmapCache.Entry>(entryField.GetValue(lease));
        FieldInfo countField = typeof(SharedCanvasBitmapCache.Entry).GetField("_referenceCount",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Missing shared bitmap reference count.");
        return Assert.IsType<int>(countField.GetValue(entry));
    }

    private static T GetPrivateField<T>(object instance, string name) where T : class
    {
        FieldInfo field = instance.GetType().GetField(name,
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"Missing private field {name}.");
        return Assert.IsType<T>(field.GetValue(instance));
    }

    public enum ContainerMutation
    {
        Margin,
        ImageGeometry,
        LayoutConfiguration,
        ChildOrder,
    }

    public enum UnsupportedNestedShape
    {
        Code,
        TaskList,
        Html,
        Table,
    }

    private sealed class DocumentFixture(
        MarkdownLayoutContext context,
        LayoutSnapshot snapshot,
        MarkdownSourceMap sourceMap,
        ContainerResolver resolver) : IAsyncDisposable
    {
        internal MarkdownLayoutContext Context { get; } = context;
        internal LayoutSnapshot Snapshot { get; } = snapshot;
        internal MarkdownSourceMap SourceMap { get; } = sourceMap;
        internal ContainerResolver Resolver { get; } = resolver;

        public async ValueTask DisposeAsync()
        {
            try
            {
                await Snapshot.Retire().WaitAsync(AsyncDeadline);
            }
            finally
            {
                await Context.DrainOwnedRetirementsAsync().WaitAsync(AsyncDeadline);
            }
        }
    }

    private sealed class ContainerResolver(bool holdPending = false, bool holdImages = false) : IMarkdownImageResolver
    {
        private readonly byte[] _png = DisplaySizedRasterDecodeTests.CreatePng(96, 192);
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, int> _counts = new(StringComparer.Ordinal);
        private readonly string _suffix = Guid.NewGuid().ToString("N");
        private readonly string _cachePrefix = "root-local-container-relayout:" + Guid.NewGuid().ToString("N") + ":";

        internal string FirstUrl => $"https://images.example.test/{_suffix}-one.png";
        internal string SecondUrl => $"https://images.example.test/{_suffix}-two.png";
        internal string ThirdUrl => $"https://images.example.test/{_suffix}-three.png";
        internal string SvgUrl => $"https://images.example.test/{_suffix}-vector.svg";
        internal string PendingUrl => $"https://images.example.test/{_suffix}-pending.png";
        internal string FarUrl => $"https://images.example.test/{_suffix}-far.png";
        private readonly TaskCompletionSource _pendingStarted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _imagesReleased =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal int[] CaptureResolveCounts() =>
            [ResolveCountFor(FirstUrl), ResolveCountFor(SecondUrl), ResolveCountFor(ThirdUrl), ResolveCountFor(SvgUrl)];

        internal int ResolveCountFor(string source) => _counts.TryGetValue(source, out int count) ? count : 0;

        internal Task WaitForPendingResolveAsync() => _pendingStarted.Task;
        internal void ReleaseImages() => _imagesReleased.TrySetResult();

        public async ValueTask<MarkdownImageResolution> ResolveAsync(
            string source,
            MarkdownImageResolveContext context,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _counts.AddOrUpdate(source, 1, static (_, count) => checked(count + 1));
            if (source == PendingUrl && holdPending)
            {
                _pendingStarted.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
                throw new OperationCanceledException(cancellationToken);
            }
            if (holdImages)
                await _imagesReleased.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            if (source == SvgUrl)
            {
                string svg = "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"64\" height=\"32\"><rect width=\"64\" height=\"32\" fill=\"#2367a8\"/></svg>";
                return MarkdownImageResolution.Resolved(new MarkdownImageAsset(
                    Encoding.UTF8.GetBytes(svg), "image/svg+xml", new Uri(source), _cachePrefix + source));
            }
            if (source != FirstUrl && source != SecondUrl && source != ThirdUrl &&
                source != PendingUrl && source != FarUrl)
                throw new InvalidOperationException("The container test resolver received an unexpected URL.");
            return MarkdownImageResolution.Resolved(new MarkdownImageAsset(
                _png,
                "image/png",
                CacheKey: _cachePrefix + source));
        }
    }

    private sealed class ContainerSvgRenderer : IMarkdownSvgRenderer
    {
        private int _openCount;
        private int _fullRenderCount;
        private int _tileRenderCount;

        internal int OpenCount => Volatile.Read(ref _openCount);
        internal int FullRenderCount => Volatile.Read(ref _fullRenderCount);
        internal int TileRenderCount => Volatile.Read(ref _tileRenderCount);
        public long CacheGeneration => 0;

        public MarkdownSvgSourcePreparation PrepareSource(byte[] source, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return MarkdownSvgSourcePreparation.Admit(source);
        }

        public ValueTask<IMarkdownSvgDocument> OpenAsync(
            MarkdownSvgOpenRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _openCount);
            var info = new MarkdownSvgDocumentInfo(64, 32, 2,
                "Nested container SVG description", HasText: false);
            return ValueTask.FromResult<IMarkdownSvgDocument>(new ContainerSvgDocument(this, info));
        }

        private sealed class ContainerSvgDocument(
            ContainerSvgRenderer owner,
            MarkdownSvgDocumentInfo info) : IMarkdownSvgDocument
        {
            private int _disposed;
            public MarkdownSvgDocumentInfo Info { get; } = info;

            public ValueTask<MarkdownSvgRaster> RenderAsync(
                MarkdownSvgRenderRequest request,
                CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (Volatile.Read(ref _disposed) != 0)
                    throw new MarkdownSvgException(MarkdownSvgFailureReason.Canceled,
                        "The container SVG document has been retired.");
                if (request.TileRegion is null)
                    Interlocked.Increment(ref owner._fullRenderCount);
                else
                    Interlocked.Increment(ref owner._tileRenderCount);

                int width = request.TileRegion?.Width ?? request.TargetWidthPixels;
                int height = request.TileRegion?.Height ?? request.TargetHeightPixels;
                int stride = checked(width * 4);
                int length = checked(stride * height);
                IMemoryOwner<byte> memory = MemoryPool<byte>.Shared.Rent(length);
                Span<byte> pixels = memory.Memory.Span[..length];
                for (int index = 0; index < length; index += 4)
                {
                    if (request.PixelFormat == MarkdownSvgPixelFormat.Bgra8Premultiplied)
                    {
                        pixels[index] = 0xA8;
                        pixels[index + 1] = 0x67;
                        pixels[index + 2] = 0x23;
                    }
                    else
                    {
                        pixels[index] = 0x23;
                        pixels[index + 1] = 0x67;
                        pixels[index + 2] = 0xA8;
                    }
                    pixels[index + 3] = byte.MaxValue;
                }
                return ValueTask.FromResult(new MarkdownSvgRaster(
                    memory, length, width, height, stride, request.PixelFormat));
            }

            public void Dispose() => Interlocked.Exchange(ref _disposed, 1);
        }
    }
}
