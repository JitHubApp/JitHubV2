using MarkdownRenderer.Html;
using MarkdownRenderer.Layout;
using MarkdownRenderer.Layout.Boxes;
using MarkdownRenderer.Theming;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.UI.Xaml;
using Windows.UI;
using Xunit;
using static MarkdownRenderer.GitHub.Tests.LinkInlineStyleMapAssertions;
using static MarkdownRenderer.GitHub.Tests.RelativeScriptFontSizeFixture;

namespace MarkdownRenderer.GitHub.Tests;

/// <summary>
/// Checks how the relative profile styles scripts and formatting inside
/// links: nested link style resolution, GFM child and containing links,
/// the legacy profile and the safe HTML link policy.
/// </summary>
public sealed class RelativeScriptLinkStyleTests
{
    [Fact]
    public void NestedLinkStylesResolveOncePerBoxAndPreserveLinkAndCodeTypography()
    {
        Color linkColor = Color.FromArgb(255, 20, 80, 160);
        Color codeBackground = Color.FromArgb(255, 240, 240, 240);
        MarkdownStyleSheet sheet = CreateNestedLinkSheet(linkColor, codeBackground);
        using LayoutSnapshot snapshot = BuildParsedSnapshot(
            "[**bold** and *italic* and `code`](https://example.test/link)",
            sheet);
        InlineContainerBox sourceBox = Assert.Single(snapshot.Blocks.OfType<InlineContainerBox>());
        LinkRun sourceLink = Assert.Single(sourceBox.Runs.OfType<LinkRun>());
        LinkInlineStyleMap map = Assert.IsType<LinkInlineStyleMap>(sourceLink.InlineStyleMap);

        var context = CreateContext(CreateTheme(sheet), FlowDirection.LeftToRight);
        LinkRun firstRun = sourceLink.CloneForPrivateRasterPlacement();
        LinkRun secondRun = sourceLink.CloneForPrivateRasterPlacement();
        var firstBox = new InlineContainerBox(context, MarkdownElementKeys.Body)
        {
            BlockIndex = context.NextBlockIndex(),
        };
        var secondBox = new InlineContainerBox(context, MarkdownElementKeys.Body)
        {
            BlockIndex = context.NextBlockIndex(),
        };
        try
        {
            firstBox.Add(firstRun);
            secondBox.Add(secondRun);

            ElementStyle[] firstResolution = firstBox.GetLinkInlineSpanStyles(firstRun);
            ElementStyle[] repeatedResolution = firstBox.GetLinkInlineSpanStyles(firstRun);
            ElementStyle[] otherBoxResolution = secondBox.GetLinkInlineSpanStyles(secondRun);

            Assert.Same(map, firstRun.InlineStyleMap);
            Assert.Same(map, secondRun.InlineStyleMap);
            Assert.Same(firstResolution, repeatedResolution);
            Assert.NotSame(firstResolution, otherBoxResolution);
            Assert.Equal(map.Count, firstResolution.Length);

            AssertNestedSpanStylesKeepLinkPaint(map, firstResolution, linkColor, codeBackground);

            LinkInlineStyleSpan codeSpan = GetSpan(map, MarkdownElementKeys.CodeInline);
            CanvasTextLayout actualLayout = Assert.IsType<CanvasTextLayout>(sourceBox.TextLayout);
            Assert.Equal(16f, ReadUniformFontSize(actualLayout, codeSpan.Start, codeSpan.Length));
            Assert.Equal("Segoe UI", ReadUniformFontFamily(actualLayout, codeSpan.Start, codeSpan.Length));
            LinkInlineStyleSpan strongSpan = GetSpan(map, MarkdownElementKeys.Strong);
            Assert.Equal(20f, ReadUniformFontSize(actualLayout, strongSpan.Start, strongSpan.Length));
            Assert.Equal("Arial", ReadUniformFontFamily(actualLayout, strongSpan.Start, strongSpan.Length));
        }
        finally
        {
            try
            {
                firstBox.Dispose();
            }
            finally
            {
                secondBox.Dispose();
            }
        }
    }

    [Fact]
    public void PlainLinkDoesNotAllocateNestedStyleCacheAndHeadingCodeKeepsHeadingSize()
    {
        Color headingForeground = Color.FromArgb(255, 30, 40, 50);
        Color codeBackground = Color.FromArgb(255, 230, 230, 230);
        MarkdownStyleSheet sheet = new MarkdownStyleSheet(
        [
            new MarkdownStyleRule(
                new MarkdownStyleSelector(MarkdownStyleRole.FromElementKey(MarkdownElementKeys.Heading1)),
                new ElementStyleOverride { Foreground = headingForeground }),
            new MarkdownStyleRule(
                new MarkdownStyleSelector(MarkdownStyleRole.FromElementKey(MarkdownElementKeys.CodeInline)),
                new ElementStyleOverride
                {
                    FontFamily = "Cascadia Mono",
                    FontSize = 13.6f,
                    Background = codeBackground,
                }),
        ]);
        var context = CreateContext(CreateTheme(sheet), FlowDirection.LeftToRight);
        var plainBox = new InlineContainerBox(context, MarkdownElementKeys.Body)
        {
            BlockIndex = context.NextBlockIndex(),
        };
        var plainLink = new LinkRun("ordinary", "https://example.test/");
        try
        {
            plainBox.Add(plainLink);
            _ = plainBox.GetRunStyle(plainLink);

            Assert.Null(plainLink.InlineStyleMap);
            Assert.Null(plainBox.ResolvedLinkInlineSpanStyles);
        }
        finally
        {
            plainBox.Dispose();
        }

        using LayoutSnapshot headingSnapshot = BuildParsedSnapshot("# Heading `code`", sheet);
        InlineContainerBox heading = Assert.Single(headingSnapshot.Blocks.OfType<InlineContainerBox>());
        CodeInlineRun codeRun = Assert.Single(heading.Runs.OfType<CodeInlineRun>());
        ElementStyle resolved = heading.GetRunStyle(codeRun);

        Assert.Equal(32f, resolved.FontSize);
        Assert.Equal("Cascadia Mono", resolved.FontFamily);
        Assert.Equal((ushort)700, resolved.FontWeight.Weight);
        Assert.Equal(headingForeground, resolved.Foreground);
        Assert.Equal(codeBackground, resolved.Background);
        Assert.True(heading.ShouldApplyRunTextStyle(codeRun));
        CanvasTextLayout headingLayout = Assert.IsType<CanvasTextLayout>(heading.TextLayout);
        int codeStart = 0;
        foreach (InlineRun run in heading.Runs)
        {
            if (ReferenceEquals(run, codeRun))
                break;
            codeStart += run.TextLength;
        }
        Assert.Equal(32f, ReadUniformFontSize(headingLayout, codeStart, codeRun.TextLength));
        Assert.Equal("Cascadia Mono", ReadUniformFontFamily(headingLayout, codeStart, codeRun.TextLength));
    }

    [Fact]
    public void NestedLinkLabelStylesKeepPlainOffsetsAndSourceOrderedScriptFormatting()
    {
        const string label = "before **bold ~~strike~~ ^~script~^** after";
        using LayoutSnapshot snapshot = BuildParsedSnapshot(
            $"[{label}](https://example.test/label)",
            RelativeSheet());
        InlineContainerBox box = Assert.Single(snapshot.Blocks.OfType<InlineContainerBox>());
        LinkRun link = Assert.Single(box.Runs.OfType<LinkRun>());
        LinkInlineStyleMap map = Assert.IsType<LinkInlineStyleMap>(link.InlineStyleMap);

        Assert.Equal("before bold strike script after", link.Text);
        Assert.True(map.IsValidForTextLength(link.TextLength));
        LinkInlineStyleSpan strongSpan = GetSpan(map, MarkdownElementKeys.Strong);
        LinkInlineStyleSpan strikeSpan = GetSpan(map, MarkdownElementKeys.Strikethrough);
        LinkInlineStyleSpan superSpan = GetSpan(map, MarkdownElementKeys.Superscript);
        LinkInlineStyleSpan subSpan = GetSpan(map, MarkdownElementKeys.Subscript);
        AssertSpanText(link, strongSpan, "bold ");
        AssertSpanText(link, strikeSpan, "strike");
        AssertSpanText(link, superSpan, "script");
        AssertSpanText(link, subSpan, "script");
        Assert.Equal(
            new[]
            {
                MarkdownElementKeys.Strong,
                MarkdownElementKeys.Superscript,
                MarkdownElementKeys.Subscript,
            },
            GetPathKeys(subSpan.Path));

        ElementStyle[] resolved = box.GetLinkInlineSpanStyles(link);
        int subSpanIndex = FindSpanIndex(map, subSpan);
        Assert.Equal(9f, resolved[subSpanIndex].FontSize);
    }

    [Theory]
    [InlineData("table")]
    [InlineData("task-list")]
    public void GfmChildLinkFormattingPreservesNestedRangesAndPlainOffsets(string container)
    {
        const string linkedLabel = "left **bold** and ^~script~^ right";
        string linkMarkdown = $"[{linkedLabel}](https://example.test/gfm)";
        string source = container switch
        {
            "table" => $"| label |\n| --- |\n| {linkMarkdown} |",
            "task-list" => $"- [x] {linkMarkdown}",
            _ => throw new ArgumentOutOfRangeException(nameof(container)),
        };
        using LayoutSnapshot snapshot = BuildParsedSnapshot(
            source,
            RelativeSheet(),
            useGfmContainerExtensions: true);

        InlineContainerBox content = container switch
        {
            "table" => GetBodyCell(Assert.Single(snapshot.Blocks.OfType<TableBox>()), 0, 0),
            "task-list" => Assert.Single(
                EnumerateListItems(snapshot.Blocks)
                    .SelectMany(static item => item.Content.Children.OfType<InlineContainerBox>())),
            _ => throw new ArgumentOutOfRangeException(nameof(container)),
        };
        LinkRun link = Assert.Single(content.Runs.OfType<LinkRun>());
        LinkInlineStyleMap map = Assert.IsType<LinkInlineStyleMap>(link.InlineStyleMap);
        Assert.Equal("left bold and script right", link.Text);

        LinkInlineStyleSpan strongSpan = GetSpan(map, MarkdownElementKeys.Strong);
        LinkInlineStyleSpan subSpan = GetSpan(map, MarkdownElementKeys.Subscript);
        AssertSpanText(link, strongSpan, "bold");
        AssertSpanText(link, subSpan, "script");
        Assert.Equal(
            new[] { MarkdownElementKeys.Superscript, MarkdownElementKeys.Subscript },
            GetPathKeys(subSpan.Path));
        ElementStyle[] resolved = content.GetLinkInlineSpanStyles(link);
        Assert.Equal(9f, resolved[FindSpanIndex(map, subSpan)].FontSize);
    }

    [Fact]
    public void DeepNestedLinkStylePathResolutionUsesAnIterativeWalk()
    {
        const int depth = 512;
        LinkInlineStylePath? path = null;
        for (int index = 0; index < depth; index++)
        {
            path = new LinkInlineStylePath(
                MarkdownElementKeys.Emphasis,
                Array.Empty<string>(),
                path);
        }

        var map = new LinkInlineStyleMap(
            [new LinkInlineStyleSpan(0, 1, Assert.IsType<LinkInlineStylePath>(path))],
            depth + 1L);
        var context = CreateContext(CreateTheme(MarkdownStyleSheet.Empty), FlowDirection.LeftToRight);
        var box = new InlineContainerBox(context, MarkdownElementKeys.Body)
        {
            BlockIndex = context.NextBlockIndex(),
        };
        var link = new LinkRun(
            "x",
            "https://example.test/deep",
            title: null,
            inlineStyleMap: map);
        try
        {
            box.Add(link);
            ElementStyle[] resolved = box.GetLinkInlineSpanStyles(link);
            Assert.Single(resolved);
            Assert.True(float.IsFinite(resolved[0].FontSize));
            Assert.True(resolved[0].FontSize > 0f);
        }
        finally
        {
            box.Dispose();
        }
    }

    [Fact]
    public void RelativeProfilePreservesUnlinkedNestedScriptsAndRestoresSiblingScope()
    {
        const string source = "before ^<sub>x</sub>^ after";

        using LayoutSnapshot snapshot = BuildParsedSnapshot(source, RelativeSheet());
        InlineContainerBox box = Assert.Single(snapshot.Blocks.OfType<InlineContainerBox>());
        InlineRun nested = Assert.Single(box.Runs, static run => run.Text == "x");

        Assert.Equal(
            new[] { MarkdownElementKeys.Superscript, MarkdownElementKeys.Subscript },
            nested.ScriptStyleAncestryKeys);
        Assert.Equal(9f, ResolveRunStyle(MarkdownElementKeys.Body, nested, RelativeSheet()).FontSize);
        Assert.All(
            box.Runs.Where(run => !ReferenceEquals(run, nested)),
            static run => Assert.Empty(run.ScriptStyleAncestryKeys));
    }

    [Fact]
    public void RelativeProfilePreservesBoldAroundUnlinkedSafeHtmlSuperscript()
    {
        using LayoutSnapshot snapshot = BuildParsedSnapshot("**<sup>x</sup>**", RelativeSheet());
        InlineContainerBox box = Assert.Single(snapshot.Blocks.OfType<InlineContainerBox>());
        InlineRun nested = Assert.Single(box.Runs, static run => run.Text == "x");

        Assert.Equal([MarkdownElementKeys.Superscript], nested.ScriptStyleAncestryKeys);
        Assert.Contains(MarkdownElementKeys.Strong, nested.StyleModifierKeys);
        ElementStyle resolved = ResolveRunStyle(MarkdownElementKeys.Body, nested, RelativeSheet());
        Assert.Equal(12f, resolved.FontSize);
        Assert.Equal((ushort)700, resolved.FontWeight.Weight);
    }

    [Theory]
    [InlineData("table")]
    [InlineData("task-list")]
    public void RelativeProfilePreservesUnlinkedNestedScriptsInGfmChildBuilder(string container)
    {
        string source = container switch
        {
            "table" => "| cell |\n| --- |\n| ^<sub>x</sub>^ |",
            "task-list" => "- [x] **<sup>x</sup>**",
            _ => throw new ArgumentOutOfRangeException(nameof(container)),
        };
        using LayoutSnapshot snapshot = BuildParsedSnapshot(
            source,
            RelativeSheet(),
            useGfmContainerExtensions: true);

        InlineContainerBox content = container switch
        {
            "table" => GetBodyCell(Assert.Single(snapshot.Blocks.OfType<TableBox>()), 0, 0),
            "task-list" => Assert.Single(
                EnumerateListItems(snapshot.Blocks)
                    .SelectMany(static item => item.Content.Children.OfType<InlineContainerBox>())),
            _ => throw new ArgumentOutOfRangeException(nameof(container)),
        };
        InlineRun nested = Assert.Single(content.Runs, static run => run.Text == "x");

        Assert.Equal(
            container == "table"
                ? new[] { MarkdownElementKeys.Superscript, MarkdownElementKeys.Subscript }
                : new[] { MarkdownElementKeys.Superscript },
            nested.ScriptStyleAncestryKeys);
        if (container == "table")
        {
            Assert.Equal(9f, ResolveRunStyle(MarkdownElementKeys.TableCell, nested, RelativeSheet()).FontSize);
        }
        else
        {
            Assert.Contains(MarkdownElementKeys.Strong, nested.StyleModifierKeys);
            Assert.Equal(12f, ResolveRunStyle(MarkdownElementKeys.Body, nested, RelativeSheet()).FontSize);
        }
    }

    [Theory]
    [InlineData("table")]
    [InlineData("task-list")]
    public void GfmContainingLinkPreservesScriptMetadataWhenWrappingRuns(string container)
    {
        const string linkedContent = "[^x^ ![icon](https://example.test/icon.png)](https://example.test/outer)";
        string source = container switch
        {
            "table" => $"| header |\n| --- |\n| {linkedContent} |",
            "task-list" => $"- [x] {linkedContent}",
            _ => throw new ArgumentOutOfRangeException(nameof(container)),
        };
        using LayoutSnapshot snapshot = BuildParsedSnapshot(
            source,
            RelativeSheet(),
            useGfmContainerExtensions: true);

        InlineContainerBox content = container switch
        {
            "table" => GetBodyCell(Assert.Single(snapshot.Blocks.OfType<TableBox>()), 0, 0),
            "task-list" => Assert.Single(
                EnumerateListItems(snapshot.Blocks)
                    .SelectMany(static item => item.Content.Children.OfType<InlineContainerBox>())),
            _ => throw new ArgumentOutOfRangeException(nameof(container)),
        };
        LinkRun script = Assert.Single(content.Runs.OfType<LinkRun>(), static run => run.Text == "x");

        Assert.Equal("https://example.test/outer", script.Url);
        Assert.Contains(content.Runs, static run => run is InlineImageRun);
        Assert.Equal([MarkdownElementKeys.Superscript], script.ScriptStyleAncestryKeys);
        Assert.False(script.SafeHtmlScriptRoleIsRunElement);
    }

    [Theory]
    [InlineData("table")]
    [InlineData("task-list")]
    public void GfmContainingLinkPreservesMixedScriptOrderWhenWrappingRuns(string container)
    {
        const string linkedContent = "[^<sub>x</sub>^ ![icon](https://example.test/icon.png)](https://example.test/outer)";
        string source = container switch
        {
            "table" => $"| header |\n| --- |\n| {linkedContent} |",
            "task-list" => $"- [x] {linkedContent}",
            _ => throw new ArgumentOutOfRangeException(nameof(container)),
        };
        using LayoutSnapshot snapshot = BuildParsedSnapshot(
            source,
            RelativeSheet(),
            useGfmContainerExtensions: true);

        InlineContainerBox content = container switch
        {
            "table" => GetBodyCell(Assert.Single(snapshot.Blocks.OfType<TableBox>()), 0, 0),
            "task-list" => Assert.Single(
                EnumerateListItems(snapshot.Blocks)
                    .SelectMany(static item => item.Content.Children.OfType<InlineContainerBox>())),
            _ => throw new ArgumentOutOfRangeException(nameof(container)),
        };
        LinkRun script = Assert.Single(content.Runs.OfType<LinkRun>(), static run => run.Text == "x");

        Assert.Equal("https://example.test/outer", script.Url);
        Assert.Contains(content.Runs, static run => run is InlineImageRun);
        Assert.Equal(
            new[] { MarkdownElementKeys.Superscript, MarkdownElementKeys.Subscript },
            script.ScriptStyleAncestryKeys);
        Assert.True(script.SafeHtmlScriptRoleIsRunElement);
        string containerRole = container == "table"
            ? MarkdownElementKeys.TableCell
            : MarkdownElementKeys.Body;
        Assert.Equal(9f, ResolveRunStyle(containerRole, script, RelativeSheet()).FontSize);
    }

    [Fact]
    public void LegacyProfileKeepsNoLinkEmphasisFlatteningUnchanged()
    {
        using LayoutSnapshot snapshot = BuildParsedSnapshot(
            "before ^<sub>x</sub>^ after",
            MarkdownStyleSheet.Empty);
        InlineContainerBox box = Assert.Single(snapshot.Blocks.OfType<InlineContainerBox>());
        SuperscriptRun flattened = Assert.Single(box.Runs.OfType<SuperscriptRun>());

        Assert.Equal("x", flattened.Text);
        Assert.Equal([MarkdownElementKeys.Superscript], flattened.ScriptStyleAncestryKeys);
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public void RelativeRecursionHonorsSafeHtmlLinkPolicy(
        bool enableSafeHtmlLinks,
        bool expectedLink)
    {
        const string source = "^<sub><a href=\"https://example.test/\">x</a></sub>^";
        SafeHtmlOptions? safeHtmlOptions = enableSafeHtmlLinks
            ? null
            : new SafeHtmlOptions(enableLinks: false, enableImages: false);

        using LayoutSnapshot snapshot = BuildParsedSnapshot(
            source,
            RelativeSheet(),
            safeHtmlOptions);
        InlineContainerBox box = Assert.Single(snapshot.Blocks.OfType<InlineContainerBox>());

        Assert.Equal(expectedLink, box.Runs.OfType<LinkRun>().Any());
    }

    [Fact]
    public void RelativeRecursionDoesNotPromoteUnsafeSafeHtmlLinkSchemes()
    {
        const string source = "^<sub><a href=\"javascript:alert(1)\">x</a></sub>^";

        using LayoutSnapshot snapshot = BuildParsedSnapshot(source, RelativeSheet());
        InlineContainerBox box = Assert.Single(snapshot.Blocks.OfType<InlineContainerBox>());

        Assert.Empty(box.Runs.OfType<LinkRun>());
    }

    private static InlineContainerBox GetBodyCell(TableBox table, int row, int column)
        => table.BodyCells[row][column];

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
}
