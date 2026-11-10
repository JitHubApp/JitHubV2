using MarkdownRenderer.Accessibility.Internal.Semantics;
using MarkdownRenderer.Document;
using MarkdownRenderer.GitHub.Tests.Layout.Internal.Images.BitmapCache;
using MarkdownRenderer.GitHub.Tests.Layout.Internal.Images.Raster;
using MarkdownRenderer.GitHub.Tests.Layout.Internal.Inlines.Placement;
using MarkdownRenderer.GitHub.Tests.TestSupport.Fixtures;
using MarkdownRenderer.Images;
using MarkdownRenderer.Layout.Internal.Boxes;
using MarkdownRenderer.Layout.Internal.CodeBlocks;
using MarkdownRenderer.Layout.Internal.Context;
using MarkdownRenderer.Layout.Internal.Images;
using MarkdownRenderer.Layout.Internal.Images.BitmapCache;
using MarkdownRenderer.Layout.Internal.Inlines;
using MarkdownRenderer.Layout.Internal.Inlines.Runs;
using MarkdownRenderer.Layout.Internal.Relayout;
using MarkdownRenderer.Performance;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.UI.Xaml;
using Windows.Foundation;
using Xunit;
using static MarkdownRenderer.GitHub.Tests.TestSupport.Fixtures.ImageRelayoutTestSupport;
using static MarkdownRenderer.GitHub.Tests.Layout.Internal.Relayout.RootLocalInlineImageRelayoutFixture;

namespace MarkdownRenderer.GitHub.Tests.Layout.Internal.Relayout;

/// <summary>
/// Completed inline image runs, alone or batched with standalone image roots,
/// relayout through the completed-image path without replacing their runs or
/// image owners, and match a protected full-reflow fallback.
/// </summary>
/// <remarks>
/// This file is longer than 500 lines because it holds the eleven scenarios of
/// one inline relayout entry point (SVG and raster commits, native clip, late
/// run mutation, lazy siblings, cancellation, retry and each preflight
/// rejection), each of which arranges its own document and states its own
/// oracle. Documents, sources and shared oracles live in
/// <see cref="RootLocalInlineImageDocument"/> and
/// <see cref="RootLocalInlineImageRelayoutFixture"/>.
/// </remarks>
[Collection(SharedCanvasBitmapCacheTestCollection.Name)]
public sealed class RootLocalInlineImageRelayoutTests
{
    private const float LayoutWidth = RootLocalInlineImageDocument.LayoutWidth;
    private const long AutomationGeneration = RootLocalInlineImageDocument.AutomationGeneration;

    [Fact]
    public async Task FullSvgInlineBatchPreservesRasterMetadataAndReadsProviderOutsideSourceGates()
    {
        await using var seed = new CompletedRasterPlacementCloneTests.PlacementFixture();
        using var session = new MarkdownPerformanceSession(MarkdownPerformanceOptions.Progressive);
        var resolver = new InlineSvgResolver();
        var renderer = new InlineSvgRenderer();
        string sourceText = $"Before **bold** ![full SVG badge]({resolver.Url}) after.\n\nTail text.\n";
        await using RootLocalInlineImageDocument optimized = BuildDocument(seed, session, resolver, sourceText,
            FlowDirection.LeftToRight, 1.5f, lazy: false, svgRenderer: renderer);
        await using RootLocalInlineImageDocument fallback = BuildDocument(seed, session, resolver, sourceText,
            FlowDirection.LeftToRight, 1.5f, lazy: false, svgRenderer: renderer);

        InlineContainerBox inline = FindInlineImageRoot(optimized.Snapshot, expectedImages: 1);
        InlineContainerBox fallbackInline = FindInlineImageRoot(fallback.Snapshot, expectedImages: 1);
        InlineImageRun run = Assert.Single(inline.Runs.OfType<InlineImageRun>());
        ImageBox image = run.Image;
        ImageBox fallbackImage = Assert.Single(fallbackInline.Runs.OfType<InlineImageRun>()).Image;
        await LoadImageAsync(image);
        await LoadImageAsync(fallbackImage);

        Assert.True(image.IsSvg);
        CompletedSvgOutput output = CompletedSvgOutput.Capture(image);
        Assert.Null(image.SvgDocuments.Current);
        Assert.Null(image.Svg.AcceptedPlan);
        Assert.Empty(image.SvgTileCache.CaptureSnapshot().Tiles);
        Assert.True(renderer.FullRenderCount > 0);
        Assert.Equal(0, renderer.TiledRenderCount);

        fallback.Snapshot.RelayoutChangedBlocks(
            [fallbackInline.BlockIndex], LayoutWidth, CancellationToken.None);
        object layoutLock = optimized.Snapshot.Geometry.Gate;
        object imageLock = image.Lifetime.Sync;
        int providerReadsUnderSourceGate = 0;
        renderer.GenerationReadHook = () =>
        {
            if (Monitor.IsEntered(layoutLock) || Monitor.IsEntered(imageLock))
                Interlocked.Increment(ref providerReadsUnderSourceGate);
        };
        int providerReadsBefore = renderer.CacheGenerationReadCount;
        long revisionBefore = optimized.Snapshot.LayoutRevision;
        Size sizeBefore = optimized.Snapshot.Size;
        Rect[] fallbackBounds = fallback.Snapshot.Blocks.Select(static root => root.Bounds).ToArray();
        MarkdownLayoutContext? constructionContext = null;
        try
        {
            PreparedImageRelayoutResult result = optimized.Snapshot.TryRelayoutCompletedImageRoots(
                [inline.BlockIndex], LayoutWidth, CancellationToken.None, out constructionContext,
                expectedPointerInputEpoch: optimized.Snapshot.PointerInputEpoch);

            Assert.Equal(PreparedImageRelayoutResult.Committed, result);
            Assert.True(renderer.CacheGenerationReadCount > providerReadsBefore,
                "The completed SVG provider generation fence was not queried.");
            Assert.Equal(0, Volatile.Read(ref providerReadsUnderSourceGate));
            Assert.Equal(revisionBefore + 1, optimized.Snapshot.LayoutRevision);
            Assert.Equal(fallback.Snapshot.Size, optimized.Snapshot.Size);
            Assert.NotEqual(sizeBefore, optimized.Snapshot.Size);
            Assert.Same(inline, optimized.Snapshot.Blocks.Single(
                root => ReferenceEquals(root, inline)));
            Assert.Same(run, inline.Runs.OfType<InlineImageRun>().Single());
            Assert.Same(image, inline.Runs.OfType<InlineImageRun>().Single().Image);
            output.AssertPreserved(image);
            Assert.Equal(fallbackBounds,
                fallback.Snapshot.Blocks.Select(static root => root.Bounds).ToArray());
            Assert.Equal(Paint(optimized.Snapshot, seed.Device, 1.5f),
                Paint(fallback.Snapshot, seed.Device, 1.5f));
        }
        finally
        {
            renderer.GenerationReadHook = null;
            await DrainAsync(constructionContext);
        }
    }

    [Fact]
    public async Task TextSvgInlineBatchRejectsStaleFontGenerationWithoutChangingSourcePlacement()
    {
        await using var seed = new CompletedRasterPlacementCloneTests.PlacementFixture();
        using var session = new MarkdownPerformanceSession(MarkdownPerformanceOptions.Progressive);
        var resolver = new InlineSvgResolver();
        var renderer = new InlineSvgRenderer();
        await using RootLocalInlineImageDocument document = BuildDocument(seed, session, resolver,
            $"Text ![font-dependent SVG]({resolver.Url}) tail.\n",
            FlowDirection.RightToLeft, 1f, lazy: false, svgRenderer: renderer);
        InlineContainerBox inline = FindInlineImageRoot(document.Snapshot, expectedImages: 1);
        InlineImageRun run = Assert.Single(inline.Runs.OfType<InlineImageRun>());
        ImageBox image = run.Image;
        await LoadImageAsync(image);
        Assert.True(image.IsSvg);
        Assert.True(Assert.IsType<MarkdownSvgDocumentInfo>(image.Svg.Info).HasText);

        BlockBox[] rootsBefore = document.Snapshot.Blocks.ToArray();
        Rect[] boundsBefore = rootsBefore.Select(static root => root.Bounds).ToArray();
        Rect imageBoundsBefore = image.Bounds;
        CanvasBitmap bitmapBefore = Assert.IsType<CanvasBitmap>(image.Bitmap);
        SharedCanvasBitmapCache.Lease leaseBefore = GetBitmapLease(image);
        MarkdownSvgDocumentInfo infoBefore = Assert.IsType<MarkdownSvgDocumentInfo>(image.Svg.Info);
        long revisionBefore = document.Snapshot.LayoutRevision;
        Size sizeBefore = document.Snapshot.Size;
        int providerReadsBefore = renderer.CacheGenerationReadCount;
        renderer.SetGeneration(1);
        MarkdownLayoutContext? constructionContext = null;
        try
        {
            PreparedImageRelayoutResult result = document.Snapshot.TryRelayoutCompletedImageRoots(
                [inline.BlockIndex], LayoutWidth, CancellationToken.None, out constructionContext,
                expectedPointerInputEpoch: document.Snapshot.PointerInputEpoch);

            Assert.Equal(PreparedImageRelayoutResult.Retry, result);
            Assert.True(renderer.CacheGenerationReadCount > providerReadsBefore);
            Assert.Equal(revisionBefore, document.Snapshot.LayoutRevision);
            Assert.Equal(sizeBefore, document.Snapshot.Size);
            Assert.Same(inline, document.Snapshot.Blocks.Single(
                root => ReferenceEquals(root, inline)));
            Assert.Same(run, inline.Runs.OfType<InlineImageRun>().Single());
            Assert.Equal(boundsBefore,
                document.Snapshot.Blocks.Select(static root => root.Bounds).ToArray());
            Assert.Equal(imageBoundsBefore, image.Bounds);
            Assert.Same(bitmapBefore, image.Bitmap);
            Assert.Same(leaseBefore, GetBitmapLease(image));
            Assert.Equal(infoBefore, image.Svg.Info);
        }
        finally
        {
            await DrainAsync(constructionContext);
        }
    }

    [Fact]
    public async Task InlineBatchAcceptsNativeVisualClipNarrowerThanRunDesiredWidth()
    {
        await using var seed = new CompletedRasterPlacementCloneTests.PlacementFixture();
        using var session = new MarkdownPerformanceSession(MarkdownPerformanceOptions.Progressive);
        var resolver = new InlineRasterResolver(DisplaySizedRasterDecodeTests.CreatePng(96, 192));
        string sourceText =
            $"![earlier standalone image]({resolver.StandaloneUrl})\n\n" +
            $"Text before ![clipped inline image]({resolver.InlineFirstUrl}) after.\n";
        await using RootLocalInlineImageDocument optimized = BuildDocument(seed, session, resolver, sourceText,
            FlowDirection.LeftToRight, 1.5f, lazy: false);
        await using RootLocalInlineImageDocument fallback = BuildDocument(seed, session, resolver, sourceText,
            FlowDirection.LeftToRight, 1.5f, lazy: false);

        ImageBox standalone = Assert.Single(optimized.Snapshot.Blocks.OfType<ImageBox>());
        ImageBox fallbackStandalone = Assert.Single(fallback.Snapshot.Blocks.OfType<ImageBox>());
        InlineContainerBox inline = FindInlineImageRoot(optimized.Snapshot, expectedImages: 1);
        InlineContainerBox fallbackInline = FindInlineImageRoot(fallback.Snapshot, expectedImages: 1);
        InlineImageRun run = Assert.Single(inline.Runs.OfType<InlineImageRun>());
        InlineImageRun fallbackRun = Assert.Single(fallbackInline.Runs.OfType<InlineImageRun>());
        ImageBox image = run.Image;
        ImageBox fallbackImage = fallbackRun.Image;
        Assert.True(standalone.BlockIndex < inline.BlockIndex);
        await LoadImageAsync(standalone);
        await LoadImageAsync(image);
        await LoadImageAsync(fallbackStandalone);
        await LoadImageAsync(fallbackImage);
        ClipInlineImageToHalfDesiredWidth(inline, run, image);

        BlockBox[] rootsBefore = optimized.Snapshot.Blocks.ToArray();
        CanvasBitmap standaloneBitmap = Assert.IsType<CanvasBitmap>(standalone.Bitmap);
        CanvasBitmap inlineBitmap = Assert.IsType<CanvasBitmap>(image.Bitmap);
        SharedCanvasBitmapCache.Lease standaloneLease = GetBitmapLease(standalone);
        SharedCanvasBitmapCache.Lease inlineLease = GetBitmapLease(image);
        InlineRun[] runsBefore = inline.Runs.ToArray();
        CanvasTextLayout oldInlineLayout = GetInlineLayout(inline);
        long revisionBefore = optimized.Snapshot.LayoutRevision;
        string[] resolverSources = [resolver.StandaloneUrl, resolver.InlineFirstUrl];
        int[] resolverCountsBefore = resolverSources.Select(resolver.ResolveCountFor).ToArray();
        int[] changedBlockIndices = [standalone.BlockIndex, inline.BlockIndex];
        int[] fallbackChangedBlockIndices = [fallbackStandalone.BlockIndex, fallbackInline.BlockIndex];
        fallback.Snapshot.RelayoutChangedBlocks(
            fallbackChangedBlockIndices, LayoutWidth, CancellationToken.None);

        MarkdownLayoutContext? constructionContext = null;
        try
        {
            PreparedImageRelayoutResult result = optimized.Snapshot.TryRelayoutCompletedImageRoots(
                changedBlockIndices, LayoutWidth, CancellationToken.None, out constructionContext,
                expectedPointerInputEpoch: optimized.Snapshot.PointerInputEpoch);

            Assert.Equal(PreparedImageRelayoutResult.Committed, result);
            Assert.Equal(revisionBefore + 1, optimized.Snapshot.LayoutRevision);
            Assert.Equal(fallback.Snapshot.Size, optimized.Snapshot.Size);
            Assert.Equal(rootsBefore.Length, optimized.Snapshot.Blocks.Count);
            for (int index = 0; index < rootsBefore.Length; index++)
                Assert.Same(rootsBefore[index], optimized.Snapshot.Blocks[index]);
            Assert.Same(standalone, optimized.Snapshot.Blocks.Single(root => ReferenceEquals(root, standalone)));
            Assert.Same(inline, optimized.Snapshot.Blocks.Single(root => ReferenceEquals(root, inline)));
            Assert.NotSame(oldInlineLayout, GetInlineLayout(inline));
            AssertRunsRetained(inline, runsBefore);
            Assert.Same(image, run.Image);
            AssertPresentationRetained(image, inlineBitmap, inlineLease);
            AssertPresentationRetained(standalone, standaloneBitmap, standaloneLease);
            AssertMeasuredLikeFallback(run, fallbackRun, image, fallbackImage);
            Assert.Equal(
                LayoutPlacementTestGeometry.ToLogical(fallback.Snapshot, fallback.Snapshot.Blocks),
                LayoutPlacementTestGeometry.ToLogical(optimized.Snapshot, optimized.Snapshot.Blocks));
            Assert.Equal(resolverCountsBefore,
                resolverSources.Select(resolver.ResolveCountFor).ToArray());
            AssertPublishedInlineRegionAndPlanMatchFallback(
                optimized.Snapshot, fallback.Snapshot, inline, fallbackInline, run, image, standalone);
            Assert.Equal(Paint(optimized.Snapshot, seed.Device, 1.5f),
                Paint(fallback.Snapshot, seed.Device, 1.5f));
        }
        finally
        {
            await DrainAsync(constructionContext);
        }
    }

    [Fact]
    public async Task MixedBatchRejectsLateInlineRunIdentityChangeBeforeAnyImagePlacementTransfer()
    {
        await using var seed = new CompletedRasterPlacementCloneTests.PlacementFixture();
        using var session = new MarkdownPerformanceSession(MarkdownPerformanceOptions.Progressive);
        var resolver = new InlineRasterResolver(DisplaySizedRasterDecodeTests.CreatePng(96, 192));
        string sourceText =
            $"![earlier standalone image]({resolver.StandaloneUrl})\n\n" +
            $"Text before ![inline image]({resolver.InlineFirstUrl}) after.\n";
        await using RootLocalInlineImageDocument document = BuildDocument(seed, session, resolver, sourceText,
            FlowDirection.RightToLeft, 2f, lazy: false);
        ImageBox standalone = Assert.Single(document.Snapshot.Blocks.OfType<ImageBox>());
        InlineContainerBox inline = FindInlineImageRoot(document.Snapshot, expectedImages: 1);
        InlineRun lastInlineRun = inline.Runs[^1];
        InlineImageRun imageRun = Assert.Single(inline.Runs.OfType<InlineImageRun>());
        ImageBox image = imageRun.Image;
        Assert.True(standalone.BlockIndex < inline.BlockIndex);
        await LoadImageAsync(standalone);
        await LoadImageAsync(image);

        BlockBox[] rootsBefore = document.Snapshot.Blocks.ToArray();
        Rect[] boundsBefore = rootsBefore.Select(static root => root.Bounds).ToArray();
        Rect standaloneBoundsBefore = standalone.Bounds;
        Rect inlineBoundsBefore = inline.Bounds;
        Rect imageBoundsBefore = image.Bounds;
        CanvasBitmap standaloneBitmap = Assert.IsType<CanvasBitmap>(standalone.Bitmap);
        CanvasBitmap inlineBitmap = Assert.IsType<CanvasBitmap>(image.Bitmap);
        SharedCanvasBitmapCache.Lease standaloneLease = GetBitmapLease(standalone);
        SharedCanvasBitmapCache.Lease inlineLease = GetBitmapLease(image);
        CanvasTextLayout inlineLayout = GetInlineLayout(inline);
        long revisionBefore = document.Snapshot.LayoutRevision;
        Size sizeBefore = document.Snapshot.Size;
        float standaloneWidthBefore = standalone.MeasuredImageWidth;
        float standaloneHeightBefore = standalone.MeasuredImageHeight;
        float inlineWidthBefore = image.MeasuredImageWidth;
        float inlineHeightBefore = image.MeasuredImageHeight;
        int originalInlineIndex = lastInlineRun.InlineIndex;
        bool mutationRan = false;
        MarkdownLayoutContext? constructionContext = null;
        PreparedImageRelayoutResult result = PreparedImageRelayoutResult.Unavailable;
        try
        {
            result = document.Snapshot.TryRelayoutCompletedImageRoots(
                [standalone.BlockIndex, inline.BlockIndex], LayoutWidth, CancellationToken.None,
                out constructionContext,
                phaseHooks: CompletedImageRelayoutPhaseHooks.BeforeMeasure(() =>
                {
                    mutationRan = true;
                    lastInlineRun.InlineIndex = originalInlineIndex + 1;
                }),
                expectedPointerInputEpoch: document.Snapshot.PointerInputEpoch);
        }
        finally
        {
            // Restore the source run even if an assertion or the private measure
            // path fails, so snapshot retirement sees a structurally valid tree.
            lastInlineRun.InlineIndex = originalInlineIndex;
            await DrainAsync(constructionContext);
        }

        Assert.Equal(PreparedImageRelayoutResult.Retry, result);
        Assert.True(mutationRan);
        Assert.Equal(revisionBefore, document.Snapshot.LayoutRevision);
        Assert.Equal(sizeBefore, document.Snapshot.Size);
        Assert.Equal(rootsBefore.Length, document.Snapshot.Blocks.Count);
        AssertRootsUnchanged(document.Snapshot, rootsBefore, boundsBefore);
        Assert.Equal(standaloneBoundsBefore, standalone.Bounds);
        Assert.Equal(inlineBoundsBefore, inline.Bounds);
        Assert.Equal(imageBoundsBefore, image.Bounds);
        Assert.Same(inlineLayout, GetInlineLayout(inline));
        Assert.Same(lastInlineRun, inline.Runs[^1]);
        Assert.Equal(originalInlineIndex, lastInlineRun.InlineIndex);
        Assert.Same(standalone, document.Snapshot.Blocks.Single(root => ReferenceEquals(root, standalone)));
        Assert.Same(inline, document.Snapshot.Blocks.Single(root => ReferenceEquals(root, inline)));
        Assert.Equal(standaloneWidthBefore, standalone.MeasuredImageWidth);
        Assert.Equal(standaloneHeightBefore, standalone.MeasuredImageHeight);
        Assert.Equal(inlineWidthBefore, image.MeasuredImageWidth);
        Assert.Equal(inlineHeightBefore, image.MeasuredImageHeight);
        AssertPresentationRetained(standalone, standaloneBitmap, standaloneLease);
        AssertPresentationRetained(image, inlineBitmap, inlineLease);
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
    public async Task MixedInlineAndStandaloneBatchMatchesFallbackPixelsAndPreservesIdentities(
        float rasterizationScale,
        bool rightToLeft,
        bool forceSoftwareRenderer)
    {
        await using var seed = new CompletedRasterPlacementCloneTests.PlacementFixture(
            forceSoftwareRenderer: forceSoftwareRenderer);
        Assert.Equal(forceSoftwareRenderer, seed.ForceSoftwareRenderer);
        using var session = new MarkdownPerformanceSession(MarkdownPerformanceOptions.Progressive);
        var resolver = new InlineRasterResolver(DisplaySizedRasterDecodeTests.CreatePng(96, 192));
        string sourceText = CreateMixedSource(resolver);
        FlowDirection direction = rightToLeft ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
        await using RootLocalInlineImageDocument optimized = BuildDocument(
            seed, session, resolver, sourceText, direction, rasterizationScale, lazy: false);
        await using RootLocalInlineImageDocument fallback = BuildDocument(
            seed, session, resolver, sourceText, direction, rasterizationScale, lazy: false);

        InlineContainerBox optimizedInline = FindInlineImageRoot(optimized.Snapshot, expectedImages: 2);
        InlineContainerBox fallbackInline = FindInlineImageRoot(fallback.Snapshot, expectedImages: 2);
        ImageBox optimizedStandalone = Assert.Single(optimized.Snapshot.Blocks.OfType<ImageBox>());
        ImageBox fallbackStandalone = Assert.Single(fallback.Snapshot.Blocks.OfType<ImageBox>());
        InlineImageRun[] inlineRuns = optimizedInline.Runs.OfType<InlineImageRun>().ToArray();
        Assert.Equal(2, inlineRuns.Length);
        Assert.All(inlineRuns, run => Assert.Equal(optimizedInline.BlockIndex, run.Image.BlockIndex));

        await LoadImagesAsync(EnumerateImages(optimized.Snapshot));
        await LoadImagesAsync(EnumerateImages(fallback.Snapshot));

        BlockBox[] rootsBefore = optimized.Snapshot.Blocks.ToArray();
        Rect[] boundsBefore = rootsBefore.Select(static root => root.Bounds).ToArray();
        InlineRun[] runsBefore = optimizedInline.Runs.ToArray();
        RetainedImageOwners imagesBefore = RetainedImageOwners.Capture(
            inlineRuns.Select(static run => run.Image).Append(optimizedStandalone).ToArray());
        CanvasTextLayout inlineLayoutBefore = GetInlineLayout(optimizedInline);
        Size sizeBefore = optimized.Snapshot.Size;
        long revisionBefore = optimized.Snapshot.LayoutRevision;
        MarkdownSourceMap sourceMapBefore = optimized.Snapshot.SourceMap;
        MarkdownSemanticDocument semanticBefore = optimized.Snapshot.SemanticDocument;
        int[] changedBlockIndices = [optimizedInline.BlockIndex, optimizedStandalone.BlockIndex];
        int[] fallbackChangedBlockIndices = [fallbackInline.BlockIndex, fallbackStandalone.BlockIndex];
        Assert.NotEqual(changedBlockIndices[0], changedBlockIndices[1]);

        fallback.Snapshot.RelayoutChangedBlocks(
            fallbackChangedBlockIndices, LayoutWidth, CancellationToken.None);
        int[] resolverCountsBeforePublication = resolver.Sources
            .Select(resolver.ResolveCountFor).ToArray();

        MarkdownLayoutContext? constructionContext = null;
        try
        {
            PreparedImageRelayoutResult result = optimized.Snapshot.TryRelayoutCompletedImageRoots(
                changedBlockIndices, LayoutWidth, CancellationToken.None, out constructionContext,
                expectedPointerInputEpoch: optimized.Snapshot.PointerInputEpoch);

            Assert.Equal(PreparedImageRelayoutResult.Committed, result);
            Assert.Equal(revisionBefore + 1, optimized.Snapshot.LayoutRevision);
            Assert.NotEqual(sizeBefore, optimized.Snapshot.Size);
            Assert.Equal(fallback.Snapshot.Size, optimized.Snapshot.Size);
            Assert.Same(sourceMapBefore, optimized.Snapshot.SourceMap);
            Assert.Same(semanticBefore, optimized.Snapshot.SemanticDocument);
            Assert.Equal(AutomationGeneration, optimized.Snapshot.AutomationDocumentGeneration);
            Assert.NotSame(inlineLayoutBefore, GetInlineLayout(optimizedInline));
            Assert.Same(optimizedInline, optimized.Snapshot.Blocks.Single(
                root => root.BlockIndex == optimizedInline.BlockIndex));
            AssertRunsRetained(optimizedInline, runsBefore);
            AssertRootsMatchFallback(fallback.Snapshot, optimized.Snapshot, rootsBefore);
            AssertRootHeightChanged(rootsBefore, boundsBefore, optimizedInline);
            AssertRootHeightChanged(rootsBefore, boundsBefore, optimizedStandalone);
            imagesBefore.AssertRetained(optimized.Snapshot);
            Assert.Equal(resolverCountsBeforePublication,
                resolver.Sources.Select(resolver.ResolveCountFor).ToArray());
            Assert.Equal(Paint(optimized.Snapshot, seed.Device, rasterizationScale),
                Paint(fallback.Snapshot, seed.Device, rasterizationScale));
        }
        finally
        {
            await DrainAsync(constructionContext);
        }
    }

    [Fact]
    public async Task LazyInlineRelayoutLeavesPendingSiblingAndDistantEstimatedRootsUntouched()
    {
        await using var seed = new CompletedRasterPlacementCloneTests.PlacementFixture();
        using var session = new MarkdownPerformanceSession(MarkdownPerformanceOptions.Progressive);
        var resolver = new InlineRasterResolver(DisplaySizedRasterDecodeTests.CreatePng(96, 192));
        string sourceText = CreateLongLazySource(resolver);
        await using RootLocalInlineImageDocument optimized = BuildDocument(seed, session, resolver, sourceText,
            FlowDirection.LeftToRight, 1.5f, lazy: true, lazyViewportHeight: 520);
        await using RootLocalInlineImageDocument fallback = BuildDocument(seed, session, resolver, sourceText,
            FlowDirection.LeftToRight, 1.5f, lazy: true, lazyViewportHeight: 520);

        InlineContainerBox optimizedInline = FindInlineImageRoot(optimized.Snapshot, expectedImages: 2);
        InlineContainerBox fallbackInline = FindInlineImageRoot(fallback.Snapshot, expectedImages: 2);
        ImageBox optimizedPending = FindImage(optimized.Snapshot, resolver.PendingUrl);
        ImageBox fallbackPending = FindImage(fallback.Snapshot, resolver.PendingUrl);
        ImageBox optimizedFar = FindImage(optimized.Snapshot, resolver.FarUrl);
        ImageBox fallbackFar = FindImage(fallback.Snapshot, resolver.FarUrl);
        BlockBox optimizedPendingOwner = FindTopLevelOwner(optimized.Snapshot, optimizedPending);
        BlockBox optimizedFarOwner = FindTopLevelOwner(optimized.Snapshot, optimizedFar);
        BlockBox fallbackPendingOwner = FindTopLevelOwner(fallback.Snapshot, fallbackPending);
        BlockBox fallbackFarOwner = FindTopLevelOwner(fallback.Snapshot, fallbackFar);

        Assert.True(optimized.Snapshot.TopLevelBlockCount > 64);
        Assert.True(optimized.Snapshot.IsTopLevelBlockMeasured(optimizedInline));
        Assert.True(optimized.Snapshot.IsTopLevelBlockMeasured(optimizedPendingOwner));
        Assert.False(optimized.Snapshot.IsTopLevelBlockMeasured(optimizedFarOwner));
        Assert.True(optimized.Snapshot.MeasuredTopLevelBlockCount < optimized.Snapshot.TopLevelBlockCount);
        Assert.True(fallback.Snapshot.IsTopLevelBlockMeasured(fallbackPendingOwner));
        Assert.False(fallback.Snapshot.IsTopLevelBlockMeasured(fallbackFarOwner));

        await StartPendingImagesAsync(resolver, optimizedPending, fallbackPending);
        await LoadImagesAsync(FindInlineImages(optimizedInline));
        await LoadImagesAsync(FindInlineImages(fallbackInline));

        BlockBox[] rootsBefore = optimized.Snapshot.Blocks.ToArray();
        Rect[] boundsBefore = rootsBefore.Select(static root => root.Bounds).ToArray();
        long revisionBefore = optimized.Snapshot.LayoutRevision;
        long pendingResolveCount = resolver.ResolveCountFor(resolver.PendingUrl);
        long farResolveCount = resolver.ResolveCountFor(resolver.FarUrl);
        int pendingWorkBefore = optimizedPending.GetWorkState().ActiveWorkCount;
        int[] changedBlockIndices = [optimizedInline.BlockIndex];
        fallback.Snapshot.RelayoutChangedBlocks(
            [fallbackInline.BlockIndex], LayoutWidth, CancellationToken.None);

        MarkdownLayoutContext? constructionContext = null;
        try
        {
            PreparedImageRelayoutResult result = optimized.Snapshot.TryRelayoutCompletedImageRoots(
                changedBlockIndices, LayoutWidth, CancellationToken.None, out constructionContext,
                expectedPointerInputEpoch: optimized.Snapshot.PointerInputEpoch);

            Assert.Equal(PreparedImageRelayoutResult.Committed, result);
            Assert.Equal(revisionBefore + 1, optimized.Snapshot.LayoutRevision);
            Assert.Equal(fallback.Snapshot.Size, optimized.Snapshot.Size);
            Assert.Same(optimizedInline, optimized.Snapshot.Blocks.Single(
                root => ReferenceEquals(root, optimizedInline)));
            Assert.Same(optimizedPending, FindImage(optimized.Snapshot, resolver.PendingUrl));
            Assert.Same(optimizedFar, FindImage(optimized.Snapshot, resolver.FarUrl));
            Assert.True(optimizedPending.GetWorkState().ActiveWorkCount > 0);
            Assert.Equal(pendingWorkBefore, optimizedPending.GetWorkState().ActiveWorkCount);
            Assert.Null(optimizedPending.Bitmap);
            Assert.Equal(pendingResolveCount, resolver.ResolveCountFor(resolver.PendingUrl));
            Assert.Equal(farResolveCount, resolver.ResolveCountFor(resolver.FarUrl));
            Assert.False(optimized.Snapshot.IsTopLevelBlockMeasured(optimizedFarOwner));
            Assert.False(fallback.Snapshot.IsTopLevelBlockMeasured(fallbackFarOwner));
            AssertRootsMatchFallbackKeepingEstimatedOwner(
                fallback.Snapshot, optimized.Snapshot, rootsBefore, boundsBefore, optimizedFarOwner);
        }
        finally
        {
            await DrainAsync(constructionContext);
        }
    }

    [Fact]
    public async Task CancellationBeforeInlineBatchCommitLeavesSourcePlacementAndOwnersUntouched()
    {
        await using var seed = new CompletedRasterPlacementCloneTests.PlacementFixture();
        using var session = new MarkdownPerformanceSession(MarkdownPerformanceOptions.Progressive);
        var resolver = new InlineRasterResolver(DisplaySizedRasterDecodeTests.CreatePng(96, 192));
        await using RootLocalInlineImageDocument document = BuildDocument(seed, session, resolver,
            CreateInlineSource(resolver), FlowDirection.RightToLeft, 2f, lazy: false);
        InlineContainerBox inline = FindInlineImageRoot(document.Snapshot, expectedImages: 2);
        ImageBox[] images = FindInlineImages(inline).ToArray();
        foreach (ImageBox image in images)
            await LoadImageAsync(image);

        BlockBox[] rootsBefore = document.Snapshot.Blocks.ToArray();
        Rect[] boundsBefore = rootsBefore.Select(static root => root.Bounds).ToArray();
        Rect[] imageBoundsBefore = images.Select(static image => image.Bounds).ToArray();
        SharedCanvasBitmapCache.Lease[] leasesBefore = images.Select(GetBitmapLease).ToArray();
        CanvasBitmap[] bitmapsBefore = images.Select(image => Assert.IsType<CanvasBitmap>(image.Bitmap)).ToArray();
        InlineRun[] runsBefore = inline.Runs.ToArray();
        Size sizeBefore = document.Snapshot.Size;
        long revisionBefore = document.Snapshot.LayoutRevision;
        using var cancellation = new CancellationTokenSource();
        MarkdownLayoutContext? constructionContext = null;

        try
        {
            Assert.Throws<OperationCanceledException>(() => document.Snapshot.TryRelayoutCompletedImageRoots(
                [inline.BlockIndex],
                LayoutWidth,
                cancellation.Token,
                out constructionContext,
                phaseHooks: CompletedImageRelayoutPhaseHooks.BeforeMeasure(cancellation.Cancel),
                expectedPointerInputEpoch: document.Snapshot.PointerInputEpoch));

            Assert.True(cancellation.IsCancellationRequested);
            Assert.Equal(revisionBefore, document.Snapshot.LayoutRevision);
            Assert.Equal(sizeBefore, document.Snapshot.Size);
            Assert.Equal(runsBefore.Length, inline.Runs.Count);
            for (int index = 0; index < runsBefore.Length; index++)
                Assert.Same(runsBefore[index], inline.Runs[index]);
            AssertRootsUnchanged(document.Snapshot, rootsBefore, boundsBefore);
            for (int index = 0; index < images.Length; index++)
            {
                Assert.Equal(imageBoundsBefore[index], images[index].Bounds);
                Assert.Same(leasesBefore[index], GetBitmapLease(images[index]));
                Assert.Same(bitmapsBefore[index], images[index].Bitmap);
            }
        }
        finally
        {
            await DrainAsync(constructionContext);
        }
    }

    [Fact]
    public async Task PointerInputDuringInlinePrivateMeasureRetriesWithoutPlacementMutation()
    {
        await using var seed = new CompletedRasterPlacementCloneTests.PlacementFixture();
        using var session = new MarkdownPerformanceSession(MarkdownPerformanceOptions.Progressive);
        var resolver = new InlineRasterResolver(DisplaySizedRasterDecodeTests.CreatePng(96, 192));
        await using RootLocalInlineImageDocument document = BuildDocument(seed, session, resolver,
            CreateInlineSource(resolver), FlowDirection.LeftToRight, 1.5f, lazy: false);
        InlineContainerBox inline = FindInlineImageRoot(document.Snapshot, expectedImages: 2);
        ImageBox[] images = FindInlineImages(inline).ToArray();
        foreach (ImageBox image in images)
            await LoadImageAsync(image);

        Rect[] rootsBefore = document.Snapshot.Blocks.Select(static root => root.Bounds).ToArray();
        Rect[] imagesBefore = images.Select(static image => image.Bounds).ToArray();
        long revisionBefore = document.Snapshot.LayoutRevision;
        long pointerEpochBefore = document.Snapshot.PointerInputEpoch;
        Size sizeBefore = document.Snapshot.Size;
        MarkdownLayoutContext? constructionContext = null;
        try
        {
            PreparedImageRelayoutResult result = document.Snapshot.TryRelayoutCompletedImageRoots(
                [inline.BlockIndex],
                LayoutWidth,
                CancellationToken.None,
                out constructionContext,
                phaseHooks: CompletedImageRelayoutPhaseHooks.BeforeMeasure(document.Snapshot.NotifyPointerInput),
                expectedPointerInputEpoch: pointerEpochBefore);

            Assert.Equal(PreparedImageRelayoutResult.Retry, result);
            Assert.Equal(pointerEpochBefore + 1, document.Snapshot.PointerInputEpoch);
            Assert.Equal(revisionBefore, document.Snapshot.LayoutRevision);
            Assert.Equal(sizeBefore, document.Snapshot.Size);
            Assert.Equal(rootsBefore,
                document.Snapshot.Blocks.Select(static root => root.Bounds).ToArray());
            Assert.Equal(imagesBefore, images.Select(static image => image.Bounds).ToArray());
        }
        finally
        {
            await DrainAsync(constructionContext);
        }
    }

    [Fact]
    public async Task StaleInlineSourceRevisionRetriesAfterExternalRelayoutWithoutExtraMutation()
    {
        await using var seed = new CompletedRasterPlacementCloneTests.PlacementFixture();
        using var session = new MarkdownPerformanceSession(MarkdownPerformanceOptions.Progressive);
        var resolver = new InlineRasterResolver(DisplaySizedRasterDecodeTests.CreatePng(96, 192));
        await using RootLocalInlineImageDocument document = BuildDocument(seed, session, resolver,
            CreateInlineSource(resolver), FlowDirection.RightToLeft, 1f, lazy: false);
        InlineContainerBox inline = FindInlineImageRoot(document.Snapshot, expectedImages: 2);
        foreach (ImageBox image in FindInlineImages(inline))
            await LoadImageAsync(image);

        long callbackRevision = -1;
        Rect[]? callbackBounds = null;
        MarkdownLayoutContext? constructionContext = null;
        try
        {
            PreparedImageRelayoutResult result = document.Snapshot.TryRelayoutCompletedImageRoots(
                [inline.BlockIndex],
                LayoutWidth,
                CancellationToken.None,
                out constructionContext,
                phaseHooks: CompletedImageRelayoutPhaseHooks.BeforeMeasure(() =>
                {
                    document.Snapshot.RelayoutChangedBlocks(
                        [inline.BlockIndex], LayoutWidth, CancellationToken.None);
                    callbackRevision = document.Snapshot.LayoutRevision;
                    callbackBounds = document.Snapshot.Blocks.Select(static root => root.Bounds).ToArray();
                }),
                expectedPointerInputEpoch: document.Snapshot.PointerInputEpoch);

            Assert.Equal(PreparedImageRelayoutResult.Retry, result);
            Assert.True(callbackRevision > 0);
            Assert.Equal(callbackRevision, document.Snapshot.LayoutRevision);
            Assert.Equal(callbackBounds,
                document.Snapshot.Blocks.Select(static root => root.Bounds).ToArray());
            Assert.Same(inline, document.Snapshot.Blocks.Single(
                root => ReferenceEquals(root, inline)));
            Assert.All(FindInlineImages(inline), image => Assert.NotNull(image.Bitmap));
        }
        finally
        {
            await DrainAsync(constructionContext);
        }
    }

    [Fact]
    public async Task InlineImageCountLimitRejectsBeforeAnySourcePlacementOrResolverChange()
    {
        await using var seed = new CompletedRasterPlacementCloneTests.PlacementFixture();
        using var session = new MarkdownPerformanceSession(MarkdownPerformanceOptions.Progressive);
        var resolver = new InlineRasterResolver(DisplaySizedRasterDecodeTests.CreatePng(32, 64));
        string sourceText = string.Join(" ", Enumerable.Range(0, 33)
            .Select(index => $"![cap image {index}]({resolver.InlineFirstUrl})"));
        await using RootLocalInlineImageDocument document = BuildDocument(seed, session, resolver,
            sourceText, FlowDirection.LeftToRight, 1f, lazy: false);
        InlineContainerBox inline = FindInlineImageRoot(document.Snapshot, expectedImages: 33);
        ImageBox[] images = FindInlineImages(inline).ToArray();
        Assert.Equal(33, images.Length);
        foreach (ImageBox image in images)
            await LoadImageAsync(image);

        BlockBox[] rootsBefore = document.Snapshot.Blocks.ToArray();
        Rect[] boundsBefore = rootsBefore.Select(static root => root.Bounds).ToArray();
        Rect[] imageBoundsBefore = images.Select(static image => image.Bounds).ToArray();
        CanvasBitmap[] bitmapsBefore = images.Select(image => Assert.IsType<CanvasBitmap>(image.Bitmap)).ToArray();
        InlineRun[] runsBefore = inline.Runs.ToArray();
        Size sizeBefore = document.Snapshot.Size;
        long revisionBefore = document.Snapshot.LayoutRevision;
        int resolverCountBefore = resolver.ResolveCountFor(resolver.InlineFirstUrl);
        MarkdownLayoutContext? constructionContext = null;
        try
        {
            PreparedImageRelayoutResult result = document.Snapshot.TryRelayoutCompletedImageRoots(
                [inline.BlockIndex], LayoutWidth, CancellationToken.None, out constructionContext,
                expectedPointerInputEpoch: document.Snapshot.PointerInputEpoch);

            Assert.Equal(PreparedImageRelayoutResult.Unavailable, result);
            Assert.Equal(revisionBefore, document.Snapshot.LayoutRevision);
            Assert.Equal(sizeBefore, document.Snapshot.Size);
            Assert.Equal(resolverCountBefore, resolver.ResolveCountFor(resolver.InlineFirstUrl));
            AssertRootsUnchanged(document.Snapshot, rootsBefore, boundsBefore);
            Assert.Equal(runsBefore.Length, inline.Runs.Count);
            for (int index = 0; index < runsBefore.Length; index++)
                Assert.Same(runsBefore[index], inline.Runs[index]);
            for (int index = 0; index < images.Length; index++)
            {
                Assert.Equal(imageBoundsBefore[index], images[index].Bounds);
                Assert.Same(bitmapsBefore[index], images[index].Bitmap);
            }
        }
        finally
        {
            await DrainAsync(constructionContext);
        }
    }

    [Fact]
    public async Task ListWithUnsupportedCodeChildRejectsCompletedImageWithoutPlacementMutation()
    {
        await using var seed = new CompletedRasterPlacementCloneTests.PlacementFixture();
        using var session = new MarkdownPerformanceSession(MarkdownPerformanceOptions.Progressive);
        var resolver = new InlineRasterResolver(DisplaySizedRasterDecodeTests.CreatePng(96, 192));
        string sourceText = $"- item text ![list image]({resolver.InlineFirstUrl})\n\n" +
            "  ```text\n  A code child retains the protected reflow path.\n  ```\n\n" +
            "A trailing paragraph.\n";
        await using RootLocalInlineImageDocument document = BuildDocument(seed, session, resolver,
            sourceText, FlowDirection.LeftToRight, 1f, lazy: false);
        ImageBox image = FindImage(document.Snapshot, resolver.InlineFirstUrl);
        await LoadImageAsync(image);
        BlockBox owner = FindTopLevelOwner(document.Snapshot, image);
        Assert.IsNotType<InlineContainerBox>(owner);
        var list = Assert.IsType<StackBox>(owner);
        var item = Assert.IsType<ListItemBox>(Assert.Single(list.Children));
        Assert.Contains(item.Content.Children, static child => child is CodeBlockBox);

        BlockBox[] rootsBefore = document.Snapshot.Blocks.ToArray();
        Rect[] boundsBefore = rootsBefore.Select(static root => root.Bounds).ToArray();
        Rect imageBoundsBefore = image.Bounds;
        CanvasBitmap bitmapBefore = Assert.IsType<CanvasBitmap>(image.Bitmap);
        long revisionBefore = document.Snapshot.LayoutRevision;
        Size sizeBefore = document.Snapshot.Size;
        MarkdownLayoutContext? constructionContext = null;
        try
        {
            PreparedImageRelayoutResult result = document.Snapshot.TryRelayoutCompletedImageRoots(
                [image.BlockIndex], LayoutWidth, CancellationToken.None, out constructionContext,
                expectedPointerInputEpoch: document.Snapshot.PointerInputEpoch);

            Assert.Equal(PreparedImageRelayoutResult.Unavailable, result);
            Assert.Equal(revisionBefore, document.Snapshot.LayoutRevision);
            Assert.Equal(sizeBefore, document.Snapshot.Size);
            Assert.Equal(imageBoundsBefore, image.Bounds);
            Assert.Same(bitmapBefore, image.Bitmap);
            AssertRootsUnchanged(document.Snapshot, rootsBefore, boundsBefore);
        }
        finally
        {
            await DrainAsync(constructionContext);
        }
    }

    private static RootLocalInlineImageDocument BuildDocument(
        CompletedRasterPlacementCloneTests.PlacementFixture seed,
        MarkdownPerformanceSession session,
        IMarkdownImageResolver resolver,
        string sourceText,
        FlowDirection direction,
        float rasterizationScale,
        bool lazy,
        float lazyViewportHeight = 120,
        IMarkdownSvgRenderer? svgRenderer = null) =>
        RootLocalInlineImageDocument.Build(seed, session, resolver, sourceText, direction,
            rasterizationScale, lazy, lazyViewportHeight, svgRenderer);
}
