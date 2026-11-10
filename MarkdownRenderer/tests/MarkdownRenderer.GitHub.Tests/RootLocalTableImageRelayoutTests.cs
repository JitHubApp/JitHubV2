using MarkdownRenderer.Accessibility.Internal.Semantics;
using MarkdownRenderer.Document;
using MarkdownRenderer.GitHub.Tests.Layout.Internal.Images.BitmapCache;
using MarkdownRenderer.GitHub.Tests.Layout.Internal.Images.Raster;
using MarkdownRenderer.GitHub.Tests.TestSupport.Assertions;
using MarkdownRenderer.GitHub.Tests.TestSupport.Fixtures;
using MarkdownRenderer.Images;
using MarkdownRenderer.Layout.Internal.Boxes;
using MarkdownRenderer.Layout.Internal.Context;
using MarkdownRenderer.Layout.Internal.Images;
using MarkdownRenderer.Layout.Internal.Images.BitmapCache;
using MarkdownRenderer.Layout.Internal.Inlines;
using MarkdownRenderer.Layout.Internal.Inlines.Placement;
using MarkdownRenderer.Layout.Internal.Inlines.Runs;
using MarkdownRenderer.Layout.Internal.PrivatePlacement;
using MarkdownRenderer.Layout.Internal.Relayout;
using MarkdownRenderer.Layout.Internal.Snapshots;
using MarkdownRenderer.Layout.Internal.Tables;
using MarkdownRenderer.Performance;
using MarkdownRenderer.Theming;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.UI.Xaml;
using Windows.Foundation;
using Xunit;
using static MarkdownRenderer.GitHub.Tests.RootLocalTableFixtureChecks;
using static MarkdownRenderer.GitHub.Tests.TestSupport.Assertions.RootLocalTableQueries;
using static MarkdownRenderer.GitHub.Tests.TestSupport.Assertions.RootLocalTableRelayoutAssertions;

namespace MarkdownRenderer.GitHub.Tests;

/// <summary>
/// Root-local relayout of completed raster and SVG images inside GitHub
/// tables, checked against a full-rebuild fallback document.
/// </summary>
/// <remarks>
/// This file is longer than 500 lines because it holds thirteen table
/// relayout scenarios, each one arrange, relayout and verify sequence over a
/// pair of documents, that share one fixture family. Their fixtures, captures
/// and assertion blocks live in the <c>RootLocalTable*</c> support types.
/// </remarks>
[Collection(SharedCanvasBitmapCacheTestCollection.Name)]
public sealed class RootLocalTableImageRelayoutTests
{
    private const float LayoutWidth = RootLocalTableDocument.DefaultLayoutWidth;

    [Fact]
    public async Task EveryMeasuredTableCellHasAnIndependentPrivatePlacementRecipe()
    {
        await using var seed = new CompletedRasterPlacementCloneTests.PlacementFixture();
        using var session = new MarkdownPerformanceSession(MarkdownPerformanceOptions.Progressive);
        var resolver = RootLocalTableResolver.CreateStandard();
        await using RootLocalTableDocument document = RootLocalTableDocument.Build(seed, session, resolver,
            RootLocalTableSources.CreateStandard(resolver), FlowDirection.LeftToRight, 1.5f, lazy: false);
        TableBox table = FindTable(document.Snapshot);
        await LoadImagesAsync(EnumerateTableImages(table));
        MarkdownLayoutContext context = document.Context.CreateRasterPlacementContext(
            document.Context.ImageCancellationToken, document.Context.ImagePreparationOwner!,
            retirementSink: null, imageUnavailable: null);
        try
        {
            foreach (InlineContainerBox cell in table.GetCellBoxes())
            {
                float width = (float)cell.Bounds.Width;
                string state = $"Cell {cell.BlockIndex}: element={cell.ElementKey}, runs={cell.Runs.Count}, " +
                    $"width={width}, styleState={cell.StyleState}, dirty={cell.IsDirty}";
                Assert.True(cell.TryGetPrivateRasterPlacementCounts(context, width, out _, out _),
                    "Count preflight: " + state);
                Assert.True(cell.TryCreatePrivateInlinePlacementClone(context, width,
                    out InlineContainerBox? clone, out _), "Clone: " + state);
                Assert.NotNull(clone);
                try
                {
                    Assert.True(InlineMeasuredPlacementCapture.TryCaptureTableCellMeasuredPlacementTransfer(cell, clone, width, out var transfer),
                        "Capture: " + state);
                    Assert.NotNull(transfer);
                    _ = clone.Measure(width);
                    clone.Arrange(0, 0, width);
                    Assert.True(transfer.IsCurrentAndPrepared(), "Final fence: " + state);
                }
                finally
                {
                    context.DiscardConstructionBlock(clone);
                }
            }
        }
        finally
        {
            await context.DrainOwnedRetirementsAsync().WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    [Theory]
    [InlineData(640f, 1f, false, false)]
    [InlineData(640f, 1f, false, true)]
    [InlineData(640f, 1f, true, false)]
    [InlineData(640f, 1f, true, true)]
    [InlineData(640f, 1.5f, false, false)]
    [InlineData(640f, 1.5f, false, true)]
    [InlineData(640f, 1.5f, true, false)]
    [InlineData(640f, 1.5f, true, true)]
    [InlineData(640f, 2f, false, false)]
    [InlineData(640f, 2f, false, true)]
    [InlineData(640f, 2f, true, false)]
    [InlineData(640f, 2f, true, true)]
    [InlineData(640f, 4f, false, false)]
    [InlineData(640f, 4f, false, true)]
    [InlineData(640f, 4f, true, false)]
    [InlineData(640f, 4f, true, true)]
    [InlineData(320f, 1f, false, false)]
    [InlineData(320f, 1f, true, true)]
    [InlineData(320f, 2f, false, false)]
    [InlineData(320f, 2f, true, true)]
    public async Task CompletedTableImagesMatchFallbackGeometryAndPixelsWithoutReplacingSemanticOwners(
        float layoutWidth,
        float rasterizationScale,
        bool rightToLeft,
        bool forceSoftwareRenderer)
    {
        await using var seed = new CompletedRasterPlacementCloneTests.PlacementFixture(
            forceSoftwareRenderer: forceSoftwareRenderer);
        using var session = new MarkdownPerformanceSession(MarkdownPerformanceOptions.Progressive);
        var resolver = RootLocalTableResolver.CreateStandard();
        string sourceText = RootLocalTableSources.CreateStandard(resolver);
        FlowDirection direction = rightToLeft ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
        await using RootLocalTableDocument optimized = RootLocalTableDocument.Build(
            seed, session, resolver, sourceText, direction, rasterizationScale, lazy: false,
            layoutWidth: layoutWidth);
        await using RootLocalTableDocument fallback = RootLocalTableDocument.Build(
            seed, session, resolver, sourceText, direction, rasterizationScale, lazy: false,
            layoutWidth: layoutWidth);

        TableBox table = FindTable(optimized.Snapshot);
        TableBox fallbackTable = FindTable(fallback.Snapshot);
        Assert.Equal((double)layoutWidth, optimized.Snapshot.Size.Width);
        ImageBox[] images = EnumerateTableImages(table).ToArray();
        ImageBox[] fallbackImages = EnumerateTableImages(fallbackTable).ToArray();
        AssertStandardTableFixtureShape(table, images, fallbackImages);
        Rect[] initialCellBounds = CaptureCellBounds(optimized.Snapshot, table);
        float[] initialColumnWidths = CaptureColumnWidths(table);

        await LoadImagesAsync(images.Concat(fallbackImages));
        ScrollBothTablesToTheSameOffset(table, fallbackTable);

        var before = RootLocalTableOwners.Capture(optimized.Snapshot, table, images);
        float[] imageWidthsBefore = images.Select(static image => image.MeasuredImageWidth).ToArray();
        float[] imageHeightsBefore = images.Select(static image => image.MeasuredImageHeight).ToArray();
        int[] resolverCountsBefore = resolver.CaptureResolveCounts();
        fallback.Snapshot.RelayoutChangedBlocks([fallbackTable.BlockIndex], layoutWidth, CancellationToken.None);

        MarkdownLayoutContext? constructionContext = null;
        try
        {
            PreparedImageRelayoutResult result = optimized.Snapshot.TryRelayoutCompletedImageRoots(
                [table.BlockIndex], layoutWidth, CancellationToken.None, out constructionContext,
                expectedPointerInputEpoch: optimized.Snapshot.PointerInputEpoch);
            Assert.Equal(PreparedImageRelayoutResult.Committed, result);
            bool hasFlexibleColumnSpace = layoutWidth > LayoutWidth;
            AssertCommittedSnapshotKeepsSemanticOwners(before, optimized.Snapshot, fallback.Snapshot, hasFlexibleColumnSpace);
            Assert.Same(table, optimized.Snapshot.Blocks.Single(root => ReferenceEquals(root, table)));
            AssertViewportMatches(fallbackTable, table);
            Assert.Equal(fallbackTable.CanScrollHorizontally, table.CanScrollHorizontally);
            Assert.Equal(initialCellBounds.Length, before.Cells.Length);
            Assert.Equal(before.Cells.Length, table.GetCellBoxes().Count());
            AssertCellsMatchFallbackSelectionAndCaret(before, table, fallbackTable, optimized.Snapshot, fallback.Snapshot);
            AssertColumnsRebalancedLikeFallback(initialCellBounds, initialColumnWidths, optimized.Snapshot, table,
                fallbackTable, hasFlexibleColumnSpace);
            Assert.True(images[0].MeasuredImageWidth < 480f,
                "The wide decoded image must be measured against its narrower responsive table cell.");

            ImageBox[] afterImages = EnumerateTableImages(table).ToArray();
            Assert.Equal(images.Length, afterImages.Length);
            AssertImagesMatchFallback(images, afterImages, before.Bitmaps, before.Leases, fallbackImages,
                optimized.Snapshot, fallback.Snapshot);
            if (hasFlexibleColumnSpace)
                Assert.True(AnyMeasuredImageSizeChanged(imageWidthsBefore, imageHeightsBefore, images),
                    "Decoded image aspect ratios must change at least one committed inline image size when flexible width is available.");
            Assert.Equal(resolverCountsBefore, resolver.CaptureResolveCounts());

            // Build the native image plan before Paint can update inline bounds;
            // this is the same enumeration used by the control's publication path.
            RasterPlacementImagePlan plan = AssertPublicationPlan(optimized.Snapshot, images, images.Length);
            AssertPlanIndexMatchesImageBounds(plan, optimized.Snapshot, images);
            Assert.Equal(Paint(optimized.Snapshot, seed.Device, rasterizationScale),
                Paint(fallback.Snapshot, seed.Device, rasterizationScale));
        }
        finally
        {
            await DrainConstructionAsync(constructionContext);
        }
    }

    [Theory]
    [InlineData(FlowDirection.LeftToRight)]
    [InlineData(FlowDirection.RightToLeft)]
    public async Task OverflowTableReflowPreservesOrClampsScrolledOffsetAndRestoresSourceOwners(FlowDirection direction)
    {
        await using var seed = new CompletedRasterPlacementCloneTests.PlacementFixture(
            forceSoftwareRenderer: true);
        using var session = new MarkdownPerformanceSession(MarkdownPerformanceOptions.Progressive);
        var resolver = RootLocalTableResolver.CreateStandard(narrowImages: true);
        string sourceText = RootLocalTableSources.CreateStandard(resolver, forceOverflow: true)
            .Replace("**Preview**", "**P**", StringComparison.Ordinal);
        await using RootLocalTableDocument optimized = RootLocalTableDocument.Build(seed, session, resolver, sourceText,
            direction, 1.5f, lazy: false);
        await using RootLocalTableDocument fallback = RootLocalTableDocument.Build(seed, session, resolver, sourceText,
            direction, 1.5f, lazy: false);

        TableBox table = FindTable(optimized.Snapshot);
        TableBox fallbackTable = FindTable(fallback.Snapshot);
        ImageBox[] images = EnumerateTableImages(table).ToArray();
        ImageBox[] fallbackImages = EnumerateTableImages(fallbackTable).ToArray();
        await LoadImagesAsync(images.Concat(fallbackImages));

        double previousMaximum = System.Math.Max(0, table.HorizontalExtent - table.HorizontalViewport);
        Assert.True(table.CanScrollHorizontally);
        Assert.True(previousMaximum > 0);
        Assert.True(table.SetHorizontalOffset(previousMaximum));
        Assert.True(fallbackTable.SetHorizontalOffset(previousMaximum));
        Assert.True(table.HorizontalOffset > 0);

        var before = RootLocalTableOwners.Capture(optimized.Snapshot, table, images);
        int[] resolverCountsBefore = resolver.CaptureResolveCounts();
        fallback.Snapshot.RelayoutChangedBlocks([fallbackTable.BlockIndex], LayoutWidth, CancellationToken.None);

        MarkdownLayoutContext? constructionContext = null;
        try
        {
            PreparedImageRelayoutResult result = optimized.Snapshot.TryRelayoutCompletedImageRoots(
                [table.BlockIndex], LayoutWidth, CancellationToken.None, out constructionContext,
                expectedPointerInputEpoch: optimized.Snapshot.PointerInputEpoch);
            Assert.Equal(PreparedImageRelayoutResult.Committed, result);
            double fallbackMaximum = System.Math.Max(0,
                fallbackTable.HorizontalExtent - fallbackTable.HorizontalViewport);
            Assert.Equal(System.Math.Clamp(previousMaximum, 0, fallbackMaximum), table.HorizontalOffset);
            Assert.Equal(fallbackTable.HorizontalOffset, table.HorizontalOffset);
            if (fallbackMaximum == previousMaximum)
                Assert.Equal(previousMaximum, table.HorizontalOffset);
            Assert.Equal(fallbackTable.HorizontalExtent, table.HorizontalExtent);
            Assert.Equal(fallbackTable.HorizontalViewport, table.HorizontalViewport);

            before.AssertCommittedMatchesFallback(optimized.Snapshot, table, fallback.Snapshot, fallbackTable, fallbackImages);
            Assert.Same(table, Assert.Single(optimized.Snapshot.Blocks, root => ReferenceEquals(root, table)));
            Assert.Equal(resolverCountsBefore, resolver.CaptureResolveCounts());
            AssertPublicationPlan(optimized.Snapshot, images, images.Length);
            Assert.Equal(Paint(optimized.Snapshot, seed.Device, 1.5f),
                Paint(fallback.Snapshot, seed.Device, 1.5f));
        }
        finally
        {
            await DrainConstructionAsync(constructionContext);
        }
    }

    [Fact]
    public async Task TableImageChildBlockIndicesCommitTheirOwningTableWithFallbackParity()
    {
        await using var seed = new CompletedRasterPlacementCloneTests.PlacementFixture(
            forceSoftwareRenderer: true);
        using var session = new MarkdownPerformanceSession(MarkdownPerformanceOptions.Progressive);
        var resolver = RootLocalTableResolver.CreateStandard();
        string sourceText = RootLocalTableSources.CreateStandard(resolver);
        await using RootLocalTableDocument optimized = RootLocalTableDocument.Build(seed, session, resolver, sourceText,
            FlowDirection.LeftToRight, 1.5f, lazy: false, layoutWidth: 640f);
        await using RootLocalTableDocument fallback = RootLocalTableDocument.Build(seed, session, resolver, sourceText,
            FlowDirection.LeftToRight, 1.5f, lazy: false, layoutWidth: 640f);

        TableBox table = FindTable(optimized.Snapshot);
        TableBox fallbackTable = FindTable(fallback.Snapshot);
        ImageBox[] images = EnumerateTableImages(table).ToArray();
        ImageBox[] fallbackImages = EnumerateTableImages(fallbackTable).ToArray();
        Assert.Equal(3, images.Length);
        await LoadImagesAsync(images.Concat(fallbackImages));

        int[] imageChildBlockIndices = AssertDistinctImageChildIndices(table, images);

        var before = RootLocalTableOwners.Capture(optimized.Snapshot, table, images);
        int[] leaseReferencesBefore = before.Leases.Select(GetLeaseReferenceCount).ToArray();
        int[] resolverCountsBefore = resolver.CaptureResolveCounts();
        fallback.Snapshot.RelayoutChangedBlocks([fallbackTable.BlockIndex], 640f, CancellationToken.None);

        MarkdownLayoutContext? constructionContext = null;
        try
        {
            PreparedImageRelayoutResult result = optimized.Snapshot.TryRelayoutCompletedImageRoots(
                imageChildBlockIndices, 640f, CancellationToken.None, out constructionContext,
                expectedPointerInputEpoch: optimized.Snapshot.PointerInputEpoch);
            Assert.Equal(PreparedImageRelayoutResult.Committed, result);
            Assert.Equal(before.Revision + 1, optimized.Snapshot.LayoutRevision);
            Assert.Equal(before.Roots.Length, optimized.Snapshot.Blocks.Count);
            Assert.Same(table, FindTable(optimized.Snapshot));
            AssertViewportMatches(fallbackTable, table);
            Assert.Equal(images.Length, EnumerateTableImages(table).Count());
            before.AssertCommittedMatchesFallback(optimized.Snapshot, table, fallback.Snapshot, fallbackTable, fallbackImages);
            Assert.Equal(resolverCountsBefore, resolver.CaptureResolveCounts());

            AssertPublicationPlan(optimized.Snapshot, images, images.Length);
            Assert.Equal(Paint(optimized.Snapshot, seed.Device, 1.5f),
                Paint(fallback.Snapshot, seed.Device, 1.5f));
        }
        finally
        {
            await DrainConstructionAsync(constructionContext);
        }

        AssertLeaseReferenceCounts(before.Leases, leaseReferencesBefore);
    }

    [Fact]
    public async Task CompletedFullSvgTableImagesPreserveProviderAndSemanticOwnersWithFallbackParity()
    {
        await using var seed = new CompletedRasterPlacementCloneTests.PlacementFixture(
            forceSoftwareRenderer: true);
        using var session = new MarkdownPerformanceSession(MarkdownPerformanceOptions.Progressive);
        var resolver = new SvgTableResolver();
        var renderer = new RootLocalTableSvgRenderer();
        string sourceText = RootLocalTableSources.CreateSvg(resolver);
        await using RootLocalTableDocument optimized = RootLocalTableDocument.Build(seed, session, resolver, sourceText,
            FlowDirection.LeftToRight, 1.5f, lazy: false, layoutWidth: 640f, svgRenderer: renderer);
        await using RootLocalTableDocument fallback = RootLocalTableDocument.Build(seed, session, resolver, sourceText,
            FlowDirection.LeftToRight, 1.5f, lazy: false, layoutWidth: 640f, svgRenderer: renderer);

        LayoutSnapshot snapshotBefore = optimized.Snapshot;
        TableBox table = FindTable(snapshotBefore);
        TableBox fallbackTable = FindTable(fallback.Snapshot);
        InlineContainerBox[] cellsBefore = table.GetCellBoxes().ToArray();
        ImageBox[] images = EnumerateTableImages(table).ToArray();
        ImageBox[] fallbackImages = EnumerateTableImages(fallbackTable).ToArray();
        AssertSvgTableFixtureShape(cellsBefore, images, fallbackImages);
        await LoadImagesAsync(images.Concat(fallbackImages));
        AssertSvgImagesLoadedWithoutTiles(images, fallbackImages, renderer);

        var before = RootLocalTableOwners.Capture(snapshotBefore, table, images, cellsBefore);
        float[] columnWidthsBefore = CaptureColumnWidths(table);
        int[] leaseCountsBefore = before.Leases.Select(GetLeaseReferenceCount).ToArray();
        float[] imageWidthsBefore = images.Select(static image => image.MeasuredImageWidth).ToArray();
        float[] imageHeightsBefore = images.Select(static image => image.MeasuredImageHeight).ToArray();
        var svgBefore = RootLocalTableSvgImageOwners.CaptureTableFixture(images);
        long generationBefore = snapshotBefore.AutomationDocumentGeneration;
        int openCountBefore = renderer.OpenCount;
        int fullRenderCountBefore = renderer.FullRenderCount;
        int[] resolverCountsBefore = resolver.Sources.Select(resolver.ResolveCountFor).ToArray();
        fallback.Snapshot.RelayoutChangedBlocks([fallbackTable.BlockIndex], 640f, CancellationToken.None);
        int[] childImageIndices = AssertDistinctImageChildIndices(table, images);

        MarkdownLayoutContext? constructionContext = null;
        try
        {
            PreparedImageRelayoutResult result = snapshotBefore.TryRelayoutCompletedImageRoots(
                childImageIndices, 640f, CancellationToken.None, out constructionContext,
                expectedPointerInputEpoch: snapshotBefore.PointerInputEpoch);
            Assert.Equal(PreparedImageRelayoutResult.Committed, result);
            Assert.Same(snapshotBefore, optimized.Snapshot);
            Assert.Equal(before.Revision + 1, snapshotBefore.LayoutRevision);
            Assert.Equal(generationBefore, snapshotBefore.AutomationDocumentGeneration);
            Assert.Equal(before.Roots.Length, snapshotBefore.Blocks.Count);
            Assert.Equal(LayoutPlacementTestGeometry.ToLogical(fallback.Snapshot, fallbackTable),
                LayoutPlacementTestGeometry.ToLogical(snapshotBefore, table));
            AssertViewportMatches(fallbackTable, table);
            // Column rebalancing moves the same semantic selection. Its
            // rectangles must match the authoritative fallback, not the
            // source's obsolete pre-completion cell positions.
            before.AssertCommittedMatchesFallback(snapshotBefore, table, fallback.Snapshot, fallbackTable, fallbackImages);
            AssertSvgColumnsRebalancedLikeFallback(columnWidthsBefore, snapshotBefore, table, fallback.Snapshot, fallbackTable);
            ImageBox[] imagesAfter = EnumerateTableImages(table).ToArray();
            Assert.Equal(images.Length, imagesAfter.Length);
            svgBefore.AssertPreserved(imagesAfter);
            Assert.True(AnyMeasuredImageSizeChanged(imageWidthsBefore, imageHeightsBefore, imagesAfter),
                "The opened SVG intrinsic dimensions must participate in cell/image remeasurement.");
            Assert.Equal(openCountBefore, renderer.OpenCount);
            Assert.Equal(fullRenderCountBefore, renderer.FullRenderCount);
            Assert.Equal(0, renderer.CacheGeneration);
            Assert.Equal(resolverCountsBefore, resolver.Sources.Select(resolver.ResolveCountFor).ToArray());

            // Enumerate the publish plan before painting can update image bounds.
            AssertPublicationPlan(snapshotBefore, images, images.Length);
            Assert.Equal(Paint(snapshotBefore, seed.Device, 1.5f), Paint(fallback.Snapshot, seed.Device, 1.5f));
        }
        finally
        {
            await DrainConstructionAsync(constructionContext);
        }

        // Prepared clones borrow bitmap leases until their asynchronous
        // retirement completes. Assert the exact final ownership count after
        // the same barrier the production pipeline awaits, not while cleanup
        // may still be queued on the thread pool.
        AssertLeaseReferenceCounts(before.Leases, leaseCountsBefore);
    }

    [Fact]
    public async Task TableSvgProviderGenerationChangeAfterPrivateMeasureRejectsWithoutSourceMutation()
    {
        await using var seed = new CompletedRasterPlacementCloneTests.PlacementFixture(
            forceSoftwareRenderer: true);
        using var session = new MarkdownPerformanceSession(MarkdownPerformanceOptions.Progressive);
        var resolver = new SvgTableResolver();
        var renderer = new RootLocalTableSvgRenderer();
        await using RootLocalTableDocument document = RootLocalTableDocument.Build(seed, session, resolver,
            RootLocalTableSources.CreateSvg(resolver), FlowDirection.LeftToRight, 1.5f, lazy: false,
            layoutWidth: 640f, svgRenderer: renderer);
        LayoutSnapshot snapshot = document.Snapshot;
        TableBox table = FindTable(snapshot);
        ImageBox[] images = EnumerateTableImages(table).ToArray();
        await LoadImagesAsync(images);

        var before = RootLocalTableOwners.Capture(snapshot, table, images);
        Rect[] rootBoundsBefore = before.Roots.Select(static root => root.Bounds).ToArray();
        Rect[][] selectionBefore = before.Cells.Select(cell => GetCellSelectionRects(snapshot, cell)).ToArray();
        int[] leaseCountsBefore = before.Leases.Select(GetLeaseReferenceCount).ToArray();
        MarkdownSvgDocumentInfo[] infoBefore = images.Select(image =>
            Assert.IsType<MarkdownSvgDocumentInfo>(image.Svg.Info)).ToArray();
        long[] providerGenerationsBefore = images.Select(image => image.Svg.ProviderCacheGeneration).ToArray();
        int opensBefore = renderer.OpenCount;
        int rendersBefore = renderer.FullRenderCount;
        int[] resolverCountsBefore = resolver.Sources.Select(resolver.ResolveCountFor).ToArray();

        MarkdownLayoutContext? constructionContext = null;
        try
        {
            PreparedImageRelayoutResult result = snapshot.TryRelayoutCompletedImageRoots(
                images.Select(static image => image.BlockIndex).Distinct().ToArray(), 640f,
                CancellationToken.None, out constructionContext,
                phaseHooks: CompletedImageRelayoutPhaseHooks.BeforeMeasure(renderer.AdvanceCacheGeneration),
                expectedPointerInputEpoch: snapshot.PointerInputEpoch);
            Assert.Equal(PreparedImageRelayoutResult.Retry, result);
            Assert.Equal(before.Revision, snapshot.LayoutRevision);
            Assert.Same(before.SourceMap, snapshot.SourceMap);
            Assert.Same(before.Semantic, snapshot.SemanticDocument);
            AssertRootsPreserved(before.Roots, snapshot);
            for (int rootIndex = 0; rootIndex < before.Roots.Length; rootIndex++)
                Assert.Equal(rootBoundsBefore[rootIndex], snapshot.Blocks[rootIndex].Bounds);
            AssertCellsUnchanged(before, selectionBefore, snapshot, table.GetCellBoxes().ToArray());
            for (int imageIndex = 0; imageIndex < images.Length; imageIndex++)
            {
                Assert.Same(images[imageIndex], EnumerateTableImages(table).ElementAt(imageIndex));
                Assert.Same(before.Bitmaps[imageIndex], images[imageIndex].Bitmap);
                Assert.Same(before.Leases[imageIndex], GetBitmapLease(images[imageIndex]));
                Assert.Same(infoBefore[imageIndex], images[imageIndex].Svg.Info);
                Assert.Equal(providerGenerationsBefore[imageIndex],
                    images[imageIndex].Svg.ProviderCacheGeneration);
            }
            Assert.Equal(opensBefore, renderer.OpenCount);
            Assert.Equal(rendersBefore, renderer.FullRenderCount);
            Assert.Equal(1, renderer.CacheGeneration);
            Assert.Equal(resolverCountsBefore, resolver.Sources.Select(resolver.ResolveCountFor).ToArray());
        }
        finally
        {
            await DrainConstructionAsync(constructionContext);
        }
        AssertLeaseReferenceCounts(before.Leases, leaseCountsBefore);
    }

    [Fact]
    public async Task MixedThreeTableParagraphAndStandaloneImageBatchMatchesFallback()
    {
        await using var seed = new CompletedRasterPlacementCloneTests.PlacementFixture(
            forceSoftwareRenderer: true);
        using var session = new MarkdownPerformanceSession(MarkdownPerformanceOptions.Progressive);
        var resolver = RootLocalTableResolver.CreateStandard(includeStandalone: true);
        string sourceText = RootLocalTableSources.CreateThreeTableMixed(resolver);
        await using RootLocalTableDocument optimized = RootLocalTableDocument.Build(seed, session, resolver, sourceText,
            FlowDirection.LeftToRight, 1.5f, lazy: false);
        await using RootLocalTableDocument fallback = RootLocalTableDocument.Build(seed, session, resolver, sourceText,
            FlowDirection.LeftToRight, 1.5f, lazy: false);

        RootLocalTableMixedBatch mixed = RootLocalTableMixedBatch.Find(optimized.Snapshot, resolver);
        RootLocalTableMixedBatch fallbackMixed = RootLocalTableMixedBatch.Find(fallback.Snapshot, resolver);
        Assert.Equal(3, mixed.Tables.Length);
        Assert.Equal(mixed.Tables.Length, fallbackMixed.Tables.Length);
        Assert.Equal(5, mixed.Images.Length);
        Assert.Equal(mixed.Images.Length, fallbackMixed.Images.Length);
        await LoadImagesAsync(mixed.Images.Concat(fallbackMixed.Images));

        BlockBox[] rootsBefore = optimized.Snapshot.Blocks.ToArray();
        Dictionary<BlockBox, Rect> rootBoundsBeforeByOwner = rootsBefore.ToDictionary<BlockBox, BlockBox, Rect>(
            static root => root,
            static root => root.Bounds,
            ReferenceEqualityComparer.Instance);
        RootLocalTableOwners[] tablesBefore = mixed.Tables.Select(table => RootLocalTableOwners.Capture(
            optimized.Snapshot, table, EnumerateTableImages(table).ToArray())).ToArray();
        float[][] widthsBefore = mixed.Tables.Select(CaptureColumnWidths).ToArray();
        CanvasBitmap[] bitmapsBefore = mixed.Images.Select(GetBitmap).ToArray();
        SharedCanvasBitmapCache.Lease[] leasesBefore = mixed.Images.Select(GetBitmapLease).ToArray();
        MarkdownSourceMap sourceMapBefore = optimized.Snapshot.SourceMap;
        MarkdownSemanticDocument semanticBefore = optimized.Snapshot.SemanticDocument;
        InlineRun[] paragraphRunsBefore = mixed.Paragraph.Runs.ToArray();
        CanvasTextLayout paragraphLayoutBefore = GetInlineLayout(mixed.Paragraph);
        HashSet<int> changedRoots = mixed.CollectChangedRoots();
        int[] resolverCountsBefore = resolver.CaptureResolveCounts();
        fallback.Snapshot.RelayoutChangedBlocks(fallbackMixed.ChangedRootIndices(), LayoutWidth, CancellationToken.None);

        MarkdownLayoutContext? constructionContext = null;
        try
        {
            PreparedImageRelayoutResult result = optimized.Snapshot.TryRelayoutCompletedImageRoots(
                changedRoots, LayoutWidth, CancellationToken.None, out constructionContext,
                expectedPointerInputEpoch: optimized.Snapshot.PointerInputEpoch);
            Assert.Equal(PreparedImageRelayoutResult.Committed, result);
            Assert.Same(sourceMapBefore, optimized.Snapshot.SourceMap);
            Assert.Same(semanticBefore, optimized.Snapshot.SemanticDocument);
            Assert.Equal(fallback.Snapshot.Size, optimized.Snapshot.Size);
            Assert.True(mixed.ChangedRootOwners().Any(root =>
                System.Math.Abs(rootBoundsBeforeByOwner[root].Height - root.Bounds.Height) > 0.5),
                "At least one completed image root must change its committed geometry.");
            AssertRootsMatchFallback(rootsBefore, optimized.Snapshot, fallback.Snapshot);
            mixed.AssertParagraphRebuiltLikeFallback(paragraphRunsBefore, paragraphLayoutBefore, fallbackMixed,
                optimized.Snapshot, fallback.Snapshot);
            mixed.AssertTablesRebuiltLikeFallback(fallbackMixed, tablesBefore, widthsBefore, optimized.Snapshot, fallback.Snapshot);

            ImageBox[] finalImages = optimized.Snapshot.Blocks.SelectMany(EnumerateImages).ToArray();
            Assert.Equal(mixed.Images.Length, finalImages.Length);
            AssertImagesMatchFallback(mixed.Images, finalImages, bitmapsBefore, leasesBefore, fallbackMixed.Images,
                optimized.Snapshot, fallback.Snapshot);
            Assert.True(finalImages.Any(static image =>
                    System.Math.Abs(image.MeasuredImageWidth - image.MeasuredImageHeight) > 1f),
                "The mixed batch must retain the authored non-square image size pairs.");
            Assert.Equal(resolverCountsBefore, resolver.CaptureResolveCounts());

            AssertPublicationPlan(optimized.Snapshot,
                optimized.Snapshot.Blocks.SelectMany(EnumerateImages).ToArray(), mixed.Images.Length);
            Assert.Equal(Paint(optimized.Snapshot, seed.Device, 1.5f),
                Paint(fallback.Snapshot, seed.Device, 1.5f));
        }
        finally
        {
            await DrainConstructionAsync(constructionContext);
        }
    }

    [Fact]
    public async Task LazyTableRelayoutLeavesPendingSiblingAndDistantEstimatedImageUntouched()
    {
        await using var seed = new CompletedRasterPlacementCloneTests.PlacementFixture();
        using var session = new MarkdownPerformanceSession(MarkdownPerformanceOptions.Progressive);
        var resolver = RootLocalTableResolver.CreateStandard(includePending: true);
        string sourceText = RootLocalTableSources.CreateLazyWithPendingAndFarImages(resolver);
        await using RootLocalTableDocument optimized = RootLocalTableDocument.Build(seed, session, resolver, sourceText,
            FlowDirection.LeftToRight, 1.5f, lazy: true, lazyViewportHeight: 520);
        await using RootLocalTableDocument fallback = RootLocalTableDocument.Build(seed, session, resolver, sourceText,
            FlowDirection.LeftToRight, 1.5f, lazy: true, lazyViewportHeight: 520);

        TableBox table = FindTable(optimized.Snapshot);
        TableBox fallbackTable = FindTable(fallback.Snapshot);
        ImageBox pending = FindImage(optimized.Snapshot, resolver.PendingUrl);
        ImageBox fallbackPending = FindImage(fallback.Snapshot, resolver.PendingUrl);
        ImageBox far = FindImage(optimized.Snapshot, resolver.FarUrl);
        ImageBox fallbackFar = FindImage(fallback.Snapshot, resolver.FarUrl);
        BlockBox pendingOwner = FindTopLevelOwner(optimized.Snapshot, pending);
        BlockBox farOwner = FindTopLevelOwner(optimized.Snapshot, far);
        BlockBox fallbackPendingOwner = FindTopLevelOwner(fallback.Snapshot, fallbackPending);
        BlockBox fallbackFarOwner = FindTopLevelOwner(fallback.Snapshot, fallbackFar);
        Assert.True(optimized.Snapshot.TopLevelBlockCount > 64);
        Assert.True(optimized.Snapshot.IsTopLevelBlockMeasured(table));
        Assert.True(optimized.Snapshot.IsTopLevelBlockMeasured(pendingOwner));
        Assert.False(optimized.Snapshot.IsTopLevelBlockMeasured(farOwner));
        Assert.True(optimized.Snapshot.MeasuredTopLevelBlockCount < optimized.Snapshot.TopLevelBlockCount);
        Assert.True(fallback.Snapshot.IsTopLevelBlockMeasured(fallbackPendingOwner));
        Assert.False(fallback.Snapshot.IsTopLevelBlockMeasured(fallbackFarOwner));

        pending.EnsureLoading();
        fallbackPending.EnsureLoading();
        await resolver.WaitForPendingResolveAsync().WaitAsync(TimeSpan.FromSeconds(10));
        await DisplaySizedRasterDecodeTests.WaitUntilAsync(
            () => pending.GetWorkState().ActiveWorkCount > 0 &&
                  fallbackPending.GetWorkState().ActiveWorkCount > 0 &&
                  resolver.ResolveCountFor(resolver.PendingUrl) >= 2);
        await LoadImagesAsync(EnumerateTableImages(table));
        await LoadImagesAsync(EnumerateTableImages(fallbackTable));

        BlockBox[] rootsBefore = optimized.Snapshot.Blocks.ToArray();
        Rect[] rootBoundsBefore = rootsBefore.Select(static root => root.Bounds).ToArray();
        Rect[] tableCellBoundsBefore = CaptureCellBounds(optimized.Snapshot, table);
        long revisionBefore = optimized.Snapshot.LayoutRevision;
        int pendingWorkBefore = pending.GetWorkState().ActiveWorkCount;
        int pendingResolveCount = resolver.ResolveCountFor(resolver.PendingUrl);
        int farResolveCount = resolver.ResolveCountFor(resolver.FarUrl);
        fallback.Snapshot.RelayoutChangedBlocks([fallbackTable.BlockIndex], LayoutWidth, CancellationToken.None);
        MarkdownLayoutContext? constructionContext = null;
        try
        {
            PreparedImageRelayoutResult result = optimized.Snapshot.TryRelayoutCompletedImageRoots(
                [table.BlockIndex], LayoutWidth, CancellationToken.None, out constructionContext,
                expectedPointerInputEpoch: optimized.Snapshot.PointerInputEpoch);

            Assert.Equal(PreparedImageRelayoutResult.Committed, result);
            Assert.Equal(revisionBefore + 1, optimized.Snapshot.LayoutRevision);
            Assert.Equal(fallback.Snapshot.Size, optimized.Snapshot.Size);
            Assert.Same(table, optimized.Snapshot.Blocks.Single(root => ReferenceEquals(root, table)));
            Assert.Equal(tableCellBoundsBefore.Length, CaptureCellBounds(optimized.Snapshot, table).Length);
            Assert.Same(pending, FindImage(optimized.Snapshot, resolver.PendingUrl));
            Assert.Same(far, FindImage(optimized.Snapshot, resolver.FarUrl));
            Assert.True(pending.GetWorkState().ActiveWorkCount > 0);
            Assert.Equal(pendingWorkBefore, pending.GetWorkState().ActiveWorkCount);
            Assert.Null(pending.Bitmap);
            Assert.Equal(pendingResolveCount, resolver.ResolveCountFor(resolver.PendingUrl));
            Assert.Equal(farResolveCount, resolver.ResolveCountFor(resolver.FarUrl));
            Assert.False(optimized.Snapshot.IsTopLevelBlockMeasured(farOwner));
            Assert.False(fallback.Snapshot.IsTopLevelBlockMeasured(fallbackFarOwner));
            AssertRootsMatchFallbackKeepingEstimatedOwner(rootsBefore, rootBoundsBefore, farOwner,
                optimized.Snapshot, fallback.Snapshot);
        }
        finally
        {
            await DrainConstructionAsync(constructionContext);
        }
    }

    [Theory]
    [InlineData(TablePreparationRace.Cancellation)]
    [InlineData(TablePreparationRace.PointerInput)]
    [InlineData(TablePreparationRace.LayoutRevision)]
    [InlineData(TablePreparationRace.HorizontalScroll)]
    public async Task TablePreparationRacesRejectWithoutPlacementMutation(TablePreparationRace race)
    {
        await using var seed = new CompletedRasterPlacementCloneTests.PlacementFixture();
        using var session = new MarkdownPerformanceSession(MarkdownPerformanceOptions.Progressive);
        var resolver = RootLocalTableResolver.CreateStandard();
        string sourceText = RootLocalTableSources.CreateStandard(resolver, forceOverflow: true);
        await using RootLocalTableDocument document = RootLocalTableDocument.Build(seed, session, resolver, sourceText,
            FlowDirection.RightToLeft, 1.5f, lazy: false);
        TableBox table = FindTable(document.Snapshot);
        await LoadImagesAsync(EnumerateTableImages(table).ToArray());
        Assert.True(table.CanScrollHorizontally);

        BlockBox[] rootsBefore = document.Snapshot.Blocks.ToArray();
        var initialState = RootLocalTableSourceState.Capture(document.Snapshot, table);
        BlockBox revisionTrigger = Assert.Single(document.Snapshot.Blocks.OfType<InlineContainerBox>(),
            inline => inline.ElementKey == MarkdownElementKeys.Heading1);
        using var driver = new RootLocalTablePreparationRaceDriver(race, document.Snapshot, table, revisionTrigger, LayoutWidth);
        MarkdownLayoutContext? constructionContext = null;

        try
        {
            if (race == TablePreparationRace.Cancellation)
            {
                Assert.Throws<OperationCanceledException>(() => document.Snapshot.TryRelayoutCompletedImageRoots(
                    [table.BlockIndex], LayoutWidth, driver.Cancellation.Token, out constructionContext,
                    phaseHooks: CompletedImageRelayoutPhaseHooks.BeforeMeasure(driver.Apply),
                    expectedPointerInputEpoch: driver.ExpectedPointerEpoch));
            }
            else
            {
                PreparedImageRelayoutResult result = document.Snapshot.TryRelayoutCompletedImageRoots(
                    [table.BlockIndex], LayoutWidth, CancellationToken.None, out constructionContext,
                    phaseHooks: CompletedImageRelayoutPhaseHooks.BeforeMeasure(driver.Apply),
                    expectedPointerInputEpoch: driver.ExpectedPointerEpoch);
                Assert.Equal(PreparedImageRelayoutResult.Retry, result);
            }
            Assert.True(driver.Applied);
            RootLocalTableSourceState expected = driver.ExpectedStateAfterRace ?? initialState;
            RootLocalTableSourceState.AssertEqual(expected, RootLocalTableSourceState.Capture(document.Snapshot, table));
            Assert.Equal(driver.RevisionAfterRace, document.Snapshot.LayoutRevision);
            AssertRootsPreserved(rootsBefore, document.Snapshot);
            if (race == TablePreparationRace.PointerInput)
                Assert.Equal(driver.ExpectedPointerEpoch, document.Snapshot.PointerInputEpoch);
            if (race == TablePreparationRace.HorizontalScroll)
                Assert.NotEqual(initialState.HorizontalOffset, table.HorizontalOffset);
        }
        finally
        {
            await DrainConstructionAsync(constructionContext);
        }
    }

    [Fact]
    public async Task LateTableCellRunMutationRejectsMixedStandaloneAndTableBatchAtomically()
    {
        await using var seed = new CompletedRasterPlacementCloneTests.PlacementFixture();
        using var session = new MarkdownPerformanceSession(MarkdownPerformanceOptions.Progressive);
        var resolver = RootLocalTableResolver.CreateStandard(includeStandalone: true);
        string sourceText =
            $"![earlier standalone image]({resolver.StandaloneUrl})\n\n" +
            RootLocalTableSources.CreateStandard(resolver);
        await using RootLocalTableDocument document = RootLocalTableDocument.Build(seed, session, resolver, sourceText,
            FlowDirection.LeftToRight, 2f, lazy: false);
        ImageBox standalone = Assert.Single(document.Snapshot.Blocks.OfType<ImageBox>());
        TableBox table = FindTable(document.Snapshot);
        Assert.True(standalone.BlockIndex < table.BlockIndex);
        ImageBox[] images = EnumerateTableImages(table).ToArray();
        await LoadImagesAsync(images.Append(standalone));

        InlineContainerBox lastCell = table.GetCellBoxes().Last(static cell => cell.Runs.Count > 0);
        InlineRun lateRun = lastCell.Runs[^1];
        int originalInlineIndex = lateRun.InlineIndex;
        var initialState = RootLocalTableSourceState.Capture(document.Snapshot, table, standalone);
        BlockBox[] rootsBefore = document.Snapshot.Blocks.ToArray();
        CanvasTextLayout lateCellLayout = GetInlineLayout(lastCell);
        long revisionBefore = document.Snapshot.LayoutRevision;
        int[] resolverCountsBefore = resolver.CaptureResolveCounts();
        bool hookRan = false;
        MarkdownLayoutContext? constructionContext = null;
        PreparedImageRelayoutResult result = PreparedImageRelayoutResult.Unavailable;
        try
        {
            result = document.Snapshot.TryRelayoutCompletedImageRoots(
                [standalone.BlockIndex, table.BlockIndex], LayoutWidth, CancellationToken.None,
                out constructionContext,
                phaseHooks: CompletedImageRelayoutPhaseHooks.BeforeMeasure(() =>
                {
                    hookRan = true;
                    lateRun.InlineIndex = originalInlineIndex + 1;
                }),
                expectedPointerInputEpoch: document.Snapshot.PointerInputEpoch);
        }
        finally
        {
            lateRun.InlineIndex = originalInlineIndex;
            await DrainConstructionAsync(constructionContext);
        }

        Assert.True(hookRan);
        Assert.Equal(PreparedImageRelayoutResult.Retry, result);
        Assert.Equal(revisionBefore, document.Snapshot.LayoutRevision);
        Assert.Equal(originalInlineIndex, lateRun.InlineIndex);
        Assert.Same(lateRun, lastCell.Runs[^1]);
        Assert.Same(lateCellLayout, GetInlineLayout(lastCell));
        RootLocalTableSourceState.AssertEqual(initialState,
            RootLocalTableSourceState.Capture(document.Snapshot, table, standalone));
        AssertRootsPreserved(rootsBefore, document.Snapshot);
        Assert.Equal(resolverCountsBefore, resolver.CaptureResolveCounts());
    }

    [Fact]
    public async Task TableImagePlaceholderAliasedToCellTextRejectsBeforeAnySourceSwap()
    {
        await using var seed = new CompletedRasterPlacementCloneTests.PlacementFixture(
            forceSoftwareRenderer: true);
        using var session = new MarkdownPerformanceSession(MarkdownPerformanceOptions.Progressive);
        var resolver = RootLocalTableResolver.CreateStandard();
        await using RootLocalTableDocument document = RootLocalTableDocument.Build(seed, session, resolver,
            RootLocalTableSources.CreateStandard(resolver), FlowDirection.LeftToRight, 1.5f,
            lazy: false, layoutWidth: 640f);
        TableBox table = FindTable(document.Snapshot);
        ImageBox[] images = EnumerateTableImages(table).ToArray();
        await LoadImagesAsync(images);

        InlineContainerBox cell = table.GetCellBoxes().First(static candidate => candidate.Runs.Count > 0);
        CanvasTextLayout cellLayout = GetInlineLayout(cell);
        ImageBox imageSource = images[0];
        CanvasTextLayout? originalPlaceholder = imageSource.MeasureCustody.GetLayoutState().Placeholder;
        Assert.NotSame(cellLayout, originalPlaceholder);

        var initialState = RootLocalTableSourceState.Capture(document.Snapshot, table);
        BlockBox[] rootsBefore = document.Snapshot.Blocks.ToArray();
        MarkdownSourceMap sourceMapBefore = document.Snapshot.SourceMap;
        MarkdownSemanticDocument semanticBefore = document.Snapshot.SemanticDocument;
        long revisionBefore = document.Snapshot.LayoutRevision;
        int[] resolverCountsBefore = resolver.CaptureResolveCounts();
        SharedCanvasBitmapCache.Lease[] leasesBefore = images.Select(GetBitmapLease).ToArray();
        int[] leaseReferencesBefore = leasesBefore.Select(GetLeaseReferenceCount).ToArray();
        int hookCalls = 0;
        bool aliasInstalled = false;
        PreparedImageRelayoutResult result = PreparedImageRelayoutResult.Unavailable;
        MarkdownLayoutContext? constructionContext = null;
        try
        {
            result = document.Snapshot.TryRelayoutCompletedImageRoots(
                [table.BlockIndex], 640f, CancellationToken.None, out constructionContext,
                phaseHooks: CompletedImageRelayoutPhaseHooks.BeforeMeasure(() =>
                {
                    hookCalls++;
                    aliasInstalled = true;
                    imageSource.MeasureCustody.Placeholder = cellLayout;
                }),
                expectedPointerInputEpoch: document.Snapshot.PointerInputEpoch);
        }
        finally
        {
            try
            {
                // The image source must never retire the cell-owned layout.
                // Restore its exact prior owner field before draining candidates
                // or the document snapshot, even when the assertion path throws.
                if (aliasInstalled)
                    imageSource.MeasureCustody.Placeholder = originalPlaceholder;
            }
            finally
            {
                await DrainConstructionAsync(constructionContext);
            }
        }

        Assert.Same(originalPlaceholder, imageSource.MeasureCustody.GetLayoutState().Placeholder);
        Assert.Equal(1, hookCalls);
        Assert.Equal(PreparedImageRelayoutResult.Retry, result);
        Assert.Equal(revisionBefore, document.Snapshot.LayoutRevision);
        Assert.Same(sourceMapBefore, document.Snapshot.SourceMap);
        Assert.Same(semanticBefore, document.Snapshot.SemanticDocument);
        AssertRootsPreserved(rootsBefore, document.Snapshot);
        Assert.Contains(table.GetCellBoxes(), candidate => ReferenceEquals(candidate, cell));
        Assert.Same(cellLayout, GetInlineLayout(cell));
        RootLocalTableSourceState.AssertEqual(initialState, RootLocalTableSourceState.Capture(document.Snapshot, table));
        Assert.Equal(resolverCountsBefore, resolver.CaptureResolveCounts());
        AssertLeasesHeld(images, leasesBefore, leaseReferencesBefore);
    }

    [Fact]
    public async Task MoreThan256ZeroLengthBuiltInTableCellRunsRejectBeforeImageLeaseAcquisition()
    {
        await using var seed = new CompletedRasterPlacementCloneTests.PlacementFixture();
        using var session = new MarkdownPerformanceSession(MarkdownPerformanceOptions.Progressive);
        var resolver = RootLocalTableResolver.CreateStandard();
        await using RootLocalTableDocument document = RootLocalTableDocument.Build(seed, session, resolver,
            RootLocalTableSources.CreateStandard(resolver), FlowDirection.LeftToRight, 1.5f, lazy: false);
        TableBox table = FindTable(document.Snapshot);
        ImageBox[] images = EnumerateTableImages(table).ToArray();
        await LoadImagesAsync(images);

        InlineContainerBox emptyCell = table.GetCellBoxes().First(static candidate =>
            candidate.Runs.Count > 0 && candidate.PrivateRasterPlacementTextLength == 0 &&
            candidate.Runs.All(static run => run.Text.Length == 0 && run is TextRun));
        Assert.Equal(MarkdownElementKeys.TableCell, emptyCell.ElementKey);
        Rect priorBounds = emptyCell.Bounds;
        CanvasTextLayout priorLayout = GetInlineLayout(emptyCell);
        int missingRuns = InlineContainerBox.MaximumPrivateRasterPlacementRuns + 1 - emptyCell.Runs.Count;
        for (int index = 0; index < missingRuns; index++)
            emptyCell.Add(new TextRun(string.Empty));
        Assert.Equal(InlineContainerBox.MaximumPrivateRasterPlacementRuns + 1, emptyCell.Runs.Count);
        Assert.Equal(0, emptyCell.PrivateRasterPlacementTextLength);
        Assert.All(emptyCell.Runs, static run =>
        {
            Assert.IsType<TextRun>(run);
            Assert.Equal(string.Empty, run.Text);
            Assert.Equal(0, run.RenderedLength);
            Assert.Equal(string.Empty, run.LayoutText);
        });

        float cellWidth = (float)priorBounds.Width;
        Assert.True(cellWidth > 0);
        _ = emptyCell.Measure(cellWidth);
        emptyCell.Arrange((float)priorBounds.X, (float)priorBounds.Y, cellWidth);
        Assert.NotSame(priorLayout, GetInlineLayout(emptyCell));
        Assert.False(emptyCell.TryGetPrivateRasterPlacementCounts(
            document.Context, cellWidth, out _, out _, allowUnmeasuredTextOnly: false));

        SharedCanvasBitmapCache.Lease[] leasesBefore = images.Select(GetBitmapLease).ToArray();
        int[] leaseReferencesBefore = leasesBefore.Select(GetLeaseReferenceCount).ToArray();
        int[] resolverCountsBefore = resolver.CaptureResolveCounts();
        int hookCalls = 0;
        MarkdownLayoutContext? constructionContext = null;
        try
        {
            PreparedImageRelayoutResult result = document.Snapshot.TryRelayoutCompletedImageRoots(
                [table.BlockIndex], LayoutWidth, CancellationToken.None, out constructionContext,
                phaseHooks: CompletedImageRelayoutPhaseHooks.BeforeMeasure(() => hookCalls++),
                expectedPointerInputEpoch: document.Snapshot.PointerInputEpoch);
            Assert.Equal(PreparedImageRelayoutResult.Unavailable, result);
            Assert.Equal(0, hookCalls);
            Assert.Equal(resolverCountsBefore, resolver.CaptureResolveCounts());
            AssertLeasesHeld(images, leasesBefore, leaseReferencesBefore);
        }
        finally
        {
            await DrainConstructionAsync(constructionContext);
        }
    }

    [Fact]
    public async Task AggregateImageAndCellCapsRejectBeforeLeaseAcquisitionOrAdditionalResolution()
    {
        await using var seed = new CompletedRasterPlacementCloneTests.PlacementFixture();
        using var session = new MarkdownPerformanceSession(MarkdownPerformanceOptions.Progressive);
        var imageResolver = RootLocalTableResolver.CreateGenerated(imageCount: 33, columns: 33, bodyRows: 1);
        await using RootLocalTableDocument imageDocument = RootLocalTableDocument.Build(seed, session, imageResolver,
            RootLocalTableSources.CreateGenerated(imageResolver, columns: 33, bodyRows: 1, images: true),
            FlowDirection.LeftToRight, 1f, lazy: false);
        TableBox imageTable = FindTable(imageDocument.Snapshot);
        ImageBox[] imageTableImages = EnumerateTableImages(imageTable).ToArray();
        Assert.Equal(33, imageTableImages.Length);
        await LoadImagesAsync(imageTableImages);
        SharedCanvasBitmapCache.Lease[] imageLeases = imageTableImages.Select(GetBitmapLease).ToArray();
        int[] imageLeaseReferences = imageLeases.Select(GetLeaseReferenceCount).ToArray();
        int[] imageResolveCounts = imageResolver.CaptureResolveCounts();
        int imageHookCalls = 0;
        MarkdownLayoutContext? imageConstructionContext = null;
        try
        {
            PreparedImageRelayoutResult result = imageDocument.Snapshot.TryRelayoutCompletedImageRoots(
                [imageTable.BlockIndex], LayoutWidth, CancellationToken.None,
                out imageConstructionContext,
                phaseHooks: CompletedImageRelayoutPhaseHooks.BeforeMeasure(() => imageHookCalls++),
                expectedPointerInputEpoch: imageDocument.Snapshot.PointerInputEpoch);
            Assert.Equal(PreparedImageRelayoutResult.Unavailable, result);
            Assert.Equal(0, imageHookCalls);
            AssertLeasesHeld(imageTableImages, imageLeases, imageLeaseReferences);
            Assert.Equal(imageResolveCounts, imageResolver.CaptureResolveCounts());
        }
        finally
        {
            await DrainConstructionAsync(imageConstructionContext);
        }

        var cellResolver = RootLocalTableResolver.CreateGenerated(imageCount: 1, columns: 18, bodyRows: 14);
        await using RootLocalTableDocument cellDocument = RootLocalTableDocument.Build(seed, session, cellResolver,
            RootLocalTableSources.CreateGenerated(cellResolver, columns: 18, bodyRows: 14, images: true),
            FlowDirection.RightToLeft, 1.5f, lazy: false);
        TableBox cellTable = FindTable(cellDocument.Snapshot);
        InlineContainerBox[] cells = cellTable.GetCellBoxes().ToArray();
        Assert.True(cells.Length > 256);
        Assert.True(cells.Sum(static cell => cell.Runs.Count) > 256);
        ImageBox qualifiedImage = Assert.Single(EnumerateTableImages(cellTable));
        await LoadImageAsync(qualifiedImage);
        SharedCanvasBitmapCache.Lease cellLease = GetBitmapLease(qualifiedImage);
        int leaseReferences = GetLeaseReferenceCount(cellLease);
        int[] cellResolveCounts = cellResolver.CaptureResolveCounts();
        int cellHookCalls = 0;
        MarkdownLayoutContext? cellConstructionContext = null;
        try
        {
            PreparedImageRelayoutResult result = cellDocument.Snapshot.TryRelayoutCompletedImageRoots(
                [cellTable.BlockIndex], LayoutWidth, CancellationToken.None,
                out cellConstructionContext,
                phaseHooks: CompletedImageRelayoutPhaseHooks.BeforeMeasure(() => cellHookCalls++),
                expectedPointerInputEpoch: cellDocument.Snapshot.PointerInputEpoch);
            Assert.Equal(PreparedImageRelayoutResult.Unavailable, result);
            Assert.Equal(0, cellHookCalls);
            Assert.Same(cellLease, GetBitmapLease(qualifiedImage));
            Assert.Equal(leaseReferences, GetLeaseReferenceCount(cellLease));
            Assert.Equal(cellResolveCounts, cellResolver.CaptureResolveCounts());
        }
        finally
        {
            await DrainConstructionAsync(cellConstructionContext);
        }
    }

    public enum TablePreparationRace
    {
        Cancellation,
        PointerInput,
        LayoutRevision,
        HorizontalScroll,
    }
}
