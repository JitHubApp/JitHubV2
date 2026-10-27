using System.Collections.Concurrent;
using System.Buffers;
using System.Reflection;
using System.Text;
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
using Microsoft.Graphics.Canvas.Text;
using Microsoft.UI.Xaml;
using Windows.Foundation;
using Windows.Graphics.DirectX;
using Windows.UI;
using Xunit;

namespace MarkdownRenderer.GitHub.Tests;

[Collection(SharedCanvasBitmapCacheTestCollection.Name)]
public sealed class RootLocalInlineImageRelayoutTests
{
    private const float LayoutWidth = 320f;
    private const long AutomationGeneration = 8_421;

    [Fact]
    public async Task FullSvgInlineBatchPreservesRasterMetadataAndReadsProviderOutsideSourceGates()
    {
        await using var seed = new CompletedRasterPlacementCloneTests.PlacementFixture();
        using var session = new MarkdownPerformanceSession(MarkdownPerformanceOptions.Progressive);
        var resolver = new InlineSvgResolver();
        var renderer = new InlineSvgRenderer();
        string sourceText = $"Before **bold** ![full SVG badge]({resolver.Url}) after.\n\nTail text.\n";
        await using ImageDocument optimized = BuildDocument(seed, session, resolver, sourceText,
            FlowDirection.LeftToRight, 1.5f, lazy: false, svgRenderer: renderer);
        await using ImageDocument fallback = BuildDocument(seed, session, resolver, sourceText,
            FlowDirection.LeftToRight, 1.5f, lazy: false, svgRenderer: renderer);

        InlineContainerBox inline = FindInlineImageRoot(optimized.Snapshot, expectedImages: 1);
        InlineContainerBox fallbackInline = FindInlineImageRoot(fallback.Snapshot, expectedImages: 1);
        InlineImageRun run = Assert.Single(inline.Runs.OfType<InlineImageRun>());
        ImageBox image = run.Image;
        ImageBox fallbackImage = Assert.Single(fallbackInline.Runs.OfType<InlineImageRun>()).Image;
        await LoadImageAsync(image);
        await LoadImageAsync(fallbackImage);

        Assert.True(image.IsSvg);
        CanvasBitmap bitmap = Assert.IsType<CanvasBitmap>(image.Bitmap);
        SharedCanvasBitmapCache.Lease lease = GetBitmapLease(image);
        Assert.Same(bitmap, lease.Bitmap);
        Assert.True(lease.IsBitmapOutputAdmitted);
        long leaseWeight = lease.PixelWeightBytes;
        MarkdownSvgDocumentInfo info = ReadField<MarkdownSvgDocumentInfo>(image, "_svgInfo");
        Size intrinsic = ReadField<Size>(image, "_svgIntrinsicSize");
        string? title = image.SvgTitle;
        string? description = image.SvgDesc;
        (int Width, int Height) rasterSize = image.SvgRasterPixelSize;
        Assert.True(rasterSize.Width > 0 && rasterSize.Height > 0);
        Assert.Null(ReadRawField(image, "_svgDocument"));
        Assert.Null(ReadRawField(image, "_svgTileRenderPlan"));
        Assert.Empty(Assert.IsAssignableFrom<System.Collections.IDictionary>(
            ReadRawField(image, "_svgTiles")));
        Assert.True(renderer.FullRenderCount > 0);
        Assert.Equal(0, renderer.TiledRenderCount);

        fallback.Snapshot.RelayoutChangedBlocks(
            [fallbackInline.BlockIndex], LayoutWidth, CancellationToken.None);
        object layoutLock = ReadField<object>(optimized.Snapshot, "_layoutLock");
        object imageLock = ReadField<object>(image, "_svgWorkSync");
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
            Assert.Same(bitmap, image.Bitmap);
            Assert.Equal(info, ReadField<MarkdownSvgDocumentInfo>(image, "_svgInfo"));
            Assert.Equal(intrinsic, ReadField<Size>(image, "_svgIntrinsicSize"));
            Assert.Equal(title, image.SvgTitle);
            Assert.Equal(description, image.SvgDesc);
            Assert.Equal(rasterSize, image.SvgRasterPixelSize);
            SharedCanvasBitmapCache.Lease currentLease = GetBitmapLease(image);
            Assert.Same(lease, currentLease);
            Assert.Same(bitmap, currentLease.Bitmap);
            Assert.True(currentLease.IsBitmapOutputAdmitted);
            Assert.Equal(leaseWeight, currentLease.PixelWeightBytes);
            Assert.Equal(fallbackBounds,
                fallback.Snapshot.Blocks.Select(static root => root.Bounds).ToArray());
            Assert.Equal(Paint(optimized.Snapshot, seed.Device, 1.5f),
                Paint(fallback.Snapshot, seed.Device, 1.5f));
        }
        finally
        {
            renderer.GenerationReadHook = null;
            if (constructionContext is not null)
                await constructionContext.DrainOwnedRetirementsAsync().WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    [Fact]
    public async Task TextSvgInlineBatchRejectsStaleFontGenerationWithoutChangingSourcePlacement()
    {
        await using var seed = new CompletedRasterPlacementCloneTests.PlacementFixture();
        using var session = new MarkdownPerformanceSession(MarkdownPerformanceOptions.Progressive);
        var resolver = new InlineSvgResolver();
        var renderer = new InlineSvgRenderer();
        await using ImageDocument document = BuildDocument(seed, session, resolver,
            $"Text ![font-dependent SVG]({resolver.Url}) tail.\n",
            FlowDirection.RightToLeft, 1f, lazy: false, svgRenderer: renderer);
        InlineContainerBox inline = FindInlineImageRoot(document.Snapshot, expectedImages: 1);
        InlineImageRun run = Assert.Single(inline.Runs.OfType<InlineImageRun>());
        ImageBox image = run.Image;
        await LoadImageAsync(image);
        Assert.True(image.IsSvg);
        Assert.True(ReadField<MarkdownSvgDocumentInfo>(image, "_svgInfo").HasText);

        BlockBox[] rootsBefore = document.Snapshot.Blocks.ToArray();
        Rect[] boundsBefore = rootsBefore.Select(static root => root.Bounds).ToArray();
        Rect imageBoundsBefore = image.Bounds;
        CanvasBitmap bitmapBefore = Assert.IsType<CanvasBitmap>(image.Bitmap);
        SharedCanvasBitmapCache.Lease leaseBefore = GetBitmapLease(image);
        MarkdownSvgDocumentInfo infoBefore = ReadField<MarkdownSvgDocumentInfo>(image, "_svgInfo");
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
            Assert.Equal(infoBefore, ReadField<MarkdownSvgDocumentInfo>(image, "_svgInfo"));
        }
        finally
        {
            if (constructionContext is not null)
                await constructionContext.DrainOwnedRetirementsAsync().WaitAsync(TimeSpan.FromSeconds(10));
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
        await using ImageDocument optimized = BuildDocument(seed, session, resolver, sourceText,
            FlowDirection.LeftToRight, 1.5f, lazy: false);
        await using ImageDocument fallback = BuildDocument(seed, session, resolver, sourceText,
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

        float desiredWidth = run.DesiredWidth;
        float desiredHeight = run.DesiredHeight;
        Assert.True(desiredWidth > 2f);
        Rect priorImageBounds = image.Bounds;
        image.SetInlineBounds(new Rect(
            priorImageBounds.X,
            priorImageBounds.Y,
            desiredWidth / 2f,
            desiredHeight));
        Assert.Equal(desiredWidth, run.DesiredWidth);
        Assert.Equal(desiredWidth / 2f, image.MeasuredImageWidth);
        Assert.Equal(desiredHeight, image.MeasuredImageHeight);
        Assert.True(inline.HasMeasuredLayout);

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
            Assert.Same(standalone, optimized.Snapshot.Blocks.Single(
                root => ReferenceEquals(root, standalone)));
            Assert.Same(inline, optimized.Snapshot.Blocks.Single(
                root => ReferenceEquals(root, inline)));
            Assert.NotSame(oldInlineLayout, GetInlineLayout(inline));
            Assert.Equal(runsBefore.Length, inline.Runs.Count);
            for (int index = 0; index < runsBefore.Length; index++)
                Assert.Same(runsBefore[index], inline.Runs[index]);
            Assert.Same(image, run.Image);
            Assert.Same(inlineBitmap, image.Bitmap);
            Assert.Same(inlineLease, GetBitmapLease(image));
            Assert.Same(standaloneBitmap, standalone.Bitmap);
            Assert.Same(standaloneLease, GetBitmapLease(standalone));
            Assert.Equal(fallbackRun.DesiredWidth, run.DesiredWidth);
            Assert.Equal(fallbackRun.DesiredHeight, run.DesiredHeight);
            Assert.Equal(fallbackImage.MeasuredImageWidth, image.MeasuredImageWidth);
            Assert.Equal(fallbackImage.MeasuredImageHeight, image.MeasuredImageHeight);
            Assert.Equal(
                LayoutPlacementTestGeometry.ToLogical(fallback.Snapshot, fallback.Snapshot.Blocks),
                LayoutPlacementTestGeometry.ToLogical(optimized.Snapshot, optimized.Snapshot.Blocks));
            Assert.Equal(resolverCountsBefore,
                resolverSources.Select(resolver.ResolveCountFor).ToArray());
            // The control synchronizes inline bounds during image-plan collection,
            // before paint. Verify that path explicitly so Paint cannot repair a
            // stale local placement and conceal a broken publication/index.
            static (InlineImageRun Run, Rect Rect) CollectInlineRegionUnderProductionPlacementScope(
                LayoutSnapshot snapshot,
                InlineContainerBox root)
            {
                Assert.True(snapshot.TryBeginPaint());
                try
                {
                    using LayoutPlacementTransformContext.Scope placementScope =
                        snapshot.EnterPlacementTransformScopeForTopLevelBlockUnderPaint(root);
                    return Assert.Single(root.EnumerateInlineImageRects());
                }
                finally
                {
                    snapshot.EndPaint();
                }
            }

            var publishedRegion = CollectInlineRegionUnderProductionPlacementScope(
                optimized.Snapshot, inline);
            var fallbackRegion = CollectInlineRegionUnderProductionPlacementScope(
                fallback.Snapshot, fallbackInline);
            Assert.Same(run, publishedRegion.Run);
            Assert.Equal(
                LayoutPlacementTestGeometry.ToLogical(fallback.Snapshot, fallbackInline.BlockIndex, [fallbackRegion.Rect]),
                LayoutPlacementTestGeometry.ToLogical(optimized.Snapshot, inline.BlockIndex, [publishedRegion.Rect]));
            Assert.Equal(LayoutPlacementTestGeometry.ToLogical(optimized.Snapshot, image),
                LayoutPlacementTestGeometry.ToLogical(optimized.Snapshot, inline.BlockIndex, [publishedRegion.Rect])[0]);
            var publishedPlan = new RasterPlacementImagePlan(optimized.Snapshot,
                optimized.Snapshot.Blocks, expectedCount: 2);
            Assert.Same(standalone, publishedPlan.PublicationImages[0]);
            Assert.Same(image, publishedPlan.PublicationImages[1]);
            Assert.Equal(1, publishedPlan.Index.GetBlockOrdinal(1));
            Rect logicalImageBounds = LayoutPlacementTestGeometry.ToLogical(optimized.Snapshot, image);
            Assert.Equal((logicalImageBounds.Top, logicalImageBounds.Bottom),
                publishedPlan.Index.GetBounds(1));
            Assert.Equal(Paint(optimized.Snapshot, seed.Device, 1.5f),
                Paint(fallback.Snapshot, seed.Device, 1.5f));
        }
        finally
        {
            if (constructionContext is not null)
                await constructionContext.DrainOwnedRetirementsAsync().WaitAsync(TimeSpan.FromSeconds(10));
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
        await using ImageDocument document = BuildDocument(seed, session, resolver, sourceText,
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
                beforeMeasureOutsideGateForTests: () =>
                {
                    mutationRan = true;
                    lastInlineRun.InlineIndex = originalInlineIndex + 1;
                },
                expectedPointerInputEpoch: document.Snapshot.PointerInputEpoch);
        }
        finally
        {
            // Restore the source run even if an assertion or the private measure
            // path fails, so snapshot retirement sees a structurally valid tree.
            lastInlineRun.InlineIndex = originalInlineIndex;
            if (constructionContext is not null)
                await constructionContext.DrainOwnedRetirementsAsync().WaitAsync(TimeSpan.FromSeconds(10));
        }

        Assert.Equal(PreparedImageRelayoutResult.Retry, result);
        Assert.True(mutationRan);
        Assert.Equal(revisionBefore, document.Snapshot.LayoutRevision);
        Assert.Equal(sizeBefore, document.Snapshot.Size);
        Assert.Equal(rootsBefore.Length, document.Snapshot.Blocks.Count);
        for (int index = 0; index < rootsBefore.Length; index++)
        {
            Assert.Same(rootsBefore[index], document.Snapshot.Blocks[index]);
            Assert.Equal(boundsBefore[index], document.Snapshot.Blocks[index].Bounds);
        }
        Assert.Equal(standaloneBoundsBefore, standalone.Bounds);
        Assert.Equal(inlineBoundsBefore, inline.Bounds);
        Assert.Equal(imageBoundsBefore, image.Bounds);
        Assert.Same(inlineLayout, GetInlineLayout(inline));
        Assert.Same(lastInlineRun, inline.Runs[^1]);
        Assert.Equal(originalInlineIndex, lastInlineRun.InlineIndex);
        Assert.Same(standalone, document.Snapshot.Blocks.Single(
            root => ReferenceEquals(root, standalone)));
        Assert.Same(inline, document.Snapshot.Blocks.Single(
            root => ReferenceEquals(root, inline)));
        Assert.Equal(standaloneWidthBefore, standalone.MeasuredImageWidth);
        Assert.Equal(standaloneHeightBefore, standalone.MeasuredImageHeight);
        Assert.Equal(inlineWidthBefore, image.MeasuredImageWidth);
        Assert.Equal(inlineHeightBefore, image.MeasuredImageHeight);
        Assert.Same(standaloneBitmap, standalone.Bitmap);
        Assert.Same(standaloneLease, GetBitmapLease(standalone));
        Assert.Same(inlineBitmap, image.Bitmap);
        Assert.Same(inlineLease, GetBitmapLease(image));
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
        await using ImageDocument optimized = BuildDocument(
            seed, session, resolver, sourceText, direction, rasterizationScale, lazy: false);
        await using ImageDocument fallback = BuildDocument(
            seed, session, resolver, sourceText, direction, rasterizationScale, lazy: false);

        InlineContainerBox optimizedInline = FindInlineImageRoot(optimized.Snapshot, expectedImages: 2);
        InlineContainerBox fallbackInline = FindInlineImageRoot(fallback.Snapshot, expectedImages: 2);
        ImageBox optimizedStandalone = Assert.Single(optimized.Snapshot.Blocks.OfType<ImageBox>());
        ImageBox fallbackStandalone = Assert.Single(fallback.Snapshot.Blocks.OfType<ImageBox>());
        InlineImageRun[] inlineRuns = optimizedInline.Runs.OfType<InlineImageRun>().ToArray();
        Assert.Equal(2, inlineRuns.Length);
        Assert.All(inlineRuns, run => Assert.Equal(optimizedInline.BlockIndex, run.Image.BlockIndex));

        foreach (ImageBox image in EnumerateImages(optimized.Snapshot))
            await LoadImageAsync(image);
        foreach (ImageBox image in EnumerateImages(fallback.Snapshot))
            await LoadImageAsync(image);

        BlockBox[] rootsBefore = optimized.Snapshot.Blocks.ToArray();
        Rect[] boundsBefore = rootsBefore.Select(static root => root.Bounds).ToArray();
        InlineRun[] runsBefore = optimizedInline.Runs.ToArray();
        ImageBox[] imagesBefore = inlineRuns.Select(static run => run.Image)
            .Append(optimizedStandalone).ToArray();
        CanvasBitmap[] bitmapsBefore = imagesBefore
            .Select(image => Assert.IsType<CanvasBitmap>(image.Bitmap)).ToArray();
        bool[] imageDirtyBefore = imagesBefore.Select(static image => image.IsDirty).ToArray();
        SharedCanvasBitmapCache.Lease[] leasesBefore = imagesBefore.Select(GetBitmapLease).ToArray();
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
                changedBlockIndices,
                LayoutWidth,
                CancellationToken.None,
                out constructionContext,
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
            Assert.Equal(runsBefore.Length, optimizedInline.Runs.Count);
            for (int index = 0; index < runsBefore.Length; index++)
                Assert.Same(runsBefore[index], optimizedInline.Runs[index]);
            for (int index = 0; index < rootsBefore.Length; index++)
            {
                Assert.Same(rootsBefore[index], optimized.Snapshot.Blocks[index]);
            Assert.Equal(LayoutPlacementTestGeometry.ToLogical(fallback.Snapshot, fallback.Snapshot.Blocks[index]),
                LayoutPlacementTestGeometry.ToLogical(optimized.Snapshot, optimized.Snapshot.Blocks[index]));
            }
            int inlineOrdinal = Array.FindIndex(rootsBefore,
                root => ReferenceEquals(root, optimizedInline));
            int standaloneOrdinal = Array.FindIndex(rootsBefore,
                root => ReferenceEquals(root, optimizedStandalone));
            Assert.True(inlineOrdinal >= 0);
            Assert.True(standaloneOrdinal >= 0);
            Assert.NotEqual(boundsBefore[inlineOrdinal].Height, optimizedInline.Bounds.Height);
            Assert.NotEqual(boundsBefore[standaloneOrdinal].Height, optimizedStandalone.Bounds.Height);
            for (int index = 0; index < imagesBefore.Length; index++)
            {
                Assert.Same(imagesBefore[index], EnumerateImages(optimized.Snapshot).ElementAt(index));
                Assert.Same(bitmapsBefore[index], imagesBefore[index].Bitmap);
                Assert.Same(leasesBefore[index], GetBitmapLease(imagesBefore[index]));
                Assert.Equal(imageDirtyBefore[index], imagesBefore[index].IsDirty);
            }
            Assert.Equal(resolverCountsBeforePublication,
                resolver.Sources.Select(resolver.ResolveCountFor).ToArray());
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
    public async Task LazyInlineRelayoutLeavesPendingSiblingAndDistantEstimatedRootsUntouched()
    {
        await using var seed = new CompletedRasterPlacementCloneTests.PlacementFixture();
        using var session = new MarkdownPerformanceSession(MarkdownPerformanceOptions.Progressive);
        var resolver = new InlineRasterResolver(DisplaySizedRasterDecodeTests.CreatePng(96, 192));
        string sourceText = CreateLongLazySource(resolver);
        await using ImageDocument optimized = BuildDocument(seed, session, resolver, sourceText,
            FlowDirection.LeftToRight, 1.5f, lazy: true, lazyViewportHeight: 520);
        await using ImageDocument fallback = BuildDocument(seed, session, resolver, sourceText,
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

        optimizedPending.EnsureLoading();
        fallbackPending.EnsureLoading();
        await resolver.WaitForPendingResolveAsync().WaitAsync(TimeSpan.FromSeconds(10));
        await DisplaySizedRasterDecodeTests.WaitUntilAsync(
            () => optimizedPending.ActiveImageWorkCountForTests > 0 &&
                  fallbackPending.ActiveImageWorkCountForTests > 0 &&
                  resolver.ResolveCountFor(resolver.PendingUrl) >= 2);
        foreach (ImageBox image in FindInlineImages(optimizedInline))
            await LoadImageAsync(image);
        foreach (ImageBox image in FindInlineImages(fallbackInline))
            await LoadImageAsync(image);

        BlockBox[] rootsBefore = optimized.Snapshot.Blocks.ToArray();
        Rect[] boundsBefore = rootsBefore.Select(static root => root.Bounds).ToArray();
        long revisionBefore = optimized.Snapshot.LayoutRevision;
        long pendingResolveCount = resolver.ResolveCountFor(resolver.PendingUrl);
        long farResolveCount = resolver.ResolveCountFor(resolver.FarUrl);
        int pendingWorkBefore = optimizedPending.ActiveImageWorkCountForTests;
        int[] changedBlockIndices = [optimizedInline.BlockIndex];
        fallback.Snapshot.RelayoutChangedBlocks(
            [fallbackInline.BlockIndex], LayoutWidth, CancellationToken.None);

        MarkdownLayoutContext? constructionContext = null;
        try
        {
            PreparedImageRelayoutResult result = optimized.Snapshot.TryRelayoutCompletedImageRoots(
                changedBlockIndices,
                LayoutWidth,
                CancellationToken.None,
                out constructionContext,
                expectedPointerInputEpoch: optimized.Snapshot.PointerInputEpoch);

            Assert.Equal(PreparedImageRelayoutResult.Committed, result);
            Assert.Equal(revisionBefore + 1, optimized.Snapshot.LayoutRevision);
            Assert.Equal(fallback.Snapshot.Size, optimized.Snapshot.Size);
            Assert.Same(optimizedInline, optimized.Snapshot.Blocks.Single(
                root => ReferenceEquals(root, optimizedInline)));
            Assert.Same(optimizedPending, FindImage(optimized.Snapshot, resolver.PendingUrl));
            Assert.Same(optimizedFar, FindImage(optimized.Snapshot, resolver.FarUrl));
            Assert.True(optimizedPending.ActiveImageWorkCountForTests > 0);
            Assert.Equal(pendingWorkBefore, optimizedPending.ActiveImageWorkCountForTests);
            Assert.Null(optimizedPending.Bitmap);
            Assert.Equal(pendingResolveCount, resolver.ResolveCountFor(resolver.PendingUrl));
            Assert.Equal(farResolveCount, resolver.ResolveCountFor(resolver.FarUrl));
            Assert.False(optimized.Snapshot.IsTopLevelBlockMeasured(optimizedFarOwner));
            Assert.False(fallback.Snapshot.IsTopLevelBlockMeasured(fallbackFarOwner));
            for (int ordinal = 0; ordinal < rootsBefore.Length; ordinal++)
            {
                Assert.Same(rootsBefore[ordinal], optimized.Snapshot.Blocks[ordinal]);
                Assert.Equal(LayoutPlacementTestGeometry.ToLogical(fallback.Snapshot, fallback.Snapshot.Blocks[ordinal]),
                    LayoutPlacementTestGeometry.ToLogical(optimized.Snapshot, optimized.Snapshot.Blocks[ordinal]));
                if (ReferenceEquals(rootsBefore[ordinal], optimizedFarOwner))
                {
                    Assert.Equal(boundsBefore[ordinal].X,
                        optimized.Snapshot.Blocks[ordinal].Bounds.X);
                    Assert.Equal(boundsBefore[ordinal].Width,
                        optimized.Snapshot.Blocks[ordinal].Bounds.Width);
                    Assert.Equal(boundsBefore[ordinal].Height,
                        optimized.Snapshot.Blocks[ordinal].Bounds.Height);
                }
            }
        }
        finally
        {
            if (constructionContext is not null)
                await constructionContext.DrainOwnedRetirementsAsync().WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    [Fact]
    public async Task CancellationBeforeInlineBatchCommitLeavesSourcePlacementAndOwnersUntouched()
    {
        await using var seed = new CompletedRasterPlacementCloneTests.PlacementFixture();
        using var session = new MarkdownPerformanceSession(MarkdownPerformanceOptions.Progressive);
        var resolver = new InlineRasterResolver(DisplaySizedRasterDecodeTests.CreatePng(96, 192));
        await using ImageDocument document = BuildDocument(seed, session, resolver,
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
                beforeMeasureOutsideGateForTests: cancellation.Cancel,
                expectedPointerInputEpoch: document.Snapshot.PointerInputEpoch));

            Assert.True(cancellation.IsCancellationRequested);
            Assert.Equal(revisionBefore, document.Snapshot.LayoutRevision);
            Assert.Equal(sizeBefore, document.Snapshot.Size);
            Assert.Equal(runsBefore.Length, inline.Runs.Count);
            for (int index = 0; index < runsBefore.Length; index++)
                Assert.Same(runsBefore[index], inline.Runs[index]);
            for (int index = 0; index < rootsBefore.Length; index++)
            {
                Assert.Same(rootsBefore[index], document.Snapshot.Blocks[index]);
                Assert.Equal(boundsBefore[index], document.Snapshot.Blocks[index].Bounds);
            }
            for (int index = 0; index < images.Length; index++)
            {
                Assert.Equal(imageBoundsBefore[index], images[index].Bounds);
                Assert.Same(leasesBefore[index], GetBitmapLease(images[index]));
                Assert.Same(bitmapsBefore[index], images[index].Bitmap);
            }
        }
        finally
        {
            if (constructionContext is not null)
                await constructionContext.DrainOwnedRetirementsAsync().WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    [Fact]
    public async Task PointerInputDuringInlinePrivateMeasureRetriesWithoutPlacementMutation()
    {
        await using var seed = new CompletedRasterPlacementCloneTests.PlacementFixture();
        using var session = new MarkdownPerformanceSession(MarkdownPerformanceOptions.Progressive);
        var resolver = new InlineRasterResolver(DisplaySizedRasterDecodeTests.CreatePng(96, 192));
        await using ImageDocument document = BuildDocument(seed, session, resolver,
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
                beforeMeasureOutsideGateForTests: document.Snapshot.NotifyPointerInput,
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
            if (constructionContext is not null)
                await constructionContext.DrainOwnedRetirementsAsync().WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    [Fact]
    public async Task StaleInlineSourceRevisionRetriesAfterExternalRelayoutWithoutExtraMutation()
    {
        await using var seed = new CompletedRasterPlacementCloneTests.PlacementFixture();
        using var session = new MarkdownPerformanceSession(MarkdownPerformanceOptions.Progressive);
        var resolver = new InlineRasterResolver(DisplaySizedRasterDecodeTests.CreatePng(96, 192));
        await using ImageDocument document = BuildDocument(seed, session, resolver,
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
                beforeMeasureOutsideGateForTests: () =>
                {
                    document.Snapshot.RelayoutChangedBlocks(
                        [inline.BlockIndex], LayoutWidth, CancellationToken.None);
                    callbackRevision = document.Snapshot.LayoutRevision;
                    callbackBounds = document.Snapshot.Blocks.Select(static root => root.Bounds).ToArray();
                },
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
            if (constructionContext is not null)
                await constructionContext.DrainOwnedRetirementsAsync().WaitAsync(TimeSpan.FromSeconds(10));
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
        await using ImageDocument document = BuildDocument(seed, session, resolver,
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
            for (int index = 0; index < rootsBefore.Length; index++)
            {
                Assert.Same(rootsBefore[index], document.Snapshot.Blocks[index]);
                Assert.Equal(boundsBefore[index], document.Snapshot.Blocks[index].Bounds);
            }
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
            if (constructionContext is not null)
                await constructionContext.DrainOwnedRetirementsAsync().WaitAsync(TimeSpan.FromSeconds(10));
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
        await using ImageDocument document = BuildDocument(seed, session, resolver,
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
            for (int index = 0; index < rootsBefore.Length; index++)
            {
                Assert.Same(rootsBefore[index], document.Snapshot.Blocks[index]);
                Assert.Equal(boundsBefore[index], document.Snapshot.Blocks[index].Bounds);
            }
        }
        finally
        {
            if (constructionContext is not null)
                await constructionContext.DrainOwnedRetirementsAsync().WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    private static string CreateMixedSource(InlineRasterResolver resolver) =>
        "# Inline and standalone image placement\n\n" +
        $"Text **before** ![first inline image]({resolver.InlineFirstUrl}), middle " +
        $"![second inline image]({resolver.InlineSecondUrl}), and *after*.\n\n" +
        $"![standalone image]({resolver.StandaloneUrl})\n\n" +
        "A trailing paragraph preserves suffix placement and paint.\n";

    private static string CreateInlineSource(InlineRasterResolver resolver) =>
        $"Prefix text ![first inline image]({resolver.InlineFirstUrl}) between " +
        $"![second inline image]({resolver.InlineSecondUrl}) suffix text.\n";

    private static string CreateLongLazySource(InlineRasterResolver resolver) =>
        $"Front ![first inline image]({resolver.InlineFirstUrl}) and " +
        $"![second inline image]({resolver.InlineSecondUrl}) tail.\n\n" +
        $"![pending sibling]({resolver.PendingUrl})\n\n" +
        string.Join("\n\n", Enumerable.Range(0, 90).Select(static index =>
            $"Lazy sibling {index}: preserve the measured mask and pending image work.")) +
        $"\n\n![far estimated image]({resolver.FarUrl})\n";

    private static ImageDocument BuildDocument(
        CompletedRasterPlacementCloneTests.PlacementFixture seed,
        MarkdownPerformanceSession session,
        IMarkdownImageResolver resolver,
        string sourceText,
        FlowDirection direction,
        float rasterizationScale,
        bool lazy,
        float lazyViewportHeight = 120,
        IMarkdownSvgRenderer? svgRenderer = null)
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
            SvgRenderer = svgRenderer,
            AllowThirdPartyRemoteImages = true,
            RasterizationScale = rasterizationScale,
        };

        LayoutSnapshot? snapshot = null;
        try
        {
            var builder = new LayoutBuilder(context, semanticDocument: semanticDocument);
            snapshot = lazy
                ? builder.BuildLazy(parsed, LayoutWidth, viewportTop: 0,
                    viewportHeight: lazyViewportHeight, overscan: 0, CancellationToken.None)
                : builder.Build(parsed, LayoutWidth, CancellationToken.None);
            snapshot.AssignAutomationDocumentGeneration(AutomationGeneration);
            var result = new ImageDocument(context, snapshot, sourceMap, resolver);
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

    private static InlineContainerBox FindInlineImageRoot(LayoutSnapshot snapshot, int expectedImages) =>
        Assert.Single(snapshot.Blocks.OfType<InlineContainerBox>(),
            inline => inline.Runs.OfType<InlineImageRun>().Count() == expectedImages);

    private static IEnumerable<ImageBox> FindInlineImages(InlineContainerBox inline) =>
        inline.Runs.OfType<InlineImageRun>().Select(static run => run.Image);

    private static ImageBox FindImage(LayoutSnapshot snapshot, string url) =>
        Assert.Single(EnumerateImages(snapshot), image =>
            string.Equals(image.Url, url, StringComparison.Ordinal));

    private static IEnumerable<ImageBox> EnumerateImages(LayoutSnapshot snapshot)
    {
        foreach (BlockBox root in snapshot.Blocks)
        foreach (ImageBox image in EnumerateImages(root))
            yield return image;
    }

    private static IEnumerable<ImageBox> EnumerateImages(BlockBox block)
    {
        switch (block)
        {
            case ImageBox image:
                yield return image;
                yield break;
            case InlineContainerBox inline:
                foreach (ImageBox image in FindInlineImages(inline))
                    yield return image;
                yield break;
            case StackBox stack:
                foreach (BlockBox child in stack.Children)
                foreach (ImageBox image in EnumerateImages(child))
                    yield return image;
                yield break;
            case ListItemBox listItem:
                foreach (ImageBox image in EnumerateImages(listItem.Marker))
                    yield return image;
                foreach (ImageBox image in EnumerateImages(listItem.Content))
                    yield return image;
                yield break;
            case TableBox table:
                foreach (InlineContainerBox cell in table.GetCellBoxes())
                foreach (ImageBox image in EnumerateImages(cell))
                    yield return image;
                yield break;
        }
    }

    private static BlockBox FindTopLevelOwner(LayoutSnapshot snapshot, ImageBox image)
    {
        foreach (BlockBox root in snapshot.Blocks)
            if (EnumerateImages(root).Any(candidate => ReferenceEquals(candidate, image)))
                return root;
        throw new InvalidOperationException("The image is not part of this snapshot.");
    }

    private static async Task LoadImageAsync(ImageBox image)
    {
        // WaitForLoadAsync registers before EnsureLoading; a synchronous cache
        // hit must not publish its sole completion before the test subscribes.
        await DisplaySizedRasterDecodeTests.WaitForLoadAsync(image).WaitAsync(TimeSpan.FromSeconds(10));
        await DisplaySizedRasterDecodeTests.WaitUntilAsync(
            () => image.ActiveImageWorkCountForTests == 0);
        Assert.NotNull(image.Bitmap);
    }

    private static CanvasTextLayout GetInlineLayout(InlineContainerBox inline)
    {
        FieldInfo field = typeof(InlineContainerBox).GetField("_layout",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Missing inline native-layout field.");
        return Assert.IsType<CanvasTextLayout>(field.GetValue(inline));
    }

    private static SharedCanvasBitmapCache.Lease GetBitmapLease(ImageBox image)
    {
        FieldInfo field = typeof(ImageBox).GetField("_bitmapLease",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Missing ImageBox bitmap lease field.");
        return Assert.IsType<SharedCanvasBitmapCache.Lease>(field.GetValue(image));
    }

    private static T ReadField<T>(object instance, string name)
    {
        return Assert.IsType<T>(ReadRawField(instance, name));
    }

    private static object? ReadRawField(object instance, string name)
    {
        FieldInfo field = instance.GetType().GetField(name,
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"Missing test field {name}.");
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

    private sealed class ImageDocument : IAsyncDisposable
    {
        internal ImageDocument(
            MarkdownLayoutContext context,
            LayoutSnapshot snapshot,
            MarkdownSourceMap sourceMap,
            IMarkdownImageResolver resolver)
        {
            Context = context;
            Snapshot = snapshot;
            SourceMap = sourceMap;
            Resolver = resolver;
        }

        internal MarkdownLayoutContext Context { get; }
        internal LayoutSnapshot Snapshot { get; }
        internal MarkdownSourceMap SourceMap { get; }
        internal IMarkdownImageResolver Resolver { get; }

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

    private sealed class InlineRasterResolver : IMarkdownImageResolver
    {
        private readonly byte[] _png;
        private readonly ConcurrentDictionary<string, int> _resolveCounts = new(StringComparer.Ordinal);
        private readonly TaskCompletionSource<bool> _pendingResolve =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly string _cachePrefix = "root-local-inline:" + Guid.NewGuid().ToString("N") + ":";

        internal InlineRasterResolver(byte[] png)
        {
            _png = png;
            string suffix = Guid.NewGuid().ToString("N");
            InlineFirstUrl = $"https://images.example.test/inline-first-{suffix}.png";
            InlineSecondUrl = $"https://images.example.test/inline-second-{suffix}.png";
            StandaloneUrl = $"https://images.example.test/standalone-{suffix}.png";
            PendingUrl = $"https://images.example.test/pending-{suffix}.png";
            FarUrl = $"https://images.example.test/far-{suffix}.png";
        }

        internal string InlineFirstUrl { get; }
        internal string InlineSecondUrl { get; }
        internal string StandaloneUrl { get; }
        internal string PendingUrl { get; }
        internal string FarUrl { get; }
        internal string[] Sources => [InlineFirstUrl, InlineSecondUrl, StandaloneUrl, PendingUrl, FarUrl];

        internal int ResolveCountFor(string source) =>
            _resolveCounts.TryGetValue(source, out int count) ? count : 0;

        internal Task WaitForPendingResolveAsync() => _pendingResolve.Task;

        public ValueTask<MarkdownImageResolution> ResolveAsync(
            string source,
            MarkdownImageResolveContext context,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _resolveCounts.AddOrUpdate(source, 1, static (_, count) => checked(count + 1));
            if (string.Equals(source, PendingUrl, StringComparison.Ordinal))
                return WaitForPendingAsync(cancellationToken);

            if (!Sources.Take(3).Contains(source, StringComparer.Ordinal) &&
                !string.Equals(source, FarUrl, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("The test resolver received an unexpected image source.");
            }

            return ValueTask.FromResult(MarkdownImageResolution.Resolved(new MarkdownImageAsset(
                _png,
                "image/png",
                new Uri(source),
                _cachePrefix + source)));
        }

        private async ValueTask<MarkdownImageResolution> WaitForPendingAsync(CancellationToken cancellationToken)
        {
            _pendingResolve.TrySetResult(true);
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
            throw new OperationCanceledException(cancellationToken);
        }
    }

    private sealed class InlineSvgResolver : IMarkdownImageResolver
    {
        private readonly byte[] _source = Encoding.UTF8.GetBytes(
            "<svg xmlns='http://www.w3.org/2000/svg' width='80' height='40'>" +
            "<title>Offline test badge</title><desc>Provider metadata is authoritative.</desc>" +
            "<rect width='80' height='40' fill='#d04020'/><text x='2' y='20'>badge</text></svg>");
        private readonly string _cacheKey = "root-local-inline-svg:" + Guid.NewGuid().ToString("N");

        internal string Url { get; } =
            "https://images.example.test/root-local-inline-svg-" + Guid.NewGuid().ToString("N") + ".svg";

        public ValueTask<MarkdownImageResolution> ResolveAsync(
            string source,
            MarkdownImageResolveContext context,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!string.Equals(source, Url, StringComparison.Ordinal))
                throw new InvalidOperationException("The offline SVG resolver received an unexpected source.");
            return ValueTask.FromResult(MarkdownImageResolution.Resolved(new MarkdownImageAsset(
                _source,
                "image/svg+xml",
                new Uri(source),
                _cacheKey)));
        }
    }

    private sealed class InlineSvgRenderer : IMarkdownSvgRenderer
    {
        private long _generation;
        private int _cacheGenerationReadCount;
        private int _fullRenderCount;
        private int _tiledRenderCount;
        private readonly MarkdownSvgDocumentInfo _info = new(
            IntrinsicWidthDips: 80,
            IntrinsicHeightDips: 40,
            IntrinsicAspectRatio: 2,
            Description: "Provider supplied SVG description",
            HasText: true,
            UsesCurrentColor: false,
            UsesColorScheme: false);

        internal Action? GenerationReadHook { get; set; }
        internal int CacheGenerationReadCount => Volatile.Read(ref _cacheGenerationReadCount);
        internal int FullRenderCount => Volatile.Read(ref _fullRenderCount);
        internal int TiledRenderCount => Volatile.Read(ref _tiledRenderCount);

        public long CacheGeneration
        {
            get
            {
                Interlocked.Increment(ref _cacheGenerationReadCount);
                GenerationReadHook?.Invoke();
                return Interlocked.Read(ref _generation);
            }
        }

        internal void SetGeneration(long generation) => Interlocked.Exchange(ref _generation, generation);

        public MarkdownSvgSourcePreparation PrepareSource(
            byte[] source,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return MarkdownSvgSourcePreparation.Admit(source);
        }

        public ValueTask<IMarkdownSvgDocument> OpenAsync(
            MarkdownSvgOpenRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult<IMarkdownSvgDocument>(new InlineSvgDocument(this, _info));
        }

        private sealed class InlineSvgDocument(
            InlineSvgRenderer renderer,
            MarkdownSvgDocumentInfo info) : IMarkdownSvgDocument
        {
            private int _disposed;
            public MarkdownSvgDocumentInfo Info { get; } = info;

            public ValueTask<MarkdownSvgRaster> RenderAsync(
                MarkdownSvgRenderRequest request,
                CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
                int width = request.TileRegion?.Width ?? request.TargetWidthPixels;
                int height = request.TileRegion?.Height ?? request.TargetHeightPixels;
                if (request.TileRegion is null)
                    Interlocked.Increment(ref renderer._fullRenderCount);
                else
                    Interlocked.Increment(ref renderer._tiledRenderCount);

                int stride = checked(width * 4);
                int length = checked(stride * height);
                IMemoryOwner<byte> owner = MemoryPool<byte>.Shared.Rent(length);
                Span<byte> pixels = owner.Memory.Span[..length];
                bool bgra = request.PixelFormat == MarkdownSvgPixelFormat.Bgra8Premultiplied;
                byte red = 208;
                byte green = 64;
                byte blue = 32;
                byte first = bgra ? blue : red;
                byte third = bgra ? red : blue;
                for (int offset = 0; offset < length; offset += 4)
                {
                    pixels[offset] = first;
                    pixels[offset + 1] = green;
                    pixels[offset + 2] = third;
                    pixels[offset + 3] = byte.MaxValue;
                }

                return ValueTask.FromResult(new MarkdownSvgRaster(
                    owner,
                    length,
                    width,
                    height,
                    stride,
                    request.PixelFormat));
            }

            public void Dispose() => Interlocked.Exchange(ref _disposed, 1);
        }
    }
}
