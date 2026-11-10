using MarkdownRenderer.Accessibility.Internal.Semantics;
using MarkdownRenderer.GitHub.Tests.TestSupport.Fixtures;
using MarkdownRenderer.Layout.Internal.Boxes;
using MarkdownRenderer.Layout.Internal.Context;
using MarkdownRenderer.Layout.Internal.Images;
using MarkdownRenderer.Layout.Internal.Images.BitmapCache;
using MarkdownRenderer.Layout.Internal.Placement;
using MarkdownRenderer.Layout.Internal.Relayout;
using MarkdownRenderer.Layout.Internal.RenderResources;
using MarkdownRenderer.Layout.Internal.Snapshots;
using MarkdownRenderer.Performance;
using Microsoft.Graphics.Canvas;
using Microsoft.UI.Xaml;
using Windows.Foundation;
using Windows.UI;
using Xunit;
using static MarkdownRenderer.GitHub.Tests.TestSupport.Fixtures.ImageRelayoutTestSupport;
using static MarkdownRenderer.GitHub.Tests.RootLocalImageRelayoutFixture;

namespace MarkdownRenderer.GitHub.Tests;

/// <summary>
/// Completed standalone raster image roots relayout through
/// <c>LayoutSnapshot.TryRelayoutCompletedImageRoot(s)</c> without replacing
/// their owners, and match a protected full-reflow fallback.
/// </summary>
/// <remarks>
/// This file is longer than 500 lines because it holds the fifteen scenarios
/// of one entry point (commit, batch commit, cancellation, retry on pointer
/// input, revision or lease change, lazy snapshots and preflight rejection),
/// each arranging its own document and stating its own oracle. Documents,
/// sources and shared oracles live in <see cref="RootLocalImageDocument"/>
/// and <see cref="RootLocalImageRelayoutFixture"/>.
/// </remarks>
[Collection(SharedCanvasBitmapCacheTestCollection.Name)]
public sealed class RootLocalImageRelayoutTests
{
    private readonly MarkdownRenderResources _renderResources = new();
    private const float LayoutWidth = RootLocalImageDocument.LayoutWidth;
    private const long AutomationGeneration = RootLocalImageDocument.AutomationGeneration;

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
    public async Task CompletedRasterRootMatchesSuffixFallbackAcrossScaleAndDirection(
        float rasterizationScale,
        bool rightToLeft,
        bool forceSoftwareRenderer)
    {
        await using var seed = new CompletedRasterPlacementCloneTests.PlacementFixture(
            forceSoftwareRenderer: forceSoftwareRenderer);
        Assert.Equal(forceSoftwareRenderer, seed.ForceSoftwareRenderer);
        using var session = new MarkdownPerformanceSession(MarkdownPerformanceOptions.Progressive);
        var resolver = new ControlledRasterResolver(DisplaySizedRasterDecodeTests.CreatePng(96, 192));
        string sourceText = CreateDocumentSource(resolver, includePendingSibling: true);
        FlowDirection direction = rightToLeft ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
        await using RootLocalImageDocument optimized = BuildDocument(seed, session, resolver, sourceText, direction,
            rasterizationScale, lazy: false);
        await using RootLocalImageDocument fallback = BuildDocument(seed, session, resolver, sourceText, direction,
            rasterizationScale, lazy: false);

        ImageBox optimizedImage = optimized.TargetImage;
        ImageBox fallbackImage = fallback.TargetImage;
        Assert.Equal(optimizedImage.Bounds, fallbackImage.Bounds);

        await LoadImageAsync(optimizedImage);
        await LoadImageAsync(fallbackImage);
        long sourceWaiterEpoch = await AssertCompletedPlacementTransferPredicatesAsync(
            optimizedImage, LayoutWidth);
        await StartPendingSiblingAsync(optimized);
        await StartPendingSiblingAsync(fallback);
        await WaitForPendingWorkAndResolverStabilityAsync(optimized, fallback);

        LayoutSnapshot snapshot = optimized.Snapshot;
        BlockBox[] sourceRoots = snapshot.Blocks.ToArray();
        int targetOrdinal = Array.FindIndex(sourceRoots, block => ReferenceEquals(block, optimizedImage));
        Assert.True(targetOrdinal >= 0);
        Rect[] oldBounds = sourceRoots.Select(static block => block.Bounds).ToArray();
        Size oldSize = snapshot.Size;
        long oldRevision = snapshot.LayoutRevision;
        MarkdownSemanticDocument oldSemantics = snapshot.SemanticDocument;

        fallback.Snapshot.RelayoutChangedBlocks(
            [fallbackImage.BlockIndex], LayoutWidth, CancellationToken.None);
        int targetResolveCount = resolver.ResolveCountFor(optimized.Resolver.TargetUrl);
        int pendingResolveCount = resolver.ResolveCountFor(optimized.Resolver.PendingUrl);

        MarkdownLayoutContext? constructionContext = null;
        try
        {
            PreparedImageRelayoutResult result = snapshot.TryRelayoutCompletedImageRoot(
                [optimizedImage.BlockIndex],
                LayoutWidth,
                CancellationToken.None,
                out constructionContext,
                expectedPointerInputEpoch: snapshot.PointerInputEpoch);
            Assert.Equal(PreparedImageRelayoutResult.Committed, result);
            Assert.Equal(sourceWaiterEpoch, optimizedImage.OutputDeferral.GetState().WaiterEpoch);
            Assert.Equal(oldRevision + 1, snapshot.LayoutRevision);
            Assert.NotEqual(oldSize, snapshot.Size);
            Assert.Equal(fallback.Snapshot.Size, snapshot.Size);
            Assert.Equal(sourceRoots.Length, snapshot.Blocks.Count);
            AssertRootsMatchFallback(fallback.Snapshot, snapshot, sourceRoots);
            Assert.NotEqual(oldBounds[targetOrdinal].Height, snapshot.Blocks[targetOrdinal].Bounds.Height);
            Assert.Same(optimizedImage, snapshot.Blocks[targetOrdinal]);
            Assert.NotNull(optimizedImage.Bitmap);
            Assert.NotNull(fallbackImage.Bitmap);
            Assert.Same(optimized.SourceMap, snapshot.SourceMap);
            Assert.Equal(AutomationGeneration, snapshot.AutomationDocumentGeneration);
            AssertSemanticTextAndSpansPreserved(oldSemantics, snapshot);
            Assert.Equal(GetSemanticProjection(fallback.Snapshot),
                GetSemanticProjection(snapshot));
            Assert.Equal(targetResolveCount, resolver.ResolveCountFor(optimized.Resolver.TargetUrl));
            Assert.Equal(pendingResolveCount, resolver.ResolveCountFor(optimized.Resolver.PendingUrl));
            AssertPendingSiblingsStillUnloaded(optimized, fallback);
            AssertViewportIndexAndPixelsMatchFallback(snapshot, fallback.Snapshot, seed.Device, rasterizationScale);
        }
        finally
        {
            await DrainAsync(constructionContext);
        }
    }

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
    public async Task CompletedRasterBatchMatchesOneSuffixFallbackAcrossScaleAndDirection(
        float rasterizationScale,
        bool rightToLeft,
        bool forceSoftwareRenderer)
    {
        await using var seed = new CompletedRasterPlacementCloneTests.PlacementFixture(
            forceSoftwareRenderer: forceSoftwareRenderer);
        Assert.Equal(forceSoftwareRenderer, seed.ForceSoftwareRenderer);
        using var session = new MarkdownPerformanceSession(MarkdownPerformanceOptions.Progressive);
        var resolver = new ControlledRasterResolver(DisplaySizedRasterDecodeTests.CreatePng(96, 192));
        string sourceText = CreateBatchDocumentSource(resolver);
        FlowDirection direction = rightToLeft ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
        await using RootLocalImageDocument optimized = BuildDocument(seed, session, resolver, sourceText, direction,
            rasterizationScale, lazy: false);
        await using RootLocalImageDocument fallback = BuildDocument(seed, session, resolver, sourceText, direction,
            rasterizationScale, lazy: false);

        ImageBox optimizedFirst = optimized.TargetImage;
        ImageBox optimizedSecond = FindSecondTarget(optimized);
        ImageBox fallbackFirst = fallback.TargetImage;
        ImageBox fallbackSecond = FindSecondTarget(fallback);
        Assert.NotEqual(optimizedFirst.BlockIndex, optimizedSecond.BlockIndex);
        Assert.NotEqual(fallbackFirst.BlockIndex, fallbackSecond.BlockIndex);

        await LoadImageAsync(optimizedFirst);
        await LoadImageAsync(optimizedSecond);
        await LoadImageAsync(fallbackFirst);
        await LoadImageAsync(fallbackSecond);

        LayoutSnapshot snapshot = optimized.Snapshot;
        BlockBox[] rootsBefore = snapshot.Blocks.ToArray();
        Rect[] oldBounds = rootsBefore.Select(static block => block.Bounds).ToArray();
        Size oldSize = snapshot.Size;
        long oldRevision = snapshot.LayoutRevision;
        MarkdownSemanticDocument oldSemantics = snapshot.SemanticDocument;
        CanvasBitmap firstBitmap = Assert.IsType<CanvasBitmap>(optimizedFirst.Bitmap);
        CanvasBitmap secondBitmap = Assert.IsType<CanvasBitmap>(optimizedSecond.Bitmap);
        int firstResolveCount = resolver.ResolveCountFor(optimizedFirst.Url);
        int secondResolveCount = resolver.ResolveCountFor(optimizedSecond.Url);
        int[] changedIndices = [optimizedSecond.BlockIndex, optimizedFirst.BlockIndex];
        int[] fallbackChangedIndices = [fallbackSecond.BlockIndex, fallbackFirst.BlockIndex];

        fallback.Snapshot.RelayoutChangedBlocks(fallbackChangedIndices, LayoutWidth, CancellationToken.None);

        MarkdownLayoutContext? constructionContext = null;
        try
        {
            PreparedImageRelayoutResult result = snapshot.TryRelayoutCompletedImageRoots(
                changedIndices, LayoutWidth, CancellationToken.None, out constructionContext,
                expectedPointerInputEpoch: snapshot.PointerInputEpoch);

            Assert.Equal(PreparedImageRelayoutResult.Committed, result);
            Assert.Equal(oldRevision + 1, snapshot.LayoutRevision);
            Assert.NotEqual(oldSize, snapshot.Size);
            Assert.Equal(fallback.Snapshot.Size, snapshot.Size);
            Assert.Same(optimized.SourceMap, snapshot.SourceMap);
            Assert.Same(oldSemantics, snapshot.SemanticDocument);
            AssertSemanticFingerprintsPreserved(oldSemantics, snapshot);
            Assert.Same(firstBitmap, optimizedFirst.Bitmap);
            Assert.Same(secondBitmap, optimizedSecond.Bitmap);
            Assert.Equal(firstResolveCount, resolver.ResolveCountFor(optimizedFirst.Url));
            Assert.Equal(secondResolveCount, resolver.ResolveCountFor(optimizedSecond.Url));

            AssertRootsMatchFallback(fallback.Snapshot, snapshot, rootsBefore);
            Assert.Equal(GetSemanticProjection(fallback.Snapshot),
                GetSemanticProjection(snapshot));
            Assert.NotEqual(oldBounds[Array.IndexOf(rootsBefore, optimizedFirst)].Height,
                optimizedFirst.Bounds.Height);
            Assert.NotEqual(oldBounds[Array.IndexOf(rootsBefore, optimizedSecond)].Height,
                optimizedSecond.Bounds.Height);
            AssertViewportIndexAndPixelsMatchFallback(snapshot, fallback.Snapshot, seed.Device, rasterizationScale);
        }
        finally
        {
            await DrainAsync(constructionContext);
        }
    }

    [Fact]
    public async Task BatchCancellationAfterPreparationLeavesPublishedGenerationAndSourcesUntouched()
    {
        await using var seed = new CompletedRasterPlacementCloneTests.PlacementFixture();
        using var session = new MarkdownPerformanceSession(MarkdownPerformanceOptions.Progressive);
        var resolver = new ControlledRasterResolver(DisplaySizedRasterDecodeTests.CreatePng(96, 192));
        await using RootLocalImageDocument document = BuildDocument(seed, session, resolver,
            CreateBatchDocumentSource(resolver), FlowDirection.LeftToRight, 1.5f, lazy: false);
        ImageBox first = document.TargetImage;
        ImageBox second = FindSecondTarget(document);
        await LoadImageAsync(first);
        await LoadImageAsync(second);

        LayoutSnapshot snapshot = document.Snapshot;
        BlockBox[] rootsBefore = snapshot.Blocks.ToArray();
        Rect[] rawBoundsBefore = rootsBefore.Select(static root => root.Bounds).ToArray();
        Rect[] logicalBoundsBefore = rootsBefore.Select(root =>
            LayoutPlacementTestGeometry.ToLogical(snapshot, root)).ToArray();
        LayoutPlacementGeneration generationBefore = snapshot.PublishedPlacementGeneration;
        Size sizeBefore = snapshot.Size;
        long revisionBefore = snapshot.LayoutRevision;
        int measuredCountBefore = snapshot.MeasuredTopLevelBlockCount;
        MarkdownSemanticDocument semanticsBefore = snapshot.SemanticDocument;
        CanvasBitmap firstBitmap = Assert.IsType<CanvasBitmap>(first.Bitmap);
        CanvasBitmap secondBitmap = Assert.IsType<CanvasBitmap>(second.Bitmap);
        SharedCanvasBitmapCache.Lease firstLease = GetImageLease(first);
        SharedCanvasBitmapCache.Lease secondLease = GetImageLease(second);
        int firstReferences = GetEntryReferenceCount(firstLease);
        int secondReferences = GetEntryReferenceCount(secondLease);
        using var cancellation = new CancellationTokenSource();
        var commitBoundaryReached = false;
        MarkdownLayoutContext? constructionContext = null;
        try
        {
            Assert.Throws<OperationCanceledException>(() => snapshot.TryRelayoutCompletedImageRoots(
                [second.BlockIndex, first.BlockIndex],
                LayoutWidth,
                cancellation.Token,
                out constructionContext,
                expectedPointerInputEpoch: snapshot.PointerInputEpoch,
                phaseHooks: CompletedImageRelayoutPhaseHooks.BeforeCommit(() =>
                {
                    // This hook is reached only after private measurement,
                    // native-owner validation and successor generation creation.
                    commitBoundaryReached = true;
                    cancellation.Cancel();
                })));

            Assert.True(commitBoundaryReached,
                "Cancellation must be exercised at the prepared-to-publication boundary.");
            Assert.True(cancellation.IsCancellationRequested);
            Assert.NotNull(constructionContext);
            Assert.Same(generationBefore, snapshot.PublishedPlacementGeneration);
            Assert.Equal(revisionBefore, snapshot.LayoutRevision);
            Assert.Equal(sizeBefore, snapshot.Size);
            Assert.Equal(measuredCountBefore, snapshot.MeasuredTopLevelBlockCount);
            Assert.Same(semanticsBefore, snapshot.SemanticDocument);
            Assert.Same(firstBitmap, first.Bitmap);
            Assert.Same(secondBitmap, second.Bitmap);
            Assert.Same(firstLease, GetImageLease(first));
            Assert.Same(secondLease, GetImageLease(second));
            Assert.Equal(0, first.GetWorkState().ActiveWorkCount);
            Assert.Equal(0, second.GetWorkState().ActiveWorkCount);
            Assert.Equal(rootsBefore.Length, snapshot.Blocks.Count);
            for (int ordinal = 0; ordinal < rootsBefore.Length; ordinal++)
            {
                Assert.Same(rootsBefore[ordinal], snapshot.Blocks[ordinal]);
                Assert.Equal(rawBoundsBefore[ordinal], snapshot.Blocks[ordinal].Bounds);
                Assert.Equal(logicalBoundsBefore[ordinal],
                    LayoutPlacementTestGeometry.ToLogical(snapshot, snapshot.Blocks[ordinal]));
            }
        }
        finally
        {
            await DrainAsync(constructionContext);
        }

        Assert.Equal(firstReferences, GetEntryReferenceCount(firstLease));
        Assert.Equal(secondReferences, GetEntryReferenceCount(secondLease));
    }

    [Fact]
    public async Task BatchCancellationBeforeMeasurementLeavesAllSourcePlacementsUntouched()
    {
        await using var seed = new CompletedRasterPlacementCloneTests.PlacementFixture();
        using var session = new MarkdownPerformanceSession(MarkdownPerformanceOptions.Progressive);
        var resolver = new ControlledRasterResolver(DisplaySizedRasterDecodeTests.CreatePng(96, 192));
        await using RootLocalImageDocument document = BuildDocument(seed, session, resolver,
            CreateBatchDocumentSource(resolver), FlowDirection.LeftToRight, 1.5f, lazy: false);
        ImageBox first = document.TargetImage;
        ImageBox second = FindSecondTarget(document);
        await LoadImageAsync(first);
        await LoadImageAsync(second);

        LayoutSnapshot snapshot = document.Snapshot;
        BlockBox[] rootsBefore = snapshot.Blocks.ToArray();
        Rect[] boundsBefore = rootsBefore.Select(static block => block.Bounds).ToArray();
        CanvasBitmap firstBitmap = Assert.IsType<CanvasBitmap>(first.Bitmap);
        CanvasBitmap secondBitmap = Assert.IsType<CanvasBitmap>(second.Bitmap);
        SharedCanvasBitmapCache.Lease firstLease = GetImageLease(first);
        SharedCanvasBitmapCache.Lease secondLease = GetImageLease(second);
        int firstReferences = GetEntryReferenceCount(firstLease);
        int secondReferences = GetEntryReferenceCount(secondLease);
        Size sizeBefore = snapshot.Size;
        long revisionBefore = snapshot.LayoutRevision;
        MarkdownSemanticDocument semanticBefore = snapshot.SemanticDocument;
        using var cancellation = new CancellationTokenSource();
        MarkdownLayoutContext? constructionContext = null;

        try
        {
            Assert.Throws<OperationCanceledException>(() => snapshot.TryRelayoutCompletedImageRoots(
                [second.BlockIndex, first.BlockIndex], LayoutWidth, cancellation.Token,
                out constructionContext,
                phaseHooks: CompletedImageRelayoutPhaseHooks.BeforeMeasure(cancellation.Cancel),
                expectedPointerInputEpoch: snapshot.PointerInputEpoch));

            Assert.True(cancellation.IsCancellationRequested);
            Assert.NotNull(constructionContext);
            Assert.Equal(revisionBefore, snapshot.LayoutRevision);
            Assert.Equal(sizeBefore, snapshot.Size);
            Assert.Same(semanticBefore, snapshot.SemanticDocument);
            Assert.Same(firstBitmap, first.Bitmap);
            Assert.Same(secondBitmap, second.Bitmap);
            Assert.Same(firstLease, GetImageLease(first));
            Assert.Same(secondLease, GetImageLease(second));
            AssertRootsUnchanged(snapshot, rootsBefore, boundsBefore);
        }
        finally
        {
            await DrainAsync(constructionContext);
        }

        Assert.Equal(firstReferences, GetEntryReferenceCount(firstLease));
        Assert.Equal(secondReferences, GetEntryReferenceCount(secondLease));
    }

    [Fact]
    public async Task NativePreparationRunsOutsideSnapshotGateAndOrdinaryInteractionDoesNotInvalidateIt()
    {
        await using var seed = new CompletedRasterPlacementCloneTests.PlacementFixture();
        using var session = new MarkdownPerformanceSession(MarkdownPerformanceOptions.Progressive);
        var resolver = new ControlledRasterResolver(DisplaySizedRasterDecodeTests.CreatePng(96, 192));
        await using RootLocalImageDocument document = BuildDocument(seed, session, resolver,
            CreateDocumentSource(resolver, includePendingSibling: false), FlowDirection.LeftToRight, 1f,
            lazy: false);
        await LoadImageAsync(document.TargetImage);

        LayoutSnapshot snapshot = document.Snapshot;
        long pointerEpoch = snapshot.PointerInputEpoch;
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        Task<PreparedImageRelayoutResult>? work = null;
        var preparedContext = new RootLocalImageRelayoutFixture.ContextCapture();
        try
        {
            work = Task.Run(() =>
            {
                MarkdownLayoutContext? context = null;
                try
                {
                    return snapshot.TryRelayoutCompletedImageRoot(
                        [document.TargetImage.BlockIndex],
                        LayoutWidth,
                        CancellationToken.None,
                        out context,
                        phaseHooks: CompletedImageRelayoutPhaseHooks.BeforeMeasure(() =>
                        {
                            entered.Set();
                            if (!release.Wait(TimeSpan.FromSeconds(10)))
                                throw new TimeoutException("The native preparation test gate was not released.");
                        }),
                        expectedPointerInputEpoch: pointerEpoch);
                }
                finally
                {
                    preparedContext.Value = context;
                }
            });

            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)), "Private preparation did not reach its outside-gate hook.");
            Task<bool> interaction = Task.Run(() =>
            {
                if (!snapshot.TryBeginInteraction())
                    return false;
                try { return true; }
                finally { snapshot.EndInteraction(); }
            });
            Assert.Same(interaction, await Task.WhenAny(interaction, Task.Delay(TimeSpan.FromSeconds(2))));
            Assert.True(await interaction, "The snapshot read gate remained held during private native preparation.");
            Assert.Equal(pointerEpoch, snapshot.PointerInputEpoch);

            release.Set();
            Assert.Equal(PreparedImageRelayoutResult.Committed,
                await work.WaitAsync(TimeSpan.FromSeconds(10)));
        }
        finally
        {
            release.Set();
            if (work is not null)
            {
                try { await work.WaitAsync(TimeSpan.FromSeconds(10)); }
                catch { /* Preserve the assertion/timeout that caused cleanup. */ }
            }
            await DrainAsync(preparedContext.Value);
        }
        Assert.Equal(pointerEpoch, snapshot.PointerInputEpoch);
    }

    [Fact]
    public async Task PointerInputDuringPrivatePreparationReturnsRetryWithoutSourceMutation()
    {
        await using var seed = new CompletedRasterPlacementCloneTests.PlacementFixture();
        using var session = new MarkdownPerformanceSession(MarkdownPerformanceOptions.Progressive);
        var resolver = new ControlledRasterResolver(DisplaySizedRasterDecodeTests.CreatePng(96, 192));
        await using RootLocalImageDocument document = BuildDocument(seed, session, resolver,
            CreateDocumentSource(resolver, includePendingSibling: false), FlowDirection.RightToLeft, 1.5f,
            lazy: false);
        await LoadImageAsync(document.TargetImage);

        LayoutSnapshot snapshot = document.Snapshot;
        BlockBox[] rootsBefore = snapshot.Blocks.ToArray();
        Rect[] boundsBefore = rootsBefore.Select(static block => block.Bounds).ToArray();
        Size sizeBefore = snapshot.Size;
        long revisionBefore = snapshot.LayoutRevision;
        long pointerEpochBefore = snapshot.PointerInputEpoch;
        CanvasBitmap bitmapBefore = Assert.IsType<CanvasBitmap>(document.TargetImage.Bitmap);
        MarkdownSemanticDocument semanticBefore = snapshot.SemanticDocument;

        MarkdownLayoutContext? constructionContext = null;
        try
        {
            PreparedImageRelayoutResult result = snapshot.TryRelayoutCompletedImageRoot(
                [document.TargetImage.BlockIndex],
                LayoutWidth,
                CancellationToken.None,
                out constructionContext,
                phaseHooks: CompletedImageRelayoutPhaseHooks.BeforeMeasure(snapshot.NotifyPointerInput),
                expectedPointerInputEpoch: pointerEpochBefore);
            Assert.Equal(PreparedImageRelayoutResult.Retry, result);
            Assert.Equal(pointerEpochBefore + 1, snapshot.PointerInputEpoch);
            Assert.Equal(revisionBefore, snapshot.LayoutRevision);
            Assert.Equal(sizeBefore, snapshot.Size);
            Assert.Same(semanticBefore, snapshot.SemanticDocument);
            Assert.Same(bitmapBefore, document.TargetImage.Bitmap);
            AssertRootsUnchanged(snapshot, rootsBefore, boundsBefore);
        }
        finally
        {
            await DrainAsync(constructionContext);
        }
    }

    [Fact]
    public async Task RevisionChangeDuringPrivatePreparationReturnsRetryWithoutAdditionalMutation()
    {
        await using var seed = new CompletedRasterPlacementCloneTests.PlacementFixture();
        using var session = new MarkdownPerformanceSession(MarkdownPerformanceOptions.Progressive);
        var resolver = new ControlledRasterResolver(DisplaySizedRasterDecodeTests.CreatePng(96, 192));
        await using RootLocalImageDocument document = BuildDocument(seed, session, resolver,
            CreateDocumentSource(resolver, includePendingSibling: false), FlowDirection.LeftToRight, 2f,
            lazy: false);
        await LoadImageAsync(document.TargetImage);

        LayoutSnapshot snapshot = document.Snapshot;
        BlockBox nonImageSibling = Assert.IsAssignableFrom<BlockBox>(snapshot.Blocks.First(
            block => !ReferenceEquals(block, document.TargetImage) && !ReferenceEquals(block, document.PendingImage)));
        Rect[] stateAfterExternalCommit = [];
        Size sizeAfterExternalCommit = default;
        long revisionAfterExternalCommit = -1;

        MarkdownLayoutContext? constructionContext = null;
        try
        {
            PreparedImageRelayoutResult result = snapshot.TryRelayoutCompletedImageRoot(
                [document.TargetImage.BlockIndex],
                LayoutWidth,
                CancellationToken.None,
                out constructionContext,
                phaseHooks: CompletedImageRelayoutPhaseHooks.BeforeMeasure(() =>
                {
                    snapshot.RelayoutChangedBlocks([nonImageSibling.BlockIndex], LayoutWidth, CancellationToken.None);
                    stateAfterExternalCommit = snapshot.Blocks.Select(static block => block.Bounds).ToArray();
                    sizeAfterExternalCommit = snapshot.Size;
                    revisionAfterExternalCommit = snapshot.LayoutRevision;
                }),
                expectedPointerInputEpoch: snapshot.PointerInputEpoch);
            Assert.Equal(PreparedImageRelayoutResult.Retry, result);
            Assert.Equal(revisionAfterExternalCommit, snapshot.LayoutRevision);
            Assert.Equal(sizeAfterExternalCommit, snapshot.Size);
            Assert.Equal(stateAfterExternalCommit,
                snapshot.Blocks.Select(static block => block.Bounds).ToArray());
            Assert.Same(document.TargetImage, snapshot.Blocks.Single(block => block.BlockIndex == document.TargetImage.BlockIndex));
        }
        finally
        {
            await DrainAsync(constructionContext);
        }
    }

    [Fact]
    public async Task CancellationDuringPrivatePreparationLeavesSourceGeometryAndBitmapUntouched()
    {
        await using var seed = new CompletedRasterPlacementCloneTests.PlacementFixture();
        using var session = new MarkdownPerformanceSession(MarkdownPerformanceOptions.Progressive);
        var resolver = new ControlledRasterResolver(DisplaySizedRasterDecodeTests.CreatePng(96, 192));
        await using RootLocalImageDocument document = BuildDocument(seed, session, resolver,
            CreateDocumentSource(resolver, includePendingSibling: false), FlowDirection.LeftToRight, 1f,
            lazy: false);
        await LoadImageAsync(document.TargetImage);

        LayoutSnapshot snapshot = document.Snapshot;
        BlockBox[] rootsBefore = snapshot.Blocks.ToArray();
        Rect[] boundsBefore = rootsBefore.Select(static block => block.Bounds).ToArray();
        Size sizeBefore = snapshot.Size;
        long revisionBefore = snapshot.LayoutRevision;
        CanvasBitmap bitmapBefore = Assert.IsType<CanvasBitmap>(document.TargetImage.Bitmap);
        using var cancellation = new CancellationTokenSource();
        MarkdownLayoutContext? constructionContext = null;
        try
        {
            Assert.Throws<OperationCanceledException>(() => snapshot.TryRelayoutCompletedImageRoot(
                [document.TargetImage.BlockIndex],
                LayoutWidth,
                cancellation.Token,
                out constructionContext,
                phaseHooks: CompletedImageRelayoutPhaseHooks.BeforeMeasure(cancellation.Cancel),
                expectedPointerInputEpoch: snapshot.PointerInputEpoch));

            Assert.Equal(revisionBefore, snapshot.LayoutRevision);
            Assert.Equal(sizeBefore, snapshot.Size);
            Assert.Same(bitmapBefore, document.TargetImage.Bitmap);
            AssertRootsUnchanged(snapshot, rootsBefore, boundsBefore);
        }
        finally
        {
            await DrainAsync(constructionContext);
        }
    }

    [Fact]
    public async Task LazySnapshotOverSixtyFourRootsRelayoutsOneMeasuredImageWithoutRealizingFarImage()
    {
        await using var seed = new CompletedRasterPlacementCloneTests.PlacementFixture();
        using var session = new MarkdownPerformanceSession(MarkdownPerformanceOptions.Progressive);
        var resolver = new ControlledRasterResolver(DisplaySizedRasterDecodeTests.CreatePng(96, 192));
        string sourceText = CreateLongLazySource(resolver);
        await using RootLocalImageDocument optimized = BuildDocument(seed, session, resolver, sourceText,
            FlowDirection.LeftToRight, 1.5f, lazy: true);
        await using RootLocalImageDocument fallback = BuildDocument(seed, session, resolver, sourceText,
            FlowDirection.LeftToRight, 1.5f, lazy: true);
        ImageBox optimizedFarImage = Assert.IsType<ImageBox>(optimized.FarImage);
        ImageBox fallbackFarImage = Assert.IsType<ImageBox>(fallback.FarImage);

        Assert.True(optimized.Snapshot.TopLevelBlockCount > 64);
        Assert.Equal(optimized.Snapshot.TopLevelBlockCount, fallback.Snapshot.TopLevelBlockCount);
        Assert.True(optimized.Snapshot.IsTopLevelBlockMeasured(optimized.TargetImage));
        Assert.False(optimized.Snapshot.IsTopLevelBlockMeasured(optimizedFarImage));
        Assert.False(fallback.Snapshot.IsTopLevelBlockMeasured(fallbackFarImage));
        await LoadImageAsync(optimized.TargetImage);
        await LoadImageAsync(fallback.TargetImage);
        Assert.Equal(0, resolver.ResolveCountFor(optimized.Resolver.FarUrl));

        LayoutSnapshot snapshot = optimized.Snapshot;
        BlockBox[] rootsBefore = snapshot.Blocks.ToArray();
        bool[] measuredBefore = rootsBefore.Select(snapshot.IsTopLevelBlockMeasured).ToArray();
        int measuredCountBefore = snapshot.MeasuredTopLevelBlockCount;
        Rect[] boundsBefore = rootsBefore.Select(static block => block.Bounds).ToArray();
        MarkdownSemanticDocument semanticsBefore = snapshot.SemanticDocument;
        long revisionBefore = snapshot.LayoutRevision;

        fallback.Snapshot.RelayoutChangedBlocks([fallback.TargetImage.BlockIndex], LayoutWidth, CancellationToken.None);

        MarkdownLayoutContext? constructionContext = null;
        try
        {
            PreparedImageRelayoutResult result = snapshot.TryRelayoutCompletedImageRoot(
                [optimized.TargetImage.BlockIndex],
                LayoutWidth,
                CancellationToken.None,
                out constructionContext,
                expectedPointerInputEpoch: snapshot.PointerInputEpoch);
            Assert.Equal(PreparedImageRelayoutResult.Committed, result);
            Assert.Equal(revisionBefore + 1, snapshot.LayoutRevision);
            Assert.Equal(fallback.Snapshot.TopLevelBlockCount, snapshot.Blocks.Count);
            Assert.Equal(measuredCountBefore, snapshot.MeasuredTopLevelBlockCount);
            Assert.Same(semanticsBefore, snapshot.SemanticDocument);
            AssertSemanticFingerprintsPreserved(semanticsBefore, snapshot);
            Assert.Equal(GetSemanticProjection(fallback.Snapshot),
                GetSemanticProjection(snapshot));
            Assert.Equal(0, resolver.ResolveCountFor(optimized.Resolver.FarUrl));
            Assert.Null(optimizedFarImage.Bitmap);
            Assert.False(snapshot.IsTopLevelBlockMeasured(optimizedFarImage));
            AssertRootsMatchFallbackWithMeasuredMask(fallback.Snapshot, snapshot, rootsBefore, measuredBefore);
            Assert.NotEqual(boundsBefore[0].Height, snapshot.Blocks[0].Bounds.Height);
            AssertViewportIndexMatchesFullRebuild(snapshot);
            AssertViewportIndexMatchesFullRebuild(fallback.Snapshot);

            Color[] actualPixels = PaintViewport(snapshot, seed.Device, height: 180, dpi: 144);
            Color[] expectedPixels = PaintViewport(fallback.Snapshot, seed.Device, height: 180, dpi: 144);
            Assert.Equal(expectedPixels, actualPixels);
        }
        finally
        {
            await DrainAsync(constructionContext);
        }
    }

    [Fact]
    public async Task LazyLargeSnapshotRelayoutKeepsMeasuredPendingSiblingActive()
    {
        await using var seed = new CompletedRasterPlacementCloneTests.PlacementFixture();
        using var session = new MarkdownPerformanceSession(MarkdownPerformanceOptions.Progressive);
        var resolver = new ControlledRasterResolver(DisplaySizedRasterDecodeTests.CreatePng(96, 192));
        string sourceText = CreateLongLazySourceWithPendingSibling(resolver);
        await using RootLocalImageDocument optimized = BuildDocument(seed, session, resolver, sourceText,
            FlowDirection.LeftToRight, 1.5f, lazy: true, lazyViewportHeight: 600);
        await using RootLocalImageDocument fallback = BuildDocument(seed, session, resolver, sourceText,
            FlowDirection.LeftToRight, 1.5f, lazy: true, lazyViewportHeight: 600);

        ImageBox optimizedTarget = optimized.TargetImage;
        ImageBox optimizedSecond = FindSecondTarget(optimized);
        ImageBox optimizedPending = Assert.IsType<ImageBox>(optimized.PendingImage);
        ImageBox optimizedFar = Assert.IsType<ImageBox>(optimized.FarImage);
        ImageBox fallbackSecond = FindSecondTarget(fallback);
        ImageBox fallbackPending = Assert.IsType<ImageBox>(fallback.PendingImage);
        Assert.True(optimized.Snapshot.TopLevelBlockCount > 64);
        AssertMeasured(optimized.Snapshot, optimizedTarget, optimizedSecond, optimizedPending);
        AssertMeasured(fallback.Snapshot, fallbackSecond, fallbackPending);
        Assert.False(optimized.Snapshot.IsTopLevelBlockMeasured(optimizedFar));

        await LoadImageAsync(optimizedTarget);
        await LoadImageAsync(optimizedSecond);
        await LoadImageAsync(fallback.TargetImage);
        await LoadImageAsync(fallbackSecond);
        await StartPendingSiblingAsync(optimized);
        await StartPendingSiblingAsync(fallback);
        await WaitForPendingWorkAndResolverStabilityAsync(optimized, fallback);
        Assert.True(fallbackPending.GetWorkState().ActiveWorkCount > 0);

        LayoutSnapshot snapshot = optimized.Snapshot;
        BlockBox[] rootsBefore = snapshot.Blocks.ToArray();
        bool[] measuredBefore = rootsBefore.Select(snapshot.IsTopLevelBlockMeasured).ToArray();
        Size sizeBefore = snapshot.Size;
        long revisionBefore = snapshot.LayoutRevision;
        MarkdownSemanticDocument semanticBefore = snapshot.SemanticDocument;
        int targetResolvesBefore = resolver.ResolveCountFor(optimized.Resolver.TargetUrl);
        int secondResolvesBefore = resolver.ResolveCountFor(optimized.Resolver.SecondTargetUrl);
        int pendingResolvesBefore = resolver.ResolveCountFor(optimized.Resolver.PendingUrl);
        int pendingWorkBefore = optimizedPending.GetWorkState().ActiveWorkCount;
        Assert.True(pendingWorkBefore > 0);
        Assert.Null(optimizedPending.Bitmap);

        fallback.Snapshot.RelayoutChangedBlocks(
            [fallbackSecond.BlockIndex, fallback.TargetImage.BlockIndex], LayoutWidth, CancellationToken.None);

        MarkdownLayoutContext? constructionContext = null;
        try
        {
            PreparedImageRelayoutResult result = snapshot.TryRelayoutCompletedImageRoots(
                [optimizedSecond.BlockIndex, optimizedTarget.BlockIndex], LayoutWidth, CancellationToken.None,
                out constructionContext,
                expectedPointerInputEpoch: snapshot.PointerInputEpoch);

            Assert.Equal(PreparedImageRelayoutResult.Committed, result);
            Assert.Equal(revisionBefore + 1, snapshot.LayoutRevision);
            Assert.NotEqual(sizeBefore, snapshot.Size);
            Assert.Equal(fallback.Snapshot.Size, snapshot.Size);
            Assert.Same(semanticBefore, snapshot.SemanticDocument);
            AssertSemanticFingerprintsPreserved(semanticBefore, snapshot);
            Assert.Equal(targetResolvesBefore, resolver.ResolveCountFor(optimized.Resolver.TargetUrl));
            Assert.Equal(secondResolvesBefore, resolver.ResolveCountFor(optimized.Resolver.SecondTargetUrl));
            Assert.Equal(pendingResolvesBefore, resolver.ResolveCountFor(optimized.Resolver.PendingUrl));
            AssertPendingWorkAndFarRootUntouched(snapshot, optimizedPending, pendingWorkBefore, optimizedFar);
            Assert.NotNull(optimizedTarget.Bitmap);
            Assert.NotNull(optimizedSecond.Bitmap);

            AssertRootsMatchFallbackWithMeasuredMask(fallback.Snapshot, snapshot, rootsBefore, measuredBefore);
            Assert.Equal(PaintViewport(snapshot, seed.Device, height: 600, dpi: 144),
                PaintViewport(fallback.Snapshot, seed.Device, height: 600, dpi: 144));
        }
        finally
        {
            await DrainAsync(constructionContext);
        }
    }

    [Fact]
    public async Task RootRelayoutRetriesWhenSourceLeaseChangesAfterPrivateClone()
    {
        await using var seed = new CompletedRasterPlacementCloneTests.PlacementFixture();
        using var session = new MarkdownPerformanceSession(MarkdownPerformanceOptions.Progressive);
        var resolver = new ControlledRasterResolver(DisplaySizedRasterDecodeTests.CreatePng(96, 192));
        await using RootLocalImageDocument document = BuildDocument(seed, session, resolver,
            CreateDocumentSource(resolver, includePendingSibling: false),
            FlowDirection.LeftToRight, 1f, lazy: false);
        ImageBox source = document.TargetImage;
        await LoadImageAsync(source);

        LayoutSnapshot snapshot = document.Snapshot;
        BlockBox[] rootsBefore = snapshot.Blocks.ToArray();
        Rect[] boundsBefore = rootsBefore.Select(static block => block.Bounds).ToArray();
        Rect sourceBounds = source.Bounds;
        Size sizeBefore = snapshot.Size;
        long revisionBefore = snapshot.LayoutRevision;
        MarkdownSemanticDocument semanticsBefore = snapshot.SemanticDocument;
        CanvasBitmap oldBitmap = Assert.IsType<CanvasBitmap>(source.Bitmap);
        CanvasBitmap? replacementBitmap = null;
        MarkdownLayoutContext? constructionContext = null;
        try
        {
            PreparedImageRelayoutResult result = snapshot.TryRelayoutCompletedImageRoot(
                [source.BlockIndex], LayoutWidth, CancellationToken.None,
                out constructionContext,
                phaseHooks: CompletedImageRelayoutPhaseHooks.BeforeMeasure(() =>
                {
                    replacementBitmap = CreateBitmap(seed.Device, 24, 12);
                    SharedCanvasBitmapCache.Lease replacementLease =
                        _renderResources.Images.BitmapCache.CreateUncachedLease(replacementBitmap);
                    InvokeReplaceBitmap(source, replacementLease);
                }),
                expectedPointerInputEpoch: snapshot.PointerInputEpoch);

            Assert.Equal(PreparedImageRelayoutResult.Retry, result);
            Assert.NotNull(replacementBitmap);
            Assert.NotSame(oldBitmap, replacementBitmap);
            Assert.Same(replacementBitmap, source.Bitmap);
            Assert.Equal(revisionBefore, snapshot.LayoutRevision);
            Assert.Equal(sizeBefore, snapshot.Size);
            Assert.Same(semanticsBefore, snapshot.SemanticDocument);
            Assert.Contains(snapshot.Blocks, block => ReferenceEquals(block, source));
            Assert.Equal(sourceBounds, source.Bounds);
            Assert.Equal(0, source.GetWorkState().ActiveWorkCount);
            AssertRootsUnchanged(snapshot, rootsBefore, boundsBefore);
        }
        finally
        {
            await DrainAsync(constructionContext);
        }
    }

    [Fact]
    public async Task BatchRetriesWhenLastSourceLeaseChangesWithoutTransferringEarlierPlacement()
    {
        await using var seed = new CompletedRasterPlacementCloneTests.PlacementFixture();
        using var session = new MarkdownPerformanceSession(MarkdownPerformanceOptions.Progressive);
        var resolver = new ControlledRasterResolver(DisplaySizedRasterDecodeTests.CreatePng(96, 192));
        await using RootLocalImageDocument document = BuildDocument(seed, session, resolver,
            CreateBatchDocumentSource(resolver), FlowDirection.RightToLeft, 2f, lazy: false);
        ImageBox first = document.TargetImage;
        ImageBox second = FindSecondTarget(document);
        await LoadImageAsync(first);
        await LoadImageAsync(second);

        LayoutSnapshot snapshot = document.Snapshot;
        BlockBox[] rootsBefore = snapshot.Blocks.ToArray();
        Rect[] boundsBefore = rootsBefore.Select(static block => block.Bounds).ToArray();
        Size sizeBefore = snapshot.Size;
        long revisionBefore = snapshot.LayoutRevision;
        MarkdownSemanticDocument semanticBefore = snapshot.SemanticDocument;
        CanvasBitmap firstBitmap = Assert.IsType<CanvasBitmap>(first.Bitmap);
        CanvasBitmap secondBitmap = Assert.IsType<CanvasBitmap>(second.Bitmap);
        SharedCanvasBitmapCache.Lease firstLease = GetImageLease(first);
        CanvasBitmap? replacementBitmap = null;
        MarkdownLayoutContext? constructionContext = null;

        try
        {
            PreparedImageRelayoutResult result = snapshot.TryRelayoutCompletedImageRoots(
                [second.BlockIndex, first.BlockIndex], LayoutWidth, CancellationToken.None,
                out constructionContext,
                phaseHooks: CompletedImageRelayoutPhaseHooks.BeforeMeasure(() =>
                {
                    replacementBitmap = CreateBitmap(seed.Device, 24, 12);
                    InvokeReplaceBitmap(second,
                        _renderResources.Images.BitmapCache.CreateUncachedLease(replacementBitmap));
                }),
                expectedPointerInputEpoch: snapshot.PointerInputEpoch);

            Assert.Equal(PreparedImageRelayoutResult.Retry, result);
            Assert.NotNull(replacementBitmap);
            Assert.Same(firstBitmap, first.Bitmap);
            Assert.Same(firstLease, GetImageLease(first));
            Assert.NotSame(secondBitmap, replacementBitmap);
            Assert.Same(replacementBitmap, second.Bitmap);
            Assert.Equal(revisionBefore, snapshot.LayoutRevision);
            Assert.Equal(sizeBefore, snapshot.Size);
            Assert.Same(semanticBefore, snapshot.SemanticDocument);
            AssertRootsUnchanged(snapshot, rootsBefore, boundsBefore);
        }
        finally
        {
            await DrainAsync(constructionContext);
        }
    }

    [Fact]
    public async Task DuplicateAndUnknownChangedRootIndicesAreUnavailableWithoutMutation()
    {
        await using var seed = new CompletedRasterPlacementCloneTests.PlacementFixture();
        using var session = new MarkdownPerformanceSession(MarkdownPerformanceOptions.Progressive);
        var resolver = new ControlledRasterResolver(DisplaySizedRasterDecodeTests.CreatePng(96, 192));
        await using RootLocalImageDocument document = BuildDocument(seed, session, resolver,
            CreateDocumentSource(resolver, includePendingSibling: false),
            FlowDirection.RightToLeft, 2f, lazy: false);
        await LoadImageAsync(document.TargetImage);

        LayoutSnapshot snapshot = document.Snapshot;
        BlockBox[] rootsBefore = snapshot.Blocks.ToArray();
        Rect[] boundsBefore = rootsBefore.Select(static block => block.Bounds).ToArray();
        Size sizeBefore = snapshot.Size;
        long revisionBefore = snapshot.LayoutRevision;
        MarkdownSemanticDocument semanticsBefore = snapshot.SemanticDocument;
        CanvasBitmap bitmapBefore = Assert.IsType<CanvasBitmap>(document.TargetImage.Bitmap);
        Assert.NotEqual(int.MaxValue, document.TargetImage.BlockIndex);
        Assert.DoesNotContain(snapshot.Blocks, block => block.BlockIndex == int.MaxValue);

        foreach (IReadOnlyCollection<int> indices in new IReadOnlyCollection<int>[]
                 {
                     new[] { document.TargetImage.BlockIndex, document.TargetImage.BlockIndex },
                     new[] { int.MaxValue },
                 })
        {
            MarkdownLayoutContext? constructionContext = null;
            try
            {
                PreparedImageRelayoutResult result = snapshot.TryRelayoutCompletedImageRoot(
                    indices, LayoutWidth, CancellationToken.None, out constructionContext,
                    expectedPointerInputEpoch: snapshot.PointerInputEpoch);

                Assert.Equal(PreparedImageRelayoutResult.Unavailable, result);
                Assert.Null(constructionContext);
                Assert.Equal(revisionBefore, snapshot.LayoutRevision);
                Assert.Equal(sizeBefore, snapshot.Size);
                Assert.Same(semanticsBefore, snapshot.SemanticDocument);
                Assert.Same(bitmapBefore, document.TargetImage.Bitmap);
                AssertRootsUnchanged(snapshot, rootsBefore, boundsBefore);
            }
            finally
            {
                await DrainAsync(constructionContext);
            }
        }
    }

    [Fact]
    public async Task MultipleChangedRootsRemainUnavailableAndDoNotTouchSource()
    {
        await using var seed = new CompletedRasterPlacementCloneTests.PlacementFixture();
        using var session = new MarkdownPerformanceSession(MarkdownPerformanceOptions.Progressive);
        var resolver = new ControlledRasterResolver(DisplaySizedRasterDecodeTests.CreatePng(96, 192));
        await using RootLocalImageDocument document = BuildDocument(seed, session, resolver,
            CreateDocumentSource(resolver, includePendingSibling: false), FlowDirection.LeftToRight, 1f,
            lazy: false);
        await LoadImageAsync(document.TargetImage);

        LayoutSnapshot snapshot = document.Snapshot;
        BlockBox[] rootsBefore = snapshot.Blocks.ToArray();
        Rect[] boundsBefore = rootsBefore.Select(static block => block.Bounds).ToArray();
        Size sizeBefore = snapshot.Size;
        long revisionBefore = snapshot.LayoutRevision;
        BlockBox sibling = Assert.IsAssignableFrom<BlockBox>(
            rootsBefore.First(block => !ReferenceEquals(block, document.TargetImage)));
        MarkdownLayoutContext? constructionContext = null;

        try
        {
            PreparedImageRelayoutResult result = snapshot.TryRelayoutCompletedImageRoot(
                [document.TargetImage.BlockIndex, sibling.BlockIndex],
                LayoutWidth,
                CancellationToken.None,
                out constructionContext,
                expectedPointerInputEpoch: snapshot.PointerInputEpoch);
            Assert.Equal(PreparedImageRelayoutResult.Unavailable, result);
            Assert.Null(constructionContext);
            Assert.Equal(revisionBefore, snapshot.LayoutRevision);
            Assert.Equal(sizeBefore, snapshot.Size);
            AssertRootsUnchanged(snapshot, rootsBefore, boundsBefore);
        }
        finally
        {
            await DrainAsync(constructionContext);
        }
    }

    [Fact]
    public async Task BatchPreflightRejectsDuplicateUnknownTooManyAndOverBudgetAltBeforeCloning()
    {
        await using var seed = new CompletedRasterPlacementCloneTests.PlacementFixture();
        using var session = new MarkdownPerformanceSession(MarkdownPerformanceOptions.Progressive);
        var resolver = new ControlledRasterResolver(DisplaySizedRasterDecodeTests.CreatePng(96, 192));
        await using RootLocalImageDocument document = BuildDocument(seed, session, resolver,
            CreateBatchDocumentSource(resolver), FlowDirection.LeftToRight, 1f, lazy: false);
        ImageBox first = document.TargetImage;
        ImageBox second = FindSecondTarget(document);
        await LoadImageAsync(first);
        await LoadImageAsync(second);

        LayoutSnapshot snapshot = document.Snapshot;
        BlockBox[] rootsBefore = snapshot.Blocks.ToArray();
        Rect[] boundsBefore = rootsBefore.Select(static block => block.Bounds).ToArray();
        SharedCanvasBitmapCache.Lease firstLease = GetImageLease(first);
        SharedCanvasBitmapCache.Lease secondLease = GetImageLease(second);
        int firstReferences = GetEntryReferenceCount(firstLease);
        int secondReferences = GetEntryReferenceCount(secondLease);
        Size sizeBefore = snapshot.Size;
        long revisionBefore = snapshot.LayoutRevision;
        MarkdownSemanticDocument semanticBefore = snapshot.SemanticDocument;

        IReadOnlyCollection<int>[] rejectedBatches =
        [
            new[] { first.BlockIndex, second.BlockIndex, first.BlockIndex },
            new[] { first.BlockIndex, int.MaxValue },
            Enumerable.Range(10_000, LayoutSnapshot.MaximumPreparedImageRoots + 1).ToArray(),
        ];
        foreach (IReadOnlyCollection<int> indices in rejectedBatches)
        {
            await AssertBatchUnavailableWithoutLeaseChangeAsync(
                snapshot, indices, firstLease, firstReferences, secondLease, secondReferences);
        }

        const int firstAltLength = 32 * 1024;
        const int secondAltLength = 32 * 1024 + 1;
        string largeAltSource = CreateBatchDocumentSource(
            resolver, new string('a', firstAltLength), new string('b', secondAltLength));
        await using RootLocalImageDocument overBudget = BuildDocument(seed, session, resolver, largeAltSource,
            FlowDirection.LeftToRight, 1f, lazy: false);
        await AssertOverBudgetAltBatchUnavailableAsync(overBudget, firstAltLength, secondAltLength);

        Assert.Equal(revisionBefore, snapshot.LayoutRevision);
        Assert.Equal(sizeBefore, snapshot.Size);
        Assert.Same(semanticBefore, snapshot.SemanticDocument);
        AssertRootsUnchanged(snapshot, rootsBefore, boundsBefore);
    }

    private RootLocalImageDocument BuildDocument(
        CompletedRasterPlacementCloneTests.PlacementFixture seed,
        MarkdownPerformanceSession session,
        ControlledRasterResolver resolver,
        string sourceText,
        FlowDirection direction,
        float rasterizationScale,
        bool lazy,
        float lazyViewportHeight = 120) =>
        RootLocalImageDocument.Build(_renderResources, seed, session, resolver, sourceText, direction,
            rasterizationScale, lazy, lazyViewportHeight);
}
