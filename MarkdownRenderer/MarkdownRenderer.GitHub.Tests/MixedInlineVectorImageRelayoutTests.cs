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
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.UI.Xaml;
using Windows.Foundation;
using Windows.UI;
using Xunit;

namespace MarkdownRenderer.GitHub.Tests;

[Collection(SharedCanvasBitmapCacheTestCollection.Name)]
public sealed class MixedInlineVectorImageRelayoutTests
{
    private const float LayoutWidth = 320f;

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

        await using ImageDocument optimized = await BuildDocumentAsync(
            seed, session, optimizedResolver, optimizedSource, direction, rasterizationScale);
        await using ImageDocument fallback = await BuildDocumentAsync(
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

        // The general private-inline COW recipe is intentionally still
        // conservative. Only the image-completion root-local recipe may use a
        // non-paintable measured vector clone.
        MarkdownLayoutContext probeContext = optimized.Context.CreateRasterPlacementContext(
            CancellationToken.None,
            optimized.Context.ImagePreparationOwner!,
            retirementSink: null,
            imageUnavailable: null);
        Assert.False(optimizedInline.TryGetPrivateRasterPlacementCounts(
            probeContext, LayoutWidth, out _, out _, out _));

        InlineVectorSceneRun measuredOnly = sourceVector.CloneForMeasuredPlacement();
        try
        {
            Assert.True(measuredOnly.IsMeasuredPlacementOnly);
            Assert.False(measuredOnly.IsNativeResourcesPrepared);
            Assert.Null(ReadRawField(measuredOnly, "_drawing"));
            Assert.Same(sourceVector.Scene, measuredOnly.Scene);
            Assert.Throws<InvalidOperationException>(() =>
                measuredOnly.PrepareNativeResources(CancellationToken.None));
        }
        finally
        {
            measuredOnly.Dispose();
        }
        Assert.True(sourceVector.IsNativeResourcesPrepared);
        Assert.Same(sourceDrawing, GetVectorDrawing(sourceVector));

        InlineRun[] sourceRuns = optimizedInline.Runs.ToArray();
        CanvasTextLayout oldLayout = GetInlineLayout(optimizedInline);
        SharedCanvasBitmapCache.Lease sourceLease = GetBitmapLease(sourceImage);
        CanvasBitmap sourceBitmap = Assert.IsType<CanvasBitmap>(sourceImage.Bitmap);
        Rect[] oldRootBounds = optimized.Snapshot.Blocks.Select(static block => block.Bounds).ToArray();
        Size oldSize = optimized.Snapshot.Size;
        long oldRevision = optimized.Snapshot.LayoutRevision;
        MarkdownSemanticDocument semanticDocument = optimized.Snapshot.SemanticDocument;
        MarkdownSourceMap sourceMap = optimized.Snapshot.SourceMap;
        string semanticText = semanticDocument.Text;
        var semanticProjection = semanticDocument.TextProjectionFingerprint;
        var semanticRanges = semanticDocument.SemanticRangeFingerprint;
        Rect oldVectorBounds = sourceVector.LocalBounds;
        Rect[] oldSelection = SelectionFor(optimized.Snapshot, optimizedInline, sourceVector);
        Rect oldImageBounds = sourceImage.Bounds;
        int resolveCountBeforeCommit = optimizedResolver.ResolveCount;

        // Keep the old full root reflow as the compatibility oracle. The new
        // path must not get its result from a second parser/image-provider pass.
        fallback.Snapshot.RelayoutChangedBlocks(
            [fallbackInline.BlockIndex], LayoutWidth, CancellationToken.None);

        MarkdownLayoutContext? constructionContext = null;
        try
        {
            PreparedImageRelayoutResult result = optimized.Snapshot.TryRelayoutCompletedImageRoot(
                [optimizedInline.BlockIndex],
                LayoutWidth,
                CancellationToken.None,
                out constructionContext,
                expectedPointerInputEpoch: optimized.Snapshot.PointerInputEpoch);

            Assert.Equal(PreparedImageRelayoutResult.Committed, result);
            Assert.NotNull(constructionContext);
            Assert.Equal(oldRevision + 1, optimized.Snapshot.LayoutRevision);
            Assert.NotEqual(oldSize, optimized.Snapshot.Size);
            Assert.Equal(fallback.Snapshot.Size, optimized.Snapshot.Size);
            Assert.Same(semanticDocument, optimized.Snapshot.SemanticDocument);
            Assert.Same(sourceMap, optimized.Snapshot.SourceMap);
            Assert.Equal(semanticText, optimized.Snapshot.SemanticDocument.Text);
            Assert.Equal(semanticProjection, optimized.Snapshot.SemanticDocument.TextProjectionFingerprint);
            Assert.Equal(semanticRanges, optimized.Snapshot.SemanticDocument.SemanticRangeFingerprint);
            Assert.Equal(resolveCountBeforeCommit, optimizedResolver.ResolveCount);

            Assert.Same(optimizedInline, optimized.Snapshot.Blocks.Single(
                block => ReferenceEquals(block, optimizedInline)));
            Assert.Equal(oldRootBounds.Length, optimized.Snapshot.Blocks.Count);
            for (int index = 0; index < oldRootBounds.Length; index++)
            {
                Rect expectedBounds = LayoutPlacementTestGeometry.ToLogical(
                    fallback.Snapshot, fallback.Snapshot.Blocks[index]);
                Rect actualBounds = LayoutPlacementTestGeometry.ToLogical(
                    optimized.Snapshot, optimized.Snapshot.Blocks[index]);
                Assert.Equal(expectedBounds, actualBounds);
                Assert.NotEqual(oldRootBounds[index], actualBounds);
            }

            Assert.NotSame(oldLayout, GetInlineLayout(optimizedInline));
            Assert.Equal(sourceRuns.Length, optimizedInline.Runs.Count);
            for (int index = 0; index < sourceRuns.Length; index++)
                Assert.Same(sourceRuns[index], optimizedInline.Runs[index]);
            Assert.Same(sourceVector, optimizedInline.Runs.OfType<InlineVectorSceneRun>().Single());
            Assert.Same(sourceImageRun, optimizedInline.Runs.OfType<InlineImageRun>().Single());
            Assert.Same(sourceImage, sourceImageRun.Image);
            Assert.Same(sourceBitmap, sourceImage.Bitmap);
            Assert.Same(sourceLease, GetBitmapLease(sourceImage));
            Rect expectedImageBounds = LayoutPlacementTestGeometry.ToLogical(
                fallback.Snapshot, optimizedInline.BlockIndex, [fallbackImage.Bounds])[0];
            Rect actualImageBounds = LayoutPlacementTestGeometry.ToLogical(
                optimized.Snapshot, optimizedInline.BlockIndex, [sourceImage.Bounds])[0];
            Assert.Equal(expectedImageBounds, actualImageBounds);
            Assert.NotEqual(oldImageBounds, actualImageBounds);
            Assert.Equal(fallbackImageRun.DesiredWidth, sourceImageRun.DesiredWidth);
            Assert.Equal(fallbackImageRun.DesiredHeight, sourceImageRun.DesiredHeight);

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
            Assert.Equal(SelectionFor(fallback.Snapshot, fallbackInline, updatedFallbackVector),
                SelectionFor(optimized.Snapshot, optimizedInline, sourceVector));
            Assert.NotEmpty(SelectionFor(optimized.Snapshot, optimizedInline, sourceVector));
            Assert.NotEqual(oldVectorBounds, sourceVector.LocalBounds);
            Assert.False(oldSelection.SequenceEqual(
                SelectionFor(optimized.Snapshot, optimizedInline, sourceVector)));

            AssertSemanticSpanGeometryEqual(
                CaptureSemanticSpanGeometry(optimized.Snapshot, optimizedInline),
                CaptureSemanticSpanGeometry(fallback.Snapshot, fallbackInline));
            Assert.Equal(Paint(optimized.Snapshot, seed.Device, rasterizationScale),
                Paint(fallback.Snapshot, seed.Device, rasterizationScale));
        }
        finally
        {
            if (constructionContext is not null)
                await constructionContext.DrainOwnedRetirementsAsync().WaitAsync(TimeSpan.FromSeconds(10));
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
        await using ImageDocument optimized = await BuildProgressiveLazyDocumentAsync(
            seed, session, optimizedResolver, optimizedSource, FlowDirection.LeftToRight, 1.5f);
        await using ImageDocument fallback = await BuildProgressiveLazyDocumentAsync(
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
        Assert.True(optimizedInline.TryCaptureCurrentSceneGeometry(
            CancellationToken.None, out long generation, out InlineSceneGeometry[] initialGeometry));
        Assert.Contains(initialGeometry, item => ReferenceEquals(item.Run, vector) && item.RegionCount > 0);
        if (optimizedInline.TryGetCurrentSceneGeometry(
                out long pendingGeneration, out InlineSceneGeometry[] pendingGeometry))
        {
            Assert.Equal(generation, pendingGeneration);
            Assert.Contains(pendingGeometry, item => ReferenceEquals(item.Run, vector) && item.RegionCount > 0);
        }

        // Lazy realization may already have released the optional pending
        // array after building the active index. Capture from the authoritative
        // live layout above, then explicitly exercise the released-cache path;
        // if the pending cache still exists, its run identity is checked too.
        // Reflow must recapture candidate geometry and rebind every geometry
        // record to the retained source run.
        optimizedInline.ReleasePendingSceneGeometry(generation);
        Assert.False(optimizedInline.TryGetCurrentSceneGeometry(out _, out _));
        Assert.True(fallbackInline.TryCaptureCurrentSceneGeometry(
            CancellationToken.None, out long fallbackGeneration, out InlineSceneGeometry[] fallbackInitial));
        Assert.Contains(fallbackInitial, item => ReferenceEquals(item.Run, fallbackVector) && item.RegionCount > 0);
        if (fallbackInline.TryGetCurrentSceneGeometry(
                out long fallbackPendingGeneration, out InlineSceneGeometry[] fallbackPending))
        {
            Assert.Equal(fallbackGeneration, fallbackPendingGeneration);
            Assert.Contains(fallbackPending, item => ReferenceEquals(item.Run, fallbackVector) && item.RegionCount > 0);
        }
        fallbackInline.ReleasePendingSceneGeometry(fallbackGeneration);
        Assert.False(fallbackInline.TryGetCurrentSceneGeometry(out _, out _));

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
            if (constructionContext is not null)
                await constructionContext.DrainOwnedRetirementsAsync().WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    [Fact]
    public async Task ProgressiveImageCommitRefreshesStationaryInlineIndexAndKeepsDeferredNeighborDiscoverable()
    {
        await using var seed = new CompletedRasterPlacementCloneTests.PlacementFixture();
        using var session = new MarkdownPerformanceSession(MarkdownPerformanceOptions.Progressive);
        var resolver = new InlineVectorRasterResolver(DisplaySizedRasterDecodeTests.CreatePng(80, 160));
        string source = $"before ![badge]({resolver.Url}) with $x^2$ and $y^2$ after\n\n$$\n\\frac{{1}}{{2}}\n$$";
        await using ImageDocument document = await BuildProgressiveLazyDocumentAsync(
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
            Assert.Equal(originalRuns.Length, inline.Runs.Count);
            for (int index = 0; index < originalRuns.Length; index++)
                Assert.Same(originalRuns[index], inline.Runs[index]);
            foreach (InlineVectorSceneRun vector in vectors)
                Assert.Same(vector, inline.Runs.Single(run => ReferenceEquals(run, vector)));

            Assert.True(inline.TryGetCurrentSceneGeometry(
                out long pendingGeneration, out InlineSceneGeometry[] pendingGeometry));
            Assert.Equal(inline.SceneGeometryGeneration, pendingGeneration);
            foreach (InlineVectorSceneRun vector in vectors)
                Assert.Contains(pendingGeometry, geometry => ReferenceEquals(geometry.Run, vector) && geometry.RegionCount > 0);

            LazyLayoutBand stationaryBand = LazyLayoutBand.FromViewport(0, 1024, 0);
            document.Snapshot.EnsureMeasuredBand(stationaryBand, CancellationToken.None);
            Assert.True(document.Snapshot.IsBandWorkSettled(stationaryBand));

            MarkdownVectorSceneResidencyPolicy policy = Assert.IsType<MarkdownVectorSceneResidencyPolicy>(
                document.Snapshot.VectorSceneResidencyPolicy);
            var inlineMarkers = new List<string>();
            bool inlineResourcesReady = true;
            Assert.True(policy.TryAppendVisibleInlineSceneMarkers(
                inline, stationaryBand.Top, stationaryBand.Bottom, viewport: null,
                inlineMarkers, ref inlineResourcesReady));
            Assert.True(inlineResourcesReady);
            Assert.Empty(inlineMarkers);

            Assert.True(document.Snapshot.TryBeginPaint());
            try
            {
                Assert.True(document.Snapshot.TryGetMeasuredTopLevelBlocksInBandForSceneProbe(
                    0, 1024,
                    out IReadOnlyList<BlockBox> measuredBlocks,
                    out IReadOnlyList<int> measuredOrdinals,
                    out LayoutPlacementGeneration placements,
                    out IReadOnlyDictionary<int, int> ordinalByBlockIndex));
                Assert.True(MarkdownProgressiveScenePlan.TryCollectVisibleDeferredSceneMarkers(
                    measuredBlocks, measuredOrdinals, placements, ordinalByBlockIndex,
                    0, 1024, viewport: null, residencyPolicy: policy,
                    out IReadOnlyList<string> visibleMarkers, out _));
                Assert.Contains(deferredMarker, visibleMarkers);
                Assert.Equal(1, plan.UpdateVisibleMarkers(visibleMarkers));
            }
            finally
            {
                document.Snapshot.EndPaint();
            }

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
            Assert.True(document.Snapshot.TryBeginPaint());
            try
            {
                Assert.True(document.Snapshot.TryGetMeasuredTopLevelBlocksInBandForSceneProbe(
                    translatedVisibleBand.Top,
                    translatedVisibleBand.Bottom,
                    out IReadOnlyList<BlockBox> translatedBlocks,
                    out IReadOnlyList<int> translatedOrdinals,
                    out LayoutPlacementGeneration translatedPlacements,
                    out IReadOnlyDictionary<int, int> translatedOrdinalByBlockIndex));
                Assert.True(MarkdownProgressiveScenePlan.TryCollectVisibleDeferredSceneMarkers(
                    translatedBlocks,
                    translatedOrdinals,
                    translatedPlacements,
                    translatedOrdinalByBlockIndex,
                    translatedVisibleBand.Top,
                    translatedVisibleBand.Bottom,
                    viewport: null,
                    residencyPolicy: policy,
                    out IReadOnlyList<string> translatedMarkers,
                    out _));
                Assert.Contains(deferredMarker, translatedMarkers);
            }
            finally
            {
                document.Snapshot.EndPaint();
            }

            await PublishAsync(plan, deferredMarker);
            Assert.True(plan.TryGetResult(deferredMarker, out _));
        }
        finally
        {
            if (constructionContext is not null)
                await constructionContext.DrainOwnedRetirementsAsync().WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    [Fact]
    public async Task StaleLaterVectorRunRejectsWholeRootBeforeEarlierImageTransfer()
    {
        await using var seed = new CompletedRasterPlacementCloneTests.PlacementFixture();
        using var session = new MarkdownPerformanceSession(MarkdownPerformanceOptions.Progressive);
        var resolver = new InlineVectorRasterResolver(DisplaySizedRasterDecodeTests.CreatePng(96, 192));
        string source = $"before ![badge]({resolver.Url}) then $x^2$ and later $y^2$";
        await using ImageDocument document = await BuildDocumentAsync(
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
                beforeMeasureOutsideGateForTests: () => vectors[^1].RestoreLayoutState(
                    mutatedWidth, mutatedHeight, mutatedBaseline, mutatedBounds),
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
            if (constructionContext is not null)
                await constructionContext.DrainOwnedRetirementsAsync().WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    [Fact]
    public async Task CancellationAfterCandidateCaptureLeavesSourceVectorAndImagePlacementUntouched()
    {
        await using var seed = new CompletedRasterPlacementCloneTests.PlacementFixture();
        using var session = new MarkdownPerformanceSession(MarkdownPerformanceOptions.Progressive);
        var resolver = new InlineVectorRasterResolver(DisplaySizedRasterDecodeTests.CreatePng(96, 192));
        string source = $"before $x^2$ and ![badge]({resolver.Url}) after";
        await using ImageDocument document = await BuildDocumentAsync(
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
                beforeMeasureOutsideGateForTests: cancellation.Cancel,
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
            if (constructionContext is not null)
                await constructionContext.DrainOwnedRetirementsAsync().WaitAsync(TimeSpan.FromSeconds(10));
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
        await using ImageDocument overBudget = await BuildDocumentAsync(
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
        await using ImageDocument malformed = await BuildDocumentAsync(
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

    private static async Task<ImageDocument> BuildDocumentAsync(
        CompletedRasterPlacementCloneTests.PlacementFixture seed,
        MarkdownPerformanceSession session,
        InlineVectorRasterResolver resolver,
        string source,
        FlowDirection direction,
        float rasterizationScale)
    {
        MarkdownEngine engine = CreateMathEngine();
        try
        {
            MarkdownDocument markdown = await engine.ParseAsync(source);
            return BuildSnapshot(seed, session, resolver, markdown, engine, direction, rasterizationScale, lazy: false);
        }
        catch
        {
            engine.Dispose();
            throw;
        }
    }

    private static async Task<ImageDocument> BuildProgressiveLazyDocumentAsync(
        CompletedRasterPlacementCloneTests.PlacementFixture seed,
        MarkdownPerformanceSession session,
        InlineVectorRasterResolver resolver,
        string source,
        FlowDirection direction,
        float rasterizationScale,
        bool materializeAllInlineMarkers = false)
    {
        MarkdownEngine engine = CreateMathEngine();
        object owner = new();
        var registry = new MarkdownExtensionRegistry().ConfigureGitHubReadmeRegistry();
        try
        {
            MarkdownDocument markdown = Assert.IsType<MarkdownDocument>(
                await ((IMarkdownPerformanceSessionInternal)session).ParseAndPrepareDocumentAsync(
                    engine, null, source, registry, owner, CancellationToken.None));
            string[] allInlineMarkers = GetInlineMarkers(markdown);
            string[] markersToPublish = materializeAllInlineMarkers
                ? allInlineMarkers
                : [Assert.Single(allInlineMarkers)];
            MarkdownProgressiveScenePlan plan = GetRegisteredPlan(session, owner);
            plan.UpdateVisibleMarkers(markersToPublish);
            foreach (string marker in markersToPublish)
                await PublishAsync(plan, marker);
            return BuildSnapshot(seed, session, resolver, markdown, engine, direction, rasterizationScale,
                lazy: true, performanceOwner: owner, registry: registry);
        }
        catch
        {
            engine.Dispose();
            await ((IMarkdownPerformanceSessionInternal)session).ReleaseDeferredSceneDocument(owner);
            throw;
        }
    }

    private static MarkdownEngine CreateMathEngine() => new MarkdownEngineBuilder()
        .UseProfile(MarkdownProfiles.GfmStrict)
        .UseMathematics()
        .WithParseCacheBudgetBytes(0)
        .Build();

    private static ImageDocument BuildSnapshot(
        CompletedRasterPlacementCloneTests.PlacementFixture seed,
        MarkdownPerformanceSession session,
        InlineVectorRasterResolver resolver,
        MarkdownDocument markdown,
        MarkdownEngine engine,
        FlowDirection direction,
        float rasterizationScale,
        bool lazy,
        object? performanceOwner = null,
        MarkdownExtensionRegistry? registry = null)
    {
        registry ??= new MarkdownExtensionRegistry().ConfigureGitHubReadmeRegistry();
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
            PerformanceDocumentOwner = performanceOwner ?? new object(),
            PerformanceSession = session,
            ImageResolver = resolver,
            AllowThirdPartyRemoteImages = true,
            RasterizationScale = rasterizationScale,
        };

        LayoutSnapshot? snapshot = null;
        try
        {
            var builder = new LayoutBuilder(context, semanticDocument: markdown);
            snapshot = lazy
                ? builder.BuildLazy(markdown.ParsedDocument!, LayoutWidth, viewportTop: 0,
                    viewportHeight: 1024, overscan: 0, CancellationToken.None)
                : builder.Build(markdown.ParsedDocument!, LayoutWidth, CancellationToken.None);
            snapshot.AssignAutomationDocumentGeneration(42_081);
            var result = new ImageDocument(context, snapshot, sourceMap, resolver, engine, performanceOwner);
            snapshot = null;
            return result;
        }
        catch
        {
            if (snapshot is not null)
                snapshot.Retire().GetAwaiter().GetResult();
            context.DrainOwnedRetirementsAsync().GetAwaiter().GetResult();
            throw;
        }
    }

    private static string[] GetInlineMarkers(MarkdownDocument document) =>
        document.ExtensionInlineNodeContent.Values
            .SelectMany(static fragment => fragment.Items)
        .Where(static content => content.Attributes.ContainsKey("renderer.internal.deferred-scene"))
        .Select(static content => Assert.IsType<string>(content.Attributes["renderer.internal.deferred-scene"]))
        .ToArray();

    private static MarkdownProgressiveScenePlan GetRegisteredPlan(MarkdownPerformanceSession session, object owner)
    {
        FieldInfo field = typeof(MarkdownPerformanceSession).GetField(
            "_scenePlans", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Missing progressive scene plan registry.");
        return Assert.IsType<Dictionary<object, MarkdownProgressiveScenePlan>>(field.GetValue(session))[owner];
    }

    private static async Task PublishAsync(MarkdownProgressiveScenePlan plan, string marker)
    {
        var published = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Assert.True(plan.TrySchedule(
            marker,
            static action => { action(); return true; },
            () => published.TrySetResult()));
        await published.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }

    private static async Task LoadImageAsync(ImageBox image)
    {
        await DisplaySizedRasterDecodeTests.WaitForLoadAsync(image).WaitAsync(TimeSpan.FromSeconds(10));
        await DisplaySizedRasterDecodeTests.WaitUntilAsync(() => image.ActiveImageWorkCountForTests == 0);
        Assert.NotNull(image.Bitmap);
    }

    private static Rect[] SelectionFor(LayoutSnapshot snapshot, InlineContainerBox inline, InlineRun run)
    {
        int runIndex = Enumerable.Range(0, inline.Runs.Count)
            .Single(index => ReferenceEquals(inline.Runs[index], run));
        var range = new DocumentRange(
            new DocumentPosition(inline.BlockIndex, runIndex, 0),
            new DocumentPosition(inline.BlockIndex, runIndex, run.RenderedLength));
        return LayoutPlacementTestGeometry.ToLogical(
            snapshot, inline.BlockIndex, inline.GetSelectionRects(range));
    }

    private static SemanticSpanGeometry[] CaptureSemanticSpanGeometry(
        LayoutSnapshot snapshot,
        InlineContainerBox inline)
    {
        return snapshot.SemanticDocument.TextSpans
            .Where(span => ReferenceEquals(span.InlineBox, inline))
            .Select(span => new SemanticSpanGeometry(
                span.TextStart,
                span.TextEnd,
                span.InlineRun?.GetType(),
                snapshot.SemanticDocument.Text.Substring(span.TextStart, span.TextEnd - span.TextStart),
                snapshot.SemanticDocument.GetDocumentRects(span.TextStart, span.TextEnd).ToArray()))
            .ToArray();
    }

    private static InlineSceneGeometrySnapshot[] CaptureSceneGeometry(InlineSceneGeometry[] geometry) =>
        geometry.Select(item => new InlineSceneGeometrySnapshot(
            item.RegionCount,
            Enumerable.Range(0, item.RegionCount).Select(item.GetRegionBounds).ToArray())).ToArray();

    private static void AssertSceneGeometryEqual(
        InlineSceneGeometrySnapshot[] expected,
        InlineSceneGeometrySnapshot[] actual)
    {
        Assert.Equal(expected.Length, actual.Length);
        for (int index = 0; index < expected.Length; index++)
        {
            Assert.Equal(expected[index].RegionCount, actual[index].RegionCount);
            Assert.Equal(expected[index].Regions, actual[index].Regions);
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

    private static void AssertGeometryBoundToSource(
        InlineSceneGeometry[] geometry,
        InlineVectorSceneRun source)
    {
        Assert.NotEmpty(geometry);
        Assert.All(geometry, item => Assert.Same(source, item.Run));
    }

    private static CanvasTextLayout GetInlineLayout(InlineContainerBox inline) =>
        Assert.IsType<CanvasTextLayout>(ReadRawField(inline, "_layout"));

    private static SharedCanvasBitmapCache.Lease GetBitmapLease(ImageBox image) =>
        Assert.IsType<SharedCanvasBitmapCache.Lease>(ReadRawField(image, "_bitmapLease"));

    private static VectorSceneDrawing GetVectorDrawing(InlineVectorSceneRun run) =>
        Assert.IsType<VectorSceneDrawing>(ReadRawField(run, "_drawing"));

    private static object? ReadRawField(object instance, string name)
    {
        FieldInfo field = instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"Missing private test field '{name}'.");
        return field.GetValue(instance);
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

    private readonly record struct SemanticSpanGeometry(
        int Start,
        int End,
        Type? RunType,
        string Text,
        Rect[] DocumentRects);

    private readonly record struct InlineSceneGeometrySnapshot(int RegionCount, Rect[] Regions);

    private sealed class ImageDocument : IAsyncDisposable
    {
        internal ImageDocument(
            MarkdownLayoutContext context,
            LayoutSnapshot snapshot,
            MarkdownSourceMap sourceMap,
            InlineVectorRasterResolver resolver,
            MarkdownEngine engine,
            object? performanceOwner)
        {
            Context = context;
            Snapshot = snapshot;
            SourceMap = sourceMap;
            Resolver = resolver;
            Engine = engine;
            PerformanceOwner = performanceOwner;
        }

        internal MarkdownLayoutContext Context { get; }
        internal LayoutSnapshot Snapshot { get; }
        internal MarkdownSourceMap SourceMap { get; }
        internal InlineVectorRasterResolver Resolver { get; }
        private MarkdownEngine Engine { get; }
        internal object? PerformanceOwner { get; }

        public async ValueTask DisposeAsync()
        {
            try
            {
                await Snapshot.Retire().WaitAsync(TimeSpan.FromSeconds(10));
            }
            finally
            {
                await Context.DrainOwnedRetirementsAsync().WaitAsync(TimeSpan.FromSeconds(10));
                if (PerformanceOwner is not null)
                    await ((IMarkdownPerformanceSessionInternal)Context.PerformanceSession!)
                        .ReleaseDeferredSceneDocument(PerformanceOwner);
                Engine.Dispose();
            }
        }
    }

    private sealed class InlineVectorRasterResolver : IMarkdownImageResolver
    {
        private readonly byte[] _png;
        private readonly string _cacheKey = "mixed-inline-vector:" + Guid.NewGuid().ToString("N");
        private int _resolveCount;

        internal InlineVectorRasterResolver(byte[] png)
        {
            _png = png;
            Url = $"https://images.example.test/mixed-vector-{Guid.NewGuid():N}.png";
        }

        internal string Url { get; }
        internal int ResolveCount => Volatile.Read(ref _resolveCount);

        public ValueTask<MarkdownImageResolution> ResolveAsync(
            string source,
            MarkdownImageResolveContext context,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!string.Equals(source, Url, StringComparison.Ordinal))
                throw new InvalidOperationException("The test resolver received an unexpected image URL.");
            Interlocked.Increment(ref _resolveCount);
            return ValueTask.FromResult(MarkdownImageResolution.Resolved(new MarkdownImageAsset(
                _png,
                "image/png",
                new Uri(source),
                _cacheKey)));
        }
    }
}
