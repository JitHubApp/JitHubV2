using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using MarkdownRenderer.Accessibility.Internal.Semantics;
using MarkdownRenderer.Document;
using MarkdownRenderer.GitHub;
using MarkdownRenderer.GitHub.Tests.Layout.Internal.Images.BitmapCache;
using MarkdownRenderer.Layout.Internal.Boxes;
using MarkdownRenderer.Layout.Internal.Building;
using MarkdownRenderer.Layout.Internal.Context;
using MarkdownRenderer.Layout.Internal.Images;
using MarkdownRenderer.Layout.Internal.Inlines;
using MarkdownRenderer.Layout.Internal.Inlines.Runs;
using MarkdownRenderer.Layout.Internal.PrivatePlacement;
using MarkdownRenderer.Layout.Internal.Snapshots;
using MarkdownRenderer.Layout.Internal.Tables;
using MarkdownRenderer.Parsing.Internal;
using MarkdownRenderer.Theming;
using Markdig;
using Markdig.Extensions.Tables;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.UI.Xaml;
using Windows.Foundation;
using Windows.UI;
using Xunit;
using static MarkdownRenderer.GitHub.Tests.CompositeRasterCandidateAssertions;
using static MarkdownRenderer.GitHub.Tests.CompositeRasterCandidateFixtures;

namespace MarkdownRenderer.GitHub.Tests;

[Collection(SharedCanvasBitmapCacheTestCollection.Name)]
public sealed class CompositeRasterLayoutCandidateTests
{
    private const float LayoutWidth = 120f;
    private const float NarrowLayoutWidth = 50f;

    [Fact]
    public Task ParsedGfmTableRasterRootBuildsPrivateCandidate()
        => AssertParsedCompositeRootBuildsPrivateCandidateAsync("table");

    [Fact]
    public Task ParsedNestedGfmListRasterRootBuildsPrivateCandidate()
        => AssertParsedCompositeRootBuildsPrivateCandidateAsync("nested-list");

    [Fact]
    public Task ParsedGfmQuoteRasterRootBuildsPrivateCandidate()
        => AssertParsedCompositeRootBuildsPrivateCandidateAsync("quote");

    private async Task AssertParsedCompositeRootBuildsPrivateCandidateAsync(string shape)
    {
        await using var fixture = new CompletedRasterPlacementCloneTests.PlacementFixture();
        string sourceText = CreateGitHubCompositeMarkdown(shape, fixture.RasterSource);
        var registry = new MarkdownExtensionRegistry().ConfigureGitHubReadmeRegistry();
        Markdig.Syntax.MarkdownDocument parsed = Markdown.Parse(sourceText, registry.BuildPipeline());
        AssertParsedShape(shape, parsed);

        var sourceMap = new MarkdownSourceMap(sourceText);
        MarkdownLayoutContext context = CreateParsedContext(fixture, sourceMap, registry);
        MarkdownDocument semanticDocument = MarkdownDocument.FromParsed(sourceText, parsed);
        LayoutSnapshot source = new LayoutBuilder(context, semanticDocument: semanticDocument)
            .Build(parsed, LayoutWidth);
        source.AssignAutomationDocumentGeneration(239);
        SingleRasterLayoutCandidate? candidate = null;
        MarkdownLayoutContext? candidateContext = null;
        try
        {
            BlockBox sourceRoot = Assert.Single(source.GetMeasuredTopLevelBlocks());
            AssertSourceRootShape(shape, sourceRoot);

            InlineImageRun sourceImageRun = Assert.Single(EnumerateInlineImageRuns(sourceRoot));
            Assert.NotNull(sourceImageRun.Image.Bitmap);
            LinkRun sourceLinkRun = Assert.IsType<LinkRun>(
                Assert.Single(EnumerateInlineRuns(sourceRoot), static run => run is LinkRun));
            Assert.Equal("https://example.test/docs", sourceLinkRun.Url);
            if (shape == "nested-list")
                Assert.Equal(2, EnumerateBlocks(sourceRoot).OfType<ListItemBox>().Count());

            Rect sourceBounds = sourceRoot.Bounds;
            MarkdownSemanticDocument sourceSemantics = source.SemanticDocument;
            candidate = source.CreateSingleRasterCandidate(
                new[] { sourceRoot.BlockIndex },
                LayoutWidth,
                CancellationToken.None,
                CancellationToken.None,
                fixture.ImagePreparationOwnerToken,
                retirementSink: null,
                imageUnavailable: null,
                out candidateContext);
            SingleRasterLayoutCandidate ownedCandidate = Assert.IsType<SingleRasterLayoutCandidate>(candidate);
            MarkdownLayoutContext ownedCandidateContext = Assert.IsType<MarkdownLayoutContext>(candidateContext);
            await ownedCandidate.DrainConstructionAsync().WaitAsync(TimeSpan.FromSeconds(10));

            AssertCandidateRootClonesSource(ownedCandidate, sourceRoot, sourceSemantics, sourceImageRun, sourceLinkRun);
            Assert.Equal(sourceBounds, sourceRoot.Bounds);
            Assert.NotNull(sourceImageRun.Image.Bitmap);
        }
        finally
        {
            candidate?.Dispose();
            if (candidateContext is not null)
                await candidateContext.DrainOwnedRetirementsAsync().WaitAsync(TimeSpan.FromSeconds(10));
            await source.Retire().WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    [Fact]
    public async Task TableRasterCandidateRebuildsCellsAndLeavesSourcePaintHitTestAndSemanticsUntouched()
    {
        await using var fixture = new CompletedRasterPlacementCloneTests.PlacementFixture();
        TableBox sourceTable = CreateTable(fixture, LayoutWidth, out InlineContainerBox emptyCell);
        sourceTable.CollapsedTopMargin = 4;
        sourceTable.SuppressTopMargin = true;
        sourceTable.SuppressBottomMargin = true;
        _ = sourceTable.Measure(LayoutWidth);
        sourceTable.Arrange(0, 0, LayoutWidth);
        using LayoutSnapshot source = CreateSourceSnapshot(fixture, sourceTable, LayoutWidth, 211);

        InlineContainerBox[] sourceCells = sourceTable.GetCellBoxes().ToArray();
        Rect sourceTableBounds = sourceTable.Bounds;
        Rect[] sourceCellBounds = sourceCells.Select(static cell => cell.Bounds).ToArray();
        CanvasTextLayout sourceTextLayout = fixture.Source.TextLayout!;
        CanvasBitmap sourceBitmap = Assert.IsType<CanvasBitmap>(fixture.ImageRun.Image.Bitmap);
        Point sourceHitPoint = Center(sourceCells[0].Bounds);
        bool hadHit = sourceTable.HitTest(sourceHitPoint, out DocumentPosition sourcePosition);
        Assert.True(hadHit);
        Color[] pixelsBefore = PaintTable(fixture.Device, sourceTable, LayoutWidth);
        MarkdownSemanticDocument sourceSemantics = source.SemanticDocument;

        SingleRasterLayoutCandidate? candidate = source.CreateSingleRasterCandidate(
            new[] { sourceTable.BlockIndex },
            LayoutWidth,
            CancellationToken.None,
            CancellationToken.None,
            fixture.ImagePreparationOwnerToken,
            retirementSink: null,
            imageUnavailable: null,
            out MarkdownLayoutContext? constructionContext);
        SingleRasterLayoutCandidate ownedCandidate = Assert.IsType<SingleRasterLayoutCandidate>(candidate);
        MarkdownLayoutContext candidateContext = Assert.IsType<MarkdownLayoutContext>(constructionContext);
        try
        {
            await ownedCandidate.DrainConstructionAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(ownedCandidate.HasCurrentSourceRaster());
            Assert.Equal(1, ownedCandidate.SourceImageCount);

            TableBox candidateTable = Assert.IsType<TableBox>(
                Assert.Single(ownedCandidate.Snapshot.GetMeasuredTopLevelBlocks()));
            InlineContainerBox[] candidateCells = candidateTable.GetCellBoxes().ToArray();
            AssertCellClonesMatchSource(sourceCells, candidateCells, candidateContext);
            AssertTableShellIsPrivateCopy(sourceTable, candidateTable);

            InlineImageRun candidateImageRun = Assert.Single(candidateCells[0].Runs.OfType<InlineImageRun>());
            Assert.NotSame(fixture.ImageRun.Image, candidateImageRun.Image);
            Assert.Same(sourceBitmap, candidateImageRun.Image.Bitmap);
            Assert.Same(source.SourceMap, candidateContext.SourceMap);
            Assert.Equal(sourceSemantics.Text, ownedCandidate.Snapshot.SemanticDocument.Text);
            Assert.Equal(
                sourceSemantics.SemanticRangeFingerprint,
                ownedCandidate.Snapshot.SemanticDocument.SemanticRangeFingerprint);

            Assert.Equal(sourceTableBounds, sourceTable.Bounds);
            for (int index = 0; index < sourceCells.Length; index++)
                Assert.Equal(sourceCellBounds[index], sourceCells[index].Bounds);
            Assert.Same(sourceTextLayout, fixture.Source.TextLayout!);
            Assert.Same(sourceBitmap, fixture.ImageRun.Image.Bitmap);
            Assert.True(sourceTable.HitTest(sourceHitPoint, out DocumentPosition sourcePositionAfter));
            Assert.Equal(sourcePosition, sourcePositionAfter);
            Assert.Equal(pixelsBefore, PaintTable(fixture.Device, sourceTable, LayoutWidth));
        }
        finally
        {
            ownedCandidate.Dispose();
            await candidateContext.DrainOwnedRetirementsAsync().WaitAsync(TimeSpan.FromSeconds(10));
        }

        Assert.Same(emptyCell, sourceCells[1]);
    }

    [Fact]
    public async Task TableRasterCandidatePreservesScrollOffsetAndSourceScrollInvalidatesItsFence()
    {
        await using var fixture = new CompletedRasterPlacementCloneTests.PlacementFixture();
        TableBox sourceTable = CreateTable(fixture, NarrowLayoutWidth, out _);
        Assert.True(sourceTable.CanScrollHorizontally);
        Assert.True(sourceTable.SetHorizontalOffset(double.MaxValue));
        double sourceOffset = sourceTable.HorizontalOffset;
        Assert.True(sourceOffset > 0);

        using LayoutSnapshot source = CreateSourceSnapshot(fixture, sourceTable, NarrowLayoutWidth, 223);
        SingleRasterLayoutCandidate? candidate = source.CreateSingleRasterCandidate(
            new[] { sourceTable.BlockIndex },
            NarrowLayoutWidth,
            CancellationToken.None,
            CancellationToken.None,
            fixture.ImagePreparationOwnerToken,
            retirementSink: null,
            imageUnavailable: null,
            out MarkdownLayoutContext? constructionContext);
        SingleRasterLayoutCandidate ownedCandidate = Assert.IsType<SingleRasterLayoutCandidate>(candidate);
        MarkdownLayoutContext candidateContext = Assert.IsType<MarkdownLayoutContext>(constructionContext);
        try
        {
            await ownedCandidate.DrainConstructionAsync().WaitAsync(TimeSpan.FromSeconds(10));
            TableBox replacement = Assert.IsType<TableBox>(
                Assert.Single(ownedCandidate.Snapshot.GetMeasuredTopLevelBlocks()));
            Assert.Equal(sourceOffset, replacement.HorizontalOffset);
            Assert.InRange(replacement.HorizontalOffset, 0d, System.Math.Max(0, replacement.HorizontalExtent - replacement.HorizontalViewport));
            Assert.True(ownedCandidate.HasCurrentSourceRaster());

            Assert.True(sourceTable.SetHorizontalOffset(0));
            Assert.NotEqual(sourceOffset, sourceTable.HorizontalOffset);
            Assert.False(ownedCandidate.HasCurrentSourceRaster());
        }
        finally
        {
            ownedCandidate.Dispose();
            await candidateContext.DrainOwnedRetirementsAsync().WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    [Fact]
    public async Task TableShellRejectsIncompleteAndAliasedCellMapsWithoutTakingCellOwnership()
    {
        await using var fixture = new CompletedRasterPlacementCloneTests.PlacementFixture();
        TableBox sourceTable = CreateTable(fixture, LayoutWidth, out _);
        using LayoutSnapshot source = CreateSourceSnapshot(fixture, sourceTable, LayoutWidth, 227);
        InlineContainerBox[] sourceCells = sourceTable.GetCellBoxes().ToArray();
        MarkdownLayoutContext candidateContext = fixture.CreateContext();
        var incomplete = new Dictionary<InlineContainerBox, InlineContainerBox>(ReferenceEqualityComparer.Instance);
        Assert.False(sourceTable.TryCreatePrivateRasterPlacementClone(candidateContext, incomplete, out TableBox? candidate));
        Assert.Null(candidate);

        Assert.True(sourceCells[0].TryCreatePrivateInlinePlacementClone(
            candidateContext,
            LayoutWidth,
            out InlineContainerBox? firstClone,
            out _));
        InlineContainerBox ownedFirstClone = Assert.IsType<InlineContainerBox>(firstClone);
        var aliased = new Dictionary<InlineContainerBox, InlineContainerBox>(ReferenceEqualityComparer.Instance)
        {
            [sourceCells[0]] = ownedFirstClone,
            [sourceCells[1]] = ownedFirstClone,
        };
        try
        {
            Assert.False(sourceTable.TryCreatePrivateRasterPlacementClone(candidateContext, aliased, out candidate));
            Assert.Null(candidate);
            Assert.Same(fixture.ImageRun.Image.Bitmap, ownedFirstClone.Runs.OfType<InlineImageRun>().Single().Image.Bitmap);
            Assert.NotNull(fixture.ImageRun.Image.Bitmap);
        }
        finally
        {
            ImageBox clonedImage = ownedFirstClone.Runs.OfType<InlineImageRun>().Single().Image;
            candidateContext.DiscardConstructionBlock(ownedFirstClone);
            await clonedImage.RetirementTask.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    [Fact]
    public async Task CanceledTableShellConstructionLeavesCellClonesWithTheCaller()
    {
        await using var fixture = new CompletedRasterPlacementCloneTests.PlacementFixture();
        TableBox sourceTable = CreateTable(fixture, LayoutWidth, out _);
        using LayoutSnapshot source = CreateSourceSnapshot(fixture, sourceTable, LayoutWidth, 229);
        InlineContainerBox[] sourceCells = sourceTable.GetCellBoxes().ToArray();
        MarkdownLayoutContext candidateContext = fixture.CreateContext();
        var clonedCells = new Dictionary<InlineContainerBox, InlineContainerBox>(ReferenceEqualityComparer.Instance);
        var ownedClones = new List<InlineContainerBox>();
        foreach (InlineContainerBox sourceCell in sourceCells)
        {
            Assert.True(sourceCell.TryCreatePrivateInlinePlacementClone(
                candidateContext,
                LayoutWidth,
                out InlineContainerBox? candidateCell,
                out _));
            InlineContainerBox clone = Assert.IsType<InlineContainerBox>(candidateCell);
            clonedCells.Add(sourceCell, clone);
            ownedClones.Add(clone);
        }

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        candidateContext.SetCancellationToken(cancellation.Token);
        try
        {
            Assert.Throws<OperationCanceledException>(() =>
                sourceTable.TryCreatePrivateRasterPlacementClone(candidateContext, clonedCells, out _));
            Assert.NotNull(fixture.ImageRun.Image.Bitmap);
            Assert.Same(fixture.ImageRun.Image.Bitmap,
                ownedClones[0].Runs.OfType<InlineImageRun>().Single().Image.Bitmap);
        }
        finally
        {
            foreach (InlineContainerBox ownedClone in ownedClones)
            {
                ImageBox? clonedImage = ownedClone.Runs.OfType<InlineImageRun>().SingleOrDefault()?.Image;
                candidateContext.DiscardConstructionBlock(ownedClone);
                if (clonedImage is not null)
                    await clonedImage.RetirementTask.WaitAsync(TimeSpan.FromSeconds(10));
            }
        }
    }

    [Theory]
    [InlineData(1.0, FlowDirection.LeftToRight)]
    [InlineData(1.5, FlowDirection.RightToLeft)]
    [InlineData(2.0, FlowDirection.LeftToRight)]
    [InlineData(3.0, FlowDirection.RightToLeft)]
    [InlineData(4.0, FlowDirection.LeftToRight)]
    public async Task TableCandidatePreservesDpiFlowSelectionAndCellGeometry(
        double rasterizationScale,
        FlowDirection flowDirection)
    {
        await using var fixture = new CompletedRasterPlacementCloneTests.PlacementFixture();
        MarkdownLayoutContext sourceContext = CreateContext(fixture, rasterizationScale, flowDirection);
        TableBox sourceTable = CreateStyledImageTable(
            sourceContext, fixture.RasterSource, out InlineContainerBox imageCell, out InlineImageRun sourceImageRun);
        float sourceHeight = sourceTable.Measure(LayoutWidth);
        sourceTable.Arrange(0, 0, LayoutWidth);
        using var source = new LayoutSnapshot(
            new BlockBox[] { sourceTable },
            sourceContext.SourceMap,
            LayoutWidth,
            sourceHeight);
        source.AssignAutomationDocumentGeneration(233);

        try
        {
            InlineRun sourceTextRun = imageCell.Runs[0];
            var selection = new DocumentRange(
                new DocumentPosition(imageCell.BlockIndex, sourceTextRun.InlineIndex, 1),
                new DocumentPosition(imageCell.BlockIndex, sourceTextRun.InlineIndex, 6));
            Rect[] sourceSelection = imageCell.GetRangeRects(selection).ToArray();
            Assert.NotEmpty(sourceSelection);
            InlineContainerBox[] sourceCells = sourceTable.GetCellBoxes().ToArray();
            Assert.Equal(
                flowDirection == FlowDirection.RightToLeft,
                sourceCells[0].Bounds.X > sourceCells[1].Bounds.X);

            SingleRasterLayoutCandidate? candidate = source.CreateSingleRasterCandidate(
                new[] { sourceTable.BlockIndex },
                LayoutWidth,
                CancellationToken.None,
                CancellationToken.None,
                fixture.ImagePreparationOwnerToken,
                retirementSink: null,
                imageUnavailable: null,
                out MarkdownLayoutContext? constructionContext);
            SingleRasterLayoutCandidate ownedCandidate = Assert.IsType<SingleRasterLayoutCandidate>(candidate);
            MarkdownLayoutContext candidateContext = Assert.IsType<MarkdownLayoutContext>(constructionContext);
            try
            {
                await ownedCandidate.DrainConstructionAsync().WaitAsync(TimeSpan.FromSeconds(10));
                AssertDpiReplacementMatchesSource(ownedCandidate, source, sourceTable, sourceCells, imageCell,
                    sourceImageRun, selection, sourceSelection, rasterizationScale, flowDirection);
            }
            finally
            {
                ownedCandidate.Dispose();
                await candidateContext.DrainOwnedRetirementsAsync().WaitAsync(TimeSpan.FromSeconds(10));
            }
        }
        finally
        {
            await source.Retire().WaitAsync(TimeSpan.FromSeconds(10));
        }
    }
}
