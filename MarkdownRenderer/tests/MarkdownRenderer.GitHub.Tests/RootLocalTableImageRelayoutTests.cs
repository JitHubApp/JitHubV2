using System.Buffers;
using System.Collections.Concurrent;
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
using MarkdownRenderer.Selection;
using MarkdownRenderer.Theming;
using Markdig;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.UI.Xaml;
using Windows.Foundation;
using Windows.UI;
using Xunit;

namespace MarkdownRenderer.GitHub.Tests;

[Collection(SharedCanvasBitmapCacheTestCollection.Name)]
public sealed class RootLocalTableImageRelayoutTests
{
    private const float LayoutWidth = 320f;
    private const long AutomationGeneration = 8_422;

    [Fact]
    public async Task EveryMeasuredTableCellHasAnIndependentPrivatePlacementRecipe()
    {
        await using var seed = new CompletedRasterPlacementCloneTests.PlacementFixture();
        using var session = new MarkdownPerformanceSession(MarkdownPerformanceOptions.Progressive);
        var resolver = TableResolver.CreateStandard();
        await using ImageDocument document = BuildDocument(seed, session, resolver,
            CreateStandardTableSource(resolver), FlowDirection.LeftToRight, 1.5f, lazy: false);
        TableBox table = FindTable(document.Snapshot);
        foreach (ImageBox image in EnumerateTableImages(table))
            await LoadImageAsync(image);
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
                    Assert.True(cell.TryCaptureTableCellMeasuredPlacementTransfer(clone, width, out var transfer),
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
        var resolver = TableResolver.CreateStandard();
        string sourceText = CreateStandardTableSource(resolver);
        FlowDirection direction = rightToLeft ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
        await using ImageDocument optimized = BuildDocument(
            seed, session, resolver, sourceText, direction, rasterizationScale, lazy: false,
            layoutWidth: layoutWidth);
        await using ImageDocument fallback = BuildDocument(
            seed, session, resolver, sourceText, direction, rasterizationScale, lazy: false,
            layoutWidth: layoutWidth);

        TableBox table = FindTable(optimized.Snapshot);
        TableBox fallbackTable = FindTable(fallback.Snapshot);
        Assert.Equal((double)layoutWidth, optimized.Snapshot.Size.Width);
        Assert.True(table.BlockIndex > 0);
        Assert.Equal(table.GetCellInfos().Count(), table.GetCellBoxes().Count());
        Assert.Contains(table.GetCellBoxes(), static cell => cell.Runs.OfType<LinkRun>().Any());
        InlineRun[] tableRuns = table.GetCellBoxes().SelectMany(static cell => cell.Runs).ToArray();
        Assert.Contains(tableRuns, static run => HasStyleKey(run, MarkdownElementKeys.Strong));
        Assert.Contains(tableRuns, static run => HasStyleKey(run, MarkdownElementKeys.Emphasis));
        Assert.Contains(tableRuns, static run => HasStyleKey(run, MarkdownElementKeys.CodeInline));
        Assert.Contains(table.GetCellBoxes(), static cell =>
            cell.PrivateRasterPlacementTextLength == 0 &&
            cell.Runs.All(static run => run.Text.Length == 0));

        ImageBox[] images = EnumerateTableImages(table).ToArray();
        ImageBox[] fallbackImages = EnumerateTableImages(fallbackTable).ToArray();
        Assert.Equal(3, images.Length);
        Assert.Equal(images.Length, fallbackImages.Length);
        Assert.NotEqual(images[0].Url, images[1].Url);
        Assert.NotEqual(images[1].Url, images[2].Url);
        Rect[] initialCellBounds = CaptureCellBounds(optimized.Snapshot, table);
        float[] initialColumnWidths = CaptureColumnWidths(table);

        foreach (ImageBox image in images)
            await LoadImageAsync(image);
        foreach (ImageBox image in fallbackImages)
            await LoadImageAsync(image);

        if (table.CanScrollHorizontally)
        {
            double desiredOffset = table.HorizontalOffset + System.Math.Max(24d, table.HorizontalViewport * 0.35d);
            Assert.True(table.SetHorizontalOffset(desiredOffset));
            Assert.True(fallbackTable.SetHorizontalOffset(desiredOffset));
        }
        else
        {
            Assert.Equal(0d, table.HorizontalOffset);
            Assert.Equal(0d, fallbackTable.HorizontalOffset);
        }
        Assert.Equal(table.HorizontalOffset, fallbackTable.HorizontalOffset);

        BlockBox[] rootsBefore = optimized.Snapshot.Blocks.ToArray();
        InlineContainerBox[] cellsBefore = table.GetCellBoxes().ToArray();
        InlineRun[][] runsBefore = cellsBefore.Select(static cell => cell.Runs.ToArray()).ToArray();
        CanvasTextLayout[] cellLayoutsBefore = cellsBefore.Select(GetInlineLayout).ToArray();
        CanvasBitmap[] bitmapsBefore = images.Select(image => Assert.IsType<CanvasBitmap>(image.Bitmap)).ToArray();
        SharedCanvasBitmapCache.Lease[] leasesBefore = images.Select(GetBitmapLease).ToArray();
        Rect[] imageBoundsBefore = images.Select(static image => image.Bounds).ToArray();
        float[] imageWidthsBefore = images.Select(static image => image.MeasuredImageWidth).ToArray();
        float[] imageHeightsBefore = images.Select(static image => image.MeasuredImageHeight).ToArray();
        MarkdownSourceMap sourceMapBefore = optimized.Snapshot.SourceMap;
        MarkdownSemanticDocument semanticBefore = optimized.Snapshot.SemanticDocument;
        Size sizeBefore = optimized.Snapshot.Size;
        long revisionBefore = optimized.Snapshot.LayoutRevision;
        int[] resolverCountsBefore = resolver.Sources.Select(resolver.ResolveCountFor).ToArray();
        int[] fallbackIndices = [fallbackTable.BlockIndex];
        fallback.Snapshot.RelayoutChangedBlocks(fallbackIndices, layoutWidth, CancellationToken.None);

        MarkdownLayoutContext? constructionContext = null;
        try
        {
            PreparedImageRelayoutResult result = optimized.Snapshot.TryRelayoutCompletedImageRoots(
                [table.BlockIndex], layoutWidth, CancellationToken.None, out constructionContext,
                expectedPointerInputEpoch: optimized.Snapshot.PointerInputEpoch);

            Assert.Equal(PreparedImageRelayoutResult.Committed, result);
            Assert.Equal(revisionBefore + 1, optimized.Snapshot.LayoutRevision);
            Assert.Equal(fallback.Snapshot.Size, optimized.Snapshot.Size);
            bool hasFlexibleColumnSpace = layoutWidth > LayoutWidth;
            if (hasFlexibleColumnSpace)
                Assert.NotEqual(sizeBefore, optimized.Snapshot.Size);
            Assert.Same(sourceMapBefore, optimized.Snapshot.SourceMap);
            Assert.Same(semanticBefore, optimized.Snapshot.SemanticDocument);
            Assert.Equal(AutomationGeneration, optimized.Snapshot.AutomationDocumentGeneration);
            Assert.Equal(rootsBefore.Length, optimized.Snapshot.Blocks.Count);
            for (int index = 0; index < rootsBefore.Length; index++)
            {
                Assert.Same(rootsBefore[index], optimized.Snapshot.Blocks[index]);
                Assert.Equal(LayoutPlacementTestGeometry.ToLogical(fallback.Snapshot, fallback.Snapshot.Blocks[index]),
                    LayoutPlacementTestGeometry.ToLogical(optimized.Snapshot, optimized.Snapshot.Blocks[index]));
            }
            Assert.Same(table, optimized.Snapshot.Blocks.Single(root => ReferenceEquals(root, table)));
            Assert.Equal(fallbackTable.HorizontalOffset, table.HorizontalOffset);
            Assert.Equal(fallbackTable.HorizontalExtent, table.HorizontalExtent);
            Assert.Equal(fallbackTable.HorizontalViewport, table.HorizontalViewport);
            Assert.Equal(fallbackTable.CanScrollHorizontally, table.CanScrollHorizontally);
            Assert.Equal(initialCellBounds.Length, cellsBefore.Length);
            Assert.Equal(cellsBefore.Length, table.GetCellBoxes().Count());
            InlineContainerBox[] cellsAfter = table.GetCellBoxes().ToArray();
            InlineContainerBox[] fallbackCells = fallbackTable.GetCellBoxes().ToArray();
            bool comparedCaret = false;
            for (int cellIndex = 0; cellIndex < cellsBefore.Length; cellIndex++)
            {
                Assert.Same(cellsBefore[cellIndex], cellsAfter[cellIndex]);
                Assert.Equal(runsBefore[cellIndex].Length, cellsAfter[cellIndex].Runs.Count);
                for (int runIndex = 0; runIndex < runsBefore[cellIndex].Length; runIndex++)
                    Assert.Same(runsBefore[cellIndex][runIndex], cellsAfter[cellIndex].Runs[runIndex]);
                Assert.NotSame(cellLayoutsBefore[cellIndex], GetInlineLayout(cellsAfter[cellIndex]));
                Assert.Equal(LayoutPlacementTestGeometry.ToLogical(fallback.Snapshot, fallbackCells[cellIndex]),
                    LayoutPlacementTestGeometry.ToLogical(optimized.Snapshot, cellsAfter[cellIndex]));
                Rect[] expectedSelectionRects =
                    GetCellSelectionRects(fallback.Snapshot, fallbackCells[cellIndex]);
                Rect[] actualSelectionRects =
                    GetCellSelectionRects(optimized.Snapshot, cellsAfter[cellIndex]);
                AssertUnclippedVerticalSelectionExtentIsPreserved(
                    fallback.Snapshot, fallbackTable, fallbackCells[cellIndex], expectedSelectionRects);
                AssertUnclippedVerticalSelectionExtentIsPreserved(
                    optimized.Snapshot, table, cellsAfter[cellIndex], actualSelectionRects);
                if (!expectedSelectionRects.SequenceEqual(actualSelectionRects))
                {
                    Assert.Fail(
                        $"Cell {cellIndex} selection differs. Expected={FormatRects(expectedSelectionRects)}; " +
                        $"actual={FormatRects(actualSelectionRects)}.");
                }
                Assert.Equal(expectedSelectionRects, actualSelectionRects);

                Assert.Equal(
                    GetCellSemanticTextRects(fallback.Snapshot, fallbackCells[cellIndex]),
                    GetCellSemanticTextRects(optimized.Snapshot, cellsAfter[cellIndex]));
                if (!comparedCaret && cellsAfter[cellIndex].Runs.Any(static run => run.TextLength > 0))
                {
                    Assert.Equal(
                        GetCellEndpointCaretPoint(fallback.Snapshot, fallbackCells[cellIndex]),
                        GetCellEndpointCaretPoint(optimized.Snapshot, cellsAfter[cellIndex]));
                    comparedCaret = true;
                }
            }
            Assert.True(comparedCaret, "The table fixture must exercise a text caret endpoint.");

            Rect[] finalCellBounds = CaptureCellBounds(optimized.Snapshot, table);
            float[] finalColumnWidths = CaptureColumnWidths(table);
            Assert.Equal(CaptureColumnWidths(fallbackTable), finalColumnWidths);
            if (hasFlexibleColumnSpace)
            {
                Assert.False(initialCellBounds.SequenceEqual(finalCellBounds));
                Assert.True(initialColumnWidths.Where((width, index) =>
                        System.Math.Abs(width - finalColumnWidths[index]) > 0.5f).Any(),
                    "Decoded image aspect ratios must participate in table column rebalancing when flexible width is available.");
            }
            Assert.True(images[0].MeasuredImageWidth < 480f,
                "The wide decoded image must be measured against its narrower responsive table cell.");

            ImageBox[] afterImages = EnumerateTableImages(table).ToArray();
            Assert.Equal(images.Length, afterImages.Length);
            bool imageDimensionsWereReflowed = false;
            for (int imageIndex = 0; imageIndex < images.Length; imageIndex++)
            {
                Assert.Same(images[imageIndex], afterImages[imageIndex]);
                Assert.Same(bitmapsBefore[imageIndex], images[imageIndex].Bitmap);
                Assert.Same(leasesBefore[imageIndex], GetBitmapLease(images[imageIndex]));
                Assert.Equal(fallbackImages[imageIndex].MeasuredImageWidth,
                    images[imageIndex].MeasuredImageWidth);
                Assert.Equal(fallbackImages[imageIndex].MeasuredImageHeight,
                    images[imageIndex].MeasuredImageHeight);
                Assert.Equal(LayoutPlacementTestGeometry.ToLogical(fallback.Snapshot, fallbackImages[imageIndex]),
                    LayoutPlacementTestGeometry.ToLogical(optimized.Snapshot, images[imageIndex]));
                imageDimensionsWereReflowed |=
                    System.Math.Abs(imageWidthsBefore[imageIndex] - images[imageIndex].MeasuredImageWidth) > 0.5f ||
                    System.Math.Abs(imageHeightsBefore[imageIndex] - images[imageIndex].MeasuredImageHeight) > 0.5f;
            }
            if (hasFlexibleColumnSpace)
                Assert.True(imageDimensionsWereReflowed,
                    "Decoded image aspect ratios must change at least one committed inline image size when flexible width is available.");
            Assert.Equal(resolverCountsBefore, resolver.Sources.Select(resolver.ResolveCountFor).ToArray());

            // Build the native image plan before Paint can update inline bounds;
            // this is the same enumeration used by the control's publication path.
            var plan = new RasterPlacementImagePlan(
                optimized.Snapshot, optimized.Snapshot.Blocks, expectedCount: images.Length);
            Assert.Equal(images.Length, plan.PublicationImages.Length);
            for (int imageIndex = 0; imageIndex < images.Length; imageIndex++)
            {
                Assert.Same(images[imageIndex], plan.PublicationImages[imageIndex]);
                Assert.Equal(imageIndex, plan.Index.GetBlockOrdinal(imageIndex));
                Rect logicalBounds = LayoutPlacementTestGeometry.ToLogical(optimized.Snapshot, images[imageIndex]);
                Assert.Equal((logicalBounds.Top, logicalBounds.Bottom),
                    plan.Index.GetBounds(imageIndex));
            }
            Assert.Equal(Paint(optimized.Snapshot, seed.Device, rasterizationScale),
                Paint(fallback.Snapshot, seed.Device, rasterizationScale));
        }
        finally
        {
            if (constructionContext is not null)
                await constructionContext.DrainOwnedRetirementsAsync().WaitAsync(TimeSpan.FromSeconds(10));
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
        var resolver = TableResolver.CreateStandard(narrowImages: true);
        string sourceText = CreateStandardTableSource(resolver, forceOverflow: true)
            .Replace("**Preview**", "**P**", StringComparison.Ordinal);
        await using ImageDocument optimized = BuildDocument(seed, session, resolver, sourceText,
            direction, 1.5f, lazy: false);
        await using ImageDocument fallback = BuildDocument(seed, session, resolver, sourceText,
            direction, 1.5f, lazy: false);

        TableBox table = FindTable(optimized.Snapshot);
        TableBox fallbackTable = FindTable(fallback.Snapshot);
        ImageBox[] images = EnumerateTableImages(table).ToArray();
        ImageBox[] fallbackImages = EnumerateTableImages(fallbackTable).ToArray();
        foreach (ImageBox image in images.Concat(fallbackImages))
            await LoadImageAsync(image);

        double previousMaximum = System.Math.Max(0, table.HorizontalExtent - table.HorizontalViewport);
        Assert.True(table.CanScrollHorizontally);
        Assert.True(previousMaximum > 0);
        Assert.True(table.SetHorizontalOffset(previousMaximum));
        Assert.True(fallbackTable.SetHorizontalOffset(previousMaximum));
        Assert.True(table.HorizontalOffset > 0);

        BlockBox[] rootsBefore = optimized.Snapshot.Blocks.ToArray();
        InlineContainerBox[] cellsBefore = table.GetCellBoxes().ToArray();
        InlineRun[][] runsBefore = cellsBefore.Select(static cell => cell.Runs.ToArray()).ToArray();
        CanvasTextLayout[] layoutsBefore = cellsBefore.Select(GetInlineLayout).ToArray();
        CanvasBitmap[] bitmapsBefore = images.Select(image => Assert.IsType<CanvasBitmap>(image.Bitmap)).ToArray();
        SharedCanvasBitmapCache.Lease[] leasesBefore = images.Select(GetBitmapLease).ToArray();
        MarkdownSourceMap sourceMapBefore = optimized.Snapshot.SourceMap;
        MarkdownSemanticDocument semanticBefore = optimized.Snapshot.SemanticDocument;
        int[] resolverCountsBefore = resolver.Sources.Select(resolver.ResolveCountFor).ToArray();
        fallback.Snapshot.RelayoutChangedBlocks([fallbackTable.BlockIndex], LayoutWidth, CancellationToken.None);

        MarkdownLayoutContext? constructionContext = null;
        try
        {
            PreparedImageRelayoutResult result = optimized.Snapshot.TryRelayoutCompletedImageRoots(
                [table.BlockIndex], LayoutWidth, CancellationToken.None, out constructionContext,
                expectedPointerInputEpoch: optimized.Snapshot.PointerInputEpoch);
            Assert.Equal(PreparedImageRelayoutResult.Committed, result);
            Assert.Same(sourceMapBefore, optimized.Snapshot.SourceMap);
            Assert.Same(semanticBefore, optimized.Snapshot.SemanticDocument);
            Assert.Equal(fallback.Snapshot.Size, optimized.Snapshot.Size);
            double fallbackMaximum = System.Math.Max(0,
                fallbackTable.HorizontalExtent - fallbackTable.HorizontalViewport);
            Assert.Equal(System.Math.Clamp(previousMaximum, 0, fallbackMaximum), table.HorizontalOffset);
            Assert.Equal(fallbackTable.HorizontalOffset, table.HorizontalOffset);
            if (fallbackMaximum == previousMaximum)
                Assert.Equal(previousMaximum, table.HorizontalOffset);
            Assert.Equal(fallbackTable.HorizontalExtent, table.HorizontalExtent);
            Assert.Equal(fallbackTable.HorizontalViewport, table.HorizontalViewport);

            for (int rootIndex = 0; rootIndex < rootsBefore.Length; rootIndex++)
            {
                Assert.Same(rootsBefore[rootIndex], optimized.Snapshot.Blocks[rootIndex]);
                Assert.Equal(LayoutPlacementTestGeometry.ToLogical(fallback.Snapshot, fallback.Snapshot.Blocks[rootIndex]),
                    LayoutPlacementTestGeometry.ToLogical(optimized.Snapshot, optimized.Snapshot.Blocks[rootIndex]));
            }
            Assert.Same(table, Assert.Single(optimized.Snapshot.Blocks, root => ReferenceEquals(root, table)));
            InlineContainerBox[] cellsAfter = table.GetCellBoxes().ToArray();
            InlineContainerBox[] fallbackCells = fallbackTable.GetCellBoxes().ToArray();
            Assert.Equal(cellsBefore.Length, cellsAfter.Length);
            for (int cellIndex = 0; cellIndex < cellsBefore.Length; cellIndex++)
            {
                Assert.Same(cellsBefore[cellIndex], cellsAfter[cellIndex]);
                Assert.Equal(runsBefore[cellIndex].Length, cellsAfter[cellIndex].Runs.Count);
                for (int runIndex = 0; runIndex < runsBefore[cellIndex].Length; runIndex++)
                    Assert.Same(runsBefore[cellIndex][runIndex], cellsAfter[cellIndex].Runs[runIndex]);
                Assert.NotSame(layoutsBefore[cellIndex], GetInlineLayout(cellsAfter[cellIndex]));
                Assert.Equal(LayoutPlacementTestGeometry.ToLogical(fallback.Snapshot, fallbackCells[cellIndex]),
                    LayoutPlacementTestGeometry.ToLogical(optimized.Snapshot, cellsAfter[cellIndex]));
                Assert.Equal(
                    GetCellSelectionRects(fallback.Snapshot, fallbackCells[cellIndex]),
                    GetCellSelectionRects(optimized.Snapshot, cellsAfter[cellIndex]));
            }
            for (int imageIndex = 0; imageIndex < images.Length; imageIndex++)
            {
                Assert.Same(images[imageIndex], EnumerateTableImages(table).ElementAt(imageIndex));
                Assert.Same(bitmapsBefore[imageIndex], images[imageIndex].Bitmap);
                Assert.Same(leasesBefore[imageIndex], GetBitmapLease(images[imageIndex]));
                Assert.Equal(LayoutPlacementTestGeometry.ToLogical(fallback.Snapshot, fallbackImages[imageIndex]),
                    LayoutPlacementTestGeometry.ToLogical(optimized.Snapshot, images[imageIndex]));
                Assert.Equal(fallbackImages[imageIndex].MeasuredImageWidth,
                    images[imageIndex].MeasuredImageWidth);
                Assert.Equal(fallbackImages[imageIndex].MeasuredImageHeight,
                    images[imageIndex].MeasuredImageHeight);
            }
            Assert.Equal(resolverCountsBefore, resolver.Sources.Select(resolver.ResolveCountFor).ToArray());

            var plan = new RasterPlacementImagePlan(
                optimized.Snapshot, optimized.Snapshot.Blocks, expectedCount: images.Length);
            Assert.Equal(images.Length, plan.PublicationImages.Length);
            for (int imageIndex = 0; imageIndex < images.Length; imageIndex++)
                Assert.Same(images[imageIndex], plan.PublicationImages[imageIndex]);
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
    public async Task TableImageChildBlockIndicesCommitTheirOwningTableWithFallbackParity()
    {
        await using var seed = new CompletedRasterPlacementCloneTests.PlacementFixture(
            forceSoftwareRenderer: true);
        using var session = new MarkdownPerformanceSession(MarkdownPerformanceOptions.Progressive);
        var resolver = TableResolver.CreateStandard();
        string sourceText = CreateStandardTableSource(resolver);
        await using ImageDocument optimized = BuildDocument(seed, session, resolver, sourceText,
            FlowDirection.LeftToRight, 1.5f, lazy: false, layoutWidth: 640f);
        await using ImageDocument fallback = BuildDocument(seed, session, resolver, sourceText,
            FlowDirection.LeftToRight, 1.5f, lazy: false, layoutWidth: 640f);

        TableBox table = FindTable(optimized.Snapshot);
        TableBox fallbackTable = FindTable(fallback.Snapshot);
        ImageBox[] images = EnumerateTableImages(table).ToArray();
        ImageBox[] fallbackImages = EnumerateTableImages(fallbackTable).ToArray();
        Assert.Equal(3, images.Length);
        foreach (ImageBox image in images.Concat(fallbackImages))
            await LoadImageAsync(image);

        int[] imageChildBlockIndices = images.Select(static image => image.BlockIndex).Distinct().ToArray();
        Assert.Equal(images.Length, imageChildBlockIndices.Length);
        Assert.DoesNotContain(table.BlockIndex, imageChildBlockIndices);

        BlockBox[] rootsBefore = optimized.Snapshot.Blocks.ToArray();
        InlineContainerBox[] cellsBefore = table.GetCellBoxes().ToArray();
        InlineRun[][] runsBefore = cellsBefore.Select(static cell => cell.Runs.ToArray()).ToArray();
        CanvasTextLayout[] layoutsBefore = cellsBefore.Select(GetInlineLayout).ToArray();
        CanvasBitmap[] bitmapsBefore = images.Select(image => Assert.IsType<CanvasBitmap>(image.Bitmap)).ToArray();
        SharedCanvasBitmapCache.Lease[] leasesBefore = images.Select(GetBitmapLease).ToArray();
        int[] leaseReferencesBefore = leasesBefore.Select(GetLeaseReferenceCount).ToArray();
        MarkdownSourceMap sourceMapBefore = optimized.Snapshot.SourceMap;
        MarkdownSemanticDocument semanticBefore = optimized.Snapshot.SemanticDocument;
        long revisionBefore = optimized.Snapshot.LayoutRevision;
        int[] resolverCountsBefore = resolver.Sources.Select(resolver.ResolveCountFor).ToArray();
        fallback.Snapshot.RelayoutChangedBlocks([fallbackTable.BlockIndex], 640f, CancellationToken.None);

        MarkdownLayoutContext? constructionContext = null;
        try
        {
            PreparedImageRelayoutResult result = optimized.Snapshot.TryRelayoutCompletedImageRoots(
                imageChildBlockIndices, 640f, CancellationToken.None, out constructionContext,
                expectedPointerInputEpoch: optimized.Snapshot.PointerInputEpoch);
            Assert.Equal(PreparedImageRelayoutResult.Committed, result);
            Assert.Equal(revisionBefore + 1, optimized.Snapshot.LayoutRevision);
            Assert.Same(sourceMapBefore, optimized.Snapshot.SourceMap);
            Assert.Same(semanticBefore, optimized.Snapshot.SemanticDocument);
            Assert.Equal(fallback.Snapshot.Size, optimized.Snapshot.Size);
            Assert.Equal(rootsBefore.Length, optimized.Snapshot.Blocks.Count);
            for (int rootIndex = 0; rootIndex < rootsBefore.Length; rootIndex++)
            {
                Assert.Same(rootsBefore[rootIndex], optimized.Snapshot.Blocks[rootIndex]);
                Assert.Equal(LayoutPlacementTestGeometry.ToLogical(fallback.Snapshot, fallback.Snapshot.Blocks[rootIndex]),
                    LayoutPlacementTestGeometry.ToLogical(optimized.Snapshot, optimized.Snapshot.Blocks[rootIndex]));
            }
            Assert.Same(table, FindTable(optimized.Snapshot));
            Assert.Equal(fallbackTable.HorizontalOffset, table.HorizontalOffset);
            Assert.Equal(fallbackTable.HorizontalExtent, table.HorizontalExtent);
            Assert.Equal(fallbackTable.HorizontalViewport, table.HorizontalViewport);

            InlineContainerBox[] cellsAfter = table.GetCellBoxes().ToArray();
            InlineContainerBox[] fallbackCells = fallbackTable.GetCellBoxes().ToArray();
            Assert.Equal(cellsBefore.Length, cellsAfter.Length);
            for (int cellIndex = 0; cellIndex < cellsBefore.Length; cellIndex++)
            {
                Assert.Same(cellsBefore[cellIndex], cellsAfter[cellIndex]);
                Assert.Equal(runsBefore[cellIndex].Length, cellsAfter[cellIndex].Runs.Count);
                for (int runIndex = 0; runIndex < runsBefore[cellIndex].Length; runIndex++)
                    Assert.Same(runsBefore[cellIndex][runIndex], cellsAfter[cellIndex].Runs[runIndex]);
                Assert.NotSame(layoutsBefore[cellIndex], GetInlineLayout(cellsAfter[cellIndex]));
                Assert.Equal(LayoutPlacementTestGeometry.ToLogical(fallback.Snapshot, fallbackCells[cellIndex]),
                    LayoutPlacementTestGeometry.ToLogical(optimized.Snapshot, cellsAfter[cellIndex]));
                Assert.Equal(
                    GetCellSelectionRects(fallback.Snapshot, fallbackCells[cellIndex]),
                    GetCellSelectionRects(optimized.Snapshot, cellsAfter[cellIndex]));
            }

            ImageBox[] imagesAfter = EnumerateTableImages(table).ToArray();
            Assert.Equal(images.Length, imagesAfter.Length);
            for (int imageIndex = 0; imageIndex < images.Length; imageIndex++)
            {
                Assert.Same(images[imageIndex], imagesAfter[imageIndex]);
                Assert.Same(bitmapsBefore[imageIndex], imagesAfter[imageIndex].Bitmap);
                Assert.Same(leasesBefore[imageIndex], GetBitmapLease(imagesAfter[imageIndex]));
                Assert.Equal(LayoutPlacementTestGeometry.ToLogical(fallback.Snapshot, fallbackImages[imageIndex]),
                    LayoutPlacementTestGeometry.ToLogical(optimized.Snapshot, imagesAfter[imageIndex]));
                Assert.Equal(fallbackImages[imageIndex].MeasuredImageWidth,
                    imagesAfter[imageIndex].MeasuredImageWidth);
                Assert.Equal(fallbackImages[imageIndex].MeasuredImageHeight,
                    imagesAfter[imageIndex].MeasuredImageHeight);
            }
            Assert.Equal(resolverCountsBefore, resolver.Sources.Select(resolver.ResolveCountFor).ToArray());

            var plan = new RasterPlacementImagePlan(
                optimized.Snapshot, optimized.Snapshot.Blocks, expectedCount: images.Length);
            Assert.Equal(images.Length, plan.PublicationImages.Length);
            for (int imageIndex = 0; imageIndex < images.Length; imageIndex++)
                Assert.Same(images[imageIndex], plan.PublicationImages[imageIndex]);
            Assert.Equal(Paint(optimized.Snapshot, seed.Device, 1.5f),
                Paint(fallback.Snapshot, seed.Device, 1.5f));
        }
        finally
        {
            if (constructionContext is not null)
                await constructionContext.DrainOwnedRetirementsAsync().WaitAsync(TimeSpan.FromSeconds(10));
        }

        for (int index = 0; index < leasesBefore.Length; index++)
            Assert.Equal(leaseReferencesBefore[index], GetLeaseReferenceCount(leasesBefore[index]));
    }

    [Fact]
    public async Task CompletedFullSvgTableImagesPreserveProviderAndSemanticOwnersWithFallbackParity()
    {
        await using var seed = new CompletedRasterPlacementCloneTests.PlacementFixture(
            forceSoftwareRenderer: true);
        using var session = new MarkdownPerformanceSession(MarkdownPerformanceOptions.Progressive);
        var resolver = new SvgTableResolver();
        var renderer = new TableSvgRenderer();
        string sourceText = CreateSvgTableSource(resolver);
        await using ImageDocument optimized = BuildDocument(seed, session, resolver, sourceText,
            FlowDirection.LeftToRight, 1.5f, lazy: false, layoutWidth: 640f, svgRenderer: renderer);
        await using ImageDocument fallback = BuildDocument(seed, session, resolver, sourceText,
            FlowDirection.LeftToRight, 1.5f, lazy: false, layoutWidth: 640f, svgRenderer: renderer);

        LayoutSnapshot snapshotBefore = optimized.Snapshot;
        TableBox table = FindTable(snapshotBefore);
        TableBox fallbackTable = FindTable(fallback.Snapshot);
        InlineContainerBox[] cellsBefore = table.GetCellBoxes().ToArray();
        Assert.Contains(cellsBefore, static cell => cell.Runs.OfType<LinkRun>().Any());
        Assert.Contains(cellsBefore.SelectMany(static cell => cell.Runs),
            static run => HasStyleKey(run, MarkdownElementKeys.Strong));
        Assert.Contains(cellsBefore.SelectMany(static cell => cell.Runs),
            static run => HasStyleKey(run, MarkdownElementKeys.Emphasis));
        Assert.Contains(cellsBefore.SelectMany(static cell => cell.Runs),
            static run => HasStyleKey(run, MarkdownElementKeys.CodeInline));

        ImageBox[] images = EnumerateTableImages(table).ToArray();
        ImageBox[] fallbackImages = EnumerateTableImages(fallbackTable).ToArray();
        Assert.Equal(2, images.Length);
        Assert.Equal(images.Length, fallbackImages.Length);
        Assert.All(images, static image => Assert.True(image.IsSvg));
        Assert.All(fallbackImages, static image => Assert.True(image.IsSvg));
        Assert.Equal(new[] { "wide SVG alt", "tall SVG alt" }, images.Select(static image => image.Alt));

        foreach (ImageBox image in images.Concat(fallbackImages))
            await LoadImageAsync(image);

        Assert.All(images, static image => Assert.False(image.UsesSvgTilesForAutomation));
        Assert.All(fallbackImages, static image => Assert.False(image.UsesSvgTilesForAutomation));
        Assert.True(renderer.OpenCount > 0);
        Assert.True(renderer.FullRenderCount > 0);
        Assert.Equal(0, renderer.TileRenderCount);
        Assert.All(images, static image => Assert.Equal(MarkdownImageAccessibilityState.Loaded,
            image.AccessibilityState));

        BlockBox[] rootsBefore = snapshotBefore.Blocks.ToArray();
        Rect[] rootBoundsBefore = rootsBefore.Select(static root => root.Bounds).ToArray();
        float[] columnWidthsBefore = CaptureColumnWidths(table);
        InlineRun[][] runsBefore = cellsBefore.Select(static cell => cell.Runs.ToArray()).ToArray();
        CanvasTextLayout[] layoutsBefore = cellsBefore.Select(GetInlineLayout).ToArray();
        CanvasBitmap[] bitmapsBefore = images.Select(image => Assert.IsType<CanvasBitmap>(image.Bitmap)).ToArray();
        SharedCanvasBitmapCache.Lease[] leasesBefore = images.Select(GetBitmapLease).ToArray();
        int[] leaseCountsBefore = leasesBefore.Select(GetLeaseReferenceCount).ToArray();
        float[] imageWidthsBefore = images.Select(static image => image.MeasuredImageWidth).ToArray();
        float[] imageHeightsBefore = images.Select(static image => image.MeasuredImageHeight).ToArray();
        MarkdownSvgDocumentInfo[] infoBefore = images.Select(image =>
            GetPrivateField<MarkdownSvgDocumentInfo>(image, "_svgInfo")).ToArray();
        long[] providerGenerationsBefore = images.Select(image => GetPrivateField<long>(image, "_svgProviderCacheGeneration")).ToArray();
        long[] rendererIdentitiesBefore = images.Select(image => GetPrivateField<long>(image, "_svgRendererIdentity")).ToArray();
        Assert.Equal<double?>(480d, infoBefore[0].IntrinsicWidthDips);
        Assert.Equal<double?>(96d, infoBefore[0].IntrinsicHeightDips);
        Assert.Equal<double?>(96d, infoBefore[1].IntrinsicWidthDips);
        Assert.Equal<double?>(240d, infoBefore[1].IntrinsicHeightDips);
        (int Width, int Height)[] rasterSizesBefore = images.Select(static image => image.SvgRasterPixelSize).ToArray();
        string?[] titlesBefore = images.Select(static image => image.SvgTitle).ToArray();
        string?[] descriptionsBefore = images.Select(static image => image.SvgDesc).ToArray();
        string[] altBefore = images.Select(static image => image.Alt).ToArray();
        Assert.Equal("Wide root title", titlesBefore[0]);
        Assert.Equal("Tall root title", titlesBefore[1]);
        Assert.Equal("Wide provider description", descriptionsBefore[0]);
        Assert.Equal("Tall provider description", descriptionsBefore[1]);
        MarkdownSourceMap sourceMapBefore = snapshotBefore.SourceMap;
        MarkdownSemanticDocument semanticsBefore = snapshotBefore.SemanticDocument;
        long revisionBefore = snapshotBefore.LayoutRevision;
        long generationBefore = snapshotBefore.AutomationDocumentGeneration;
        int openCountBefore = renderer.OpenCount;
        int fullRenderCountBefore = renderer.FullRenderCount;
        int[] resolverCountsBefore = resolver.Sources.Select(resolver.ResolveCountFor).ToArray();

        fallback.Snapshot.RelayoutChangedBlocks([fallbackTable.BlockIndex], 640f, CancellationToken.None);
        int[] childImageIndices = images.Select(static image => image.BlockIndex).Distinct().ToArray();
        Assert.Equal(images.Length, childImageIndices.Length);
        Assert.DoesNotContain(table.BlockIndex, childImageIndices);

        MarkdownLayoutContext? constructionContext = null;
        try
        {
            PreparedImageRelayoutResult result = snapshotBefore.TryRelayoutCompletedImageRoots(
                childImageIndices, 640f, CancellationToken.None, out constructionContext,
                expectedPointerInputEpoch: snapshotBefore.PointerInputEpoch);
            Assert.Equal(PreparedImageRelayoutResult.Committed, result);
            Assert.Same(snapshotBefore, optimized.Snapshot);
            Assert.Equal(revisionBefore + 1, snapshotBefore.LayoutRevision);
            Assert.Equal(generationBefore, snapshotBefore.AutomationDocumentGeneration);
            Assert.Same(sourceMapBefore, snapshotBefore.SourceMap);
            Assert.Same(semanticsBefore, snapshotBefore.SemanticDocument);
            Assert.Equal(fallback.Snapshot.Size, snapshotBefore.Size);
            Assert.Equal(rootBoundsBefore.Length, snapshotBefore.Blocks.Count);
            for (int rootIndex = 0; rootIndex < rootsBefore.Length; rootIndex++)
            {
                Assert.Same(rootsBefore[rootIndex], snapshotBefore.Blocks[rootIndex]);
                Assert.Equal(LayoutPlacementTestGeometry.ToLogical(fallback.Snapshot, fallback.Snapshot.Blocks[rootIndex]),
                    LayoutPlacementTestGeometry.ToLogical(snapshotBefore, snapshotBefore.Blocks[rootIndex]));
            }
            Assert.Equal(LayoutPlacementTestGeometry.ToLogical(fallback.Snapshot, fallbackTable),
                LayoutPlacementTestGeometry.ToLogical(snapshotBefore, table));
            Assert.Equal(fallbackTable.HorizontalOffset, table.HorizontalOffset);
            Assert.Equal(fallbackTable.HorizontalExtent, table.HorizontalExtent);
            Assert.Equal(fallbackTable.HorizontalViewport, table.HorizontalViewport);

            InlineContainerBox[] cellsAfter = table.GetCellBoxes().ToArray();
            InlineContainerBox[] fallbackCellsAfter = fallbackTable.GetCellBoxes().ToArray();
            Assert.Equal(cellsBefore.Length, cellsAfter.Length);
            for (int cellIndex = 0; cellIndex < cellsBefore.Length; cellIndex++)
            {
                Assert.Same(cellsBefore[cellIndex], cellsAfter[cellIndex]);
                Assert.Equal(runsBefore[cellIndex].Length, cellsAfter[cellIndex].Runs.Count);
                for (int runIndex = 0; runIndex < runsBefore[cellIndex].Length; runIndex++)
                    Assert.Same(runsBefore[cellIndex][runIndex], cellsAfter[cellIndex].Runs[runIndex]);
                Assert.NotSame(layoutsBefore[cellIndex], GetInlineLayout(cellsAfter[cellIndex]));
                Assert.Equal(LayoutPlacementTestGeometry.ToLogical(fallback.Snapshot, fallbackCellsAfter[cellIndex]),
                    LayoutPlacementTestGeometry.ToLogical(snapshotBefore, cellsAfter[cellIndex]));
                // Column rebalancing moves the same semantic selection. Its
                // rectangles must match the authoritative fallback, not the
                // source's obsolete pre-completion cell positions.
                Assert.Equal(
                    GetCellSelectionRects(fallback.Snapshot, fallbackCellsAfter[cellIndex]),
                    GetCellSelectionRects(snapshotBefore, cellsAfter[cellIndex]));
            }
            Assert.Equal(CaptureCellBounds(fallback.Snapshot, fallbackTable),
                CaptureCellBounds(snapshotBefore, table));
            float[] finalColumnWidths = CaptureColumnWidths(table);
            Assert.Equal(CaptureColumnWidths(fallbackTable), finalColumnWidths);
            Assert.True(columnWidthsBefore.Where((width, index) =>
                System.Math.Abs(width - finalColumnWidths[index]) > 0.5f).Any(),
                "Provider intrinsic sizes must feed the measured table columns.");
            ImageBox[] imagesAfter = EnumerateTableImages(table).ToArray();
            Assert.Equal(images.Length, imagesAfter.Length);
            bool imageDimensionsChanged = false;
            for (int imageIndex = 0; imageIndex < images.Length; imageIndex++)
            {
                Assert.Same(images[imageIndex], imagesAfter[imageIndex]);
                Assert.Same(bitmapsBefore[imageIndex], imagesAfter[imageIndex].Bitmap);
                Assert.Same(leasesBefore[imageIndex], GetBitmapLease(imagesAfter[imageIndex]));
                Assert.Same(infoBefore[imageIndex], GetPrivateField<MarkdownSvgDocumentInfo>(
                    imagesAfter[imageIndex], "_svgInfo"));
                Assert.Equal(providerGenerationsBefore[imageIndex],
                    GetPrivateField<long>(imagesAfter[imageIndex], "_svgProviderCacheGeneration"));
                Assert.Equal(rendererIdentitiesBefore[imageIndex],
                    GetPrivateField<long>(imagesAfter[imageIndex], "_svgRendererIdentity"));
                Assert.Equal(rasterSizesBefore[imageIndex], imagesAfter[imageIndex].SvgRasterPixelSize);
                Assert.Equal(titlesBefore[imageIndex], imagesAfter[imageIndex].SvgTitle);
                Assert.Equal(descriptionsBefore[imageIndex], imagesAfter[imageIndex].SvgDesc);
                Assert.Equal(altBefore[imageIndex], imagesAfter[imageIndex].Alt);
                Assert.Equal(LayoutPlacementTestGeometry.ToLogical(fallback.Snapshot, fallbackImages[imageIndex]),
                    LayoutPlacementTestGeometry.ToLogical(snapshotBefore, imagesAfter[imageIndex]));
                Assert.Equal(fallbackImages[imageIndex].MeasuredImageWidth,
                    imagesAfter[imageIndex].MeasuredImageWidth);
                Assert.Equal(fallbackImages[imageIndex].MeasuredImageHeight,
                    imagesAfter[imageIndex].MeasuredImageHeight);
                imageDimensionsChanged |=
                    System.Math.Abs(imageWidthsBefore[imageIndex] - imagesAfter[imageIndex].MeasuredImageWidth) > 0.5f ||
                    System.Math.Abs(imageHeightsBefore[imageIndex] - imagesAfter[imageIndex].MeasuredImageHeight) > 0.5f;
            }
            Assert.True(imageDimensionsChanged,
                "The opened SVG intrinsic dimensions must participate in cell/image remeasurement.");
            Assert.Equal(openCountBefore, renderer.OpenCount);
            Assert.Equal(fullRenderCountBefore, renderer.FullRenderCount);
            Assert.Equal(0, renderer.CacheGeneration);
            Assert.Equal(resolverCountsBefore, resolver.Sources.Select(resolver.ResolveCountFor).ToArray());

            // Enumerate the publish plan before painting can update image bounds.
            var plan = new RasterPlacementImagePlan(
                snapshotBefore, snapshotBefore.Blocks, expectedCount: images.Length);
            Assert.Equal(images.Length, plan.PublicationImages.Length);
            for (int index = 0; index < images.Length; index++)
                Assert.Same(images[index], plan.PublicationImages[index]);
            Assert.Equal(Paint(snapshotBefore, seed.Device, 1.5f), Paint(fallback.Snapshot, seed.Device, 1.5f));
        }
        finally
        {
            if (constructionContext is not null)
                await constructionContext.DrainOwnedRetirementsAsync().WaitAsync(TimeSpan.FromSeconds(10));
        }

        // Prepared clones borrow bitmap leases until their asynchronous
        // retirement completes. Assert the exact final ownership count after
        // the same barrier the production pipeline awaits, not while cleanup
        // may still be queued on the thread pool.
        for (int imageIndex = 0; imageIndex < images.Length; imageIndex++)
            Assert.Equal(leaseCountsBefore[imageIndex], GetLeaseReferenceCount(leasesBefore[imageIndex]));
    }

    [Fact]
    public async Task TableSvgProviderGenerationChangeAfterPrivateMeasureRejectsWithoutSourceMutation()
    {
        await using var seed = new CompletedRasterPlacementCloneTests.PlacementFixture(
            forceSoftwareRenderer: true);
        using var session = new MarkdownPerformanceSession(MarkdownPerformanceOptions.Progressive);
        var resolver = new SvgTableResolver();
        var renderer = new TableSvgRenderer();
        await using ImageDocument document = BuildDocument(seed, session, resolver,
            CreateSvgTableSource(resolver), FlowDirection.LeftToRight, 1.5f, lazy: false,
            layoutWidth: 640f, svgRenderer: renderer);
        LayoutSnapshot snapshot = document.Snapshot;
        TableBox table = FindTable(snapshot);
        ImageBox[] images = EnumerateTableImages(table).ToArray();
        foreach (ImageBox image in images)
            await LoadImageAsync(image);

        BlockBox[] rootsBefore = snapshot.Blocks.ToArray();
        Rect[] rootBoundsBefore = rootsBefore.Select(static root => root.Bounds).ToArray();
        InlineContainerBox[] cellsBefore = table.GetCellBoxes().ToArray();
        InlineRun[][] runsBefore = cellsBefore.Select(static cell => cell.Runs.ToArray()).ToArray();
        CanvasTextLayout[] layoutsBefore = cellsBefore.Select(GetInlineLayout).ToArray();
        Rect[][] selectionBefore = cellsBefore.Select(cell => GetCellSelectionRects(snapshot, cell)).ToArray();
        CanvasBitmap[] bitmapsBefore = images.Select(image => Assert.IsType<CanvasBitmap>(image.Bitmap)).ToArray();
        SharedCanvasBitmapCache.Lease[] leasesBefore = images.Select(GetBitmapLease).ToArray();
        int[] leaseCountsBefore = leasesBefore.Select(GetLeaseReferenceCount).ToArray();
        MarkdownSvgDocumentInfo[] infoBefore = images.Select(image =>
            GetPrivateField<MarkdownSvgDocumentInfo>(image, "_svgInfo")).ToArray();
        long[] providerGenerationsBefore = images.Select(image => GetPrivateField<long>(image, "_svgProviderCacheGeneration")).ToArray();
        MarkdownSourceMap sourceMapBefore = snapshot.SourceMap;
        MarkdownSemanticDocument semanticsBefore = snapshot.SemanticDocument;
        long revisionBefore = snapshot.LayoutRevision;
        int opensBefore = renderer.OpenCount;
        int rendersBefore = renderer.FullRenderCount;
        int[] resolverCountsBefore = resolver.Sources.Select(resolver.ResolveCountFor).ToArray();

        MarkdownLayoutContext? constructionContext = null;
        try
        {
            PreparedImageRelayoutResult result = snapshot.TryRelayoutCompletedImageRoots(
                images.Select(static image => image.BlockIndex).Distinct().ToArray(), 640f,
                CancellationToken.None, out constructionContext,
                beforeMeasureOutsideGateForTests: renderer.AdvanceCacheGeneration,
                expectedPointerInputEpoch: snapshot.PointerInputEpoch);
            Assert.Equal(PreparedImageRelayoutResult.Retry, result);
            Assert.Equal(revisionBefore, snapshot.LayoutRevision);
            Assert.Same(sourceMapBefore, snapshot.SourceMap);
            Assert.Same(semanticsBefore, snapshot.SemanticDocument);
            Assert.Equal(rootsBefore.Length, snapshot.Blocks.Count);
            for (int rootIndex = 0; rootIndex < rootsBefore.Length; rootIndex++)
            {
                Assert.Same(rootsBefore[rootIndex], snapshot.Blocks[rootIndex]);
                Assert.Equal(rootBoundsBefore[rootIndex], snapshot.Blocks[rootIndex].Bounds);
            }
            InlineContainerBox[] cellsAfter = table.GetCellBoxes().ToArray();
            Assert.Equal(cellsBefore.Length, cellsAfter.Length);
            for (int cellIndex = 0; cellIndex < cellsBefore.Length; cellIndex++)
            {
                Assert.Same(cellsBefore[cellIndex], cellsAfter[cellIndex]);
                Assert.Same(layoutsBefore[cellIndex], GetInlineLayout(cellsAfter[cellIndex]));
                Assert.Equal(selectionBefore[cellIndex], GetCellSelectionRects(snapshot, cellsAfter[cellIndex]));
                Assert.Equal(runsBefore[cellIndex].Length, cellsAfter[cellIndex].Runs.Count);
                for (int runIndex = 0; runIndex < runsBefore[cellIndex].Length; runIndex++)
                    Assert.Same(runsBefore[cellIndex][runIndex], cellsAfter[cellIndex].Runs[runIndex]);
            }
            for (int imageIndex = 0; imageIndex < images.Length; imageIndex++)
            {
                Assert.Same(images[imageIndex], EnumerateTableImages(table).ElementAt(imageIndex));
                Assert.Same(bitmapsBefore[imageIndex], images[imageIndex].Bitmap);
                Assert.Same(leasesBefore[imageIndex], GetBitmapLease(images[imageIndex]));
                Assert.Same(infoBefore[imageIndex], GetPrivateField<MarkdownSvgDocumentInfo>(
                    images[imageIndex], "_svgInfo"));
                Assert.Equal(providerGenerationsBefore[imageIndex],
                    GetPrivateField<long>(images[imageIndex], "_svgProviderCacheGeneration"));
            }
            Assert.Equal(opensBefore, renderer.OpenCount);
            Assert.Equal(rendersBefore, renderer.FullRenderCount);
            Assert.Equal(1, renderer.CacheGeneration);
            Assert.Equal(resolverCountsBefore, resolver.Sources.Select(resolver.ResolveCountFor).ToArray());
        }
        finally
        {
            if (constructionContext is not null)
                await constructionContext.DrainOwnedRetirementsAsync().WaitAsync(TimeSpan.FromSeconds(10));
        }
        for (int imageIndex = 0; imageIndex < leasesBefore.Length; imageIndex++)
            Assert.Equal(leaseCountsBefore[imageIndex], GetLeaseReferenceCount(leasesBefore[imageIndex]));
    }

    [Fact]
    public async Task MixedThreeTableParagraphAndStandaloneImageBatchMatchesFallback()
    {
        await using var seed = new CompletedRasterPlacementCloneTests.PlacementFixture(
            forceSoftwareRenderer: true);
        using var session = new MarkdownPerformanceSession(MarkdownPerformanceOptions.Progressive);
        var resolver = TableResolver.CreateStandard(includeStandalone: true);
        string sourceText = CreateThreeTableMixedSource(resolver);
        await using ImageDocument optimized = BuildDocument(seed, session, resolver, sourceText,
            FlowDirection.LeftToRight, 1.5f, lazy: false);
        await using ImageDocument fallback = BuildDocument(seed, session, resolver, sourceText,
            FlowDirection.LeftToRight, 1.5f, lazy: false);

        TableBox[] tables = optimized.Snapshot.Blocks.OfType<TableBox>().ToArray();
        TableBox[] fallbackTables = fallback.Snapshot.Blocks.OfType<TableBox>().ToArray();
        Assert.Equal(3, tables.Length);
        Assert.Equal(tables.Length, fallbackTables.Length);
        InlineContainerBox paragraph = Assert.Single(optimized.Snapshot.Blocks.OfType<InlineContainerBox>(),
            inline => inline.Runs.OfType<InlineImageRun>().Any());
        InlineContainerBox fallbackParagraph = Assert.Single(fallback.Snapshot.Blocks.OfType<InlineContainerBox>(),
            inline => inline.Runs.OfType<InlineImageRun>().Any());
        ImageBox standalone = Assert.Single(optimized.Snapshot.Blocks.OfType<ImageBox>(),
            image => string.Equals(image.Url, resolver.StandaloneUrl, StringComparison.Ordinal));
        ImageBox fallbackStandalone = Assert.Single(fallback.Snapshot.Blocks.OfType<ImageBox>(),
            image => string.Equals(image.Url, resolver.StandaloneUrl, StringComparison.Ordinal));
        ImageBox[] images = optimized.Snapshot.Blocks.SelectMany(EnumerateImages).ToArray();
        ImageBox[] fallbackImages = fallback.Snapshot.Blocks.SelectMany(EnumerateImages).ToArray();
        Assert.Equal(5, images.Length);
        Assert.Equal(images.Length, fallbackImages.Length);
        foreach (ImageBox image in images.Concat(fallbackImages))
            await LoadImageAsync(image);

        BlockBox[] rootsBefore = optimized.Snapshot.Blocks.ToArray();
        Rect[] rootBoundsBefore = rootsBefore.Select(static root => root.Bounds).ToArray();
        Dictionary<BlockBox, Rect> rootBoundsBeforeByOwner = rootsBefore.ToDictionary<BlockBox, BlockBox, Rect>(
            static root => root,
            static root => root.Bounds,
            ReferenceEqualityComparer.Instance);
        InlineContainerBox[][] cellsBefore = tables.Select(static table => table.GetCellBoxes().ToArray()).ToArray();
        InlineRun[][][] runsBefore = cellsBefore.Select(static cells =>
            cells.Select(static cell => cell.Runs.ToArray()).ToArray()).ToArray();
        CanvasTextLayout[][] layoutsBefore = cellsBefore.Select(static cells =>
            cells.Select(GetInlineLayout).ToArray()).ToArray();
        float[][] widthsBefore = tables.Select(CaptureColumnWidths).ToArray();
        ImageBox[] sourceImages = images.ToArray();
        CanvasBitmap[] bitmapsBefore = images.Select(image => Assert.IsType<CanvasBitmap>(image.Bitmap)).ToArray();
        SharedCanvasBitmapCache.Lease[] leasesBefore = images.Select(GetBitmapLease).ToArray();
        MarkdownSourceMap sourceMapBefore = optimized.Snapshot.SourceMap;
        MarkdownSemanticDocument semanticBefore = optimized.Snapshot.SemanticDocument;
        InlineRun[] paragraphRunsBefore = paragraph.Runs.ToArray();
        CanvasTextLayout paragraphLayoutBefore = GetInlineLayout(paragraph);
        var changedRoots = new HashSet<int>();
        // Intentionally mix cell-image invalidation identities and enclosing
        // root identities. The root-local entry point must normalize them to
        // distinct top-level roots before applying the batch.
        changedRoots.Add(images[0].BlockIndex);
        changedRoots.Add(tables[1].BlockIndex);
        changedRoots.Add(images[1].BlockIndex);
        changedRoots.Add(tables[0].BlockIndex);
        changedRoots.Add(paragraph.BlockIndex);
        changedRoots.Add(images[2].BlockIndex);
        changedRoots.Add(tables[2].BlockIndex);
        changedRoots.Add(standalone.BlockIndex);
        foreach (ImageBox image in images)
            changedRoots.Add(image.BlockIndex);
        Assert.True(changedRoots.Count > 5);
        Assert.All(tables, table => Assert.Contains(table.BlockIndex, changedRoots));
        Assert.All(images, image => Assert.Contains(image.BlockIndex, changedRoots));
        int[] resolverCountsBefore = resolver.Sources.Select(resolver.ResolveCountFor).ToArray();
        fallback.Snapshot.RelayoutChangedBlocks(
            fallbackTables.Select(static table => table.BlockIndex)
                .Append(fallbackParagraph.BlockIndex).Append(fallbackStandalone.BlockIndex).Order().ToArray(),
            LayoutWidth, CancellationToken.None);

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
            BlockBox[] changedRootOwners = tables.Cast<BlockBox>()
                .Append(paragraph).Append(standalone).ToArray();
            Assert.True(changedRootOwners.Any(root =>
                System.Math.Abs(rootBoundsBeforeByOwner[root].Height - root.Bounds.Height) > 0.5),
                "At least one completed image root must change its committed geometry.");
            for (int index = 0; index < rootsBefore.Length; index++)
            {
                Assert.Same(rootsBefore[index], optimized.Snapshot.Blocks[index]);
                Assert.Equal(LayoutPlacementTestGeometry.ToLogical(fallback.Snapshot, fallback.Snapshot.Blocks[index]),
                    LayoutPlacementTestGeometry.ToLogical(optimized.Snapshot, optimized.Snapshot.Blocks[index]));
            }
            Assert.Same(paragraph, Assert.Single(optimized.Snapshot.Blocks, root => ReferenceEquals(root, paragraph)));
            Assert.NotSame(paragraphLayoutBefore, GetInlineLayout(paragraph));
            Assert.Equal(paragraphRunsBefore.Length, paragraph.Runs.Count);
            for (int index = 0; index < paragraphRunsBefore.Length; index++)
                Assert.Same(paragraphRunsBefore[index], paragraph.Runs[index]);
            Assert.Equal(LayoutPlacementTestGeometry.ToLogical(fallback.Snapshot, fallbackParagraph),
                LayoutPlacementTestGeometry.ToLogical(optimized.Snapshot, paragraph));
            Assert.Equal(
                GetCellSelectionRects(fallback.Snapshot, fallbackParagraph),
                GetCellSelectionRects(optimized.Snapshot, paragraph));

            bool columnsRebalanced = false;
            for (int tableIndex = 0; tableIndex < tables.Length; tableIndex++)
            {
                TableBox table = tables[tableIndex];
                TableBox fallbackTable = fallbackTables[tableIndex];
                Assert.Same(table, Assert.Single(optimized.Snapshot.Blocks, root => ReferenceEquals(root, table)));
                Assert.Equal(fallbackTable.HorizontalOffset, table.HorizontalOffset);
                Assert.Equal(fallbackTable.HorizontalExtent, table.HorizontalExtent);
                Assert.Equal(fallbackTable.HorizontalViewport, table.HorizontalViewport);
                InlineContainerBox[] finalCells = table.GetCellBoxes().ToArray();
                InlineContainerBox[] fallbackCells = fallbackTable.GetCellBoxes().ToArray();
                Assert.Equal(cellsBefore[tableIndex].Length, finalCells.Length);
                for (int cellIndex = 0; cellIndex < finalCells.Length; cellIndex++)
                {
                    Assert.Same(cellsBefore[tableIndex][cellIndex], finalCells[cellIndex]);
                    Assert.Equal(runsBefore[tableIndex][cellIndex].Length, finalCells[cellIndex].Runs.Count);
                    for (int runIndex = 0; runIndex < runsBefore[tableIndex][cellIndex].Length; runIndex++)
                        Assert.Same(runsBefore[tableIndex][cellIndex][runIndex], finalCells[cellIndex].Runs[runIndex]);
                    Assert.NotSame(layoutsBefore[tableIndex][cellIndex], GetInlineLayout(finalCells[cellIndex]));
                    Assert.Equal(LayoutPlacementTestGeometry.ToLogical(fallback.Snapshot, fallbackCells[cellIndex]),
                        LayoutPlacementTestGeometry.ToLogical(optimized.Snapshot, finalCells[cellIndex]));
                    Assert.Equal(
                        GetCellSelectionRects(fallback.Snapshot, fallbackCells[cellIndex]),
                        GetCellSelectionRects(optimized.Snapshot, finalCells[cellIndex]));
                }
                float[] finalWidths = CaptureColumnWidths(table);
                float[] fallbackWidths = CaptureColumnWidths(fallbackTable);
                Assert.Equal(fallbackWidths, finalWidths);
                columnsRebalanced |= widthsBefore[tableIndex].Where((width, column) =>
                    System.Math.Abs(width - finalWidths[column]) > 0.5f).Any();
            }
            Assert.True(columnsRebalanced, "Image aspect ratios must rebalance at least one table in the mixed batch.");

            ImageBox[] finalImages = optimized.Snapshot.Blocks.SelectMany(EnumerateImages).ToArray();
            Assert.Equal(sourceImages.Length, finalImages.Length);
            bool distinctImagePairs = false;
            for (int imageIndex = 0; imageIndex < sourceImages.Length; imageIndex++)
            {
                Assert.Same(sourceImages[imageIndex], finalImages[imageIndex]);
                Assert.Same(bitmapsBefore[imageIndex], finalImages[imageIndex].Bitmap);
                Assert.Same(leasesBefore[imageIndex], GetBitmapLease(finalImages[imageIndex]));
                Assert.Equal(LayoutPlacementTestGeometry.ToLogical(fallback.Snapshot, fallbackImages[imageIndex]),
                    LayoutPlacementTestGeometry.ToLogical(optimized.Snapshot, finalImages[imageIndex]));
                Assert.Equal(fallbackImages[imageIndex].MeasuredImageWidth, finalImages[imageIndex].MeasuredImageWidth);
                Assert.Equal(fallbackImages[imageIndex].MeasuredImageHeight, finalImages[imageIndex].MeasuredImageHeight);
                distinctImagePairs |= System.Math.Abs(finalImages[imageIndex].MeasuredImageWidth -
                    finalImages[imageIndex].MeasuredImageHeight) > 1f;
            }
            Assert.True(distinctImagePairs, "The mixed batch must retain the authored non-square image size pairs.");
            Assert.Equal(resolverCountsBefore, resolver.Sources.Select(resolver.ResolveCountFor).ToArray());

            ImageBox[] expectedPlanOrder = optimized.Snapshot.Blocks.SelectMany(EnumerateImages).ToArray();
            var plan = new RasterPlacementImagePlan(
                optimized.Snapshot, optimized.Snapshot.Blocks, expectedCount: images.Length);
            Assert.Equal(expectedPlanOrder.Length, plan.PublicationImages.Length);
            for (int imageIndex = 0; imageIndex < expectedPlanOrder.Length; imageIndex++)
                Assert.Same(expectedPlanOrder[imageIndex], plan.PublicationImages[imageIndex]);
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
    public async Task LazyTableRelayoutLeavesPendingSiblingAndDistantEstimatedImageUntouched()
    {
        await using var seed = new CompletedRasterPlacementCloneTests.PlacementFixture();
        using var session = new MarkdownPerformanceSession(MarkdownPerformanceOptions.Progressive);
        var resolver = TableResolver.CreateStandard(includePending: true);
        string sourceText = CreateStandardTableSource(resolver) +
            $"\n\n![pending sibling]({resolver.PendingUrl})\n\n" +
            string.Join("\n\n", Enumerable.Range(0, 90).Select(static index =>
                $"Lazy table sibling {index}: preserve the measured mask and image work.")) +
            $"\n\n![far estimated image]({resolver.FarUrl})\n";
        await using ImageDocument optimized = BuildDocument(seed, session, resolver, sourceText,
            FlowDirection.LeftToRight, 1.5f, lazy: true, lazyViewportHeight: 520);
        await using ImageDocument fallback = BuildDocument(seed, session, resolver, sourceText,
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
            () => pending.ActiveImageWorkCountForTests > 0 &&
                  fallbackPending.ActiveImageWorkCountForTests > 0 &&
                  resolver.ResolveCountFor(resolver.PendingUrl) >= 2);
        foreach (ImageBox image in EnumerateTableImages(table))
            await LoadImageAsync(image);
        foreach (ImageBox image in EnumerateTableImages(fallbackTable))
            await LoadImageAsync(image);

        BlockBox[] rootsBefore = optimized.Snapshot.Blocks.ToArray();
        Rect[] rootBoundsBefore = rootsBefore.Select(static root => root.Bounds).ToArray();
        Rect[] tableCellBoundsBefore = CaptureCellBounds(optimized.Snapshot, table);
        long revisionBefore = optimized.Snapshot.LayoutRevision;
        int pendingWorkBefore = pending.ActiveImageWorkCountForTests;
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
            Assert.True(pending.ActiveImageWorkCountForTests > 0);
            Assert.Equal(pendingWorkBefore, pending.ActiveImageWorkCountForTests);
            Assert.Null(pending.Bitmap);
            Assert.Equal(pendingResolveCount, resolver.ResolveCountFor(resolver.PendingUrl));
            Assert.Equal(farResolveCount, resolver.ResolveCountFor(resolver.FarUrl));
            Assert.False(optimized.Snapshot.IsTopLevelBlockMeasured(farOwner));
            Assert.False(fallback.Snapshot.IsTopLevelBlockMeasured(fallbackFarOwner));
            for (int ordinal = 0; ordinal < rootsBefore.Length; ordinal++)
            {
                Assert.Same(rootsBefore[ordinal], optimized.Snapshot.Blocks[ordinal]);
                Assert.Equal(LayoutPlacementTestGeometry.ToLogical(fallback.Snapshot, fallback.Snapshot.Blocks[ordinal]),
                    LayoutPlacementTestGeometry.ToLogical(optimized.Snapshot, optimized.Snapshot.Blocks[ordinal]));
                if (ReferenceEquals(rootsBefore[ordinal], farOwner))
                {
                    Assert.Equal(rootBoundsBefore[ordinal].X, optimized.Snapshot.Blocks[ordinal].Bounds.X);
                    Assert.Equal(rootBoundsBefore[ordinal].Width, optimized.Snapshot.Blocks[ordinal].Bounds.Width);
                    Assert.Equal(rootBoundsBefore[ordinal].Height, optimized.Snapshot.Blocks[ordinal].Bounds.Height);
                }
            }
        }
        finally
        {
            if (constructionContext is not null)
                await constructionContext.DrainOwnedRetirementsAsync().WaitAsync(TimeSpan.FromSeconds(10));
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
        var resolver = TableResolver.CreateStandard();
        string sourceText = CreateStandardTableSource(resolver, forceOverflow: true);
        await using ImageDocument document = BuildDocument(seed, session, resolver, sourceText,
            FlowDirection.RightToLeft, 1.5f, lazy: false);
        TableBox table = FindTable(document.Snapshot);
        ImageBox[] images = EnumerateTableImages(table).ToArray();
        foreach (ImageBox image in images)
            await LoadImageAsync(image);
        Assert.True(table.CanScrollHorizontally);

        BlockBox[] rootsBefore = document.Snapshot.Blocks.ToArray();
        TableSourceState initialState = CaptureState(document.Snapshot, table);
        BlockBox revisionTrigger = Assert.Single(document.Snapshot.Blocks.OfType<InlineContainerBox>(),
            inline => inline.ElementKey == MarkdownElementKeys.Heading1);
        using var cancellation = new CancellationTokenSource();
        long expectedPointerEpoch = document.Snapshot.PointerInputEpoch;
        TableSourceState? expectedStateAfterRace = null;
        long revisionAfterRace = document.Snapshot.LayoutRevision;
        bool raceApplied = false;
        MarkdownLayoutContext? constructionContext = null;

        void ApplyRace()
        {
            switch (race)
            {
                case TablePreparationRace.Cancellation:
                    cancellation.Cancel();
                    break;
                case TablePreparationRace.PointerInput:
                    document.Snapshot.NotifyPointerInput();
                    expectedPointerEpoch++;
                    break;
                case TablePreparationRace.LayoutRevision:
                    document.Snapshot.RelayoutChangedBlocks(
                        [revisionTrigger.BlockIndex], LayoutWidth, CancellationToken.None);
                    revisionAfterRace = document.Snapshot.LayoutRevision;
                    expectedStateAfterRace = CaptureState(document.Snapshot, table);
                    break;
                case TablePreparationRace.HorizontalScroll:
                    Assert.True(table.SetHorizontalOffset(table.HorizontalOffset + 72d));
                    expectedStateAfterRace = CaptureState(document.Snapshot, table);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(race));
            }
            raceApplied = true;
        }

        try
        {
            if (race == TablePreparationRace.Cancellation)
            {
                Assert.Throws<OperationCanceledException>(() => document.Snapshot.TryRelayoutCompletedImageRoots(
                    [table.BlockIndex], LayoutWidth, cancellation.Token, out constructionContext,
                    beforeMeasureOutsideGateForTests: ApplyRace,
                    expectedPointerInputEpoch: expectedPointerEpoch));
            }
            else
            {
                PreparedImageRelayoutResult result = document.Snapshot.TryRelayoutCompletedImageRoots(
                    [table.BlockIndex], LayoutWidth, CancellationToken.None, out constructionContext,
                    beforeMeasureOutsideGateForTests: ApplyRace,
                    expectedPointerInputEpoch: expectedPointerEpoch);
                Assert.Equal(PreparedImageRelayoutResult.Retry, result);
            }
            Assert.True(raceApplied);
            TableSourceState expected = expectedStateAfterRace ?? initialState;
            AssertStateEqual(expected, CaptureState(document.Snapshot, table));
            Assert.Equal(revisionAfterRace, document.Snapshot.LayoutRevision);
            Assert.Equal(rootsBefore.Length, document.Snapshot.Blocks.Count);
            for (int index = 0; index < rootsBefore.Length; index++)
                Assert.Same(rootsBefore[index], document.Snapshot.Blocks[index]);
            if (race == TablePreparationRace.PointerInput)
                Assert.Equal(expectedPointerEpoch, document.Snapshot.PointerInputEpoch);
            if (race == TablePreparationRace.HorizontalScroll)
                Assert.NotEqual(initialState.HorizontalOffset, table.HorizontalOffset);
        }
        finally
        {
            if (constructionContext is not null)
                await constructionContext.DrainOwnedRetirementsAsync().WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    [Fact]
    public async Task LateTableCellRunMutationRejectsMixedStandaloneAndTableBatchAtomically()
    {
        await using var seed = new CompletedRasterPlacementCloneTests.PlacementFixture();
        using var session = new MarkdownPerformanceSession(MarkdownPerformanceOptions.Progressive);
        var resolver = TableResolver.CreateStandard(includeStandalone: true);
        string sourceText =
            $"![earlier standalone image]({resolver.StandaloneUrl})\n\n" +
            CreateStandardTableSource(resolver);
        await using ImageDocument document = BuildDocument(seed, session, resolver, sourceText,
            FlowDirection.LeftToRight, 2f, lazy: false);
        ImageBox standalone = Assert.Single(document.Snapshot.Blocks.OfType<ImageBox>());
        TableBox table = FindTable(document.Snapshot);
        Assert.True(standalone.BlockIndex < table.BlockIndex);
        ImageBox[] images = EnumerateTableImages(table).ToArray();
        foreach (ImageBox image in images.Append(standalone))
            await LoadImageAsync(image);

        InlineContainerBox lastCell = table.GetCellBoxes().Last(static cell => cell.Runs.Count > 0);
        InlineRun lateRun = lastCell.Runs[^1];
        int originalInlineIndex = lateRun.InlineIndex;
        TableSourceState initialState = CaptureState(document.Snapshot, table, standalone);
        BlockBox[] rootsBefore = document.Snapshot.Blocks.ToArray();
        CanvasTextLayout lateCellLayout = GetInlineLayout(lastCell);
        long revisionBefore = document.Snapshot.LayoutRevision;
        int[] resolverCountsBefore = resolver.Sources.Select(resolver.ResolveCountFor).ToArray();
        bool hookRan = false;
        MarkdownLayoutContext? constructionContext = null;
        PreparedImageRelayoutResult result = PreparedImageRelayoutResult.Unavailable;
        try
        {
            result = document.Snapshot.TryRelayoutCompletedImageRoots(
                [standalone.BlockIndex, table.BlockIndex], LayoutWidth, CancellationToken.None,
                out constructionContext,
                beforeMeasureOutsideGateForTests: () =>
                {
                    hookRan = true;
                    lateRun.InlineIndex = originalInlineIndex + 1;
                },
                expectedPointerInputEpoch: document.Snapshot.PointerInputEpoch);
        }
        finally
        {
            lateRun.InlineIndex = originalInlineIndex;
            if (constructionContext is not null)
                await constructionContext.DrainOwnedRetirementsAsync().WaitAsync(TimeSpan.FromSeconds(10));
        }

        Assert.True(hookRan);
        Assert.Equal(PreparedImageRelayoutResult.Retry, result);
        Assert.Equal(revisionBefore, document.Snapshot.LayoutRevision);
        Assert.Equal(originalInlineIndex, lateRun.InlineIndex);
        Assert.Same(lateRun, lastCell.Runs[^1]);
        Assert.Same(lateCellLayout, GetInlineLayout(lastCell));
        AssertStateEqual(initialState, CaptureState(document.Snapshot, table, standalone));
        Assert.Equal(rootsBefore.Length, document.Snapshot.Blocks.Count);
        for (int index = 0; index < rootsBefore.Length; index++)
            Assert.Same(rootsBefore[index], document.Snapshot.Blocks[index]);
        Assert.Equal(resolverCountsBefore, resolver.Sources.Select(resolver.ResolveCountFor).ToArray());
    }

    [Fact]
    public async Task TableImagePlaceholderAliasedToCellTextRejectsBeforeAnySourceSwap()
    {
        await using var seed = new CompletedRasterPlacementCloneTests.PlacementFixture(
            forceSoftwareRenderer: true);
        using var session = new MarkdownPerformanceSession(MarkdownPerformanceOptions.Progressive);
        var resolver = TableResolver.CreateStandard();
        await using ImageDocument document = BuildDocument(seed, session, resolver,
            CreateStandardTableSource(resolver), FlowDirection.LeftToRight, 1.5f,
            lazy: false, layoutWidth: 640f);
        TableBox table = FindTable(document.Snapshot);
        ImageBox[] images = EnumerateTableImages(table).ToArray();
        foreach (ImageBox image in images)
            await LoadImageAsync(image);

        InlineContainerBox cell = table.GetCellBoxes().First(static candidate => candidate.Runs.Count > 0);
        CanvasTextLayout cellLayout = GetInlineLayout(cell);
        ImageBox imageSource = images[0];
        FieldInfo placeholderField = typeof(ImageBox).GetField("_placeholder",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Missing image placeholder native-layout field.");
        CanvasTextLayout? originalPlaceholder = imageSource.PlaceholderLayoutForTests;
        Assert.NotSame(cellLayout, originalPlaceholder);

        TableSourceState initialState = CaptureState(document.Snapshot, table);
        BlockBox[] rootsBefore = document.Snapshot.Blocks.ToArray();
        MarkdownSourceMap sourceMapBefore = document.Snapshot.SourceMap;
        MarkdownSemanticDocument semanticBefore = document.Snapshot.SemanticDocument;
        long revisionBefore = document.Snapshot.LayoutRevision;
        int[] resolverCountsBefore = resolver.Sources.Select(resolver.ResolveCountFor).ToArray();
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
                beforeMeasureOutsideGateForTests: () =>
                {
                    hookCalls++;
                    aliasInstalled = true;
                    placeholderField.SetValue(imageSource, cellLayout);
                },
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
                    placeholderField.SetValue(imageSource, originalPlaceholder);
            }
            finally
            {
                if (constructionContext is not null)
                    await constructionContext.DrainOwnedRetirementsAsync().WaitAsync(TimeSpan.FromSeconds(10));
            }
        }

        Assert.Same(originalPlaceholder, imageSource.PlaceholderLayoutForTests);
        Assert.Equal(1, hookCalls);
        Assert.Equal(PreparedImageRelayoutResult.Retry, result);
        Assert.Equal(revisionBefore, document.Snapshot.LayoutRevision);
        Assert.Same(sourceMapBefore, document.Snapshot.SourceMap);
        Assert.Same(semanticBefore, document.Snapshot.SemanticDocument);
        Assert.Equal(rootsBefore.Length, document.Snapshot.Blocks.Count);
        for (int index = 0; index < rootsBefore.Length; index++)
            Assert.Same(rootsBefore[index], document.Snapshot.Blocks[index]);
        Assert.Contains(table.GetCellBoxes(), candidate => ReferenceEquals(candidate, cell));
        Assert.Same(cellLayout, GetInlineLayout(cell));
        AssertStateEqual(initialState, CaptureState(document.Snapshot, table));
        Assert.Equal(resolverCountsBefore, resolver.Sources.Select(resolver.ResolveCountFor).ToArray());
        for (int index = 0; index < images.Length; index++)
        {
            Assert.Same(leasesBefore[index], GetBitmapLease(images[index]));
            Assert.Equal(leaseReferencesBefore[index], GetLeaseReferenceCount(leasesBefore[index]));
        }
    }

    [Fact]
    public async Task MoreThan256ZeroLengthBuiltInTableCellRunsRejectBeforeImageLeaseAcquisition()
    {
        await using var seed = new CompletedRasterPlacementCloneTests.PlacementFixture();
        using var session = new MarkdownPerformanceSession(MarkdownPerformanceOptions.Progressive);
        var resolver = TableResolver.CreateStandard();
        await using ImageDocument document = BuildDocument(seed, session, resolver,
            CreateStandardTableSource(resolver), FlowDirection.LeftToRight, 1.5f, lazy: false);
        TableBox table = FindTable(document.Snapshot);
        ImageBox[] images = EnumerateTableImages(table).ToArray();
        foreach (ImageBox image in images)
            await LoadImageAsync(image);

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
        int[] resolverCountsBefore = resolver.Sources.Select(resolver.ResolveCountFor).ToArray();
        int hookCalls = 0;
        MarkdownLayoutContext? constructionContext = null;
        try
        {
            PreparedImageRelayoutResult result = document.Snapshot.TryRelayoutCompletedImageRoots(
                [table.BlockIndex], LayoutWidth, CancellationToken.None, out constructionContext,
                beforeMeasureOutsideGateForTests: () => hookCalls++,
                expectedPointerInputEpoch: document.Snapshot.PointerInputEpoch);
            Assert.Equal(PreparedImageRelayoutResult.Unavailable, result);
            Assert.Equal(0, hookCalls);
            Assert.Equal(resolverCountsBefore, resolver.Sources.Select(resolver.ResolveCountFor).ToArray());
            for (int index = 0; index < images.Length; index++)
            {
                Assert.Same(leasesBefore[index], GetBitmapLease(images[index]));
                Assert.Equal(leaseReferencesBefore[index], GetLeaseReferenceCount(leasesBefore[index]));
            }
        }
        finally
        {
            if (constructionContext is not null)
                await constructionContext.DrainOwnedRetirementsAsync().WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    [Fact]
    public async Task AggregateImageAndCellCapsRejectBeforeLeaseAcquisitionOrAdditionalResolution()
    {
        await using var seed = new CompletedRasterPlacementCloneTests.PlacementFixture();
        using var session = new MarkdownPerformanceSession(MarkdownPerformanceOptions.Progressive);
        var imageResolver = TableResolver.CreateGenerated(imageCount: 33, columns: 33, bodyRows: 1);
        await using ImageDocument imageDocument = BuildDocument(seed, session, imageResolver,
            CreateGeneratedTable(imageResolver, columns: 33, bodyRows: 1, images: true),
            FlowDirection.LeftToRight, 1f, lazy: false);
        TableBox imageTable = FindTable(imageDocument.Snapshot);
        ImageBox[] imageTableImages = EnumerateTableImages(imageTable).ToArray();
        Assert.Equal(33, imageTableImages.Length);
        foreach (ImageBox image in imageTableImages)
            await LoadImageAsync(image);
        SharedCanvasBitmapCache.Lease[] imageLeases = imageTableImages.Select(GetBitmapLease).ToArray();
        int[] imageLeaseReferences = imageLeases.Select(GetLeaseReferenceCount).ToArray();
        int[] imageResolveCounts = imageResolver.Sources.Select(imageResolver.ResolveCountFor).ToArray();
        int imageHookCalls = 0;
        MarkdownLayoutContext? imageConstructionContext = null;
        try
        {
            PreparedImageRelayoutResult result = imageDocument.Snapshot.TryRelayoutCompletedImageRoots(
                [imageTable.BlockIndex], LayoutWidth, CancellationToken.None,
                out imageConstructionContext,
                beforeMeasureOutsideGateForTests: () => imageHookCalls++,
                expectedPointerInputEpoch: imageDocument.Snapshot.PointerInputEpoch);
            Assert.Equal(PreparedImageRelayoutResult.Unavailable, result);
            Assert.Equal(0, imageHookCalls);
            for (int index = 0; index < imageTableImages.Length; index++)
            {
                Assert.Same(imageLeases[index], GetBitmapLease(imageTableImages[index]));
                Assert.Equal(imageLeaseReferences[index], GetLeaseReferenceCount(imageLeases[index]));
            }
            Assert.Equal(imageResolveCounts, imageResolver.Sources.Select(imageResolver.ResolveCountFor).ToArray());
        }
        finally
        {
            if (imageConstructionContext is not null)
                await imageConstructionContext.DrainOwnedRetirementsAsync().WaitAsync(TimeSpan.FromSeconds(10));
        }

        var cellResolver = TableResolver.CreateGenerated(imageCount: 1, columns: 18, bodyRows: 14);
        await using ImageDocument cellDocument = BuildDocument(seed, session, cellResolver,
            CreateGeneratedTable(cellResolver, columns: 18, bodyRows: 14, images: true),
            FlowDirection.RightToLeft, 1.5f, lazy: false);
        TableBox cellTable = FindTable(cellDocument.Snapshot);
        InlineContainerBox[] cells = cellTable.GetCellBoxes().ToArray();
        Assert.True(cells.Length > 256);
        Assert.True(cells.Sum(static cell => cell.Runs.Count) > 256);
        ImageBox qualifiedImage = Assert.Single(EnumerateTableImages(cellTable));
        await LoadImageAsync(qualifiedImage);
        SharedCanvasBitmapCache.Lease cellLease = GetBitmapLease(qualifiedImage);
        int leaseReferences = GetLeaseReferenceCount(cellLease);
        int[] cellResolveCounts = cellResolver.Sources.Select(cellResolver.ResolveCountFor).ToArray();
        int cellHookCalls = 0;
        MarkdownLayoutContext? cellConstructionContext = null;
        try
        {
            PreparedImageRelayoutResult result = cellDocument.Snapshot.TryRelayoutCompletedImageRoots(
                [cellTable.BlockIndex], LayoutWidth, CancellationToken.None,
                out cellConstructionContext,
                beforeMeasureOutsideGateForTests: () => cellHookCalls++,
                expectedPointerInputEpoch: cellDocument.Snapshot.PointerInputEpoch);
            Assert.Equal(PreparedImageRelayoutResult.Unavailable, result);
            Assert.Equal(0, cellHookCalls);
            Assert.Same(cellLease, GetBitmapLease(qualifiedImage));
            Assert.Equal(leaseReferences, GetLeaseReferenceCount(cellLease));
            Assert.Equal(cellResolveCounts, cellResolver.Sources.Select(cellResolver.ResolveCountFor).ToArray());
        }
        finally
        {
            if (cellConstructionContext is not null)
                await cellConstructionContext.DrainOwnedRetirementsAsync().WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    private static string CreateStandardTableSource(TableResolver resolver, bool forceOverflow = false) =>
        "# Table image placement\n\n" +
        "| **Item** | **Preview** | **Details** | **Notes** |\n" +
        "| :--- | ---: | :--- | :--- |\n" +
        $"| [wide entry](https://example.test/wide) | ![wide aspect image]({resolver.WideUrl}) | " +
        "**Available** with [reference](https://example.test/reference) and `inline code` | " +
        (forceOverflow
            ? "UnbreakableColumnMinimumTokenABCDEFGHIJKLMNOPQRSTUVWXYZABCDEFGHIJKLMNOPQRSTUVWXYZABCDEFGHIJKLMNOPQRSTUVWXYZABCDEFGHIJKLMNOPQRSTUVWXYZABCDEFGHIJKLMNOPQRSTUVWXYZABCDEFGHIJKLMNOPQRSTUVWXYZ"
            : "Breakable note text keeps the preferred widths available for responsive column rebalancing") + " |\n" +
        $"| Tall item | ![tall aspect image]({resolver.TallUrl}) | *Compact* [details](https://example.test/details) | short note |\n" +
        $"| Square item | ![square preview]({resolver.SquareUrl}) | Body text only |  |\n" +
        "| Empty preview |  | Empty image cell beside ordinary text |  |\n";

    private static string CreateThreeTableMixedSource(TableResolver resolver) =>
        "# Mixed measured roots\n\n" +
        "| **Wide item** | **Preview** |\n| --- | --- |\n" +
        $"| [first](https://example.test/first) | ![wide table image]({resolver.WideUrl}) |\n\n" +
        $"A paragraph with [a link](https://example.test/paragraph) and an inline image " +
        $"![inline paragraph image]({resolver.InlineUrl}) remains in the same document flow.\n\n" +
        "| **Tall item** | **Preview** |\n| --- | --- |\n" +
        $"| second row | ![tall table image]({resolver.TallUrl}) |\n\n" +
        $"![standalone image]({resolver.StandaloneUrl})\n\n" +
        "| **Square item** | **Preview** |\n| --- | --- |\n" +
        $"| third row | ![square table image]({resolver.SquareUrl}) |\n";

    private static string CreateGeneratedTable(
        TableResolver resolver,
        int columns,
        int bodyRows,
        bool images)
    {
        string[] header = Enumerable.Range(0, columns).Select(static column => $"Heading {column}").ToArray();
        string[] separators = Enumerable.Repeat("---", columns).ToArray();
        var lines = new List<string>
        {
            "# Bounded table placement\n",
            "| " + string.Join(" | ", header) + " |",
            "| " + string.Join(" | ", separators) + " |",
        };
        for (int row = 0; row < bodyRows; row++)
        {
            var cells = new string[columns];
            for (int column = 0; column < columns; column++)
            {
                int imageIndex = row * columns + column;
                cells[column] = images && imageIndex < resolver.GeneratedImageCount
                    ? $"![generated {imageIndex}]({resolver.GeneratedUrl(imageIndex)})"
                    : $"cell{row}_{column}";
            }
            lines.Add("| " + string.Join(" | ", cells) + " |");
        }
        return string.Join("\n", lines) + "\n";
    }

    private static ImageDocument BuildDocument(
        CompletedRasterPlacementCloneTests.PlacementFixture seed,
        MarkdownPerformanceSession session,
        IMarkdownImageResolver resolver,
        string sourceText,
        FlowDirection direction,
        float rasterizationScale,
        bool lazy,
        float lazyViewportHeight = 120,
        float layoutWidth = LayoutWidth,
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
                ? builder.BuildLazy(parsed, layoutWidth, viewportTop: 0,
                    viewportHeight: lazyViewportHeight, overscan: 0, CancellationToken.None)
                : builder.Build(parsed, layoutWidth, CancellationToken.None);
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

    private static TableBox FindTable(LayoutSnapshot snapshot) =>
        Assert.Single(snapshot.Blocks.OfType<TableBox>());

    private static IEnumerable<ImageBox> EnumerateTableImages(TableBox table)
    {
        foreach (InlineContainerBox cell in table.GetCellBoxes())
        foreach (InlineImageRun run in cell.Runs.OfType<InlineImageRun>())
            yield return run.Image;
    }

    private static ImageBox FindImage(LayoutSnapshot snapshot, string url) =>
        Assert.Single(snapshot.Blocks.SelectMany(EnumerateImages), image =>
            string.Equals(image.Url, url, StringComparison.Ordinal));

    private static IEnumerable<ImageBox> EnumerateImages(BlockBox root)
    {
        switch (root)
        {
            case ImageBox image:
                yield return image;
                yield break;
            case TableBox table:
                foreach (ImageBox image in EnumerateTableImages(table))
                    yield return image;
                yield break;
            case InlineContainerBox inline:
                foreach (InlineImageRun run in inline.Runs.OfType<InlineImageRun>())
                    yield return run.Image;
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
        await DisplaySizedRasterDecodeTests.WaitForLoadAsync(image).WaitAsync(TimeSpan.FromSeconds(10));
        await DisplaySizedRasterDecodeTests.WaitUntilAsync(() => image.ActiveImageWorkCountForTests == 0);
        Assert.NotNull(image.Bitmap);
    }

    private static Rect[] CaptureCellBounds(LayoutSnapshot snapshot, TableBox table) =>
        table.GetCellBoxes().Select(cell => LayoutPlacementTestGeometry.ToLogical(snapshot, cell)).ToArray();

    private static float[] CaptureColumnWidths(TableBox table) =>
        table.GetCellInfos().Where(static info => info.IsHeader && info.Row == 0)
            .Select(static info => (float)info.Box.Bounds.Width).ToArray();

    private static Rect[] GetCellSelectionRects(LayoutSnapshot snapshot, InlineContainerBox cell)
    {
        (DocumentPosition start, DocumentPosition end) = cell.GetBlockBoundaries();
        var selection = new SelectionController();
        selection.SetAnchor(start);
        selection.ExtendTo(end);
        return selection.GetHighlightRects(snapshot).ToArray();
    }

    private static Rect[] GetCellSemanticTextRects(LayoutSnapshot snapshot, InlineContainerBox cell)
    {
        MarkdownSemanticDocument document = snapshot.SemanticDocument;
        MarkdownTextSpan[] spans = document.TextSpans
            .Where(span => ReferenceEquals(span.InlineBox, cell) && span.TextEnd > span.TextStart)
            .ToArray();
        if (spans.Length == 0)
            return Array.Empty<Rect>();

        Assert.True(snapshot.TryBeginPaint());
        try
        {
            var rectangles = new List<Rect>();
            foreach (MarkdownTextSpan span in spans)
            {
                rectangles.AddRange(document.GetDocumentRects(
                    span.TextStart,
                    span.TextEnd,
                    expandDegenerate: false,
                    placementTransform: snapshot.ToLogicalRectForBlockIndexUnderPaint,
                    placementScope: snapshot.EnterPlacementTransformScopeForBlockIndexUnderPaint));
            }

            return rectangles.ToArray();
        }
        finally
        {
            snapshot.EndPaint();
        }
    }

    private static Point GetCellEndpointCaretPoint(LayoutSnapshot snapshot, InlineContainerBox cell)
    {
        (DocumentPosition start, DocumentPosition end) = cell.GetBlockBoundaries();
        var range = new DocumentRange(start, end).Normalized();
        var selection = new SelectionController();
        selection.SetAnchor(start);
        selection.ExtendTo(end);
        Rect viewport = new(
            -1_000,
            -1_000,
            snapshot.Size.Width + 2_000,
            snapshot.Size.Height + 2_000);

        Assert.True(snapshot.TryBeginPaint());
        try
        {
            Assert.True(selection.TryGetSelectionEndpointGeometry(
                snapshot,
                range.Start,
                range,
                viewport,
                verticalSearchMargin: 0,
                rangeStart: true,
                out Point caretPoint,
                out _,
                out bool hasCaret));
            Assert.True(hasCaret, "The text endpoint must resolve through DirectWrite's caret geometry.");
            return caretPoint;
        }
        finally
        {
            snapshot.EndPaint();
        }
    }

    private static void AssertUnclippedVerticalSelectionExtentIsPreserved(
        LayoutSnapshot snapshot,
        TableBox table,
        InlineContainerBox cell,
        Rect[] publishedRects)
    {
        Assert.True(snapshot.TryBeginPaint());
        try
        {
            using LayoutPlacementTransformContext.Scope placementScope =
                snapshot.EnterPlacementTransformScopeForTopLevelBlockUnderPaint(table);
            (DocumentPosition start, DocumentPosition end) = cell.GetBlockBoundaries();
            Rect[] sourceRects = cell.GetSelectionRects(new DocumentRange(start, end).Normalized()).ToArray();
            Assert.Equal(sourceRects.Length, publishedRects.Length);
            Rect clip = table.HorizontalViewportBounds;
            for (int index = 0; index < sourceRects.Length; index++)
            {
                Rect source = sourceRects[index];
                if (source.Top >= clip.Top && source.Bottom <= clip.Bottom)
                    Assert.Equal(source.Height, publishedRects[index].Height);
            }
        }
        finally
        {
            snapshot.EndPaint();
        }
    }

    private static string FormatRects(IEnumerable<Rect> rects) =>
        "[" + string.Join(";", rects.Select(static rect =>
            $"{rect.X:R},{rect.Y:R},{rect.Width:R},{rect.Height:R}")) + "]";

    private static bool HasStyleKey(InlineRun run, string key) =>
        string.Equals(run.ElementKey, key, StringComparison.Ordinal) ||
        run.HasStyleModifier(key) ||
        run.StyleAliases.Contains(key, StringComparer.Ordinal);

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

    private static int GetLeaseReferenceCount(SharedCanvasBitmapCache.Lease lease)
    {
        FieldInfo entryField = typeof(SharedCanvasBitmapCache.Lease).GetField("_entry",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Missing cache lease entry field.");
        object entry = Assert.IsAssignableFrom<object>(entryField.GetValue(lease));
        FieldInfo referencesField = entry.GetType().GetField("_referenceCount",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Missing cache entry reference counter.");
        return Assert.IsType<int>(referencesField.GetValue(entry));
    }

    private static T GetPrivateField<T>(object instance, string name)
    {
        FieldInfo field = instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"Missing private field {name} on {instance.GetType().Name}.");
        return Assert.IsType<T>(field.GetValue(instance));
    }

    private static string CreateSvgTableSource(SvgTableResolver resolver) =>
        "| Resource | Preview | Details |\n" +
        "| --- | --- | --- |\n" +
        $"| [Wide](https://example.test/wide) | ![wide SVG alt]({resolver.WideUrl}) | " +
        "**Measured** [wide details](https://example.test/wide-details) with `code` |\n" +
        $"| Tall resource | ![tall SVG alt]({resolver.TallUrl}) | " +
        "*Tall detail* and [second link](https://example.test/tall-details) |\n" +
        "| Empty image cell |  | Text-only sibling remains unchanged |\n";

    private sealed class SvgTableResolver : IMarkdownImageResolver
    {
        private readonly ConcurrentDictionary<string, MarkdownImageAsset> _assets = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, int> _counts = new(StringComparer.Ordinal);
        private readonly string _cachePrefix = "root-local-table-svg:" + Guid.NewGuid().ToString("N") + ":";

        internal SvgTableResolver()
        {
            string suffix = Guid.NewGuid().ToString("N");
            WideUrl = $"https://images.example.test/table-wide-{suffix}.svg";
            TallUrl = $"https://images.example.test/table-tall-{suffix}.svg";
            AddSvg(WideUrl, "wide", "Wide root title", "Wide accessible description", "#c33b21");
            AddSvg(TallUrl, "tall", "Tall root title", "Tall accessible description", "#2179c3");
        }

        internal string WideUrl { get; }
        internal string TallUrl { get; }
        internal string[] Sources => _assets.Keys.Order(StringComparer.Ordinal).ToArray();
        internal int ResolveCountFor(string source) => _counts.TryGetValue(source, out int count) ? count : 0;

        public ValueTask<MarkdownImageResolution> ResolveAsync(
            string source,
            MarkdownImageResolveContext context,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _counts.AddOrUpdate(source, 1, static (_, count) => count + 1);
            return ValueTask.FromResult(_assets.TryGetValue(source, out MarkdownImageAsset? asset)
                ? MarkdownImageResolution.Resolved(asset)
                : MarkdownImageResolution.Unavailable);
        }

        private void AddSvg(string source, string kind, string title, string description, string fill)
        {
            string svg = "<svg xmlns=\"http://www.w3.org/2000/svg\">" +
                $"<title>{title}</title><desc>{description}</desc>" +
                $"<rect width=\"100%\" height=\"100%\" fill=\"{fill}\"/>" +
                "<text x=\"0\" y=\"12\">Accessible table vector</text></svg>";
            _assets[source] = new MarkdownImageAsset(
                Encoding.UTF8.GetBytes(svg), "image/svg+xml", new Uri(source),
                _cachePrefix + kind + ":" + Guid.NewGuid().ToString("N"));
        }
    }

    private sealed class TableSvgRenderer : IMarkdownSvgRenderer
    {
        private long _cacheGeneration;
        private int _openCount;
        private int _fullRenderCount;
        private int _tileRenderCount;

        internal int OpenCount => Volatile.Read(ref _openCount);
        internal int FullRenderCount => Volatile.Read(ref _fullRenderCount);
        internal int TileRenderCount => Volatile.Read(ref _tileRenderCount);
        public long CacheGeneration => Interlocked.Read(ref _cacheGeneration);
        internal void AdvanceCacheGeneration() => Interlocked.Increment(ref _cacheGeneration);

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
            string source = Encoding.UTF8.GetString(request.SanitizedSvgBytes.Span);
            bool wide = source.Contains("fill=\"#c33b21\"", StringComparison.OrdinalIgnoreCase);
            bool tall = source.Contains("fill=\"#2179c3\"", StringComparison.OrdinalIgnoreCase);
            if (wide == tall)
                throw new MarkdownSvgException(MarkdownSvgFailureReason.UnsupportedContent,
                    "The test SVG did not contain one known table fixture marker.");
            double width = wide ? 480 : 96;
            double height = wide ? 96 : 240;
            var info = new MarkdownSvgDocumentInfo(width, height, width / height,
                wide ? "Wide provider description" : "Tall provider description", HasText: true);
            return ValueTask.FromResult<IMarkdownSvgDocument>(new TableSvgDocument(this, info,
                wide ? (byte)0xC3 : (byte)0x21,
                wide ? (byte)0x3B : (byte)0x79,
                wide ? (byte)0x21 : (byte)0xC3));
        }

        private sealed class TableSvgDocument(
            TableSvgRenderer owner,
            MarkdownSvgDocumentInfo info,
            byte red,
            byte green,
            byte blue) : IMarkdownSvgDocument
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
                        "The test SVG document has been retired.");
                if (request.TileRegion is not null)
                    Interlocked.Increment(ref owner._tileRenderCount);
                else
                    Interlocked.Increment(ref owner._fullRenderCount);

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
                        pixels[index] = blue;
                        pixels[index + 1] = green;
                        pixels[index + 2] = red;
                    }
                    else
                    {
                        pixels[index] = red;
                        pixels[index + 1] = green;
                        pixels[index + 2] = blue;
                    }
                    pixels[index + 3] = byte.MaxValue;
                }
                return ValueTask.FromResult(new MarkdownSvgRaster(
                    memory, length, width, height, stride, request.PixelFormat));
            }

            public void Dispose() => Interlocked.Exchange(ref _disposed, 1);
        }
    }

    private static TableSourceState CaptureState(
        LayoutSnapshot snapshot,
        TableBox table,
        ImageBox? standalone = null)
    {
        InlineContainerBox[] cells = table.GetCellBoxes().ToArray();
        ImageBox[] images = EnumerateTableImages(table).ToArray();
        if (standalone is not null)
            images = images.Append(standalone).ToArray();
        return new TableSourceState(
            snapshot.LayoutRevision,
            snapshot.Size,
            table.Bounds,
            table.HorizontalOffset,
            table.HorizontalExtent,
            table.HorizontalViewport,
            table.GetCellInfos().Select(static info => info.Box.Bounds).ToArray(),
            cells,
            cells.Select(static cell => cell.Runs.ToArray()).ToArray(),
            cells.Select(GetInlineLayout).ToArray(),
            cells.Select(cell => GetCellSelectionRects(snapshot, cell)).ToArray(),
            images,
            images.Select(static image => image.Bounds).ToArray(),
            images.Select(static image => image.MeasuredImageWidth).ToArray(),
            images.Select(static image => image.MeasuredImageHeight).ToArray(),
            images.Select(static image => image.Bitmap).ToArray(),
            images.Select(GetBitmapLease).ToArray());
    }

    private static void AssertStateEqual(TableSourceState expected, TableSourceState actual)
    {
        Assert.Equal(expected.Revision, actual.Revision);
        Assert.Equal(expected.Size, actual.Size);
        Assert.Equal(expected.TableBounds, actual.TableBounds);
        Assert.Equal(expected.HorizontalOffset, actual.HorizontalOffset);
        Assert.Equal(expected.HorizontalExtent, actual.HorizontalExtent);
        Assert.Equal(expected.HorizontalViewport, actual.HorizontalViewport);
        Assert.Equal(expected.CellBounds, actual.CellBounds);
        Assert.Equal(expected.Cells.Length, actual.Cells.Length);
        for (int cellIndex = 0; cellIndex < expected.Cells.Length; cellIndex++)
        {
            Assert.Same(expected.Cells[cellIndex], actual.Cells[cellIndex]);
            Assert.Equal(expected.Runs[cellIndex].Length, actual.Runs[cellIndex].Length);
            for (int runIndex = 0; runIndex < expected.Runs[cellIndex].Length; runIndex++)
                Assert.Same(expected.Runs[cellIndex][runIndex], actual.Runs[cellIndex][runIndex]);
            Assert.Same(expected.Layouts[cellIndex], actual.Layouts[cellIndex]);
            Assert.Equal(expected.CellSelectionRects[cellIndex], actual.CellSelectionRects[cellIndex]);
        }
        Assert.Equal(expected.Images.Length, actual.Images.Length);
        for (int imageIndex = 0; imageIndex < expected.Images.Length; imageIndex++)
        {
            Assert.Same(expected.Images[imageIndex], actual.Images[imageIndex]);
            Assert.Equal(expected.ImageBounds[imageIndex], actual.ImageBounds[imageIndex]);
            Assert.Equal(expected.ImageWidths[imageIndex], actual.ImageWidths[imageIndex]);
            Assert.Equal(expected.ImageHeights[imageIndex], actual.ImageHeights[imageIndex]);
            Assert.Same(expected.Bitmaps[imageIndex], actual.Bitmaps[imageIndex]);
            Assert.Same(expected.Leases[imageIndex], actual.Leases[imageIndex]);
        }
    }

    private static Color[] Paint(LayoutSnapshot snapshot, CanvasDevice device, float rasterizationScale)
    {
        float width = (float)System.Math.Max(1, System.Math.Ceiling(snapshot.Size.Width + 1));
        float height = (float)System.Math.Max(1, System.Math.Ceiling(snapshot.Size.Height + 1));
        using var target = new CanvasRenderTarget(device, width, height, 96 * rasterizationScale);
        using (CanvasDrawingSession drawing = target.CreateDrawingSession())
        {
            drawing.Clear(Color.FromArgb(0, 0, 0, 0));
            snapshot.Paint(drawing, new Rect(0, 0, snapshot.Size.Width, height));
        }
        return target.GetPixelColors();
    }

    public enum TablePreparationRace
    {
        Cancellation,
        PointerInput,
        LayoutRevision,
        HorizontalScroll,
    }

    private sealed record TableSourceState(
        long Revision,
        Size Size,
        Rect TableBounds,
        double HorizontalOffset,
        double HorizontalExtent,
        double HorizontalViewport,
        Rect[] CellBounds,
        InlineContainerBox[] Cells,
        InlineRun[][] Runs,
        CanvasTextLayout[] Layouts,
        Rect[][] CellSelectionRects,
        ImageBox[] Images,
        Rect[] ImageBounds,
        float[] ImageWidths,
        float[] ImageHeights,
        CanvasBitmap?[] Bitmaps,
        SharedCanvasBitmapCache.Lease[] Leases);

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

    private sealed class TableResolver : IMarkdownImageResolver
    {
        private readonly ConcurrentDictionary<string, MarkdownImageAsset> _assets = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, int> _counts = new(StringComparer.Ordinal);
        private readonly TaskCompletionSource<bool> _pendingResolve =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly string _cachePrefix = "root-local-table:" + Guid.NewGuid().ToString("N") + ":";
        private readonly string? _pendingUrl;
        private readonly string? _farUrl;
        private readonly string? _standaloneUrl;

        private TableResolver(int generatedImageCount, bool includePending, bool includeStandalone,
            bool narrowImages = false)
        {
            GeneratedImageCount = generatedImageCount;
            string suffix = Guid.NewGuid().ToString("N");
            WideUrl = $"https://images.example.test/table-wide-{suffix}.png";
            TallUrl = $"https://images.example.test/table-tall-{suffix}.png";
            SquareUrl = $"https://images.example.test/table-square-{suffix}.png";
            InlineUrl = $"https://images.example.test/table-inline-{suffix}.png";
            _pendingUrl = includePending ? $"https://images.example.test/table-pending-{suffix}.png" : null;
            _farUrl = includePending ? $"https://images.example.test/table-far-{suffix}.png" : null;
            _standaloneUrl = includeStandalone ? $"https://images.example.test/table-standalone-{suffix}.png" : null;
            if (generatedImageCount > 0)
                for (int index = 0; index < generatedImageCount; index++)
                    AddAsset(GeneratedUrl(index), 96 + (index % 5) * 24, 48 + (index % 3) * 32);
            if (generatedImageCount == 0)
            {
                AddAsset(WideUrl, narrowImages ? 8 : 480, narrowImages ? 2 : 96);
                AddAsset(TallUrl, narrowImages ? 12 : 96, narrowImages ? 64 : 240);
                AddAsset(SquareUrl, narrowImages ? 16 : 168, narrowImages ? 16 : 168);
                AddAsset(InlineUrl, 128, 48);
            }
            if (_farUrl is not null)
                AddAsset(_farUrl, 96, 64);
            if (_standaloneUrl is not null)
                AddAsset(_standaloneUrl, 160, 96);
        }

        internal string WideUrl { get; }
        internal string TallUrl { get; }
        internal string SquareUrl { get; }
        internal string InlineUrl { get; }
        internal string PendingUrl => _pendingUrl ?? throw new InvalidOperationException("No pending URI was configured.");
        internal string FarUrl => _farUrl ?? throw new InvalidOperationException("No far-off URI was configured.");
        internal string StandaloneUrl => _standaloneUrl ?? throw new InvalidOperationException("No standalone URI was configured.");
        internal int GeneratedImageCount { get; }
        internal string[] Sources => _assets.Keys.Order(StringComparer.Ordinal).ToArray();
        internal string GeneratedUrl(int index) => $"https://images.example.test/generated-table-{index}-{_cachePrefix}.png";
        internal int ResolveCountFor(string source) => _counts.TryGetValue(source, out int count) ? count : 0;

        internal static TableResolver CreateStandard(bool includePending = false, bool includeStandalone = false,
            bool narrowImages = false) =>
            new(generatedImageCount: 0, includePending, includeStandalone, narrowImages);

        internal static TableResolver CreateGenerated(int imageCount, int columns, int bodyRows)
        {
            _ = columns;
            _ = bodyRows;
            return new(generatedImageCount: imageCount, includePending: false, includeStandalone: false);
        }

        internal Task WaitForPendingResolveAsync() => _pendingResolve.Task;

        public async ValueTask<MarkdownImageResolution> ResolveAsync(
            string source,
            MarkdownImageResolveContext context,
            CancellationToken cancellationToken)
        {
            _counts.AddOrUpdate(source, 1, static (_, count) => count + 1);
            if (string.Equals(source, _pendingUrl, StringComparison.Ordinal))
            {
                _pendingResolve.TrySetResult(true);
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
            }

            return _assets.TryGetValue(source, out MarkdownImageAsset? asset)
                ? MarkdownImageResolution.Resolved(asset)
                : MarkdownImageResolution.Unavailable;
        }

        private void AddAsset(string source, int width, int height)
        {
            _assets[source] = new MarkdownImageAsset(
                DisplaySizedRasterDecodeTests.CreatePng(width, height),
                "image/png",
                new Uri(source),
                _cachePrefix + Guid.NewGuid().ToString("N"));
        }
    }
}
