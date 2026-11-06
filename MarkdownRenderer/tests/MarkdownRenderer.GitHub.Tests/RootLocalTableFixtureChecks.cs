using MarkdownRenderer.Accessibility;
using MarkdownRenderer.Layout;
using MarkdownRenderer.Layout.Boxes;
using MarkdownRenderer.Theming;
using Xunit;
using static MarkdownRenderer.GitHub.Tests.RootLocalTableQueries;

namespace MarkdownRenderer.GitHub.Tests;

/// <summary>
/// Arrange-time checks that the root-local table fixtures built from
/// <see cref="RootLocalTableSources"/> have the shape the relayout tests rely
/// on, plus the shared step that scrolls a document pair to the same offset.
/// </summary>
internal static class RootLocalTableFixtureChecks
{
    /// <summary>
    /// The standard table has a link cell, strong, emphasis and code runs, an
    /// empty image cell, and three distinct images in both documents.
    /// </summary>
    internal static void AssertStandardTableFixtureShape(TableBox table, ImageBox[] images, ImageBox[] fallbackImages)
    {
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

        Assert.Equal(3, images.Length);
        Assert.Equal(images.Length, fallbackImages.Length);
        Assert.NotEqual(images[0].Url, images[1].Url);
        Assert.NotEqual(images[1].Url, images[2].Url);
    }

    /// <summary>Scrolls a scrollable table pair to the same offset, or checks that neither is scrolled.</summary>
    internal static void ScrollBothTablesToTheSameOffset(TableBox table, TableBox fallbackTable)
    {
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
    }

    /// <summary>
    /// The SVG table has a link cell, strong, emphasis and code runs, and the
    /// wide and tall SVG images, in that order, in both documents.
    /// </summary>
    internal static void AssertSvgTableFixtureShape(
        InlineContainerBox[] cells,
        ImageBox[] images,
        ImageBox[] fallbackImages)
    {
        Assert.Contains(cells, static cell => cell.Runs.OfType<LinkRun>().Any());
        Assert.Contains(cells.SelectMany(static cell => cell.Runs),
            static run => HasStyleKey(run, MarkdownElementKeys.Strong));
        Assert.Contains(cells.SelectMany(static cell => cell.Runs),
            static run => HasStyleKey(run, MarkdownElementKeys.Emphasis));
        Assert.Contains(cells.SelectMany(static cell => cell.Runs),
            static run => HasStyleKey(run, MarkdownElementKeys.CodeInline));

        Assert.Equal(2, images.Length);
        Assert.Equal(images.Length, fallbackImages.Length);
        Assert.All(images, static image => Assert.True(image.IsSvg));
        Assert.All(fallbackImages, static image => Assert.True(image.IsSvg));
        Assert.Equal(new[] { "wide SVG alt", "tall SVG alt" }, images.Select(static image => image.Alt));
    }

    /// <summary>Both documents' SVG images loaded through full renders, without automation tiles.</summary>
    internal static void AssertSvgImagesLoadedWithoutTiles(
        ImageBox[] images,
        ImageBox[] fallbackImages,
        RootLocalTableSvgRenderer renderer)
    {
        Assert.All(images, static image => Assert.False(image.UsesSvgTilesForAutomation));
        Assert.All(fallbackImages, static image => Assert.False(image.UsesSvgTilesForAutomation));
        Assert.True(renderer.OpenCount > 0);
        Assert.True(renderer.FullRenderCount > 0);
        Assert.Equal(0, renderer.TileRenderCount);
        Assert.All(images, static image => Assert.Equal(MarkdownImageAccessibilityState.Loaded,
            image.AccessibilityState));
    }
}
