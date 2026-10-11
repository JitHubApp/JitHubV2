using System.Collections.Generic;
using System.Linq;
using Markdig;
using MarkdownRenderer.Document;
using MarkdownRenderer.Layout;
using MarkdownRenderer.Layout.Boxes;
using MarkdownRenderer.Parsing;
using MarkdownRenderer.Theming;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.UI.Xaml;
using Windows.UI;
using Xunit;

namespace MarkdownRenderer.GitHub.Tests;

public sealed class TightListLayoutTests
{
    [Theory]
    [InlineData(16f, 4f)]
    [InlineData(24f, 6f)]
    public void TightListUsesBodyRelativeSiblingGapWithoutTrailingGap(
        float bodyFontSize,
        float expectedGap)
    {
        const string source = "- first item\n- second item\n- last item\n\noutside paragraph";
        using LayoutSnapshot snapshot = Build(source, bodyFontSize);

        StackBox list = Assert.IsType<StackBox>(snapshot.Blocks[0]);
        ListItemBox[] items = list.Children.Cast<ListItemBox>().ToArray();
        Assert.Equal(3, items.Length);
        AssertClose(0, list.Margin.Top);
        AssertClose(0, list.Margin.Bottom);
        AssertClose(expectedGap, list.InterChildSpacing);

        foreach (ListItemBox item in items)
        {
            InlineContainerBox paragraph = Assert.Single(
                item.Content.Children.OfType<InlineContainerBox>());
            AssertClose(0, paragraph.Margin.Top);
            AssertClose(0, paragraph.Margin.Bottom);
            AssertClose(3, paragraph.Margin.Left);
            AssertClose(5, paragraph.Margin.Right);
        }

        for (int index = 0; index < items.Length - 1; index++)
            AssertClose(expectedGap, items[index + 1].Bounds.Top - items[index].Bounds.Bottom);

        AssertClose(0, list.Bounds.Bottom - items[^1].Bounds.Bottom);

        InlineContainerBox outside = Assert.IsType<InlineContainerBox>(snapshot.Blocks[1]);
        AssertClose(2, outside.Margin.Top);
        AssertClose(16, outside.Margin.Bottom);
    }

    [Fact]
    public void LooseListRetainsParagraphMarginsAndDoesNotAddTightSiblingSpacing()
    {
        const string source = "- first item\n\n- second item";
        using LayoutSnapshot snapshot = Build(source, bodyFontSize: 16);

        StackBox list = Assert.IsType<StackBox>(Assert.Single(snapshot.Blocks));
        AssertClose(0, list.InterChildSpacing);
        ListItemBox[] items = list.Children.Cast<ListItemBox>().ToArray();
        Assert.Equal(2, items.Length);

        foreach (ListItemBox item in items)
        {
            InlineContainerBox paragraph = Assert.Single(
                item.Content.Children.OfType<InlineContainerBox>());
            AssertClose(2, paragraph.Margin.Top);
            AssertClose(16, paragraph.Margin.Bottom);
        }
    }

    [Fact]
    public void TightListImageOnlyParagraphSuppressesBlockMargins()
    {
        const string source = "- ![diagram](https://example.test/diagram.png)\n- text";
        using LayoutSnapshot snapshot = Build(source, bodyFontSize: 16);

        StackBox list = Assert.IsType<StackBox>(Assert.Single(snapshot.Blocks));
        ListItemBox imageItem = Assert.IsType<ListItemBox>(list.Children[0]);
        ImageBox image = Assert.Single(imageItem.Content.Children.OfType<ImageBox>());

        AssertClose(0, image.Margin.Top);
        AssertClose(0, image.Margin.Bottom);
    }

    [Fact]
    public void NestedTightListsResolveSpacingAtTheirOwnListBoundary()
    {
        const string source = "- outer first\n  - inner first\n  - inner last\n- outer last";
        using LayoutSnapshot snapshot = Build(source, bodyFontSize: 16);

        StackBox outerList = Assert.IsType<StackBox>(Assert.Single(snapshot.Blocks));
        AssertClose(4, outerList.InterChildSpacing);
        ListItemBox[] outerItems = outerList.Children.Cast<ListItemBox>().ToArray();
        Assert.Equal(2, outerItems.Length);

        InlineContainerBox outerParagraph = Assert.Single(
            outerItems[0].Content.Children.OfType<InlineContainerBox>());
        AssertClose(0, outerParagraph.Margin.Top);
        AssertClose(0, outerParagraph.Margin.Bottom);

        StackBox nestedList = Assert.Single(
            outerItems[0].Content.Children.OfType<StackBox>());
        AssertClose(4, nestedList.InterChildSpacing);
        ListItemBox[] nestedItems = nestedList.Children.Cast<ListItemBox>().ToArray();
        Assert.Equal(2, nestedItems.Length);
        foreach (ListItemBox item in nestedItems)
        {
            InlineContainerBox paragraph = Assert.Single(
                item.Content.Children.OfType<InlineContainerBox>());
            AssertClose(0, paragraph.Margin.Top);
            AssertClose(0, paragraph.Margin.Bottom);
        }
        AssertClose(4, nestedItems[1].Bounds.Top - nestedItems[0].Bounds.Bottom);
        AssertClose(0, nestedList.Bounds.Bottom - nestedItems[^1].Bounds.Bottom);
    }

    [Theory]
    [InlineData(FlowDirection.LeftToRight)]
    [InlineData(FlowDirection.RightToLeft)]
    public void UnorderedMarkersAreCenteredWithoutChangingNestedOrOrderedGutters(
        FlowDirection flowDirection)
    {
        const string source = "- outer\n  - nested\n- after\n\n7. ordered seven\n8. ordered eight";
        using LayoutSnapshot snapshot = Build(
            source,
            bodyFontSize: 16,
            flowDirection,
            listIndent: 32,
            nestedListIndent: 8);

        ListItemBox[] items = EnumerateListItems(snapshot.Blocks).ToArray();
        Assert.Equal(5, items.Length);

        ListItemBox[] unordered = items
            .Where(static item => Assert.Single(item.Marker.Runs).Text == "•")
            .ToArray();
        ListItemBox[] ordered = items
            .Where(static item => Assert.Single(item.Marker.Runs).Text != "•")
            .ToArray();

        Assert.Equal(3, unordered.Length);
        Assert.Equal(["7.", "8."], ordered.Select(static item => Assert.Single(item.Marker.Runs).Text));
        Assert.All(unordered, item =>
            Assert.Equal(CanvasHorizontalAlignment.Center, item.Marker.TextAlignment));
        Assert.All(ordered, item =>
            Assert.NotEqual(CanvasHorizontalAlignment.Center, item.Marker.TextAlignment));

        Assert.Equal([32f, 40f, 32f, 32f, 32f], items.Select(static item => item.MarkerWidth));
        foreach (ListItemBox item in items)
        {
            AssertClose(item.MarkerWidth, item.Marker.Bounds.Width);
            AssertClose(item.Bounds.Width, item.MarkerWidth + item.Content.Bounds.Width);
            if (flowDirection == FlowDirection.RightToLeft)
                AssertClose(item.Content.Bounds.Right, item.Marker.Bounds.Left);
            else
                AssertClose(item.Marker.Bounds.Right, item.Content.Bounds.Left);
        }
    }

    private static IEnumerable<ListItemBox> EnumerateListItems(IEnumerable<BlockBox> boxes)
    {
        foreach (BlockBox box in boxes)
        {
            if (box is ListItemBox item)
            {
                yield return item;
                foreach (ListItemBox nested in EnumerateListItems(item.Content.Children))
                    yield return nested;
            }
            else if (box is StackBox stack)
            {
                foreach (ListItemBox nested in EnumerateListItems(stack.Children))
                    yield return nested;
            }
        }
    }

    private static LayoutSnapshot Build(
        string source,
        float bodyFontSize,
        FlowDirection flowDirection = FlowDirection.LeftToRight,
        float listIndent = 22,
        float nestedListIndent = 0)
    {
        var registry = new MarkdownExtensionRegistry();
        Markdig.Syntax.MarkdownDocument parsed = Markdown.Parse(source, registry.BuildPipeline());
        MarkdownDocument document = MarkdownDocument.FromParsed(source, parsed);
        var body = new ElementStyle
        {
            FontFamily = "Segoe UI",
            FontSize = bodyFontSize,
            LineHeightMultiplier = 1,
            Margin = new Thickness(3, 2, 5, 16),
        };
        var styles = new Dictionary<string, ElementStyle>
        {
            [MarkdownElementKeys.Body] = body,
            [MarkdownElementKeys.ListMarker] = new ElementStyle
            {
                FontFamily = "Segoe UI",
                FontSize = bodyFontSize,
                ListIndent = listIndent,
                NestedListIndent = nestedListIndent,
            },
        };
        Color transparent = Color.FromArgb(0, 0, 0, 0);
        var theme = new ThemeSnapshot(
            styles,
            new Dictionary<string, ElementStyleOverride>(),
            transparent,
            transparent,
            transparent,
            transparent,
            isDark: false,
            isHighContrast: false,
            textScaleFactor: 1);
        var context = new MarkdownLayoutContext(
            CanvasDevice.GetSharedDevice(),
            theme,
            new MarkdownSourceMap(source),
            registry,
            flowDirection);
        var builder = new LayoutBuilder(context, semanticDocument: document);
        return builder.Build(parsed, 600);
    }

    private static void AssertClose(double expected, double actual) =>
        Assert.InRange(actual, expected - 0.001, expected + 0.001);
}
