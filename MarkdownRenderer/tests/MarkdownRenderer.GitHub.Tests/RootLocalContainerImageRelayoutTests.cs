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
using static MarkdownRenderer.GitHub.Tests.ContainerRelayoutBudgetAssertions;
using static MarkdownRenderer.GitHub.Tests.ContainerRelayoutParityAssertions;
using static MarkdownRenderer.GitHub.Tests.ContainerRelayoutTestTree;
using static MarkdownRenderer.GitHub.Tests.RootLocalContainerImageRelayoutHarness;

namespace MarkdownRenderer.GitHub.Tests;

/// <summary>
/// Root-local relayout of completed images inside list and blockquote roots.
/// </summary>
/// <remarks>
/// This file is longer than 500 lines because it holds every scenario for the
/// one container entry point, <c>TryRelayoutCompletedImageRoots</c>: fallback
/// parity across scale and direction, the source-mutation race, cancellation,
/// lazy siblings, the five budget caps and the unsupported nested shapes. The
/// fixtures and oracles live in the <c>Container*</c> support files.
/// </remarks>
[Collection(SharedCanvasBitmapCacheTestCollection.Name)]
public sealed class RootLocalContainerImageRelayoutTests
{
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

        await using ContainerImageDocument optimized = BuildDocument(seed, session, optimizedResolver,
            optimizedSource, direction, rasterizationScale, svgRenderer: optimizedSvgRenderer);
        await using ContainerImageDocument fallback = BuildDocument(seed, session, fallbackResolver,
            fallbackSource, direction, rasterizationScale, svgRenderer: fallbackSvgRenderer);

        ApplyNonzeroListItemMarginsAndRefresh(optimized.Snapshot);
        ApplyNonzeroListItemMarginsAndRefresh(fallback.Snapshot);
        optimizedResolver.ReleaseImages();
        fallbackResolver.ReleaseImages();
        (StackBox optimizedList, StackBox optimizedQuote) =
            AssertRichListAndQuoteShape(optimized.Snapshot, fallback.Snapshot);

        ImageBox[] images = EnumerateImages(optimized.Snapshot.Blocks).ToArray();
        ImageBox[] fallbackImages = EnumerateImages(fallback.Snapshot.Blocks).ToArray();
        Assert.Equal(4, images.Length);
        Assert.Equal(images.Length, fallbackImages.Length);
        foreach (ImageBox image in images.Concat(fallbackImages))
            await LoadImageAsync(image);

        LayoutSnapshot snapshot = optimized.Snapshot;
        var before = ContainerRelayoutParityAssertions.SourceState.Capture(
            snapshot, images, optimizedResolver, optimizedSvgRenderer);

        // The ordinary fallback is intentionally exercised with child image identities too. The
        // optimized entry point additionally receives the root IDs, in reverse/mixed order, to
        // prove that descendants consolidate to exactly one list and one quote root.
        int[] imageIndices = images.Select(static image => image.BlockIndex).Distinct().ToArray();
        fallback.Snapshot.RelayoutChangedBlocks(imageIndices, LayoutWidth, CancellationToken.None);
        var changedIndices = new HashSet<int>(imageIndices);
        changedIndices.Add(optimizedQuote.BlockIndex);
        changedIndices.Add(optimizedList.BlockIndex);
        AssertPublicationPlanBeforePaint(snapshot, images);

        MarkdownLayoutContext? constructionContext = null;
        try
        {
            PreparedImageRelayoutResult result = snapshot.TryRelayoutCompletedImageRoots(
                changedIndices, LayoutWidth, CancellationToken.None, out constructionContext,
                phaseHooks: CompletedImageRelayoutPhaseHooks.BeforeMeasure(() => AssertSourceLayoutLockReleased(snapshot)),
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
            before.AssertCommitMatchesFallback(snapshot, fallback.Snapshot, images, fallbackImages,
                optimizedResolver, optimizedSvgRenderer, seed.Device, rasterizationScale);
            Assert.Equal(Paint(snapshot, seed.Device, rasterizationScale),
                Paint(fallback.Snapshot, seed.Device, rasterizationScale));
            Assert.NotEqual(before.Size, snapshot.Size);
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
        await using ContainerImageDocument document = BuildDocument(seed, session, resolver,
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
        await using ContainerImageDocument document = BuildDocument(seed, session, resolver,
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
        ContainerMutationRace race = ContainerMutationRace.Seed(snapshot, list, images, mutation);
        var before = new ContainerRetrySourceState(snapshot, images, resolver, svgRenderer);
        MarkdownLayoutContext? constructionContext = null;
        try
        {
            PreparedImageRelayoutResult result = snapshot.TryRelayoutCompletedImageRoots(
                images.Select(static image => image.BlockIndex).Distinct().ToArray(),
                LayoutWidth, CancellationToken.None, out constructionContext,
                phaseHooks: CompletedImageRelayoutPhaseHooks.BeforeMeasure(() =>
                {
                    AssertSourceLayoutLockReleased(snapshot);
                    race.Apply();
                }),
                expectedPointerInputEpoch: snapshot.PointerInputEpoch);

            Assert.True(race.Mutated);
            Assert.Equal(PreparedImageRelayoutResult.Retry, result);
            // Candidate retirement is intentionally asynchronous and outside the
            // source gate. Join it before asserting that all borrowed leases
            // have returned to their exact pre-attempt counts.
            if (constructionContext is not null)
                await constructionContext.DrainOwnedRetirementsAsync().WaitAsync(AsyncDeadline);
            before.AssertUnchanged(snapshot, images, resolver, svgRenderer);
            race.AssertSourceKeptMutation();
        }
        finally
        {
            // Restore the source only after the fence has observed the injected race. This keeps
            // teardown ownership valid even if an assertion below fails.
            race.Restore(snapshot);
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
        await using ContainerImageDocument document = BuildDocument(seed, session, resolver,
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
                    phaseHooks: CompletedImageRelayoutPhaseHooks.BeforeMeasure(canceled.Cancel),
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
        await using ContainerImageDocument document = BuildDocument(seed, session, resolver,
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
            Assert.True(pending.GetWorkState().ActiveWorkCount > 0,
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
        await using ContainerImageDocument document = BuildDocument(seed, session, resolver,
            CreateBudgetListSource(resolver, itemCount: 33, imageCount: 33),
            FlowDirection.LeftToRight, 1.5f);

        StackBox root = Assert.Single(document.Snapshot.Blocks.OfType<StackBox>());
        Assert.All(root.Children, static child => Assert.IsType<ListItemBox>(child));
        Assert.True(EnumerateBoxes([root]).Count() < PrivateRasterPlacementTree.MaximumNodes,
            "The fixture must stay below the node cap so image count is the rejecting budget.");
        ImageBox[] images = EnumerateImages([root]).ToArray();
        Assert.Equal(ImageMeasuredPlacementTransfer.MaximumBatchCount + 1, images.Length);
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
        await using ContainerImageDocument document = BuildDocument(seed, session, resolver,
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
        await using ContainerImageDocument document = BuildDocument(seed, session, resolver,
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
        await using ContainerImageDocument document = BuildDocument(seed, session, resolver,
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
        await using ContainerImageDocument document = BuildDocument(seed, session, resolver,
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
        await using ContainerImageDocument document = BuildDocument(seed, session, resolver,
            source, FlowDirection.LeftToRight, 1.5f);

        StackBox[] roots = document.Snapshot.Blocks.OfType<StackBox>()
            .Where(static stack => stack.Children.OfType<ListItemBox>().Any())
            .ToArray();
        Assert.Equal(2, roots.Length);
        ImageBox[][] rootImages = roots.Select(static root => EnumerateImages([root]).ToArray()).ToArray();
        Assert.All(rootImages, images => Assert.Equal(imagesPerRoot, images.Length));
        Assert.True(rootImages.Sum(static images => images.Length) >
                    ImageMeasuredPlacementTransfer.MaximumBatchCount);
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
        await using ContainerImageDocument document = BuildDocument(seed, session, resolver,
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
}
