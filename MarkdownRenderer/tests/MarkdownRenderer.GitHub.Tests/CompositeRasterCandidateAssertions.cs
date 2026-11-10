using MarkdownRenderer.Accessibility.Internal.Semantics;
using MarkdownRenderer.Document;
using MarkdownRenderer.Layout.Internal.Boxes;
using MarkdownRenderer.Layout.Internal.Context;
using MarkdownRenderer.Layout.Internal.Inlines;
using MarkdownRenderer.Layout.Internal.Inlines.Runs;
using MarkdownRenderer.Layout.Internal.PrivatePlacement;
using MarkdownRenderer.Layout.Internal.Snapshots;
using MarkdownRenderer.Layout.Internal.Tables;
using Markdig.Extensions.Tables;
using Microsoft.UI.Xaml;
using Windows.Foundation;
using Xunit;
using static MarkdownRenderer.GitHub.Tests.CompositeRasterCandidateFixtures;

namespace MarkdownRenderer.GitHub.Tests;

/// <summary>
/// Shape checks and clone oracles for single-raster candidates over table,
/// nested-list and quote roots: the candidate rebuilds every owner privately
/// and keeps the source's identity, geometry and semantics.
/// </summary>
internal static class CompositeRasterCandidateAssertions
{
    internal static void AssertParsedShape(string shape, Markdig.Syntax.MarkdownDocument parsed)
    {
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
    }

    internal static void AssertSourceRootShape(string shape, BlockBox sourceRoot)
    {
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
    }

    internal static void AssertCandidateRootClonesSource(
        SingleRasterLayoutCandidate candidate,
        BlockBox sourceRoot,
        MarkdownSemanticDocument sourceSemantics,
        InlineImageRun sourceImageRun,
        LinkRun sourceLinkRun)
    {
        Assert.True(candidate.HasCurrentSourceRaster());
        Assert.Equal(1, candidate.SourceImageCount);
        BlockBox candidateRoot = Assert.Single(candidate.Snapshot.GetMeasuredTopLevelBlocks());
        Assert.NotSame(sourceRoot, candidateRoot);
        Assert.Equal(sourceRoot.GetType(), candidateRoot.GetType());
        Assert.Equal(sourceRoot.BlockIndex, candidateRoot.BlockIndex);
        Assert.Equal(sourceRoot.Bounds, candidateRoot.Bounds);
        Assert.Equal(sourceSemantics.Text, candidate.Snapshot.SemanticDocument.Text);
        Assert.Equal(
            sourceSemantics.SemanticRangeFingerprint,
            candidate.Snapshot.SemanticDocument.SemanticRangeFingerprint);

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
    }

    internal static void AssertCellClonesMatchSource(
        InlineContainerBox[] sourceCells,
        InlineContainerBox[] candidateCells,
        MarkdownLayoutContext candidateContext)
    {
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
    }

    internal static void AssertTableShellIsPrivateCopy(TableBox sourceTable, TableBox candidateTable)
    {
        Assert.NotSame(sourceTable, candidateTable);
        Assert.Equal(sourceTable.BlockIndex, candidateTable.BlockIndex);
        Assert.Equal(sourceTable.HorizontalOffset, candidateTable.HorizontalOffset);
        Assert.Equal(sourceTable.Margin, candidateTable.Margin);
        Assert.Equal(sourceTable.CollapsedTopMargin, candidateTable.CollapsedTopMargin);
        Assert.Equal(sourceTable.SuppressTopMargin, candidateTable.SuppressTopMargin);
        Assert.Equal(sourceTable.SuppressBottomMargin, candidateTable.SuppressBottomMargin);
        Assert.NotSame(sourceTable.HeaderCells, candidateTable.HeaderCells);
        Assert.NotSame(sourceTable.BodyCells, candidateTable.BodyCells);
        Assert.NotSame(sourceTable.BodyCells[0], candidateTable.BodyCells[0]);
        Assert.NotSame(sourceTable.ColumnAlignments, candidateTable.ColumnAlignments);
        Assert.NotSame(Assert.IsType<float[]>(sourceTable.ColumnWidths), Assert.IsType<float[]>(candidateTable.ColumnWidths));
        Assert.NotSame(Assert.IsType<float[]>(sourceTable.RowHeights), Assert.IsType<float[]>(candidateTable.RowHeights));
        Assert.NotSame(sourceTable.RowBottomEdges, candidateTable.RowBottomEdges);
    }

    internal static void AssertDpiReplacementMatchesSource(
        SingleRasterLayoutCandidate candidate,
        LayoutSnapshot source,
        TableBox sourceTable,
        InlineContainerBox[] sourceCells,
        InlineContainerBox imageCell,
        InlineImageRun sourceImageRun,
        DocumentRange selection,
        Rect[] sourceSelection,
        double rasterizationScale,
        FlowDirection flowDirection)
    {
        TableBox replacement = Assert.IsType<TableBox>(
            Assert.Single(candidate.Snapshot.GetMeasuredTopLevelBlocks()));
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
        Assert.Equal(source.SemanticDocument.Text, candidate.Snapshot.SemanticDocument.Text);
        Assert.Equal(
            source.SemanticDocument.SemanticRangeFingerprint,
            candidate.Snapshot.SemanticDocument.SemanticRangeFingerprint);
        InlineImageRun replacementImage = Assert.Single(replacementImageCell.Runs.OfType<InlineImageRun>());
        Assert.Equal(sourceImageRun.DesiredWidth, replacementImage.DesiredWidth);
        Assert.Equal(sourceImageRun.DesiredHeight, replacementImage.DesiredHeight);
        Assert.Equal(
            sourceImageRun.Image.Presentation.RasterIntrinsicSize,
            replacementImage.Image.Presentation.RasterIntrinsicSize);
        Assert.Equal("https://example.test/table-image", replacementImage.LinkUrl);
        Assert.NotSame(imageCell.Runs.OfType<InlineImageRun>().Single().Image, replacementImage.Image);
        Assert.NotNull(replacementImage.Image.Bitmap);
        Assert.True(candidate.HasCurrentSourceRaster());
    }
}
