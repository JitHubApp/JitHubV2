using System.Collections.Concurrent;
using System.Reflection;
using MarkdownRenderer.Accessibility;
using MarkdownRenderer.Document;
using MarkdownRenderer.Extensions;
using MarkdownRenderer.Images;
using MarkdownRenderer.Layout;
using MarkdownRenderer.Layout.Boxes;
using MarkdownRenderer.Parsing;
using MarkdownRenderer.Performance;
using MarkdownRenderer.Theming;
using Markdig;
using Microsoft.Graphics.Canvas;
using Microsoft.UI.Xaml;
using Windows.Foundation;
using Windows.Graphics.DirectX;
using Windows.UI;
using Xunit;

namespace MarkdownRenderer.GitHub.Tests;

[Collection(SharedCanvasBitmapCacheTestCollection.Name)]
public sealed class RootLocalImageRelayoutTests
{
    private const float LayoutWidth = 320f;
    private const long AutomationGeneration = 4_731;

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
        await using ImageDocument optimized = BuildDocument(seed, session, resolver, sourceText, direction,
            rasterizationScale, lazy: false);
        await using ImageDocument fallback = BuildDocument(seed, session, resolver, sourceText, direction,
            rasterizationScale, lazy: false);

        ImageBox optimizedImage = optimized.TargetImage;
        ImageBox fallbackImage = fallback.TargetImage;
        Rect optimizedPlaceholderBounds = optimizedImage.Bounds;
        Rect fallbackPlaceholderBounds = fallbackImage.Bounds;
        Assert.Equal(optimizedPlaceholderBounds, fallbackPlaceholderBounds);

        await LoadTargetAsync(optimizedImage);
        await LoadTargetAsync(fallbackImage);
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
            Assert.Equal(sourceWaiterEpoch, ReadPrivateLongField(optimizedImage, "_bitmapOutputWaiterEpoch"));
            Assert.Equal(oldRevision + 1, snapshot.LayoutRevision);
            Assert.NotEqual(oldSize, snapshot.Size);
            Assert.Equal(fallback.Snapshot.Size, snapshot.Size);
            Assert.Equal(sourceRoots.Length, snapshot.Blocks.Count);
            for (int ordinal = 0; ordinal < sourceRoots.Length; ordinal++)
            {
                Assert.Same(sourceRoots[ordinal], snapshot.Blocks[ordinal]);
                Assert.Equal(LayoutPlacementTestGeometry.ToLogical(fallback.Snapshot, fallback.Snapshot.Blocks[ordinal]),
                    LayoutPlacementTestGeometry.ToLogical(snapshot, snapshot.Blocks[ordinal]));
            }
            Assert.NotEqual(oldBounds[targetOrdinal].Height, snapshot.Blocks[targetOrdinal].Bounds.Height);
            Assert.Same(optimizedImage, snapshot.Blocks[targetOrdinal]);
            Assert.NotNull(optimizedImage.Bitmap);
            Assert.NotNull(fallbackImage.Bitmap);
            Assert.Same(optimized.SourceMap, snapshot.SourceMap);
            Assert.Equal(AutomationGeneration, snapshot.AutomationDocumentGeneration);
            Assert.Equal(oldSemantics.Text, snapshot.SemanticDocument.Text);
            Assert.Equal(oldSemantics.TextProjectionFingerprint,
                snapshot.SemanticDocument.TextProjectionFingerprint);
            Assert.Equal(oldSemantics.SemanticRangeFingerprint,
                snapshot.SemanticDocument.SemanticRangeFingerprint);
            Assert.Equal(oldSemantics.TextSpans.Select(static span => (span.TextStart, span.TextEnd)),
                snapshot.SemanticDocument.TextSpans.Select(static span => (span.TextStart, span.TextEnd)));
            Assert.Equal(GetSemanticProjection(fallback.Snapshot),
                GetSemanticProjection(snapshot));
            Assert.Equal(targetResolveCount, resolver.ResolveCountFor(optimized.Resolver.TargetUrl));
            Assert.Equal(pendingResolveCount, resolver.ResolveCountFor(optimized.Resolver.PendingUrl));
            Assert.NotNull(optimized.PendingImage);
            Assert.NotNull(fallback.PendingImage);
            Assert.Null(optimized.PendingImage!.Bitmap);
            Assert.Null(fallback.PendingImage!.Bitmap);

            AssertViewportIndexMatchesFullRebuild(snapshot);
            AssertViewportIndexMatchesFullRebuild(fallback.Snapshot);
            Assert.Equal(Paint(snapshot, seed.Device, rasterizationScale),
                Paint(fallback.Snapshot, seed.Device, rasterizationScale));
        }
        finally
        {
            if (constructionContext is not null)
                await constructionContext.DrainOwnedRetirementsAsync().WaitAsync(TimeSpan.FromSeconds(10));
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
        await using ImageDocument optimized = BuildDocument(seed, session, resolver, sourceText, direction,
            rasterizationScale, lazy: false);
        await using ImageDocument fallback = BuildDocument(seed, session, resolver, sourceText, direction,
            rasterizationScale, lazy: false);

        ImageBox optimizedFirst = optimized.TargetImage;
        ImageBox optimizedSecond = FindSecondTarget(optimized);
        ImageBox fallbackFirst = fallback.TargetImage;
        ImageBox fallbackSecond = FindSecondTarget(fallback);
        Assert.NotEqual(optimizedFirst.BlockIndex, optimizedSecond.BlockIndex);
        Assert.NotEqual(fallbackFirst.BlockIndex, fallbackSecond.BlockIndex);

        await LoadTargetAsync(optimizedFirst);
        await LoadTargetAsync(optimizedSecond);
        await LoadTargetAsync(fallbackFirst);
        await LoadTargetAsync(fallbackSecond);

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
            Assert.Equal(oldSemantics.TextProjectionFingerprint,
                snapshot.SemanticDocument.TextProjectionFingerprint);
            Assert.Equal(oldSemantics.SemanticRangeFingerprint,
                snapshot.SemanticDocument.SemanticRangeFingerprint);
            Assert.Same(firstBitmap, optimizedFirst.Bitmap);
            Assert.Same(secondBitmap, optimizedSecond.Bitmap);
            Assert.Equal(firstResolveCount, resolver.ResolveCountFor(optimizedFirst.Url));
            Assert.Equal(secondResolveCount, resolver.ResolveCountFor(optimizedSecond.Url));

            LayoutPlacementGeneration fallbackPlacements = fallback.Snapshot.PublishedPlacementGeneration;
            LayoutPlacementGeneration candidatePlacements = snapshot.PublishedPlacementGeneration;

            for (int ordinal = 0; ordinal < rootsBefore.Length; ordinal++)
            {
                Assert.Same(rootsBefore[ordinal], snapshot.Blocks[ordinal]);
                Rect expectedBounds = LayoutPlacementTestGeometry.ToLogical(
                    fallback.Snapshot, fallback.Snapshot.Blocks[ordinal]);
                Rect actualBounds = LayoutPlacementTestGeometry.ToLogical(snapshot, snapshot.Blocks[ordinal]);
                Assert.Equal(expectedBounds, actualBounds);
            }
            Assert.Equal(GetSemanticProjection(fallback.Snapshot),
                GetSemanticProjection(snapshot));
            Assert.NotEqual(oldBounds[Array.IndexOf(rootsBefore, optimizedFirst)].Height,
                optimizedFirst.Bounds.Height);
            Assert.NotEqual(oldBounds[Array.IndexOf(rootsBefore, optimizedSecond)].Height,
                optimizedSecond.Bounds.Height);
            AssertViewportIndexMatchesFullRebuild(snapshot);
            AssertViewportIndexMatchesFullRebuild(fallback.Snapshot);
            Assert.Equal(Paint(snapshot, seed.Device, rasterizationScale),
                Paint(fallback.Snapshot, seed.Device, rasterizationScale));
        }
        finally
        {
            if (constructionContext is not null)
                await constructionContext.DrainOwnedRetirementsAsync().WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    [Fact]
    public async Task BatchCancellationAfterPreparationLeavesPublishedGenerationAndSourcesUntouched()
    {
        await using var seed = new CompletedRasterPlacementCloneTests.PlacementFixture();
        using var session = new MarkdownPerformanceSession(MarkdownPerformanceOptions.Progressive);
        var resolver = new ControlledRasterResolver(DisplaySizedRasterDecodeTests.CreatePng(96, 192));
        await using ImageDocument document = BuildDocument(seed, session, resolver,
            CreateBatchDocumentSource(resolver), FlowDirection.LeftToRight, 1.5f, lazy: false);
        ImageBox first = document.TargetImage;
        ImageBox second = FindSecondTarget(document);
        await LoadTargetAsync(first);
        await LoadTargetAsync(second);

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
                beforeCommitOutsideGateForTests: () =>
                {
                    // This hook is reached only after private measurement,
                    // native-owner validation and successor generation creation.
                    commitBoundaryReached = true;
                    cancellation.Cancel();
                }));

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
            Assert.Equal(0, first.ActiveImageWorkCountForTests);
            Assert.Equal(0, second.ActiveImageWorkCountForTests);
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
            if (constructionContext is not null)
                await constructionContext.DrainOwnedRetirementsAsync().WaitAsync(TimeSpan.FromSeconds(10));
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
        await using ImageDocument document = BuildDocument(seed, session, resolver,
            CreateBatchDocumentSource(resolver), FlowDirection.LeftToRight, 1.5f, lazy: false);
        ImageBox first = document.TargetImage;
        ImageBox second = FindSecondTarget(document);
        await LoadTargetAsync(first);
        await LoadTargetAsync(second);

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
                beforeMeasureOutsideGateForTests: cancellation.Cancel,
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
            for (int ordinal = 0; ordinal < rootsBefore.Length; ordinal++)
            {
                Assert.Same(rootsBefore[ordinal], snapshot.Blocks[ordinal]);
                Assert.Equal(boundsBefore[ordinal], snapshot.Blocks[ordinal].Bounds);
            }
        }
        finally
        {
            if (constructionContext is not null)
                await constructionContext.DrainOwnedRetirementsAsync().WaitAsync(TimeSpan.FromSeconds(10));
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
        await using ImageDocument document = BuildDocument(seed, session, resolver,
            CreateDocumentSource(resolver, includePendingSibling: false), FlowDirection.LeftToRight, 1f,
            lazy: false);
        await LoadTargetAsync(document.TargetImage);

        LayoutSnapshot snapshot = document.Snapshot;
        long pointerEpoch = snapshot.PointerInputEpoch;
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        Task<PreparedImageRelayoutResult>? work = null;
        var preparedContext = new ContextCapture();
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
                        beforeMeasureOutsideGateForTests: () =>
                        {
                            entered.Set();
                            if (!release.Wait(TimeSpan.FromSeconds(10)))
                                throw new TimeoutException("The native preparation test gate was not released.");
                        },
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
            if (preparedContext.Value is not null)
                await preparedContext.Value.DrainOwnedRetirementsAsync().WaitAsync(TimeSpan.FromSeconds(10));
        }
        Assert.Equal(pointerEpoch, snapshot.PointerInputEpoch);
    }

    [Fact]
    public async Task PointerInputDuringPrivatePreparationReturnsRetryWithoutSourceMutation()
    {
        await using var seed = new CompletedRasterPlacementCloneTests.PlacementFixture();
        using var session = new MarkdownPerformanceSession(MarkdownPerformanceOptions.Progressive);
        var resolver = new ControlledRasterResolver(DisplaySizedRasterDecodeTests.CreatePng(96, 192));
        await using ImageDocument document = BuildDocument(seed, session, resolver,
            CreateDocumentSource(resolver, includePendingSibling: false), FlowDirection.RightToLeft, 1.5f,
            lazy: false);
        await LoadTargetAsync(document.TargetImage);

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
                beforeMeasureOutsideGateForTests: snapshot.NotifyPointerInput,
                expectedPointerInputEpoch: pointerEpochBefore);
            Assert.Equal(PreparedImageRelayoutResult.Retry, result);
            Assert.Equal(pointerEpochBefore + 1, snapshot.PointerInputEpoch);
            Assert.Equal(revisionBefore, snapshot.LayoutRevision);
            Assert.Equal(sizeBefore, snapshot.Size);
            Assert.Same(semanticBefore, snapshot.SemanticDocument);
            Assert.Same(bitmapBefore, document.TargetImage.Bitmap);
            for (int ordinal = 0; ordinal < rootsBefore.Length; ordinal++)
            {
                Assert.Same(rootsBefore[ordinal], snapshot.Blocks[ordinal]);
                Assert.Equal(boundsBefore[ordinal], snapshot.Blocks[ordinal].Bounds);
            }
        }
        finally
        {
            if (constructionContext is not null)
                await constructionContext.DrainOwnedRetirementsAsync().WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    [Fact]
    public async Task RevisionChangeDuringPrivatePreparationReturnsRetryWithoutAdditionalMutation()
    {
        await using var seed = new CompletedRasterPlacementCloneTests.PlacementFixture();
        using var session = new MarkdownPerformanceSession(MarkdownPerformanceOptions.Progressive);
        var resolver = new ControlledRasterResolver(DisplaySizedRasterDecodeTests.CreatePng(96, 192));
        await using ImageDocument document = BuildDocument(seed, session, resolver,
            CreateDocumentSource(resolver, includePendingSibling: false), FlowDirection.LeftToRight, 2f,
            lazy: false);
        await LoadTargetAsync(document.TargetImage);

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
                beforeMeasureOutsideGateForTests: () =>
                {
                    snapshot.RelayoutChangedBlocks([nonImageSibling.BlockIndex], LayoutWidth, CancellationToken.None);
                    stateAfterExternalCommit = snapshot.Blocks.Select(static block => block.Bounds).ToArray();
                    sizeAfterExternalCommit = snapshot.Size;
                    revisionAfterExternalCommit = snapshot.LayoutRevision;
                },
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
            if (constructionContext is not null)
                await constructionContext.DrainOwnedRetirementsAsync().WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    [Fact]
    public async Task CancellationDuringPrivatePreparationLeavesSourceGeometryAndBitmapUntouched()
    {
        await using var seed = new CompletedRasterPlacementCloneTests.PlacementFixture();
        using var session = new MarkdownPerformanceSession(MarkdownPerformanceOptions.Progressive);
        var resolver = new ControlledRasterResolver(DisplaySizedRasterDecodeTests.CreatePng(96, 192));
        await using ImageDocument document = BuildDocument(seed, session, resolver,
            CreateDocumentSource(resolver, includePendingSibling: false), FlowDirection.LeftToRight, 1f,
            lazy: false);
        await LoadTargetAsync(document.TargetImage);

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
                beforeMeasureOutsideGateForTests: cancellation.Cancel,
                expectedPointerInputEpoch: snapshot.PointerInputEpoch));

            Assert.Equal(revisionBefore, snapshot.LayoutRevision);
            Assert.Equal(sizeBefore, snapshot.Size);
            Assert.Same(bitmapBefore, document.TargetImage.Bitmap);
            for (int ordinal = 0; ordinal < rootsBefore.Length; ordinal++)
            {
                Assert.Same(rootsBefore[ordinal], snapshot.Blocks[ordinal]);
                Assert.Equal(boundsBefore[ordinal], snapshot.Blocks[ordinal].Bounds);
            }
        }
        finally
        {
            if (constructionContext is not null)
                await constructionContext.DrainOwnedRetirementsAsync().WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    [Fact]
    public async Task LazySnapshotOverSixtyFourRootsRelayoutsOneMeasuredImageWithoutRealizingFarImage()
    {
        await using var seed = new CompletedRasterPlacementCloneTests.PlacementFixture();
        using var session = new MarkdownPerformanceSession(MarkdownPerformanceOptions.Progressive);
        var resolver = new ControlledRasterResolver(DisplaySizedRasterDecodeTests.CreatePng(96, 192));
        string sourceText = CreateLongLazySource(resolver);
        await using ImageDocument optimized = BuildDocument(seed, session, resolver, sourceText,
            FlowDirection.LeftToRight, 1.5f, lazy: true);
        await using ImageDocument fallback = BuildDocument(seed, session, resolver, sourceText,
            FlowDirection.LeftToRight, 1.5f, lazy: true);
        ImageBox optimizedFarImage = Assert.IsType<ImageBox>(optimized.FarImage);
        ImageBox fallbackFarImage = Assert.IsType<ImageBox>(fallback.FarImage);

        Assert.True(optimized.Snapshot.TopLevelBlockCount > 64);
        Assert.Equal(optimized.Snapshot.TopLevelBlockCount, fallback.Snapshot.TopLevelBlockCount);
        Assert.True(optimized.Snapshot.IsTopLevelBlockMeasured(optimized.TargetImage));
        Assert.False(optimized.Snapshot.IsTopLevelBlockMeasured(optimizedFarImage));
        Assert.False(fallback.Snapshot.IsTopLevelBlockMeasured(fallbackFarImage));
        await LoadTargetAsync(optimized.TargetImage);
        await LoadTargetAsync(fallback.TargetImage);
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
            Assert.Equal(semanticsBefore.TextProjectionFingerprint,
                snapshot.SemanticDocument.TextProjectionFingerprint);
            Assert.Equal(semanticsBefore.SemanticRangeFingerprint,
                snapshot.SemanticDocument.SemanticRangeFingerprint);
            Assert.Equal(GetSemanticProjection(fallback.Snapshot),
                GetSemanticProjection(snapshot));
            Assert.Equal(0, resolver.ResolveCountFor(optimized.Resolver.FarUrl));
            Assert.Null(optimizedFarImage.Bitmap);
            Assert.False(snapshot.IsTopLevelBlockMeasured(optimizedFarImage));
            for (int ordinal = 0; ordinal < rootsBefore.Length; ordinal++)
            {
                Assert.Same(rootsBefore[ordinal], snapshot.Blocks[ordinal]);
                Assert.Equal(measuredBefore[ordinal], snapshot.IsTopLevelBlockMeasured(snapshot.Blocks[ordinal]));
            Assert.Equal(LayoutPlacementTestGeometry.ToLogical(fallback.Snapshot, fallback.Snapshot.Blocks[ordinal]),
                LayoutPlacementTestGeometry.ToLogical(snapshot, snapshot.Blocks[ordinal]));
            }
            Assert.NotEqual(boundsBefore[0].Height, snapshot.Blocks[0].Bounds.Height);
            AssertViewportIndexMatchesFullRebuild(snapshot);
            AssertViewportIndexMatchesFullRebuild(fallback.Snapshot);

            using var actualPixels = new CanvasRenderTarget(seed.Device, LayoutWidth, 180, 144);
            using (CanvasDrawingSession drawing = actualPixels.CreateDrawingSession())
            {
                drawing.Clear(Color.FromArgb(0, 0, 0, 0));
                snapshot.Paint(drawing, new Rect(0, 0, LayoutWidth, 180));
            }
            using var expectedPixels = new CanvasRenderTarget(seed.Device, LayoutWidth, 180, 144);
            using (CanvasDrawingSession drawing = expectedPixels.CreateDrawingSession())
            {
                drawing.Clear(Color.FromArgb(0, 0, 0, 0));
                fallback.Snapshot.Paint(drawing, new Rect(0, 0, LayoutWidth, 180));
            }
            Assert.Equal(expectedPixels.GetPixelColors(), actualPixels.GetPixelColors());
        }
        finally
        {
            if (constructionContext is not null)
                await constructionContext.DrainOwnedRetirementsAsync().WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    [Fact]
    public async Task LazyLargeSnapshotRelayoutKeepsMeasuredPendingSiblingActive()
    {
        await using var seed = new CompletedRasterPlacementCloneTests.PlacementFixture();
        using var session = new MarkdownPerformanceSession(MarkdownPerformanceOptions.Progressive);
        var resolver = new ControlledRasterResolver(DisplaySizedRasterDecodeTests.CreatePng(96, 192));
        string sourceText = CreateLongLazySourceWithPendingSibling(resolver);
        await using ImageDocument optimized = BuildDocument(seed, session, resolver, sourceText,
            FlowDirection.LeftToRight, 1.5f, lazy: true, lazyViewportHeight: 600);
        await using ImageDocument fallback = BuildDocument(seed, session, resolver, sourceText,
            FlowDirection.LeftToRight, 1.5f, lazy: true, lazyViewportHeight: 600);

        ImageBox optimizedTarget = optimized.TargetImage;
        ImageBox optimizedSecond = FindSecondTarget(optimized);
        ImageBox optimizedPending = Assert.IsType<ImageBox>(optimized.PendingImage);
        ImageBox optimizedFar = Assert.IsType<ImageBox>(optimized.FarImage);
        ImageBox fallbackSecond = FindSecondTarget(fallback);
        ImageBox fallbackPending = Assert.IsType<ImageBox>(fallback.PendingImage);
        Assert.True(optimized.Snapshot.TopLevelBlockCount > 64);
        Assert.True(optimized.Snapshot.IsTopLevelBlockMeasured(optimizedTarget));
        Assert.True(optimized.Snapshot.IsTopLevelBlockMeasured(optimizedSecond));
        Assert.True(optimized.Snapshot.IsTopLevelBlockMeasured(optimizedPending));
        Assert.True(fallback.Snapshot.IsTopLevelBlockMeasured(fallbackSecond));
        Assert.True(fallback.Snapshot.IsTopLevelBlockMeasured(fallbackPending));
        Assert.False(optimized.Snapshot.IsTopLevelBlockMeasured(optimizedFar));

        await LoadTargetAsync(optimizedTarget);
        await LoadTargetAsync(optimizedSecond);
        await LoadTargetAsync(fallback.TargetImage);
        await LoadTargetAsync(fallbackSecond);
        await StartPendingSiblingAsync(optimized);
        await StartPendingSiblingAsync(fallback);
        await WaitForPendingWorkAndResolverStabilityAsync(optimized, fallback);
        Assert.True(fallbackPending.ActiveImageWorkCountForTests > 0);

        LayoutSnapshot snapshot = optimized.Snapshot;
        BlockBox[] rootsBefore = snapshot.Blocks.ToArray();
        bool[] measuredBefore = rootsBefore.Select(snapshot.IsTopLevelBlockMeasured).ToArray();
        Rect[] boundsBefore = rootsBefore.Select(static block => block.Bounds).ToArray();
        Size sizeBefore = snapshot.Size;
        long revisionBefore = snapshot.LayoutRevision;
        MarkdownSemanticDocument semanticBefore = snapshot.SemanticDocument;
        int targetResolvesBefore = resolver.ResolveCountFor(optimized.Resolver.TargetUrl);
        int secondResolvesBefore = resolver.ResolveCountFor(optimized.Resolver.SecondTargetUrl);
        int pendingResolvesBefore = resolver.ResolveCountFor(optimized.Resolver.PendingUrl);
        int pendingWorkBefore = optimizedPending.ActiveImageWorkCountForTests;
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
            Assert.Equal(semanticBefore.TextProjectionFingerprint,
                snapshot.SemanticDocument.TextProjectionFingerprint);
            Assert.Equal(semanticBefore.SemanticRangeFingerprint,
                snapshot.SemanticDocument.SemanticRangeFingerprint);
            Assert.Equal(targetResolvesBefore, resolver.ResolveCountFor(optimized.Resolver.TargetUrl));
            Assert.Equal(secondResolvesBefore, resolver.ResolveCountFor(optimized.Resolver.SecondTargetUrl));
            Assert.Equal(pendingResolvesBefore, resolver.ResolveCountFor(optimized.Resolver.PendingUrl));
            Assert.True(optimizedPending.ActiveImageWorkCountForTests > 0);
            Assert.Equal(pendingWorkBefore, optimizedPending.ActiveImageWorkCountForTests);
            Assert.Null(optimizedPending.Bitmap);
            Assert.False(snapshot.IsTopLevelBlockMeasured(optimizedFar));
            Assert.Null(optimizedFar.Bitmap);
            Assert.NotNull(optimizedTarget.Bitmap);
            Assert.NotNull(optimizedSecond.Bitmap);

            for (int ordinal = 0; ordinal < rootsBefore.Length; ordinal++)
            {
                Assert.Same(rootsBefore[ordinal], snapshot.Blocks[ordinal]);
                Assert.Equal(measuredBefore[ordinal], snapshot.IsTopLevelBlockMeasured(snapshot.Blocks[ordinal]));
                Assert.Equal(LayoutPlacementTestGeometry.ToLogical(fallback.Snapshot, fallback.Snapshot.Blocks[ordinal]),
                    LayoutPlacementTestGeometry.ToLogical(snapshot, snapshot.Blocks[ordinal]));
            }
            Assert.Equal(PaintViewport(snapshot, seed.Device, height: 600, dpi: 144),
                PaintViewport(fallback.Snapshot, seed.Device, height: 600, dpi: 144));
        }
        finally
        {
            if (constructionContext is not null)
                await constructionContext.DrainOwnedRetirementsAsync().WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    [Fact]
    public async Task RootRelayoutRetriesWhenSourceLeaseChangesAfterPrivateClone()
    {
        await using var seed = new CompletedRasterPlacementCloneTests.PlacementFixture();
        using var session = new MarkdownPerformanceSession(MarkdownPerformanceOptions.Progressive);
        var resolver = new ControlledRasterResolver(DisplaySizedRasterDecodeTests.CreatePng(96, 192));
        await using ImageDocument document = BuildDocument(seed, session, resolver,
            CreateDocumentSource(resolver, includePendingSibling: false),
            FlowDirection.LeftToRight, 1f, lazy: false);
        ImageBox source = document.TargetImage;
        await LoadTargetAsync(source);

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
                beforeMeasureOutsideGateForTests: () =>
                {
                    replacementBitmap = CreateBitmap(seed.Device, 24, 12);
                    SharedCanvasBitmapCache.Lease replacementLease =
                        SharedCanvasBitmapCache.CreateUncachedLease(replacementBitmap);
                    InvokeReplaceBitmap(source, replacementLease);
                },
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
            Assert.Equal(0, source.ActiveImageWorkCountForTests);
            for (int ordinal = 0; ordinal < rootsBefore.Length; ordinal++)
            {
                Assert.Same(rootsBefore[ordinal], snapshot.Blocks[ordinal]);
                Assert.Equal(boundsBefore[ordinal], snapshot.Blocks[ordinal].Bounds);
            }
        }
        finally
        {
            if (constructionContext is not null)
                await constructionContext.DrainOwnedRetirementsAsync().WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    [Fact]
    public async Task BatchRetriesWhenLastSourceLeaseChangesWithoutTransferringEarlierPlacement()
    {
        await using var seed = new CompletedRasterPlacementCloneTests.PlacementFixture();
        using var session = new MarkdownPerformanceSession(MarkdownPerformanceOptions.Progressive);
        var resolver = new ControlledRasterResolver(DisplaySizedRasterDecodeTests.CreatePng(96, 192));
        await using ImageDocument document = BuildDocument(seed, session, resolver,
            CreateBatchDocumentSource(resolver), FlowDirection.RightToLeft, 2f, lazy: false);
        ImageBox first = document.TargetImage;
        ImageBox second = FindSecondTarget(document);
        await LoadTargetAsync(first);
        await LoadTargetAsync(second);

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
                beforeMeasureOutsideGateForTests: () =>
                {
                    replacementBitmap = CreateBitmap(seed.Device, 24, 12);
                    InvokeReplaceBitmap(second,
                        SharedCanvasBitmapCache.CreateUncachedLease(replacementBitmap));
                },
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
            for (int ordinal = 0; ordinal < rootsBefore.Length; ordinal++)
            {
                Assert.Same(rootsBefore[ordinal], snapshot.Blocks[ordinal]);
                Assert.Equal(boundsBefore[ordinal], snapshot.Blocks[ordinal].Bounds);
            }
        }
        finally
        {
            if (constructionContext is not null)
                await constructionContext.DrainOwnedRetirementsAsync().WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    [Fact]
    public async Task DuplicateAndUnknownChangedRootIndicesAreUnavailableWithoutMutation()
    {
        await using var seed = new CompletedRasterPlacementCloneTests.PlacementFixture();
        using var session = new MarkdownPerformanceSession(MarkdownPerformanceOptions.Progressive);
        var resolver = new ControlledRasterResolver(DisplaySizedRasterDecodeTests.CreatePng(96, 192));
        await using ImageDocument document = BuildDocument(seed, session, resolver,
            CreateDocumentSource(resolver, includePendingSibling: false),
            FlowDirection.RightToLeft, 2f, lazy: false);
        await LoadTargetAsync(document.TargetImage);

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
                for (int ordinal = 0; ordinal < rootsBefore.Length; ordinal++)
                {
                    Assert.Same(rootsBefore[ordinal], snapshot.Blocks[ordinal]);
                    Assert.Equal(boundsBefore[ordinal], snapshot.Blocks[ordinal].Bounds);
                }
            }
            finally
            {
                if (constructionContext is not null)
                    await constructionContext.DrainOwnedRetirementsAsync().WaitAsync(TimeSpan.FromSeconds(10));
            }
        }
    }

    [Fact]
    public async Task MultipleChangedRootsRemainUnavailableAndDoNotTouchSource()
    {
        await using var seed = new CompletedRasterPlacementCloneTests.PlacementFixture();
        using var session = new MarkdownPerformanceSession(MarkdownPerformanceOptions.Progressive);
        var resolver = new ControlledRasterResolver(DisplaySizedRasterDecodeTests.CreatePng(96, 192));
        await using ImageDocument document = BuildDocument(seed, session, resolver,
            CreateDocumentSource(resolver, includePendingSibling: false), FlowDirection.LeftToRight, 1f,
            lazy: false);
        await LoadTargetAsync(document.TargetImage);

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
            for (int ordinal = 0; ordinal < rootsBefore.Length; ordinal++)
            {
                Assert.Same(rootsBefore[ordinal], snapshot.Blocks[ordinal]);
                Assert.Equal(boundsBefore[ordinal], snapshot.Blocks[ordinal].Bounds);
            }
        }
        finally
        {
            if (constructionContext is not null)
                await constructionContext.DrainOwnedRetirementsAsync().WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    [Fact]
    public async Task BatchPreflightRejectsDuplicateUnknownTooManyAndOverBudgetAltBeforeCloning()
    {
        await using var seed = new CompletedRasterPlacementCloneTests.PlacementFixture();
        using var session = new MarkdownPerformanceSession(MarkdownPerformanceOptions.Progressive);
        var resolver = new ControlledRasterResolver(DisplaySizedRasterDecodeTests.CreatePng(96, 192));
        await using ImageDocument document = BuildDocument(seed, session, resolver,
            CreateBatchDocumentSource(resolver), FlowDirection.LeftToRight, 1f, lazy: false);
        ImageBox first = document.TargetImage;
        ImageBox second = FindSecondTarget(document);
        await LoadTargetAsync(first);
        await LoadTargetAsync(second);

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
            MarkdownLayoutContext? rejectedContext = null;
            try
            {
                PreparedImageRelayoutResult result = snapshot.TryRelayoutCompletedImageRoots(
                    indices, LayoutWidth, CancellationToken.None, out rejectedContext,
                    expectedPointerInputEpoch: snapshot.PointerInputEpoch);
                Assert.Equal(PreparedImageRelayoutResult.Unavailable, result);
                Assert.Null(rejectedContext);
                Assert.Equal(firstReferences, GetEntryReferenceCount(firstLease));
                Assert.Equal(secondReferences, GetEntryReferenceCount(secondLease));
            }
            finally
            {
                if (rejectedContext is not null)
                    await rejectedContext.DrainOwnedRetirementsAsync().WaitAsync(TimeSpan.FromSeconds(10));
            }
        }

        const int firstAltLength = 32 * 1024;
        const int secondAltLength = 32 * 1024 + 1;
        string largeAltSource = CreateBatchDocumentSource(
            resolver, new string('a', firstAltLength), new string('b', secondAltLength));
        await using ImageDocument overBudget = BuildDocument(seed, session, resolver, largeAltSource,
            FlowDirection.LeftToRight, 1f, lazy: false);
        ImageBox overBudgetFirst = overBudget.TargetImage;
        ImageBox overBudgetSecond = FindSecondTarget(overBudget);
        Assert.Equal(firstAltLength, overBudgetFirst.Alt.Length);
        Assert.Equal(secondAltLength, overBudgetSecond.Alt.Length);
        Assert.True(overBudgetFirst.Alt.Length + overBudgetSecond.Alt.Length >
                    LayoutSnapshot.MaximumPrivateRasterPlacementTextLength);
        await LoadTargetAsync(overBudgetFirst);
        await LoadTargetAsync(overBudgetSecond);
        SharedCanvasBitmapCache.Lease overBudgetFirstLease = GetImageLease(overBudgetFirst);
        SharedCanvasBitmapCache.Lease overBudgetSecondLease = GetImageLease(overBudgetSecond);
        int overBudgetFirstReferences = GetEntryReferenceCount(overBudgetFirstLease);
        int overBudgetSecondReferences = GetEntryReferenceCount(overBudgetSecondLease);
        LayoutSnapshot overBudgetSnapshot = overBudget.Snapshot;
        BlockBox[] overBudgetRoots = overBudgetSnapshot.Blocks.ToArray();
        Rect[] overBudgetBounds = overBudgetRoots.Select(static block => block.Bounds).ToArray();
        long overBudgetRevision = overBudgetSnapshot.LayoutRevision;
        Size overBudgetSize = overBudgetSnapshot.Size;
        MarkdownLayoutContext? overBudgetContext = null;

        try
        {
            PreparedImageRelayoutResult overBudgetResult = overBudgetSnapshot.TryRelayoutCompletedImageRoots(
                [overBudgetFirst.BlockIndex, overBudgetSecond.BlockIndex], LayoutWidth, CancellationToken.None,
                out overBudgetContext,
                expectedPointerInputEpoch: overBudgetSnapshot.PointerInputEpoch);

            Assert.Equal(PreparedImageRelayoutResult.Unavailable, overBudgetResult);
            Assert.Null(overBudgetContext);
            Assert.Equal(overBudgetRevision, overBudgetSnapshot.LayoutRevision);
            Assert.Equal(overBudgetSize, overBudgetSnapshot.Size);
            for (int ordinal = 0; ordinal < overBudgetRoots.Length; ordinal++)
            {
                Assert.Same(overBudgetRoots[ordinal], overBudgetSnapshot.Blocks[ordinal]);
                Assert.Equal(overBudgetBounds[ordinal], overBudgetSnapshot.Blocks[ordinal].Bounds);
            }
            Assert.Equal(overBudgetFirstReferences, GetEntryReferenceCount(overBudgetFirstLease));
            Assert.Equal(overBudgetSecondReferences, GetEntryReferenceCount(overBudgetSecondLease));
        }
        finally
        {
            if (overBudgetContext is not null)
                await overBudgetContext.DrainOwnedRetirementsAsync().WaitAsync(TimeSpan.FromSeconds(10));
        }

        Assert.Equal(revisionBefore, snapshot.LayoutRevision);
        Assert.Equal(sizeBefore, snapshot.Size);
        Assert.Same(semanticBefore, snapshot.SemanticDocument);
        for (int ordinal = 0; ordinal < rootsBefore.Length; ordinal++)
        {
            Assert.Same(rootsBefore[ordinal], snapshot.Blocks[ordinal]);
            Assert.Equal(boundsBefore[ordinal], snapshot.Blocks[ordinal].Bounds);
        }
    }

    private static string CreateDocumentSource(ControlledRasterResolver resolver, bool includePendingSibling) =>
        "# Image relayout\n\n" +
        $"![target image]({resolver.TargetUrl})\n\n" +
        "This paragraph has text, a [link](https://example.test/path), and enough words to paint after the image.\n\n" +
        (includePendingSibling ? $"![pending sibling]({resolver.PendingUrl})\n" : string.Empty);

    private static string CreateBatchDocumentSource(
        ControlledRasterResolver resolver,
        string firstAlt = "first completed image",
        string secondAlt = "second completed image") =>
        "# Batch image relayout\n\n" +
        $"![{firstAlt}]({resolver.TargetUrl})\n\n" +
        "A stable paragraph separates the two standalone raster roots.\n\n" +
        $"![{secondAlt}]({resolver.SecondTargetUrl})\n\n" +
        "A trailing paragraph keeps suffix arrangement observable.\n";

    private static string CreateLongLazySource(ControlledRasterResolver resolver) =>
        $"![target image]({resolver.TargetUrl})\n\n" +
        string.Join("\n\n", Enumerable.Range(0, 80).Select(static index =>
            $"Lazy paragraph {index}: preserve the estimated tail and do not realize distant roots.")) +
        $"\n\n![far image]({resolver.FarUrl})\n";

    private static string CreateLongLazySourceWithPendingSibling(ControlledRasterResolver resolver) =>
        $"![target image]({resolver.TargetUrl})\n\n" +
        $"![second target image]({resolver.SecondTargetUrl})\n\n" +
        $"![pending sibling]({resolver.PendingUrl})\n\n" +
        string.Join("\n\n", Enumerable.Range(0, 80).Select(static index =>
            $"Lazy paragraph {index}: keep this distant root estimated during target completion.")) +
        $"\n\n![far image]({resolver.FarUrl})\n";

    private static ImageDocument BuildDocument(
        CompletedRasterPlacementCloneTests.PlacementFixture seed,
        MarkdownPerformanceSession session,
        ControlledRasterResolver resolver,
        string sourceText,
        FlowDirection direction,
        float rasterizationScale,
        bool lazy,
        float lazyViewportHeight = 120)
    {
        var registry = new MarkdownExtensionRegistry().ConfigureGitHubReadmeRegistry();
        Markdig.Syntax.MarkdownDocument parsed = Markdown.Parse(sourceText, registry.BuildPipeline());
        MarkdownDocument semanticDocument = MarkdownDocument.FromParsed(sourceText, parsed);
        var sourceMap = new MarkdownSourceMap(sourceText);
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
            var builder = new LayoutBuilder(context, semanticDocument: semanticDocument);
            snapshot = lazy
                ? builder.BuildLazy(parsed, LayoutWidth, viewportTop: 0, viewportHeight: lazyViewportHeight,
                    overscan: 0, CancellationToken.None)
                : builder.Build(parsed, LayoutWidth, CancellationToken.None);
            snapshot.AssignAutomationDocumentGeneration(AutomationGeneration);
            ImageBox target = Assert.Single(snapshot.Blocks.OfType<ImageBox>(),
                image => image.Url == resolver.TargetUrl);
            ImageBox? pending = snapshot.Blocks.OfType<ImageBox>()
                .SingleOrDefault(image => image.Url == resolver.PendingUrl);
            ImageBox? far = snapshot.Blocks.OfType<ImageBox>()
                .SingleOrDefault(image => image.Url == resolver.FarUrl);
            var result = new ImageDocument(context, snapshot, sourceMap, resolver, target, pending, far);
            snapshot = null;
            return result;
        }
        catch
        {
            try
            {
                if (snapshot is not null)
                    snapshot.Retire().GetAwaiter().GetResult();
            }
            finally
            {
                context.DrainOwnedRetirementsAsync().GetAwaiter().GetResult();
            }
            throw;
        }
    }

    private static ImageBox FindSecondTarget(ImageDocument document) =>
        Assert.Single(document.Snapshot.Blocks.OfType<ImageBox>(),
            image => image.Url == document.Resolver.SecondTargetUrl);

    private static async Task LoadTargetAsync(ImageBox image)
    {
        await DisplaySizedRasterDecodeTests.WaitForLoadAsync(image).WaitAsync(TimeSpan.FromSeconds(10));
        await DisplaySizedRasterDecodeTests.WaitUntilAsync(
            () => image.ActiveImageWorkCountForTests == 0);
        Assert.NotNull(image.Bitmap);
    }

    private static async Task<long> AssertCompletedPlacementTransferPredicatesAsync(
        ImageBox source,
        float availableWidth)
    {
        MarkdownLayoutContext context = source.Context.CreateRasterPlacementContext(
            source.Context.ImageCancellationToken,
            source.Context.ImagePreparationOwner!,
            retirementSink: null,
            imageUnavailable: null);
        ImageBox? prepared = null;
        try
        {
            Assert.True(source.TryCreateCompletedRasterPlacementClone(
                context,
                out prepared,
                out SharedCanvasBitmapCache.Lease? expectedLease,
                out ImageBox.CompletedSvgPlacementMetadataFence? svgFence));
            Assert.NotNull(prepared);
            Assert.NotNull(expectedLease);
            _ = prepared.Measure(availableWidth);
            prepared.Arrange(0, 0, availableWidth);

            string waiterState = DescribeBitmapOutputWaiterState(source);
            bool sourcePristineWaiterState =
                InvokePrivateBool(source, "HasNoBitmapOutputPlacementWaiterState");
            bool sourceHasNoLiveWaiterWork =
                InvokePrivateBool(source, "HasNoLiveBitmapOutputPlacementWork");
            bool preparedWaiterStateIsClear =
                InvokePrivateBool(prepared, "HasNoLiveBitmapOutputPlacementWork");
            bool sourcePlacementIsCoherent =
                InvokePrivateBool(source, "HasCoherentMeasuredPlacementNoLock", availableWidth, false);
            bool preparedPlacementIsCoherent =
                InvokePrivateBool(prepared, "HasCoherentMeasuredPlacementNoLock", availableWidth, false);
            bool inputsMatch = InvokePrivateBool(source, "HasMatchingPlacementInputs", prepared);
            bool transferEligible = InvokePrivateBool(source, "IsTransferSourceEligibleNoLock",
                prepared, expectedLease, svgFence, 0L, availableWidth, false);
            long sourceWaiterEpoch = ReadPrivateLongField(source, "_bitmapOutputWaiterEpoch");

            Assert.True(sourceWaiterEpoch > 0,
                $"The completed source did not exercise historical admission state: {waiterState}");
            Assert.False(sourcePristineWaiterState,
                "The old virgin-image predicate unexpectedly classified a completed source as pristine.");
            Assert.True(sourceHasNoLiveWaiterWork,
                $"Completed source still has live bitmap-admission work: {waiterState}");
            Assert.True(preparedWaiterStateIsClear,
                "Fresh placement unexpectedly owns live bitmap-admission work.");
            Assert.True(sourcePlacementIsCoherent,
                "The source placement is not internally coherent before candidate transfer.");
            Assert.True(preparedPlacementIsCoherent,
                "The privately measured placement is not internally coherent.");
            Assert.True(inputsMatch,
                "The source and private candidate have different placement inputs.");
            Assert.True(transferEligible,
                "The transfer eligibility predicate rejected an otherwise prepared raster candidate.");
            return sourceWaiterEpoch;
        }
        finally
        {
            if (prepared is not null)
            {
                context.DiscardConstructionBlock(prepared);
                await prepared.RetirementTask.WaitAsync(TimeSpan.FromSeconds(10));
            }
            await context.DrainOwnedRetirementsAsync().WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    private static bool InvokePrivateBool(ImageBox image, string methodName, params object?[] arguments)
    {
        MethodInfo method = typeof(ImageBox).GetMethod(
            methodName,
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"Missing private placement predicate {methodName}.");
        return Assert.IsType<bool>(method.Invoke(image, arguments));
    }

    private static CanvasBitmap CreateBitmap(CanvasDevice device, int width, int height)
    {
        byte[] pixels = new byte[checked(width * height * 4)];
        return CanvasBitmap.CreateFromBytes(
            device,
            pixels,
            width,
            height,
            DirectXPixelFormat.B8G8R8A8UIntNormalized);
    }

    private static void InvokeReplaceBitmap(ImageBox image, SharedCanvasBitmapCache.Lease lease)
    {
        MethodInfo method = typeof(ImageBox).GetMethod(
            "ReplaceBitmap",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Missing private bitmap replacement method.");
        // Reflection does not apply optional defaults; supply the optional
        // SVG viewport explicitly so the source-lease retry tests continue to
        // exercise the current replacement contract.
        method.Invoke(image, [lease.Bitmap, lease, null]);
    }

    private static long ReadPrivateLongField(ImageBox image, string fieldName)
    {
        FieldInfo field = typeof(ImageBox).GetField(fieldName,
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"Missing private field {fieldName}.");
        return Assert.IsType<long>(field.GetValue(image));
    }

    private static string DescribeBitmapOutputWaiterState(ImageBox image)
    {
        string[] fields =
        [
            "_bitmapOutputWaiterEpoch",
            "_bitmapOutputWaiterOwner",
            "_bitmapOutputWaiter",
            "_bitmapOutputDeferred",
            "_bitmapOutputRetryOfferPending",
            "_bitmapOutputRetryInProgress",
            "_bitmapOutputRetryQueued",
            "_bitmapOutputRegistering",
            "_bitmapOutputRegistrationNeeded",
            "_bitmapOutputRegistrationFailed",
            "_bitmapOutputCancellationRegistered",
        ];
        return string.Join(", ", fields.Select(name =>
        {
            FieldInfo field = typeof(ImageBox).GetField(name,
                BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException($"Missing bitmap-output field {name}.");
            object? value = field.GetValue(image);
            return $"{name}={(value is null ? "null" : Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture))}";
        }));
    }

    private static async Task StartPendingSiblingAsync(ImageDocument document)
    {
        if (document.PendingImage is null)
            return;
        document.PendingImage.EnsureLoading();
        await document.Resolver.WaitForPendingResolveAsync().WaitAsync(TimeSpan.FromSeconds(10));
    }

    private static async Task WaitForPendingWorkAndResolverStabilityAsync(
        ImageDocument first,
        ImageDocument second)
    {
        await DisplaySizedRasterDecodeTests.WaitUntilAsync(
            () => first.PendingImage is { ActiveImageWorkCountForTests: > 0 } &&
                  second.PendingImage is { ActiveImageWorkCountForTests: > 0 } &&
                  first.Resolver.ResolveCountFor(first.Resolver.PendingUrl) > 0);

        int previousCount = first.Resolver.ResolveCountFor(first.Resolver.PendingUrl);
        int stableSamples = 0;
        for (int attempt = 0; attempt < 400; attempt++)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(25));
            int currentCount = first.Resolver.ResolveCountFor(first.Resolver.PendingUrl);
            bool bothWorkItemsRemainActive =
                first.PendingImage is { ActiveImageWorkCountForTests: > 0 } &&
                second.PendingImage is { ActiveImageWorkCountForTests: > 0 };
            stableSamples = bothWorkItemsRemainActive && currentCount == previousCount
                ? stableSamples + 1
                : 0;
            previousCount = currentCount;
            if (stableSamples >= 5)
                return;
        }

        throw new TimeoutException("Pending image resolution did not reach a stable, active state.");
    }

    private static Color[] Paint(LayoutSnapshot snapshot, CanvasDevice device, float rasterizationScale)
    {
        float height = (float)System.Math.Max(1, System.Math.Ceiling(snapshot.Size.Height + 1));
        using var target = new CanvasRenderTarget(device, LayoutWidth, height, 96 * rasterizationScale);
        using (CanvasDrawingSession drawing = target.CreateDrawingSession())
        {
            drawing.Clear(Color.FromArgb(0, 0, 0, 0));
            snapshot.Paint(drawing, new Rect(0, 0, LayoutWidth, height));
        }
        return target.GetPixelColors();
    }

    private static Color[] PaintViewport(LayoutSnapshot snapshot, CanvasDevice device, float height, float dpi)
    {
        using var target = new CanvasRenderTarget(device, LayoutWidth, height, dpi);
        using (CanvasDrawingSession drawing = target.CreateDrawingSession())
        {
            drawing.Clear(Color.FromArgb(0, 0, 0, 0));
            snapshot.Paint(drawing, new Rect(0, 0, LayoutWidth, height));
        }
        return target.GetPixelColors();
    }

    private static void AssertViewportIndexMatchesFullRebuild(LayoutSnapshot snapshot)
    {
        LayoutPlacementGeneration actual = snapshot.PublishedPlacementGeneration;
        var expected = new ViewportBandIndex(snapshot.Blocks.Count);
        var queryBands = new List<(double Top, double Bottom)>(snapshot.Blocks.Count * 4 + 1);

        for (int ordinal = 0; ordinal < snapshot.Blocks.Count; ordinal++)
        {
            Rect bounds = actual.GetPlacement(ordinal).LogicalBounds;
            expected.SetEntry(ordinal, ordinal, bounds.Top, bounds.Bottom);
            queryBands.Add((bounds.Top, bounds.Top));
            queryBands.Add((bounds.Bottom, bounds.Bottom));
            queryBands.Add((bounds.Top, bounds.Bottom));
            if (ordinal + 1 < snapshot.Blocks.Count)
                queryBands.Add((bounds.Bottom, actual.GetPlacement(ordinal + 1).Top));
        }

        expected.Commit();
        queryBands.Add((0, snapshot.Size.Height));
        Assert.Equal(expected.Count, actual.Count);
        Assert.Equal(expected.IsDocumentOrder, actual.IsDocumentOrder);
        for (int entry = 0; entry < expected.Count; entry++)
        {
            Assert.Equal(expected.GetBlockOrdinal(entry), actual.GetBlockOrdinal(entry));
            Assert.Equal(expected.GetBounds(entry), actual.GetIndexedBounds(entry));
        }

        foreach ((double top, double bottom) in queryBands)
        {
            ViewportRange expectedRange = expected.Find(top, bottom);
            ViewportRange actualRange = actual.Find(top, bottom);
            Assert.Equal(expectedRange, actualRange);
            for (int entry = expectedRange.Start; entry < expectedRange.End; entry++)
                Assert.Equal(expected.GetBlockOrdinal(entry), actual.GetBlockOrdinal(entry));
        }
    }

    private static SharedCanvasBitmapCache.Lease GetImageLease(ImageBox image)
    {
        FieldInfo field = typeof(ImageBox).GetField("_bitmapLease",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Missing ImageBox bitmap lease field.");
        return Assert.IsType<SharedCanvasBitmapCache.Lease>(field.GetValue(image));
    }

    private static int GetEntryReferenceCount(SharedCanvasBitmapCache.Lease lease)
    {
        FieldInfo entryField = typeof(SharedCanvasBitmapCache.Lease).GetField(
            "_entry", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Missing shared bitmap entry field.");
        object entry = Assert.IsAssignableFrom<SharedCanvasBitmapCache.Entry>(entryField.GetValue(lease));
        FieldInfo countField = typeof(SharedCanvasBitmapCache.Entry).GetField(
            "_referenceCount", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Missing shared bitmap reference count.");
        return Assert.IsType<int>(countField.GetValue(entry));
    }

    private static (MarkdownSemanticRole Role, int Start, int End, int HeadingLevel,
        MarkdownAccessibilityRole AccessibilityRole, string? Name, string? Description,
        string? AutomationId, Rect Bounds)[] GetSemanticProjection(LayoutSnapshot snapshot)
    {
        MarkdownSemanticDocument document = snapshot.SemanticDocument;
        var result = new List<(MarkdownSemanticRole, int, int, int, MarkdownAccessibilityRole,
            string?, string?, string?, Rect)>();
        var pending = new Stack<MarkdownSemanticNode>();
        pending.Push(document.Root);
        Assert.True(snapshot.TryBeginInteraction());
        try
        {
            while (pending.Count > 0)
            {
                MarkdownSemanticNode node = pending.Pop();
                using LayoutPlacementTransformContext.Scope placementScope =
                    snapshot.EnterPlacementTransformScopeForBlockIndexUnderPaint(node.LayoutBlockIndex);
                Rect physicalBounds = node.VectorSceneBox is { } vector
                    ? node.VectorSemanticIndex >= 0
                        ? vector.GetVisibleSemanticBounds(node.VectorSemanticIndex)
                        : vector.Bounds
                    : node.Bounds;
                Rect logicalBounds = snapshot.ToLogicalRectForBlockIndexUnderPaint(
                    node.LayoutBlockIndex,
                    physicalBounds);
                result.Add((node.Role, node.TextStart, node.TextEnd, node.HeadingLevel,
                    node.AccessibilityRole, node.AccessibilityName, node.AccessibilityDescription,
                    node.AutomationId, logicalBounds));
                for (int child = node.Children.Count - 1; child >= 0; child--)
                    pending.Push(node.Children[child]);
            }
        }
        finally
        {
            snapshot.EndInteraction();
        }
        return result.ToArray();
    }

    private sealed class ImageDocument : IAsyncDisposable
    {
        internal ImageDocument(
            MarkdownLayoutContext context,
            LayoutSnapshot snapshot,
            MarkdownSourceMap sourceMap,
            ControlledRasterResolver resolver,
            ImageBox targetImage,
            ImageBox? pendingImage,
            ImageBox? farImage)
        {
            Context = context;
            Snapshot = snapshot;
            SourceMap = sourceMap;
            Resolver = resolver;
            TargetImage = targetImage;
            PendingImage = pendingImage;
            FarImage = farImage;
        }

        internal MarkdownLayoutContext Context { get; }
        internal LayoutSnapshot Snapshot { get; }
        internal MarkdownSourceMap SourceMap { get; }
        internal ControlledRasterResolver Resolver { get; }
        internal ImageBox TargetImage { get; }
        internal ImageBox? PendingImage { get; }
        internal ImageBox? FarImage { get; }

        public async ValueTask DisposeAsync()
        {
            try
            {
                await Snapshot.Retire().WaitAsync(TimeSpan.FromSeconds(10));
            }
            finally
            {
                await Context.DrainOwnedRetirementsAsync().WaitAsync(TimeSpan.FromSeconds(10));
            }
        }
    }

    private sealed class ContextCapture
    {
        internal MarkdownLayoutContext? Value;
    }

    private sealed class ControlledRasterResolver : IMarkdownImageResolver
    {
        private readonly byte[] _png;
        private readonly ConcurrentDictionary<string, int> _resolveCounts = new(StringComparer.Ordinal);
        private readonly TaskCompletionSource _pendingResolve =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly string _cachePrefix = "root-local-relayout:" + Guid.NewGuid().ToString("N") + ":";

        internal ControlledRasterResolver(byte[] png)
        {
            _png = png;
            string suffix = Guid.NewGuid().ToString("N");
            TargetUrl = $"https://images.example.test/target-{suffix}.png";
            SecondTargetUrl = $"https://images.example.test/second-target-{suffix}.png";
            PendingUrl = $"https://images.example.test/pending-{suffix}.png";
            FarUrl = $"https://images.example.test/far-{suffix}.png";
        }

        internal string TargetUrl { get; }
        internal string SecondTargetUrl { get; }
        internal string PendingUrl { get; }
        internal string FarUrl { get; }
        internal int ResolveCountFor(string source) =>
            _resolveCounts.TryGetValue(source, out int count) ? count : 0;

        public async ValueTask<MarkdownImageResolution> ResolveAsync(
            string source,
            MarkdownImageResolveContext context,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _resolveCounts.AddOrUpdate(source, 1, static (_, count) => checked(count + 1));
            if (string.Equals(source, PendingUrl, StringComparison.Ordinal))
            {
                _pendingResolve.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
                throw new OperationCanceledException(cancellationToken);
            }

            if (!string.Equals(source, TargetUrl, StringComparison.Ordinal) &&
                !string.Equals(source, SecondTargetUrl, StringComparison.Ordinal) &&
                !string.Equals(source, FarUrl, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("The test resolver received an unexpected image source.");
            }

            return MarkdownImageResolution.Resolved(new MarkdownImageAsset(
                _png,
                "image/png",
                CacheKey: _cachePrefix + source));
        }

        internal Task WaitForPendingResolveAsync() => _pendingResolve.Task;
    }
}
