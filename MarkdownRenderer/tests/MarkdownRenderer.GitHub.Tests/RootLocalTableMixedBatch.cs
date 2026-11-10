using MarkdownRenderer.Layout;
using MarkdownRenderer.Layout.Boxes;
using MarkdownRenderer.Layout.Internal.Images;
using Microsoft.Graphics.Canvas.Text;
using Xunit;
using static MarkdownRenderer.GitHub.Tests.RootLocalTableQueries;
using static MarkdownRenderer.GitHub.Tests.RootLocalTableRelayoutAssertions;

namespace MarkdownRenderer.GitHub.Tests;

/// <summary>
/// The roots of the three-table mixed batch: three image tables, a paragraph
/// with an inline image, a standalone image, and every image in document
/// order. It also builds the batch's changed-root set and checks the
/// committed paragraph and tables against the fallback document.
/// </summary>
internal sealed record RootLocalTableMixedBatch(
    TableBox[] Tables,
    InlineContainerBox Paragraph,
    ImageBox Standalone,
    ImageBox[] Images)
{
    internal static RootLocalTableMixedBatch Find(LayoutSnapshot snapshot, RootLocalTableResolver resolver) => new(
        snapshot.Blocks.OfType<TableBox>().ToArray(),
        Assert.Single(snapshot.Blocks.OfType<InlineContainerBox>(),
            inline => inline.Runs.OfType<InlineImageRun>().Any()),
        Assert.Single(snapshot.Blocks.OfType<ImageBox>(),
            image => string.Equals(image.Url, resolver.StandaloneUrl, StringComparison.Ordinal)),
        snapshot.Blocks.SelectMany(EnumerateImages).ToArray());

    internal BlockBox[] ChangedRootOwners() => Tables.Cast<BlockBox>().Append(Paragraph).Append(Standalone).ToArray();

    internal int[] ChangedRootIndices() => Tables.Select(static table => table.BlockIndex)
        .Append(Paragraph.BlockIndex).Append(Standalone.BlockIndex).Order().ToArray();

    internal HashSet<int> CollectChangedRoots()
    {
        var changedRoots = new HashSet<int>();
        // Intentionally mix cell-image invalidation identities and enclosing
        // root identities. The root-local entry point must normalize them to
        // distinct top-level roots before applying the batch.
        changedRoots.Add(Images[0].BlockIndex);
        changedRoots.Add(Tables[1].BlockIndex);
        changedRoots.Add(Images[1].BlockIndex);
        changedRoots.Add(Tables[0].BlockIndex);
        changedRoots.Add(Paragraph.BlockIndex);
        changedRoots.Add(Images[2].BlockIndex);
        changedRoots.Add(Tables[2].BlockIndex);
        changedRoots.Add(Standalone.BlockIndex);
        foreach (ImageBox image in Images)
            changedRoots.Add(image.BlockIndex);
        Assert.True(changedRoots.Count > 5);
        Assert.All(Tables, table => Assert.Contains(table.BlockIndex, changedRoots));
        Assert.All(Images, image => Assert.Contains(image.BlockIndex, changedRoots));
        return changedRoots;
    }

    internal void AssertParagraphRebuiltLikeFallback(
        InlineRun[] paragraphRunsBefore,
        CanvasTextLayout paragraphLayoutBefore,
        RootLocalTableMixedBatch fallbackBatch,
        LayoutSnapshot optimized,
        LayoutSnapshot fallback)
    {
        InlineContainerBox fallbackParagraph = fallbackBatch.Paragraph;
        Assert.Same(Paragraph, Assert.Single(optimized.Blocks, root => ReferenceEquals(root, Paragraph)));
        Assert.NotSame(paragraphLayoutBefore, GetInlineLayout(Paragraph));
        AssertRunsPreserved(paragraphRunsBefore, Paragraph.Runs);
        Assert.Equal(LayoutPlacementTestGeometry.ToLogical(fallback, fallbackParagraph),
            LayoutPlacementTestGeometry.ToLogical(optimized, Paragraph));
        Assert.Equal(
            GetCellSelectionRects(fallback, fallbackParagraph),
            GetCellSelectionRects(optimized, Paragraph));
    }

    internal void AssertTablesRebuiltLikeFallback(
        RootLocalTableMixedBatch fallbackBatch,
        RootLocalTableOwners[] tablesBefore,
        float[][] widthsBefore,
        LayoutSnapshot optimized,
        LayoutSnapshot fallback)
    {
        bool columnsRebalanced = false;
        for (int tableIndex = 0; tableIndex < Tables.Length; tableIndex++)
        {
            TableBox table = Tables[tableIndex];
            TableBox fallbackTable = fallbackBatch.Tables[tableIndex];
            RootLocalTableOwners before = tablesBefore[tableIndex];
            Assert.Same(table, Assert.Single(optimized.Blocks, root => ReferenceEquals(root, table)));
            AssertViewportMatches(fallbackTable, table);
            AssertCellsMatchFallback(before.Cells, before.Runs, before.Layouts, table.GetCellBoxes().ToArray(),
                fallbackTable.GetCellBoxes().ToArray(), optimized, fallback);
            float[] finalWidths = CaptureColumnWidths(table);
            Assert.Equal(CaptureColumnWidths(fallbackTable), finalWidths);
            columnsRebalanced |= AnyColumnWidthChanged(widthsBefore[tableIndex], finalWidths);
        }
        Assert.True(columnsRebalanced, "Image aspect ratios must rebalance at least one table in the mixed batch.");
    }
}
