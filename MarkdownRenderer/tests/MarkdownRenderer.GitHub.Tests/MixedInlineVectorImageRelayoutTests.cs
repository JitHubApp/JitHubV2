using MarkdownRenderer.Extensions;
using MarkdownRenderer.Layout;
using MarkdownRenderer.Layout.Boxes;
using MarkdownRenderer.Performance;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.UI.Xaml;
using Windows.Foundation;
using Xunit;
using static MarkdownRenderer.GitHub.Tests.ImageRelayoutTestSupport;
using static MarkdownRenderer.GitHub.Tests.MixedInlineVectorImageRelayoutFixture;

namespace MarkdownRenderer.GitHub.Tests;

/// <summary>
/// Inline roots that mix completed image runs with materialized math vector
/// runs relayout through the completed-image path, transferring only vector
/// metrics, and match a protected full-reflow fallback.
/// </summary>
/// <remarks>
/// This file is longer than 500 lines because it holds the six scenarios of
/// the mixed vector and image recipe (commit across scale and direction,
/// progressive geometry transfer, stationary index refresh, late vector
/// mutation, cancellation and preflight rejection), each of which arranges its
/// own document and states its own oracle. Documents and shared oracles live
/// in <see cref="MixedInlineVectorImageDocument"/> and
/// <see cref="MixedInlineVectorImageRelayoutFixture"/>.
/// </remarks>
[Collection(SharedCanvasBitmapCacheTestCollection.Name)]
public sealed class MixedInlineVectorImageRelayoutTests
{
    private const float LayoutWidth = MixedInlineVectorImageDocument.LayoutWidth;

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
    public async Task CompletedImageRootTransfersOnlyVectorMetricsAndMatchesProtectedReflow(
        float rasterizationScale,
        bool rightToLeft,
        bool forceSoftwareRenderer)
    {
        await using var seed = new CompletedRasterPlacementCloneTests.PlacementFixture(
            forceSoftwareRenderer: forceSoftwareRenderer);
        Assert.Equal(forceSoftwareRenderer, seed.ForceSoftwareRenderer);
        using var session = new MarkdownPerformanceSession(MarkdownPerformanceOptions.Progressive);
        string source = "before **bold** ![status badge](";

        var optimizedResolver = new InlineVectorRasterResolver(DisplaySizedRasterDecodeTests.CreatePng(96, 192));
        var fallbackResolver = new InlineVectorRasterResolver(DisplaySizedRasterDecodeTests.CreatePng(96, 192));
        string optimizedSource = source + optimizedResolver.Url + ") and $x^2$ after the result.";
        string fallbackSource = source + fallbackResolver.Url + ") and $x^2$ after the result.";
        FlowDirection direction = rightToLeft ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;

        await using MixedInlineVectorImageDocument optimized = await BuildDocumentAsync(
            seed, session, optimizedResolver, optimizedSource, direction, rasterizationScale);
        await using MixedInlineVectorImageDocument fallback = await BuildDocumentAsync(
            seed, session, fallbackResolver, fallbackSource, direction, rasterizationScale);

        InlineContainerBox optimizedInline = Assert.Single(optimized.Snapshot.Blocks.OfType<InlineContainerBox>());
        InlineContainerBox fallbackInline = Assert.Single(fallback.Snapshot.Blocks.OfType<InlineContainerBox>());
        InlineVectorSceneRun sourceVector = Assert.Single(optimizedInline.Runs.OfType<InlineVectorSceneRun>());
        InlineVectorSceneRun fallbackVector = Assert.Single(fallbackInline.Runs.OfType<InlineVectorSceneRun>());
        InlineImageRun sourceImageRun = Assert.Single(optimizedInline.Runs.OfType<InlineImageRun>());
        InlineImageRun fallbackImageRun = Assert.Single(fallbackInline.Runs.OfType<InlineImageRun>());
        ImageBox sourceImage = sourceImageRun.Image;
        ImageBox fallbackImage = fallbackImageRun.Image;
        await LoadImageAsync(sourceImage);
        await LoadImageAsync(fallbackImage);

        Assert.True(sourceVector.IsNativeResourcesPrepared);
        Assert.True(fallbackVector.IsNativeResourcesPrepared);
        Assert.Null(sourceVector.DeferredSceneMarker);
        MarkdownVectorScene sourceScene = Assert.IsType<MarkdownVectorScene>(sourceVector.Scene);
        VectorSceneDrawing sourceDrawing = GetVectorDrawing(sourceVector);
        Assert.False(sourceVector.IsMeasuredPlacementOnly);
        AssertOnlyRootLocalRecipeUsesMeasuredOnlyVectorClone(optimized, optimizedInline, sourceVector);
        Assert.True(sourceVector.IsNativeResourcesPrepared);
        Assert.Same(sourceDrawing, GetVectorDrawing(sourceVector));

        InlineRootBefore before = InlineRootBefore.Capture(
            optimized.Snapshot, optimizedInline, sourceVector, sourceImage, optimizedResolver);

        // Keep the old full root reflow as the compatibility oracle. The new
        // path must not get its result from a second parser/image-provider pass.
        fallback.Snapshot.RelayoutChangedBlocks(
            [fallbackInline.BlockIndex], LayoutWidth, CancellationToken.None);

        MarkdownLayoutContext? constructionContext = null;
        try
        {
            PreparedImageRelayoutResult result = optimized.Snapshot.TryRelayoutCompletedImageRoot(
                [optimizedInline.BlockIndex], LayoutWidth, CancellationToken.None, out constructionContext,
                expectedPointerInputEpoch: optimized.Snapshot.PointerInputEpoch);

            Assert.Equal(PreparedImageRelayoutResult.Committed, result);
            Assert.NotNull(constructionContext);
            before.AssertPublishedOverSameDocument(optimized.Snapshot, fallback.Snapshot, optimizedResolver);

            Assert.Same(optimizedInline, optimized.Snapshot.Blocks.Single(
                block => ReferenceEquals(block, optimizedInline)));
            before.AssertRootsMovedToFallbackPlacement(optimized.Snapshot, fallback.Snapshot);
            before.AssertRunsAndImageRetainedWithFallbackPlacement(
                optimized.Snapshot, optimizedInline, sourceVector, sourceImageRun,
                fallback.Snapshot, fallbackImageRun, fallbackImage);
            AssertVectorRetainedWithFallbackMetrics(
                optimized.Snapshot, optimizedInline, sourceVector, sourceScene, sourceDrawing,
                fallback.Snapshot, fallbackInline, before);
            AssertSemanticSpansAndPixelsMatchFallback(
                optimized.Snapshot, optimizedInline, fallback.Snapshot, fallbackInline, seed.Device, rasterizationScale);
        }
        finally
        {
            await DrainAsync(constructionContext);
        }
    }

    [Fact]
    public async Task ProgressiveMaterializedMathTransfersCurrentAndReleasedSceneGeometryToSourceRun()
    {
        await using var seed = new CompletedRasterPlacementCloneTests.PlacementFixture();
        using var session = new MarkdownPerformanceSession(MarkdownPerformanceOptions.Progressive);
        var optimizedResolver = new InlineVectorRasterResolver(DisplaySizedRasterDecodeTests.CreatePng(80, 160));
        var fallbackResolver = new InlineVectorRasterResolver(DisplaySizedRasterDecodeTests.CreatePng(80, 160));
        string optimizedSource = $"before ![badge]({optimizedResolver.Url}) and $x^2$ after";
        string fallbackSource = $"before ![badge]({fallbackResolver.Url}) and $x^2$ after";
        await using MixedInlineVectorImageDocument optimized = await BuildProgressiveLazyDocumentAsync(
            seed, session, optimizedResolver, optimizedSource, FlowDirection.LeftToRight, 1.5f);
        await using MixedInlineVectorImageDocument fallback = await BuildProgressiveLazyDocumentAsync(
            seed, session, fallbackResolver, fallbackSource, FlowDirection.LeftToRight, 1.5f);

        InlineContainerBox optimizedInline = Assert.Single(optimized.Snapshot.Blocks.OfType<InlineContainerBox>());
        InlineContainerBox fallbackInline = Assert.Single(fallback.Snapshot.Blocks.OfType<InlineContainerBox>());
        InlineVectorSceneRun vector = Assert.Single(optimizedInline.Runs.OfType<InlineVectorSceneRun>());
        InlineVectorSceneRun fallbackVector = Assert.Single(fallbackInline.Runs.OfType<InlineVectorSceneRun>());
        InlineImageRun imageRun = Assert.Single(optimizedInline.Runs.OfType<InlineImageRun>());
        await LoadImageAsync(imageRun.Image);
        await LoadImageAsync(Assert.Single(fallbackInline.Runs.OfType<InlineImageRun>()).Image);

        Assert.Null(vector.DeferredSceneMarker);
        MarkdownDeferredSceneBinding binding = Assert.IsType<MarkdownDeferredSceneBinding>(vector.DeferredSceneBinding);
        Assert.True(vector.IsNativeResourcesPrepared);
        VectorSceneDrawing drawing = GetVectorDrawing(vector);

        // Lazy realization may already have released the optional pending
        // array after building the active index. Capture from the authoritative
        // live layout, then explicitly exercise the released-cache path; if the
        // pending cache still exists, its run identity is checked too. Reflow
        // must recapture candidate geometry and rebind every geometry record to
        // the retained source run.
        long generation = CaptureThenReleasePendingSceneGeometry(optimizedInline, vector);
        _ = CaptureThenReleasePendingSceneGeometry(fallbackInline, fallbackVector);

        fallback.Snapshot.RelayoutChangedBlocks(
            [fallbackInline.BlockIndex], LayoutWidth, CancellationToken.None);
        MarkdownLayoutContext? constructionContext = null;
        try
        {
            PreparedImageRelayoutResult result = optimized.Snapshot.TryRelayoutCompletedImageRoot(
                [optimizedInline.BlockIndex], LayoutWidth, CancellationToken.None,
                out constructionContext,
                expectedPointerInputEpoch: optimized.Snapshot.PointerInputEpoch);
            Assert.Equal(PreparedImageRelayoutResult.Committed, result);
            Assert.Same(vector, Assert.Single(optimizedInline.Runs.OfType<InlineVectorSceneRun>()));
            Assert.Same(drawing, GetVectorDrawing(vector));
            Assert.True(vector.IsNativeResourcesPrepared);
            Assert.False(vector.IsMeasuredPlacementOnly);
            Assert.Equal(binding, vector.DeferredSceneBinding);
            Assert.Null(vector.DeferredSceneMarker);
            Assert.True(optimizedInline.TryGetCurrentSceneGeometry(
                out long nextGeneration, out InlineSceneGeometry[] optimizedGeometry));
            Assert.True(nextGeneration > generation);
            AssertGeometryBoundToSource(optimizedGeometry, vector);

            Assert.True(fallbackInline.TryGetCurrentSceneGeometry(
                out _, out InlineSceneGeometry[] fallbackGeometry));
            AssertGeometryBoundToSource(fallbackGeometry, fallbackVector);
            AssertSceneGeometryEqual(CaptureSceneGeometry(fallbackGeometry), CaptureSceneGeometry(optimizedGeometry));
            Assert.Equal(
                LayoutPlacementTestGeometry.ToLogical(fallback.Snapshot, fallback.Snapshot.Blocks),
                LayoutPlacementTestGeometry.ToLogical(optimized.Snapshot, optimized.Snapshot.Blocks));
            Assert.Equal(Paint(optimized.Snapshot, seed.Device, 1.5f),
                Paint(fallback.Snapshot, seed.Device, 1.5f));
            AssertSemanticSpanGeometryEqual(
                CaptureSemanticSpanGeometry(optimized.Snapshot, optimizedInline),
                CaptureSemanticSpanGeometry(fallback.Snapshot, fallbackInline));
        }
        finally
        {
            await DrainAsync(constructionContext);
        }
    }

    [Fact]
    public async Task ProgressiveImageCommitRefreshesStationaryInlineIndexAndKeepsDeferredNeighborDiscoverable()
    {
        await using var seed = new CompletedRasterPlacementCloneTests.PlacementFixture();
        using var session = new MarkdownPerformanceSession(MarkdownPerformanceOptions.Progressive);
        var resolver = new InlineVectorRasterResolver(DisplaySizedRasterDecodeTests.CreatePng(80, 160));
        string source = $"before ![badge]({resolver.Url}) with $x^2$ and $y^2$ after\n\n$$\n\\frac{{1}}{{2}}\n$$";
        await using MixedInlineVectorImageDocument document = await BuildProgressiveLazyDocumentAsync(
            seed, session, resolver, source, FlowDirection.LeftToRight, 1f,
            materializeAllInlineMarkers: true);

        InlineContainerBox inline = Assert.Single(document.Snapshot.Blocks.OfType<InlineContainerBox>());
        InlineVectorSceneRun[] vectors = inline.Runs.OfType<InlineVectorSceneRun>().ToArray();
        Assert.Equal(2, vectors.Length);
        Assert.All(vectors, static run => Assert.Null(run.DeferredSceneMarker));
        // Unmaterialized block math is an accessible code placeholder, not a
        // vector drawing. Its stable key is the registered deferred marker.
        CodeBlockBox deferredNeighbor = Assert.Single(document.Snapshot.Blocks.OfType<CodeBlockBox>());
        string deferredMarker = deferredNeighbor.StableKey;
        Rect deferredNeighborPhysicalBounds = deferredNeighbor.Bounds;
        MarkdownProgressiveScenePlan plan = GetRegisteredPlan(session, document.PerformanceOwner!);
        Assert.Contains(deferredMarker, plan.GetRegisteredMarkerSnapshot());
        Assert.False(plan.TryGetResult(deferredMarker, out _));

        InlineImageRun imageRun = Assert.Single(inline.Runs.OfType<InlineImageRun>());
        await LoadImageAsync(imageRun.Image);
        long oldGeneration = inline.SceneGeometryGeneration;
        InlineRun[] originalRuns = inline.Runs.ToArray();
        CanvasTextLayout oldLayout = GetInlineLayout(inline);

        MarkdownLayoutContext? constructionContext = null;
        try
        {
            PreparedImageRelayoutResult result = document.Snapshot.TryRelayoutCompletedImageRoot(
                [inline.BlockIndex], LayoutWidth, CancellationToken.None, out constructionContext,
                expectedPointerInputEpoch: document.Snapshot.PointerInputEpoch);
            Assert.Equal(PreparedImageRelayoutResult.Committed, result);
            Assert.True(inline.SceneGeometryGeneration > oldGeneration);
            Assert.NotSame(oldLayout, GetInlineLayout(inline));
            AssertRunsRetained(inline, originalRuns);
            foreach (InlineVectorSceneRun vector in vectors)
                Assert.Same(vector, inline.Runs.Single(run => ReferenceEquals(run, vector)));
            AssertPendingGeometryCoversVectors(inline, vectors);

            LazyLayoutBand stationaryBand = LazyLayoutBand.FromViewport(0, 1024, 0);
            document.Snapshot.EnsureMeasuredBand(stationaryBand, CancellationToken.None);
            Assert.True(document.Snapshot.IsBandWorkSettled(stationaryBand));
            MarkdownVectorSceneResidencyPolicy policy =
                AssertNoVisibleInlineSceneMarkers(document.Snapshot, inline, stationaryBand);
            ProbeVisibleDeferredMarkersUnderPaint(document.Snapshot, policy, 0, 1024, visibleMarkers =>
            {
                Assert.Contains(deferredMarker, visibleMarkers);
                Assert.Equal(1, plan.UpdateVisibleMarkers(visibleMarkers));
            });

            Assert.True(document.Snapshot.TryGetLogicalTopForBlockIndex(
                deferredNeighbor.BlockIndex, out double deferredNeighborLogicalTop));
            Assert.NotEqual(deferredNeighborPhysicalBounds.Top, deferredNeighborLogicalTop);
            Assert.Equal(deferredNeighborPhysicalBounds, deferredNeighbor.Bounds);

            // Probe only the translated root's current logical location. The
            // retained CodeBlockBox still has its prior root-local Bounds; the
            // published generation is the authority for finding and probing it.
            LazyLayoutBand translatedVisibleBand = new(
                System.Math.Max(0, deferredNeighborLogicalTop - 1),
                deferredNeighborLogicalTop + System.Math.Max(1, deferredNeighborPhysicalBounds.Height));
            Assert.True(document.Snapshot.IsBandWorkSettled(translatedVisibleBand));
            ProbeVisibleDeferredMarkersUnderPaint(
                document.Snapshot, policy, translatedVisibleBand.Top, translatedVisibleBand.Bottom,
                translatedMarkers => Assert.Contains(deferredMarker, translatedMarkers));

            await PublishAsync(plan, deferredMarker);
            Assert.True(plan.TryGetResult(deferredMarker, out _));
        }
        finally
        {
            await DrainAsync(constructionContext);
        }
    }

    [Fact]
    public async Task StaleLaterVectorRunRejectsWholeRootBeforeEarlierImageTransfer()
    {
        await using var seed = new CompletedRasterPlacementCloneTests.PlacementFixture();
        using var session = new MarkdownPerformanceSession(MarkdownPerformanceOptions.Progressive);
        var resolver = new InlineVectorRasterResolver(DisplaySizedRasterDecodeTests.CreatePng(96, 192));
        string source = $"before ![badge]({resolver.Url}) then $x^2$ and later $y^2$";
        await using MixedInlineVectorImageDocument document = await BuildDocumentAsync(
            seed, session, resolver, source, FlowDirection.LeftToRight, 1f);

        InlineContainerBox inline = Assert.Single(document.Snapshot.Blocks.OfType<InlineContainerBox>());
        InlineImageRun imageRun = Assert.Single(inline.Runs.OfType<InlineImageRun>());
        InlineVectorSceneRun[] vectors = inline.Runs.OfType<InlineVectorSceneRun>().ToArray();
        Assert.Equal(2, vectors.Length);
        Assert.True(inline.Runs.ToList().IndexOf(imageRun) < inline.Runs.ToList().IndexOf(vectors[^1]));
        await LoadImageAsync(imageRun.Image);

        InlineRun[] runsBefore = inline.Runs.ToArray();
        Rect rootBoundsBefore = inline.Bounds;
        Rect imageBoundsBefore = imageRun.Image.Bounds;
        float imageWidthBefore = imageRun.DesiredWidth;
        float imageHeightBefore = imageRun.DesiredHeight;
        CanvasTextLayout layoutBefore = GetInlineLayout(inline);
        CanvasBitmap bitmapBefore = Assert.IsType<CanvasBitmap>(imageRun.Image.Bitmap);
        SharedCanvasBitmapCache.Lease leaseBefore = GetBitmapLease(imageRun.Image);
        Rect mutatedBounds = new(9, 7, vectors[^1].LocalBounds.Width + 3, vectors[^1].LocalBounds.Height);
        float mutatedWidth = vectors[^1].DesiredWidth + 3;
        float mutatedHeight = vectors[^1].DesiredHeight;
        float mutatedBaseline = vectors[^1].DesiredBaseline;

        MarkdownLayoutContext? constructionContext = null;
        try
        {
            PreparedImageRelayoutResult result = document.Snapshot.TryRelayoutCompletedImageRoot(
                [inline.BlockIndex], LayoutWidth, CancellationToken.None, out constructionContext,
                phaseHooks: CompletedImageRelayoutPhaseHooks.BeforeMeasure(() => vectors[^1].RestoreLayoutState(
                    mutatedWidth, mutatedHeight, mutatedBaseline, mutatedBounds)),
                expectedPointerInputEpoch: document.Snapshot.PointerInputEpoch);

            Assert.Equal(PreparedImageRelayoutResult.Retry, result);
            Assert.Same(inline, document.Snapshot.Blocks.Single(block => ReferenceEquals(block, inline)));
            Assert.Equal(rootBoundsBefore, inline.Bounds);
            Assert.Same(layoutBefore, GetInlineLayout(inline));
            Assert.Equal(runsBefore.Length, inline.Runs.Count);
            for (int index = 0; index < runsBefore.Length; index++)
                Assert.Same(runsBefore[index], inline.Runs[index]);
            Assert.Equal(mutatedWidth, vectors[^1].DesiredWidth);
            Assert.Equal(mutatedHeight, vectors[^1].DesiredHeight);
            Assert.Equal(mutatedBaseline, vectors[^1].DesiredBaseline);
            Assert.Equal(mutatedBounds, vectors[^1].LocalBounds);
            Assert.Equal(imageBoundsBefore, imageRun.Image.Bounds);
            Assert.Equal(imageWidthBefore, imageRun.DesiredWidth);
            Assert.Equal(imageHeightBefore, imageRun.DesiredHeight);
            Assert.Same(bitmapBefore, imageRun.Image.Bitmap);
            Assert.Same(leaseBefore, GetBitmapLease(imageRun.Image));
        }
        finally
        {
            await DrainAsync(constructionContext);
        }
    }

    [Fact]
    public async Task CancellationAfterCandidateCaptureLeavesSourceVectorAndImagePlacementUntouched()
    {
        await using var seed = new CompletedRasterPlacementCloneTests.PlacementFixture();
        using var session = new MarkdownPerformanceSession(MarkdownPerformanceOptions.Progressive);
        var resolver = new InlineVectorRasterResolver(DisplaySizedRasterDecodeTests.CreatePng(96, 192));
        string source = $"before $x^2$ and ![badge]({resolver.Url}) after";
        await using MixedInlineVectorImageDocument document = await BuildDocumentAsync(
            seed, session, resolver, source, FlowDirection.RightToLeft, 1.5f);
        InlineContainerBox inline = Assert.Single(document.Snapshot.Blocks.OfType<InlineContainerBox>());
        InlineVectorSceneRun vector = Assert.Single(inline.Runs.OfType<InlineVectorSceneRun>());
        InlineImageRun imageRun = Assert.Single(inline.Runs.OfType<InlineImageRun>());
        await LoadImageAsync(imageRun.Image);

        VectorSceneDrawing drawing = GetVectorDrawing(vector);
        CanvasTextLayout layout = GetInlineLayout(inline);
        Rect bounds = inline.Bounds;
        Rect imageBounds = imageRun.Image.Bounds;
        float vectorWidth = vector.DesiredWidth;
        Rect vectorLocalBounds = vector.LocalBounds;
        CanvasBitmap bitmap = Assert.IsType<CanvasBitmap>(imageRun.Image.Bitmap);
        SharedCanvasBitmapCache.Lease lease = GetBitmapLease(imageRun.Image);
        using var cancellation = new CancellationTokenSource();
        MarkdownLayoutContext? constructionContext = null;

        try
        {
            Assert.ThrowsAny<OperationCanceledException>(() => document.Snapshot.TryRelayoutCompletedImageRoot(
                [inline.BlockIndex], LayoutWidth, cancellation.Token, out constructionContext,
                phaseHooks: CompletedImageRelayoutPhaseHooks.BeforeMeasure(cancellation.Cancel),
                expectedPointerInputEpoch: document.Snapshot.PointerInputEpoch));
            Assert.NotNull(constructionContext);
            Assert.Same(drawing, GetVectorDrawing(vector));
            Assert.True(vector.IsNativeResourcesPrepared);
            Assert.Equal(vectorWidth, vector.DesiredWidth);
            Assert.Equal(vectorLocalBounds, vector.LocalBounds);
            Assert.Same(layout, GetInlineLayout(inline));
            Assert.Equal(bounds, inline.Bounds);
            Assert.Equal(imageBounds, imageRun.Image.Bounds);
            Assert.Same(bitmap, imageRun.Image.Bitmap);
            Assert.Same(lease, GetBitmapLease(imageRun.Image));
        }
        finally
        {
            await DrainAsync(constructionContext);
        }
    }

    [Fact]
    public async Task MalformedAndOverBudgetVectorRootsFailPreflightWithoutCandidatePublication()
    {
        await using var seed = new CompletedRasterPlacementCloneTests.PlacementFixture();
        using var session = new MarkdownPerformanceSession(MarkdownPerformanceOptions.Progressive);
        var resolver = new InlineVectorRasterResolver(DisplaySizedRasterDecodeTests.CreatePng(32, 64));
        string tooManyImages = $"$x^2$ " + string.Join(" ", Enumerable.Range(0, 33)
            .Select(index => $"![badge {index}]({resolver.Url})"));
        await using MixedInlineVectorImageDocument overBudget = await BuildDocumentAsync(
            seed, session, resolver, tooManyImages, FlowDirection.LeftToRight, 1f);
        InlineContainerBox overBudgetInline = Assert.Single(overBudget.Snapshot.Blocks.OfType<InlineContainerBox>());
        InlineVectorSceneRun overBudgetVector = Assert.Single(overBudgetInline.Runs.OfType<InlineVectorSceneRun>());
        Assert.Equal(33, overBudgetInline.InlineImageRuns.Count);
        foreach (var imagePlacement in overBudgetInline.InlineImageRuns)
            await LoadImageAsync(imagePlacement.Run.Image);
        InlineRun[] overBudgetRuns = overBudgetInline.Runs.ToArray();
        CanvasTextLayout overBudgetLayout = GetInlineLayout(overBudgetInline);
        VectorSceneDrawing overBudgetDrawing = GetVectorDrawing(overBudgetVector);
        Rect overBudgetBounds = overBudgetInline.Bounds;
        int resolveCount = resolver.ResolveCount;

        MarkdownLayoutContext? overBudgetContext = null;
        PreparedImageRelayoutResult overBudgetResult = overBudget.Snapshot.TryRelayoutCompletedImageRoot(
            [overBudgetInline.BlockIndex], LayoutWidth, CancellationToken.None, out overBudgetContext,
            expectedPointerInputEpoch: overBudget.Snapshot.PointerInputEpoch);
        Assert.Equal(PreparedImageRelayoutResult.Unavailable, overBudgetResult);
        Assert.Null(overBudgetContext);
        Assert.Equal(resolveCount, resolver.ResolveCount);
        Assert.Equal(overBudgetBounds, overBudgetInline.Bounds);
        Assert.Same(overBudgetLayout, GetInlineLayout(overBudgetInline));
        Assert.Same(overBudgetDrawing, GetVectorDrawing(overBudgetVector));
        for (int index = 0; index < overBudgetRuns.Length; index++)
            Assert.Same(overBudgetRuns[index], overBudgetInline.Runs[index]);
        Assert.True(overBudgetVector.IsNativeResourcesPrepared);

        // Corrupt one scalar in an otherwise valid materialized run. The
        // root-local preflight must reject this malformed recipe before it
        // clones any image or publishes an alternate layout.
        var malformedResolver = new InlineVectorRasterResolver(DisplaySizedRasterDecodeTests.CreatePng(48, 96));
        await using MixedInlineVectorImageDocument malformed = await BuildDocumentAsync(
            seed, session, malformedResolver, $"![badge]({malformedResolver.Url}) then $y^2$",
            FlowDirection.LeftToRight, 1f);
        InlineContainerBox malformedInline = Assert.Single(malformed.Snapshot.Blocks.OfType<InlineContainerBox>());
        InlineVectorSceneRun malformedVector = Assert.Single(malformedInline.Runs.OfType<InlineVectorSceneRun>());
        InlineImageRun malformedImage = Assert.Single(malformedInline.Runs.OfType<InlineImageRun>());
        await LoadImageAsync(malformedImage.Image);
        CanvasTextLayout malformedLayout = GetInlineLayout(malformedInline);
        VectorSceneDrawing malformedDrawing = GetVectorDrawing(malformedVector);
        Rect malformedBounds = malformedInline.Bounds;
        Rect invalidLocalBounds = malformedVector.LocalBounds with { X = double.NaN };
        malformedVector.RestoreLayoutState(
            malformedVector.DesiredWidth,
            malformedVector.DesiredHeight,
            malformedVector.DesiredBaseline,
            invalidLocalBounds);
        int malformedResolveCount = malformedResolver.ResolveCount;
        MarkdownLayoutContext? malformedContext = null;

        PreparedImageRelayoutResult malformedResult = malformed.Snapshot.TryRelayoutCompletedImageRoot(
            [malformedInline.BlockIndex], LayoutWidth, CancellationToken.None, out malformedContext,
            expectedPointerInputEpoch: malformed.Snapshot.PointerInputEpoch);
        Assert.Equal(PreparedImageRelayoutResult.Unavailable, malformedResult);
        Assert.Null(malformedContext);
        Assert.Equal(malformedResolveCount, malformedResolver.ResolveCount);
        Assert.Equal(malformedBounds, malformedInline.Bounds);
        Assert.Same(malformedLayout, GetInlineLayout(malformedInline));
        Assert.Same(malformedDrawing, GetVectorDrawing(malformedVector));
        Assert.True(malformedVector.IsNativeResourcesPrepared);
        Assert.True(double.IsNaN(malformedVector.LocalBounds.X));
    }

    private static Task<MixedInlineVectorImageDocument> BuildDocumentAsync(
        CompletedRasterPlacementCloneTests.PlacementFixture seed,
        MarkdownPerformanceSession session,
        InlineVectorRasterResolver resolver,
        string source,
        FlowDirection direction,
        float rasterizationScale) =>
        MixedInlineVectorImageDocument.BuildAsync(seed, session, resolver, source, direction, rasterizationScale);

    private static Task<MixedInlineVectorImageDocument> BuildProgressiveLazyDocumentAsync(
        CompletedRasterPlacementCloneTests.PlacementFixture seed,
        MarkdownPerformanceSession session,
        InlineVectorRasterResolver resolver,
        string source,
        FlowDirection direction,
        float rasterizationScale,
        bool materializeAllInlineMarkers = false) =>
        MixedInlineVectorImageDocument.BuildProgressiveLazyAsync(
            seed, session, resolver, source, direction, rasterizationScale, materializeAllInlineMarkers);

    /// <summary>
    /// The general private-inline COW recipe is intentionally still
    /// conservative. Only the image-completion root-local recipe may use a
    /// non-paintable measured vector clone: this asserts the general recipe
    /// refuses the root and that a measured-only clone cannot prepare
    /// native resources.
    /// </summary>
    private static void AssertOnlyRootLocalRecipeUsesMeasuredOnlyVectorClone(
        MixedInlineVectorImageDocument document,
        InlineContainerBox inline,
        InlineVectorSceneRun sourceVector)
    {
        MarkdownLayoutContext probeContext = document.Context.CreateRasterPlacementContext(
            CancellationToken.None,
            document.Context.ImagePreparationOwner!,
            retirementSink: null,
            imageUnavailable: null);
        Assert.False(inline.TryGetPrivateRasterPlacementCounts(
            probeContext, LayoutWidth, out _, out _, out _));

        InlineVectorSceneRun measuredOnly = sourceVector.CloneForMeasuredPlacement();
        try
        {
            Assert.True(measuredOnly.IsMeasuredPlacementOnly);
            Assert.False(measuredOnly.IsNativeResourcesPrepared);
            Assert.Null(measuredOnly.Drawing);
            Assert.Same(sourceVector.Scene, measuredOnly.Scene);
            Assert.Throws<InvalidOperationException>(() =>
                measuredOnly.PrepareNativeResources(CancellationToken.None));
        }
        finally
        {
            measuredOnly.Dispose();
        }
    }

    /// <summary>
    /// Asserts that the source vector run kept its scene, drawing and native
    /// resources, and took only the fallback's metrics, bounds and selection.
    /// </summary>
    private static void AssertVectorRetainedWithFallbackMetrics(
        LayoutSnapshot snapshot,
        InlineContainerBox inline,
        InlineVectorSceneRun sourceVector,
        MarkdownVectorScene sourceScene,
        VectorSceneDrawing sourceDrawing,
        LayoutSnapshot fallback,
        InlineContainerBox fallbackInline,
        InlineRootBefore before)
    {
        InlineVectorSceneRun updatedFallbackVector = Assert.Single(
            fallbackInline.Runs.OfType<InlineVectorSceneRun>());
        Assert.Same(sourceScene, sourceVector.Scene);
        Assert.Same(sourceDrawing, GetVectorDrawing(sourceVector));
        Assert.True(sourceVector.IsNativeResourcesPrepared);
        Assert.False(sourceVector.IsMeasuredPlacementOnly);
        Assert.Equal(updatedFallbackVector.Scene.Width, sourceVector.Scene.Width);
        Assert.Equal(updatedFallbackVector.Scene.Height, sourceVector.Scene.Height);
        Assert.Equal(updatedFallbackVector.DesiredWidth, sourceVector.DesiredWidth);
        Assert.Equal(updatedFallbackVector.DesiredHeight, sourceVector.DesiredHeight);
        Assert.Equal(updatedFallbackVector.DesiredBaseline, sourceVector.DesiredBaseline);
        Assert.Equal(updatedFallbackVector.LocalBounds, sourceVector.LocalBounds);
        Assert.Equal(SelectionFor(fallback, fallbackInline, updatedFallbackVector),
            SelectionFor(snapshot, inline, sourceVector));
        Assert.NotEmpty(SelectionFor(snapshot, inline, sourceVector));
        Assert.NotEqual(before.VectorBounds, sourceVector.LocalBounds);
        Assert.False(before.Selection.SequenceEqual(
            SelectionFor(snapshot, inline, sourceVector)));
    }

    private static VectorSceneDrawing GetVectorDrawing(InlineVectorSceneRun run) =>
        Assert.IsType<VectorSceneDrawing>(run.Drawing);
}
