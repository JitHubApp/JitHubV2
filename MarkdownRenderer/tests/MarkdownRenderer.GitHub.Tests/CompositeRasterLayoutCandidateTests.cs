using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using MarkdownRenderer.Accessibility;
using MarkdownRenderer.Document;
using MarkdownRenderer.GitHub;
using MarkdownRenderer.Layout;
using MarkdownRenderer.Layout.Boxes;
using MarkdownRenderer.Parsing;
using MarkdownRenderer.Theming;
using Markdig;
using Markdig.Extensions.Tables;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.UI.Xaml;
using Windows.Foundation;
using Windows.UI;
using Xunit;

namespace MarkdownRenderer.GitHub.Tests;

[Collection(SharedCanvasBitmapCacheTestCollection.Name)]
public sealed class CompositeRasterLayoutCandidateTests
{
    private const float LayoutWidth = 120f;
    private const float NarrowLayoutWidth = 50f;
    private const string RasterMarkdownTemplate = "![raster alt]({0})";

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
        Assert.Single(parsed);
        switch (shape)
        {
            case "table":
                Assert.IsType<Table>(Assert.Single(parsed));
                break;
            case "nested-list":
                Assert.IsType<Markdig.Syntax.ListBlock>(Assert.Single(parsed));
                break;
            case "quote":
                Assert.IsType<Markdig.Syntax.QuoteBlock>(Assert.Single(parsed));
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(shape), shape, "Unknown GFM fixture shape.");
        }

        var sourceMap = new MarkdownSourceMap(sourceText);
        var context = new MarkdownLayoutContext(
            fixture.Device,
            fixture.SourceContext.ThemeSnapshot,
            sourceMap,
            registry,
            FlowDirection.LeftToRight,
            language: "en-US")
        {
            CancellationToken = CancellationToken.None,
            ImageCancellationToken = CancellationToken.None,
            ImagePreparationOwner = fixture.ImagePreparationOwnerToken,
            PerformanceDocumentOwner = fixture.PerformanceDocumentOwner,
        };
        MarkdownDocument semanticDocument = MarkdownDocument.FromParsed(sourceText, parsed);
        LayoutSnapshot source = new LayoutBuilder(context, semanticDocument: semanticDocument)
            .Build(parsed, LayoutWidth);
        source.AssignAutomationDocumentGeneration(239);
        SingleRasterLayoutCandidate? candidate = null;
        MarkdownLayoutContext? candidateContext = null;
        try
        {
            BlockBox sourceRoot = Assert.Single(source.GetMeasuredTopLevelBlocks());
            switch (shape)
            {
                case "table":
                    Assert.IsType<TableBox>(sourceRoot);
                    Assert.NotEmpty(Assert.IsType<TableBox>(sourceRoot).GetCellBoxes());
                    break;
                case "nested-list":
                case "quote":
                    Assert.IsType<StackBox>(sourceRoot);
                    break;
            }

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

            Assert.True(ownedCandidate.HasCurrentSourceRaster());
            Assert.Equal(1, ownedCandidate.SourceImageCount);
            BlockBox candidateRoot = Assert.Single(ownedCandidate.Snapshot.GetMeasuredTopLevelBlocks());
            Assert.NotSame(sourceRoot, candidateRoot);
            Assert.Equal(sourceRoot.GetType(), candidateRoot.GetType());
            Assert.Equal(sourceRoot.BlockIndex, candidateRoot.BlockIndex);
            Assert.Equal(sourceRoot.Bounds, candidateRoot.Bounds);
            Assert.Equal(sourceSemantics.Text, ownedCandidate.Snapshot.SemanticDocument.Text);
            Assert.Equal(
                sourceSemantics.SemanticRangeFingerprint,
                ownedCandidate.Snapshot.SemanticDocument.SemanticRangeFingerprint);

            InlineImageRun candidateImageRun = Assert.Single(EnumerateInlineImageRuns(candidateRoot));
            Assert.NotSame(sourceImageRun, candidateImageRun);
            Assert.NotSame(sourceImageRun.Image, candidateImageRun.Image);
            Assert.Same(sourceImageRun.Image.Bitmap, candidateImageRun.Image.Bitmap);
            Assert.Equal(sourceImageRun.Text, candidateImageRun.Text);
            Assert.Equal(sourceImageRun.SourceSpan, candidateImageRun.SourceSpan);
            Assert.Equal(sourceImageRun.LinkUrl, candidateImageRun.LinkUrl);
            Assert.Equal(sourceImageRun.LinkTitle, candidateImageRun.LinkTitle);
            LinkRun candidateLinkRun = Assert.IsType<LinkRun>(
                Assert.Single(EnumerateInlineRuns(candidateRoot), static run => run is LinkRun));
            Assert.NotSame(sourceLinkRun, candidateLinkRun);
            Assert.Equal(sourceLinkRun.Url, candidateLinkRun.Url);
            Assert.Equal(sourceLinkRun.Title, candidateLinkRun.Title);
            Assert.Equal(sourceLinkRun.SourceSpan, candidateLinkRun.SourceSpan);
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
        CanvasTextLayout sourceTextLayout = GetField<CanvasTextLayout>(fixture.Source, "_layout");
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
            Assert.Equal(sourceCells.Length, candidateCells.Length);
            for (int index = 0; index < sourceCells.Length; index++)
            {
                Assert.NotSame(sourceCells[index], candidateCells[index]);
                Assert.Same(candidateContext, candidateCells[index].Context);
                Assert.Equal(sourceCells[index].BlockIndex, candidateCells[index].BlockIndex);
                Assert.Equal(sourceCells[index].ElementKey, candidateCells[index].ElementKey);
                Assert.Equal(sourceCells[index].Runs.Count, candidateCells[index].Runs.Count);
                for (int runIndex = 0; runIndex < sourceCells[index].Runs.Count; runIndex++)
                {
                    InlineRun sourceRun = sourceCells[index].Runs[runIndex];
                    InlineRun candidateRun = candidateCells[index].Runs[runIndex];
                    Assert.NotSame(sourceRun, candidateRun);
                    Assert.Equal(sourceRun.InlineIndex, candidateRun.InlineIndex);
                    Assert.Equal(sourceRun.SourceSpan, candidateRun.SourceSpan);
                    Assert.Equal(sourceRun.Text, candidateRun.Text);
                    Assert.Equal(sourceRun.ElementKey, candidateRun.ElementKey);
                    Assert.Equal(sourceRun.StyleAliases, candidateRun.StyleAliases);
                    Assert.Equal(sourceRun.StyleModifierKeys, candidateRun.StyleModifierKeys);
                    Assert.Equal(sourceRun.SemanticHeadingKey, candidateRun.SemanticHeadingKey);
                }
            }

            Assert.NotSame(sourceTable, candidateTable);
            Assert.Equal(sourceTable.BlockIndex, candidateTable.BlockIndex);
            Assert.Equal(sourceTable.HorizontalOffset, candidateTable.HorizontalOffset);
            Assert.Equal(sourceTable.Margin, candidateTable.Margin);
            Assert.Equal(sourceTable.CollapsedTopMargin, candidateTable.CollapsedTopMargin);
            Assert.Equal(sourceTable.SuppressTopMargin, candidateTable.SuppressTopMargin);
            Assert.Equal(sourceTable.SuppressBottomMargin, candidateTable.SuppressBottomMargin);
            Assert.NotSame(GetReferenceField(sourceTable, "_headerCells"), GetReferenceField(candidateTable, "_headerCells"));
            Assert.NotSame(GetReferenceField(sourceTable, "_bodyCells"), GetReferenceField(candidateTable, "_bodyCells"));
            InlineContainerBox[][] sourceBodyRows = GetField<InlineContainerBox[][]>(sourceTable, "_bodyCells");
            InlineContainerBox[][] candidateBodyRows = GetField<InlineContainerBox[][]>(candidateTable, "_bodyCells");
            Assert.NotSame(sourceBodyRows[0], candidateBodyRows[0]);
            Assert.NotSame(GetReferenceField(sourceTable, "_columnAlignments"), GetReferenceField(candidateTable, "_columnAlignments"));
            Assert.NotSame(GetReferenceField(sourceTable, "_colWidths"), GetReferenceField(candidateTable, "_colWidths"));
            Assert.NotSame(GetReferenceField(sourceTable, "_rowHeights"), GetReferenceField(candidateTable, "_rowHeights"));
            Assert.NotSame(GetReferenceField(sourceTable, "_rowBottomEdges"), GetReferenceField(candidateTable, "_rowBottomEdges"));

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
            Assert.Same(sourceTextLayout, GetField<CanvasTextLayout>(fixture.Source, "_layout"));
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
        InlineContainerBox imageCell = new(sourceContext, MarkdownElementKeys.TableCell)
        {
            BlockIndex = sourceContext.NextBlockIndex(),
        };
        var textRun = new TextRun("caption ")
        {
            ElementKey = MarkdownElementKeys.Strong,
            SourceSpan = new SourceSpan(0, 8),
            StyleAliases = new[] { "table-caption" },
            StyleModifierKeys = new[] { MarkdownElementKeys.Emphasis },
            SemanticHeadingKey = MarkdownElementKeys.Heading2,
        };
        imageCell.Add(textRun);
        var sourceImageRun = new InlineImageRun(
            sourceContext,
            "Raster alt",
            fixture.RasterSource,
            linkUrl: "https://example.test/table-image")
        {
            ElementKey = MarkdownElementKeys.Link,
            SourceSpan = new SourceSpan(8, 1),
            StyleAliases = new[] { "table-image" },
            StyleModifierKeys = new[] { MarkdownElementKeys.Strong },
            SemanticHeadingKey = MarkdownElementKeys.Heading3,
        };
        imageCell.Add(sourceImageRun);
        InlineContainerBox emptyCell = new(sourceContext, MarkdownElementKeys.TableCell)
        {
            BlockIndex = sourceContext.NextBlockIndex(),
        };
        var sourceTable = new TableBox(
            sourceContext,
            Array.Empty<InlineContainerBox[]>(),
            new[] { new[] { imageCell, emptyCell } },
            new[] { TableBox.CellAlignment.Left, TableBox.CellAlignment.Right })
        {
            BlockIndex = sourceContext.NextBlockIndex(),
        };
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
            TableBox replacement = Assert.IsType<TableBox>(
                Assert.Single(ownedCandidate.Snapshot.GetMeasuredTopLevelBlocks()));
            InlineContainerBox[] replacementCells = replacement.GetCellBoxes().ToArray();
            InlineContainerBox replacementImageCell = replacementCells[0];

            Assert.Equal(rasterizationScale, replacement.Context.RasterizationScale);
            Assert.Equal(flowDirection, replacement.Context.FlowDirection);
            Assert.Equal(flowDirection == FlowDirection.RightToLeft, replacement.IsRightToLeft);
            Assert.Equal(sourceTable.ColumnCount, replacement.ColumnCount);
            Assert.Equal(sourceCells[0].ElementKey, replacementImageCell.ElementKey);
            Assert.Equal(sourceCells[0].BlockIndex, replacementImageCell.BlockIndex);
            Assert.Equal(sourceCells[0].Runs.Select(static run => run.InlineIndex),
                replacementImageCell.Runs.Select(static run => run.InlineIndex));
            Assert.Equal(
                sourceCells.Select(static cell => cell.Bounds).ToArray(),
                replacementCells.Select(static cell => cell.Bounds).ToArray());
            Assert.Equal(sourceTable.Bounds, replacement.Bounds);
            Assert.Equal(sourceSelection, replacementImageCell.GetRangeRects(selection).ToArray());
            Assert.Equal(source.SemanticDocument.Text, ownedCandidate.Snapshot.SemanticDocument.Text);
            Assert.Equal(
                source.SemanticDocument.SemanticRangeFingerprint,
                ownedCandidate.Snapshot.SemanticDocument.SemanticRangeFingerprint);
            InlineImageRun replacementImage = Assert.Single(replacementImageCell.Runs.OfType<InlineImageRun>());
            Assert.Equal(sourceImageRun.DesiredWidth, replacementImage.DesiredWidth);
            Assert.Equal(sourceImageRun.DesiredHeight, replacementImage.DesiredHeight);
            Assert.Equal(
                GetField<Size>(sourceImageRun.Image, "_rasterIntrinsicSize"),
                GetField<Size>(replacementImage.Image, "_rasterIntrinsicSize"));
            Assert.Equal("https://example.test/table-image", replacementImage.LinkUrl);
            Assert.NotSame(imageCell.Runs.OfType<InlineImageRun>().Single().Image, replacementImage.Image);
            Assert.NotNull(replacementImage.Image.Bitmap);
            Assert.True(ownedCandidate.HasCurrentSourceRaster());
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

    private static TableBox CreateTable(
        CompletedRasterPlacementCloneTests.PlacementFixture fixture,
        float width,
        out InlineContainerBox emptyCell)
    {
        MarkdownLayoutContext context = fixture.SourceContext;
        emptyCell = new InlineContainerBox(context, MarkdownElementKeys.TableCell)
        {
            BlockIndex = context.NextBlockIndex(),
        };
        _ = emptyCell.Measure(width);
        emptyCell.Arrange(0, 0, width);

        var table = new TableBox(
            context,
            Array.Empty<InlineContainerBox[]>(),
            new[] { new[] { fixture.Source, emptyCell } },
            new[] { TableBox.CellAlignment.Left, TableBox.CellAlignment.Right })
        {
            BlockIndex = context.NextBlockIndex(),
        };
        float height = table.Measure(width);
        table.Arrange(0, 0, width);
        return table;
    }

    private static string CreateGitHubCompositeMarkdown(string shape, string imageSource)
    {
        string image = string.Format(
            System.Globalization.CultureInfo.InvariantCulture,
            RasterMarkdownTemplate,
            imageSource);
        return shape switch
        {
            "table" => $"| Resource | Preview |\n| :--- | ---: |\n| [docs](https://example.test/docs) | {image} |",
            "nested-list" => $"1. Outer [docs](https://example.test/docs)\n   - Inner preview: {image}",
            "quote" => $"> Quoted [docs](https://example.test/docs).\n>\n> Preview: {image}",
            _ => throw new ArgumentOutOfRangeException(nameof(shape), shape, "Unknown GFM fixture shape."),
        };
    }

    private static IEnumerable<InlineRun> EnumerateInlineRuns(BlockBox root)
    {
        if (root is InlineContainerBox inline)
        {
            foreach (InlineRun run in inline.Runs)
                yield return run;
        }

        foreach (BlockBox child in EnumerateChildBlocks(root))
        {
            foreach (InlineRun run in EnumerateInlineRuns(child))
                yield return run;
        }
    }

    private static IEnumerable<InlineImageRun> EnumerateInlineImageRuns(BlockBox root)
    {
        foreach (InlineRun run in EnumerateInlineRuns(root))
        {
            if (run is InlineImageRun image)
                yield return image;
        }
    }

    private static IEnumerable<BlockBox> EnumerateChildBlocks(BlockBox root)
    {
        switch (root)
        {
            case TableBox table:
                foreach (InlineContainerBox cell in table.GetCellBoxes())
                    yield return cell;
                break;
            case ListItemBox item:
                yield return item.Marker;
                yield return item.Content;
                break;
            case StackBox stack:
                foreach (BlockBox child in stack.Children)
                    yield return child;
                break;
        }
    }

    private static IEnumerable<BlockBox> EnumerateBlocks(BlockBox root)
    {
        yield return root;
        foreach (BlockBox child in EnumerateChildBlocks(root))
        {
            foreach (BlockBox descendant in EnumerateBlocks(child))
                yield return descendant;
        }
    }

    private static MarkdownLayoutContext CreateContext(
        CompletedRasterPlacementCloneTests.PlacementFixture fixture,
        double rasterizationScale,
        FlowDirection flowDirection)
    {
        MarkdownLayoutContext template = fixture.SourceContext;
        return new MarkdownLayoutContext(
            template.ResourceCreator,
            template.ThemeSnapshot,
            template.SourceMap,
            template.Registry,
            flowDirection,
            template.Dispatcher,
            template.Language)
        {
            CancellationToken = CancellationToken.None,
            ImageCancellationToken = CancellationToken.None,
            RetirementSink = template.RetirementSink,
            RasterizationScale = rasterizationScale,
            IsCodeBlockCopyEnabled = template.IsCodeBlockCopyEnabled,
            CodeBlockLineNumberMode = template.CodeBlockLineNumberMode,
            CodeBlockWrappingMode = template.CodeBlockWrappingMode,
            CodeBlockChromeMode = template.CodeBlockChromeMode,
            IsTaskListEditingEnabled = template.IsTaskListEditingEnabled,
            CommandProvider = template.CommandProvider,
            StringProvider = template.StringProvider,
            ImageResolver = template.ImageResolver,
            PerformanceSession = template.PerformanceSession,
            PerformanceDocumentOwner = fixture.PerformanceDocumentOwner,
            ImagePreparationOwner = fixture.ImagePreparationOwnerToken,
            SvgRenderer = template.SvgRenderer,
            ImageBaseUri = template.ImageBaseUri,
            ImageDocumentPath = template.ImageDocumentPath,
            ImageDocumentSource = template.ImageDocumentSource,
            AllowThirdPartyRemoteImages = template.AllowThirdPartyRemoteImages,
            DisclosureStates = template.DisclosureStates,
        };
    }

    private static LayoutSnapshot CreateSourceSnapshot(
        CompletedRasterPlacementCloneTests.PlacementFixture fixture,
        TableBox table,
        float width,
        long automationGeneration)
    {
        var snapshot = new LayoutSnapshot(
            new BlockBox[] { table },
            fixture.SourceContext.SourceMap,
            width,
            (float)table.Bounds.Height);
        fixture.TransferSourceOwnershipToSnapshot(snapshot);
        snapshot.AssignAutomationDocumentGeneration(automationGeneration);
        return snapshot;
    }

    private static Color[] PaintTable(CanvasDevice device, TableBox table, float width)
    {
        float height = System.Math.Max(1, (float)System.Math.Ceiling(table.Bounds.Height + 4));
        using var target = new CanvasRenderTarget(device, width, height, 96);
        using (CanvasDrawingSession drawing = target.CreateDrawingSession())
        {
            drawing.Clear(Color.FromArgb(0, 0, 0, 0));
            table.Paint(drawing, new Rect(0, 0, width, height));
        }
        return target.GetPixelColors();
    }

    private static Point Center(Rect bounds)
        => new(bounds.X + bounds.Width / 2, bounds.Y + bounds.Height / 2);

    private static T GetField<T>(object instance, string name)
    {
        FieldInfo field = instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!;
        return Assert.IsType<T>(field.GetValue(instance));
    }

    private static object GetReferenceField(object instance, string name)
    {
        FieldInfo field = instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!;
        return Assert.IsAssignableFrom<object>(field.GetValue(instance));
    }
}
