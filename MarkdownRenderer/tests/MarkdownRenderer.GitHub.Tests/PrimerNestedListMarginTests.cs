using System.Collections.Generic;
using System.Linq;
using Markdig;
using MarkdownRenderer.Document;
using MarkdownRenderer.Layout;
using MarkdownRenderer.Layout.Boxes;
using MarkdownRenderer.Parsing;
using MarkdownRenderer.Theming;
using Microsoft.Graphics.Canvas;
using Microsoft.UI.Xaml;
using Windows.UI;
using Xunit;

namespace MarkdownRenderer.GitHub.Tests;

[Collection("Shared Canvas text format cache")]
public sealed class PrimerNestedListMarginTests
{
    private const float LayoutWidth = 640;

    [Fact]
    public void NestedTightListsUseNoProfileMarginAndReduceMeasuredHeightPerNestedLevel()
    {
        const string source =
            "before\n\n" +
            "- outer first\n" +
            "  - middle first\n" +
            "    - inner leaf\n" +
            "- outer last\n\n" +
            "after";

        using LayoutSnapshot v4 = Build(source, GitHubReadmeVisualStyleProfiles.PrimerLightV4.StyleSheet);
        using LayoutSnapshot v5 = Build(source, GitHubReadmeVisualStyleProfiles.PrimerLightV5.StyleSheet);

        StackBox[] v4Lists = EnumerateLists(v4.Blocks).ToArray();
        StackBox[] v5Lists = EnumerateLists(v5.Blocks).ToArray();
        Assert.Equal(3, v4Lists.Length);
        Assert.Equal(3, v5Lists.Length);

        Assert.Equal(new Thickness(0, 0, 0, 16), v4Lists[0].Margin);
        Assert.Equal(new Thickness(0, 0, 0, 16), v5Lists[0].Margin);
        Assert.All(v4Lists.Skip(1), list => Assert.Equal(new Thickness(0, 0, 0, 16), list.Margin));
        Assert.All(v5Lists.Skip(1), list => Assert.Equal(new Thickness(0), list.Margin));
        Assert.InRange(v4.Size.Height - v5.Size.Height, 31.999, 32.001);
    }

    [Fact]
    public void NestedLooseListKeepsParagraphMarginsAndTopLevelSpacingWhileRemovingNestedListMargin()
    {
        const string source =
            "before\n\n" +
            "- outer first\n\n" +
            "  - nested first\n\n" +
            "  - nested last\n\n" +
            "- outer last\n\n" +
            "after";

        using LayoutSnapshot v4 = Build(source, GitHubReadmeVisualStyleProfiles.PrimerLightV4.StyleSheet);
        using LayoutSnapshot v5 = Build(source, GitHubReadmeVisualStyleProfiles.PrimerLightV5.StyleSheet);

        StackBox[] v4Lists = EnumerateLists(v4.Blocks).ToArray();
        StackBox[] v5Lists = EnumerateLists(v5.Blocks).ToArray();
        Assert.Equal(2, v4Lists.Length);
        Assert.Equal(2, v5Lists.Length);
        Assert.Equal(new Thickness(0, 0, 0, 16), v5Lists[0].Margin);
        Assert.Equal(new Thickness(0), v5Lists[1].Margin);
        Assert.Equal(new Thickness(0, 0, 0, 16), v4Lists[1].Margin);
        Assert.InRange(v4.Size.Height - v5.Size.Height, 15.999, 16.001);

        ListItemBox outerItem = Assert.IsType<ListItemBox>(v5Lists[0].Children[0]);
        InlineContainerBox outerParagraph = Assert.Single(
            outerItem.Content.Children.OfType<InlineContainerBox>());
        ListItemBox v4OuterItem = Assert.IsType<ListItemBox>(v4Lists[0].Children[0]);
        InlineContainerBox v4OuterParagraph = Assert.Single(
            v4OuterItem.Content.Children.OfType<InlineContainerBox>());
        Assert.Equal(new Thickness(0, 0, 0, 16), v4OuterParagraph.Margin);
        Assert.Equal(v4OuterParagraph.Margin, outerParagraph.Margin);

        StackBox nested = Assert.Single(outerItem.Content.Children.OfType<StackBox>());
        StackBox v4Nested = Assert.Single(v4OuterItem.Content.Children.OfType<StackBox>());
        Assert.Equal(0f, nested.InterChildSpacing);
        Assert.Equal(v4Nested.InterChildSpacing, nested.InterChildSpacing);
        ListItemBox[] nestedItems = nested.Children.Cast<ListItemBox>().ToArray();
        ListItemBox[] v4NestedItems = v4Nested.Children.Cast<ListItemBox>().ToArray();
        Assert.Equal(v4NestedItems.Length, nestedItems.Length);
        for (int index = 0; index < nestedItems.Length; index++)
        {
            InlineContainerBox paragraph = Assert.Single(
                nestedItems[index].Content.Children.OfType<InlineContainerBox>());
            InlineContainerBox v4Paragraph = Assert.Single(
                v4NestedItems[index].Content.Children.OfType<InlineContainerBox>());
            Assert.Equal(new Thickness(0, 0, 0, 16), v4Paragraph.Margin);
            Assert.Equal(v4Paragraph.Margin, paragraph.Margin);
        }
    }

    [Fact]
    public void EarlierProfilesKeepTheirOriginalNestedListMargins()
    {
        MarkdownStyleRole listRole = MarkdownStyleRole.FromElementKey(MarkdownElementKeys.List);
        foreach (GitHubReadmeVisualStyleProfile profile in new[]
        {
            GitHubReadmeVisualStyleProfiles.PrimerLightV1,
            GitHubReadmeVisualStyleProfiles.PrimerLightV2,
            GitHubReadmeVisualStyleProfiles.PrimerLightV3,
        })
        {
            ElementStyle style = profile.StyleSheet.Resolve(
                new ElementStyle(), new MarkdownStyleContext(listRole, nestingDepth: 2));
            Assert.Equal(new Thickness(0), style.Margin);
        }

        ElementStyle v4Nested = GitHubReadmeVisualStyleProfiles.PrimerLightV4.StyleSheet.Resolve(
            new ElementStyle(), new MarkdownStyleContext(listRole, nestingDepth: 2));
        Assert.Equal(new Thickness(0, 0, 0, 16), v4Nested.Margin);
    }

    private static LayoutSnapshot Build(string source, MarkdownStyleSheet styleSheet)
    {
        var registry = new MarkdownExtensionRegistry();
        Markdig.Syntax.MarkdownDocument parsed = Markdown.Parse(source, registry.BuildPipeline());
        MarkdownDocument document = MarkdownDocument.FromParsed(source, parsed);
        Color foreground = Color.FromArgb(255, 31, 35, 40);
        var styles = new Dictionary<string, ElementStyle>(StringComparer.Ordinal)
        {
            [MarkdownElementKeys.Body] = new()
            {
                FontFamily = "Segoe UI",
                FontSize = 16,
                Foreground = foreground,
                LineHeightMultiplier = 1.5f,
                Margin = new Thickness(0, 2, 0, 16),
            },
            [MarkdownElementKeys.ListMarker] = new()
            {
                FontFamily = "Segoe UI",
                FontSize = 16,
                ListIndent = 22,
            },
        };
        var theme = new ThemeSnapshot(
            styles,
            new Dictionary<string, ElementStyleOverride>(StringComparer.Ordinal),
            Color.FromArgb(255, 255, 255, 255),
            Color.FromArgb(255, 51, 154, 240),
            Color.FromArgb(255, 255, 255, 255),
            foreground,
            isDark: false,
            isHighContrast: false,
            textScaleFactor: 1,
            styleSheet: styleSheet);
        var context = new MarkdownLayoutContext(
            CanvasDevice.GetSharedDevice(),
            theme,
            new MarkdownSourceMap(source),
            registry,
            FlowDirection.LeftToRight);
        var builder = new LayoutBuilder(context, semanticDocument: document);
        return builder.Build(parsed, LayoutWidth);
    }

    private static IEnumerable<StackBox> EnumerateLists(IEnumerable<BlockBox> blocks)
    {
        foreach (BlockBox block in blocks)
        {
            if (block is ListItemBox item)
            {
                foreach (StackBox nested in EnumerateLists(item.Content.Children))
                    yield return nested;
            }
            else if (block is StackBox stack)
            {
                if (stack.Children.Count > 0 && stack.Children[0] is ListItemBox)
                    yield return stack;
                foreach (StackBox nested in EnumerateLists(stack.Children))
                    yield return nested;
            }
        }
    }
}
