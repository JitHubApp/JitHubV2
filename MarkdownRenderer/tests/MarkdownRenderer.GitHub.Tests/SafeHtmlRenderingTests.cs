using Markdig;
using MarkdownRenderer.Document;
using MarkdownRenderer.GitHub;
using MarkdownRenderer.Html;
using MarkdownRenderer.Hosting;
using MarkdownRenderer.Images;
using MarkdownRenderer.Layout;
using MarkdownRenderer.Layout.Boxes;
using MarkdownRenderer.Math;
using MarkdownRenderer.Mermaid;
using MarkdownRenderer.Parsing;
using MarkdownRenderer.Performance;
using MarkdownRenderer.Theming;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.UI.Xaml;
using Windows.Foundation;
using Windows.UI;
using Xunit;

namespace MarkdownRenderer.GitHub.Tests;

public sealed class SafeHtmlRenderingTests
{
    [Fact]
    public void LargeGitHubRenderedHtmlDoesNotHitTheFormerTwentyThousandNodeLimit()
    {
        const int highlightedSpanCount = 25_000;
        var sourceBuilder = new System.Text.StringBuilder(highlightedSpanCount * 15);
        sourceBuilder.Append("<div>");
        for (int index = 0; index < highlightedSpanCount; index++)
            sourceBuilder.Append("<span>x</span>");
        sourceBuilder.Append("<strong>tail marker</strong></div>");

        string source = sourceBuilder.ToString();
        using LayoutSnapshot snapshot = BuildGitHub(source);
        string rendered = string.Concat(FlattenRuns(snapshot).Select(static run => run.Text));

        Assert.Contains("tail marker", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "Additional HTML content was omitted because it exceeded the renderer safety limit.",
            rendered,
            StringComparison.Ordinal);
    }

    [Fact]
    public void GitHubRenderedHtmlPreservesCodeBlocksAndNestedListStructure()
    {
        const string source = "<ol><li><p>First</p><ul><li>Nested</li></ul><pre><code>echo ok\n</code></pre></li></ol>";

        using LayoutSnapshot snapshot = Build(source, SafeHtmlOptions.Default);
        ListItemBox outer = Assert.Single(FlattenBoxes(snapshot).OfType<ListItemBox>(),
            item => item.Marker.Runs.Any(run => run.Text == "1."));
        ListItemBox nested = Assert.Single(
            outer.Content.Children
                .OfType<StackBox>()
                .SelectMany(static list => list.Children.OfType<ListItemBox>()));
        Assert.Contains(nested.Marker.Runs, run => run.Text == "\u2022");
        CodeBlockBox code = Assert.Single(outer.Content.Children.OfType<CodeBlockBox>());
        Assert.Equal("echo ok\n", code.CodeText);
        Assert.Contains(outer.Content.Children, child => child is StackBox);
        Assert.Contains(
            MarkdownRenderer.Accessibility.MarkdownSemanticDocument
                .EnumerateDepthFirst(snapshot.SemanticDocument.Root),
            node => node.Role == MarkdownRenderer.Accessibility.MarkdownSemanticRole.CodeBlock);
    }

    [Fact]
    public void PreformattedCodeInsideHtmlTableRetainsCodeSemanticsAndLineBreaks()
    {
        const string source = "<table><tr><td><div><pre><span>const</span> value = 1;\nreturn value;</pre></div></td><td>Result</td></tr></table>";

        using LayoutSnapshot snapshot = Build(source, SafeHtmlOptions.Default);
        InlineContainerBox code = Assert.Single(
            FlattenBoxes(snapshot).OfType<InlineContainerBox>(),
            static box => box.ElementKey == MarkdownElementKeys.CodeBlock);
        MarkdownRenderer.Accessibility.MarkdownSemanticNode semantic = Assert.Single(
            MarkdownRenderer.Accessibility.MarkdownSemanticDocument
                .EnumerateDepthFirst(snapshot.SemanticDocument.Root),
            static node => node.Role == MarkdownRenderer.Accessibility.MarkdownSemanticRole.CodeBlock);

        Assert.Equal("const value = 1;\nreturn value;", string.Concat(code.Runs.Select(static run => run.Text)));
        Assert.Equal("const value = 1;\nreturn value;", snapshot.SemanticDocument.GetText(semantic));
    }

    [Fact]
    public void MixedHtmlTableCellRetainsContentAroundPreformattedCode()
    {
        const string source = "<table><tr><td><h3>CLI</h3><p>Command-line agent</p><pre><code>npm i -g cline</code></pre><p><a href='https://example.test/cli'>Learn more</a></p></td></tr></table>";

        using LayoutSnapshot snapshot = Build(source, SafeHtmlOptions.Default);
        InlineContainerBox cell = Assert.Single(
            FlattenBoxes(snapshot).OfType<InlineContainerBox>(),
            static box => box.ElementKey == MarkdownElementKeys.TableCell);
        MarkdownRenderer.Accessibility.MarkdownSemanticNode[] semantics =
            MarkdownRenderer.Accessibility.MarkdownSemanticDocument
                .EnumerateDepthFirst(snapshot.SemanticDocument.Root)
                .ToArray();

        string rendered = string.Concat(cell.Runs.Select(static run => run.Text));
        Assert.Contains("CLI", rendered, StringComparison.Ordinal);
        Assert.Contains("Command-line agent", rendered, StringComparison.Ordinal);
        Assert.Contains("npm i -g cline", rendered, StringComparison.Ordinal);
        Assert.Contains("Learn more", rendered, StringComparison.Ordinal);
        Assert.Single(semantics, static node => node.Role == MarkdownRenderer.Accessibility.MarkdownSemanticRole.Heading);
        Assert.Single(semantics, static node => node.Role == MarkdownRenderer.Accessibility.MarkdownSemanticRole.CodeBlock);
        Assert.Single(semantics, static node => node.Role == MarkdownRenderer.Accessibility.MarkdownSemanticRole.Link);
    }

    [Fact]
    public void LinkedHtmlImageDoesNotCreateASecondWhitespaceOnlyLink()
    {
        const string source = "<p><a href='https://example.test'>\n  <img src='badge.svg' alt='Badge'>\n</a></p>";

        using LayoutSnapshot snapshot = Build(source, SafeHtmlOptions.Default);
        MarkdownRenderer.Accessibility.MarkdownSemanticNode[] links =
            MarkdownRenderer.Accessibility.MarkdownSemanticDocument
                .EnumerateDepthFirst(snapshot.SemanticDocument.Root)
                .Where(static node => node.Role == MarkdownRenderer.Accessibility.MarkdownSemanticRole.Link)
                .ToArray();

        Assert.Empty(links);
        Assert.Single(
            MarkdownRenderer.Accessibility.MarkdownSemanticDocument
                .EnumerateDepthFirst(snapshot.SemanticDocument.Root),
            static node => node.Role == MarkdownRenderer.Accessibility.MarkdownSemanticRole.Image);
    }

    [Fact]
    public void HtmlHeadingsInsideTableCellsRetainHeadingStyleAndSemantics()
    {
        const string source = "<table><tr><td><h3>Agentic <a href='https://example.test'>Task Manager</a></h3></td></tr></table>";

        using LayoutSnapshot snapshot = Build(source, SafeHtmlOptions.Default);
        InlineRun[] runs = FlattenRuns(snapshot).ToArray();
        MarkdownRenderer.Accessibility.MarkdownSemanticNode heading = Assert.Single(
            MarkdownRenderer.Accessibility.MarkdownSemanticDocument
                .EnumerateDepthFirst(snapshot.SemanticDocument.Root),
            static node => node.Role == MarkdownRenderer.Accessibility.MarkdownSemanticRole.Heading);

        Assert.Equal(3, heading.HeadingLevel);
        Assert.Equal("Agentic Task Manager", snapshot.SemanticDocument.GetText(heading));
        Assert.All(runs.Where(static run => run.Text.Trim().Length > 0),
            static run => Assert.Equal(MarkdownElementKeys.Heading3, run.SemanticHeadingKey));
        Assert.Single(heading.Children,
            static child => child.Role == MarkdownRenderer.Accessibility.MarkdownSemanticRole.Link);
    }

    [Fact]
    public void ImageOnlyHtmlHeadingRetainsHeadingSemantics()
    {
        const string source = "<table><tr><td><h1><picture><img src='logo.png' alt='Project logo'></picture></h1></td></tr></table>";

        using LayoutSnapshot snapshot = Build(source, SafeHtmlOptions.Default);
        InlineImageRun image = Assert.Single(
            FlattenBoxes(snapshot)
                .OfType<InlineContainerBox>()
                .SelectMany(static box => box.Runs)
                .OfType<InlineImageRun>());
        MarkdownRenderer.Accessibility.MarkdownSemanticNode heading = Assert.Single(
            MarkdownRenderer.Accessibility.MarkdownSemanticDocument
                .EnumerateDepthFirst(snapshot.SemanticDocument.Root),
            static node => node.Role == MarkdownRenderer.Accessibility.MarkdownSemanticRole.Heading);

        Assert.Equal(MarkdownElementKeys.Heading1, image.SemanticHeadingKey);
        Assert.Equal(1, heading.HeadingLevel);
        Assert.Equal("Project logo", snapshot.SemanticDocument.GetText(heading));
    }

    [Fact]
    public void RankEightOpeningHtmlFlowsThroughMarkdownParserAsSiblingLayoutBlocks()
    {
        const string source =
            "<p align=\"center\">\n" +
            "  <a href=\"https://roadmap.sh/\"><img src=\"https://roadmap.sh/img/brand.png\" height=\"70\"></a>\n" +
            "  <h2 align=\"center\"><a href=\"https://roadmap.sh\">roadmap.sh</a></h2>\n" +
            "  <p align=\"center\">Community-driven roadmaps, articles and resources for developers</p>\n" +
            "  <p align=\"center\">\n" +
            "    <a href=\"https://roadmap.sh/roadmaps\">\n" +
            "\t        <img src=\"https://img.shields.io/badge/%E2%9C%A8-Roadmaps%20-0a0a0a.svg?style=flat&colorA=0a0a0a\" alt=\"roadmaps\" />\n" +
            "    </a>\n" +
            "    <a href=\"https://roadmap.sh/best-practices\">\n" +
            "\t        <img src=\"https://img.shields.io/badge/%E2%9C%A8-Best%20Practices-0a0a0a.svg?style=flat&colorA=0a0a0a\" alt=\"best practices\" />\n" +
            "    </a>\n" +
            "    <a href=\"https://roadmap.sh/questions\">\n" +
            "\t        <img src=\"https://img.shields.io/badge/%E2%9C%A8-Questions-0a0a0a.svg?style=flat&colorA=0a0a0a\" alt=\"videos\" />\n" +
            "    </a>\n" +
            "    <a href=\"https://www.youtube.com/channel/UCA0H2KIWgWTwpTFjSxp0now?sub_confirmation=1\">\n" +
            "\t        <img src=\"https://img.shields.io/badge/%E2%9C%A8-YouTube%20Channel-0a0a0a.svg?style=flat&colorA=0a0a0a\" alt=\"roadmaps\" />\n" +
            "    </a>\n" +
            "  </p>\n" +
            "</p>";

        // These are the canonical Primer profile's paragraph and h2 margins.
        // The test checks the production path with unequal adjacent margins,
        // where CSS normal flow keeps max(16px, 24px), not their 40px sum.
        var bodyStyle = new ElementStyle
        {
            FontSize = 16,
            LineHeightMultiplier = 1.5f,
            Margin = new Thickness(0, 0, 0, 16),
        };
        var heading2Style = new ElementStyle
        {
            FontSize = 24,
            FontWeight = new Windows.UI.Text.FontWeight { Weight = 600 },
            LineHeightMultiplier = 1.25f,
            Margin = new Thickness(0, 24, 0, 16),
        };
        var profileStyles = new Dictionary<string, ElementStyle>(StringComparer.Ordinal)
        {
            [MarkdownElementKeys.Body] = bodyStyle,
            [MarkdownElementKeys.Heading2] = heading2Style,
        };

        // BuildGitHub runs Markdig's GFM pipeline and registered
        // HtmlBlockRenderer; this checks actual production boxes and margins.
        using LayoutSnapshot snapshot = BuildGitHub(
            source,
            keyedStyles: profileStyles,
            availableWidth: 660);
        StackBox htmlBlock = Assert.IsType<StackBox>(Assert.Single(snapshot.Blocks));
        Assert.Equal(4, htmlBlock.Children.Count);
        Assert.True(htmlBlock.CollapsePositiveAdjacentMargins);

        InlineContainerBox logo = Assert.IsType<InlineContainerBox>(htmlBlock.Children[0]);
        InlineContainerBox heading = Assert.IsType<InlineContainerBox>(htmlBlock.Children[1]);
        InlineContainerBox subtitle = Assert.IsType<InlineContainerBox>(htmlBlock.Children[2]);
        InlineContainerBox badges = Assert.IsType<InlineContainerBox>(htmlBlock.Children[3]);
        Assert.Equal(MarkdownElementKeys.Heading2, heading.ElementKey);
        Assert.Contains(heading.Runs.OfType<LinkRun>(), static run => run.Text == "roadmap.sh");
        Assert.Contains(subtitle.Runs, static run => run.Text.Contains("Community-driven roadmaps", StringComparison.Ordinal));
        Assert.Single(logo.Runs.OfType<InlineImageRun>(), static image => image.Url.EndsWith("/brand.png", StringComparison.Ordinal));
        Assert.Equal(4, badges.Runs.OfType<InlineImageRun>().Count());
        Assert.DoesNotContain(FlattenRuns(snapshot), static run => run.Text.Contains("<h2", StringComparison.Ordinal));

        Assert.Equal(new Thickness(0, 0, 0, 16), logo.Margin);
        Assert.Equal(new Thickness(0, 24, 0, 16), heading.Margin);
        Assert.Equal(40, logo.Margin.Bottom + heading.Margin.Top, precision: 1);
        double headingContentTop = heading.Bounds.Top + heading.Margin.Top;
        double logoContentBottom = logo.Bounds.Bottom - logo.Margin.Bottom;
        Assert.Equal(24, headingContentTop - logoContentBottom, precision: 1);

        // The opt-in is deliberately limited to safe-HTML local flow. Plain
        // Markdown continues to add its paragraph-bottom and heading-top
        // margins, protecting the application-wide block layout contract.
        using LayoutSnapshot markdownSnapshot = BuildGitHub(
            "Paragraph.\n\n## Heading",
            keyedStyles: profileStyles,
            availableWidth: 660);
        InlineContainerBox markdownParagraph = Assert.IsType<InlineContainerBox>(markdownSnapshot.Blocks[0]);
        InlineContainerBox markdownHeading = Assert.IsType<InlineContainerBox>(markdownSnapshot.Blocks[1]);
        double markdownGap = markdownHeading.Bounds.Top + markdownHeading.Margin.Top -
            (markdownParagraph.Bounds.Bottom - markdownParagraph.Margin.Bottom);
        Assert.Equal(40, markdownGap, precision: 1);
    }

    [Fact]
    public void PictureLogoKeepsCenteredLinksAndFollowingPageContentBelowItsBounds()
    {
        const string source = """
            <p align="center"><picture>
            <source media="(prefers-color-scheme: light)" srcset="https://example.test/rust-logo.svg">
            <img src="https://example.test/rust-logo.svg" alt="Rust" width="311" height="155">
            </picture><br>
            <a href="https://www.rust-lang.org">Rust homepage</a> |
            <a href="https://doc.rust-lang.org">API documentation</a></p>
            <p>Following page content remains below the logo.</p>
            """;

        using MarkdownEngine engine = new MarkdownEngineBuilder()
            .UseGitHubReadme()
            .Build();
        var registry = Assert.IsType<MarkdownExtensionRegistry>(engine.PresentationConfiguration);
        using LayoutSnapshot snapshot = Build(source, registry, availableWidth: 660f);
        InlineContainerBox host = Assert.Single(
            FlattenBoxes(snapshot).OfType<InlineContainerBox>(),
            static box => box.Runs.OfType<InlineImageRun>().Any() &&
                          box.Runs.OfType<LinkRun>().Count() == 2);
        InlineImageRun image = Assert.Single(host.Runs.OfType<InlineImageRun>());
        Rect imageBounds = Assert.Single(host.EnumerateInlineImageRects()).Rect;
        Rect[] linkBounds = host.Runs
            .OfType<LinkRun>()
            .Select(link => host.GetRunRect(link.InlineIndex))
            .ToArray();

        Assert.Equal("https://example.test/rust-logo.svg", image.Url);
        Assert.Equal(311d, imageBounds.Width, precision: 1);
        Assert.Equal(155d, imageBounds.Height, precision: 1);
        Assert.All(linkBounds, bounds => Assert.True(
            bounds.Top >= imageBounds.Bottom - 0.5,
            $"Centered link at y={bounds.Top:F2} overlaps the picture ending at y={imageBounds.Bottom:F2}."));
        Assert.True(
            snapshot.Size.Height >= imageBounds.Bottom,
            $"Document height {snapshot.Size.Height:F2} does not contain the picture ending at y={imageBounds.Bottom:F2}.");
        Assert.Contains(
            FlattenRuns(snapshot),
            static run => run is TextRun && run.Text.Contains("Following page content", StringComparison.Ordinal));
    }

    [Fact]
    public void SplitCenteredDivScopeAlignsClosingHtmlBlockButNotFollowingMarkdown()
    {
        const string source = """
            <div align="center">

            <h3>Inside centered scope</h3></div>

            Outside paragraph.
            """;

        using LayoutSnapshot snapshot = BuildGitHub(source);
        InlineContainerBox heading = Assert.Single(
            FlattenBoxes(snapshot).OfType<InlineContainerBox>(),
            static box => box.ElementKey == MarkdownElementKeys.Heading3 &&
                          box.Runs.Any(run => run.Text == "Inside centered scope"));
        InlineContainerBox outsideParagraph = Assert.Single(
            FlattenBoxes(snapshot).OfType<InlineContainerBox>(),
            static box => box.ElementKey == MarkdownElementKeys.Body &&
                          box.Runs.Any(run => run.Text == "Outside paragraph."));

        Assert.Equal(CanvasHorizontalAlignment.Center, heading.TextAlignment);
        Assert.Equal(CanvasHorizontalAlignment.Left, outsideParagraph.TextAlignment);
    }

    [Fact]
    public void SplitCenteredDivScopeDoesNotOverrideNestedHeadingAlignment()
    {
        const string source = """
            <div align="center">

            <h3 align="right">Nested right heading</h3></div>
            """;

        using LayoutSnapshot snapshot = BuildGitHub(source);
        InlineContainerBox heading = Assert.Single(
            FlattenBoxes(snapshot).OfType<InlineContainerBox>(),
            static box => box.ElementKey == MarkdownElementKeys.Heading3 &&
                          box.Runs.Any(run => run.Text == "Nested right heading"));

        Assert.Equal(CanvasHorizontalAlignment.Right, heading.TextAlignment);
    }

    [Fact]
    public void SplitCenteredDivScopeKeepsLocalAndInheritedParagraphAlignments()
    {
        const string source = """
            <div align="center">

            <p align="right">Right paragraph</p><p>Inherited centered paragraph</p></div>

            Outside paragraph.
            """;

        using LayoutSnapshot snapshot = BuildGitHub(source);
        InlineContainerBox rightParagraph = Assert.Single(
            FlattenBoxes(snapshot).OfType<InlineContainerBox>(),
            static box => box.ElementKey == MarkdownElementKeys.Body &&
                          box.Runs.Any(run => run.Text == "Right paragraph"));
        InlineContainerBox inheritedParagraph = Assert.Single(
            FlattenBoxes(snapshot).OfType<InlineContainerBox>(),
            static box => box.ElementKey == MarkdownElementKeys.Body &&
                          box.Runs.Any(run => run.Text == "Inherited centered paragraph"));
        InlineContainerBox outsideParagraph = Assert.Single(
            FlattenBoxes(snapshot).OfType<InlineContainerBox>(),
            static box => box.ElementKey == MarkdownElementKeys.Body &&
                          box.Runs.Any(run => run.Text == "Outside paragraph."));

        Assert.Equal(CanvasHorizontalAlignment.Right, rightParagraph.TextAlignment);
        Assert.Equal(CanvasHorizontalAlignment.Center, inheritedParagraph.TextAlignment);
        Assert.Equal(CanvasHorizontalAlignment.Left, outsideParagraph.TextAlignment);
    }

    [Fact]
    public void SplitInlineFragmentsInCenteredDivKeepBadgeRowsTogetherAndIgnoreCommentBoxes()
    {
        const string source = """
            <div align="center">

            <a><img src="https://example.test/first-row.png" width="40" height="20" alt="first row"></a>

            <!-- a source-only separator between badge rows -->

            <br><a><img src="https://example.test/second-row.png" width="40" height="20" alt="second row"></a>

            </div>
            """;

        using LayoutSnapshot snapshot = BuildGitHub(source);
        InlineContainerBox firstRow = Assert.Single(
            FlattenBoxes(snapshot).OfType<InlineContainerBox>(),
            static box => box.Runs.OfType<InlineImageRun>()
                .Any(static image => image.Url.EndsWith("/first-row.png", StringComparison.Ordinal)));
        InlineContainerBox secondRow = Assert.Single(
            FlattenBoxes(snapshot).OfType<InlineContainerBox>(),
            static box => box.Runs.OfType<InlineImageRun>()
                .Any(static image => image.Url.EndsWith("/second-row.png", StringComparison.Ordinal)));
        Rect firstImage = Assert.Single(firstRow.EnumerateInlineImageRects()).Rect;
        Rect secondImage = Assert.Single(secondRow.EnumerateInlineImageRects()).Rect;

        Assert.True(StackBox.SharesSafeHtmlInlineFlow(firstRow, secondRow));
        Assert.True(
            secondImage.Top - firstImage.Bottom <= 6,
            $"Adjacent badge rows in one HTML flow are separated by {secondImage.Top - firstImage.Bottom:F2} DIPs.");
        Assert.DoesNotContain(snapshot.Blocks, static block => block is StackBox { Children.Count: 0 });
    }

    [Fact]
    public void SeparateHtmlParagraphsKeepTheirBlockSpacingInsidePersistentDiv()
    {
        const string source = """
            <div align="center">

            <p>First paragraph</p>

            <p>Second paragraph</p>

            </div>
            """;

        using LayoutSnapshot snapshot = BuildGitHub(
            source,
            new ElementStyle { Margin = new Thickness(0, 0, 0, 8) });
        InlineContainerBox firstParagraph = Assert.Single(
            FlattenBoxes(snapshot).OfType<InlineContainerBox>(),
            static box => box.Runs.Any(static run => run.Text == "First paragraph"));
        InlineContainerBox secondParagraph = Assert.Single(
            FlattenBoxes(snapshot).OfType<InlineContainerBox>(),
            static box => box.Runs.Any(static run => run.Text == "Second paragraph"));
        InlineRun firstText = Assert.Single(firstParagraph.Runs, static run => run.Text == "First paragraph");
        InlineRun secondText = Assert.Single(secondParagraph.Runs, static run => run.Text == "Second paragraph");
        Rect firstTextBounds = firstParagraph.GetRunRect(firstText.InlineIndex);
        Rect secondTextBounds = secondParagraph.GetRunRect(secondText.InlineIndex);
        double visibleTextGap = secondTextBounds.Top - firstTextBounds.Bottom;

        Assert.False(StackBox.SharesSafeHtmlInlineFlow(firstParagraph, secondParagraph));
        Assert.True(
            visibleTextGap >= 7.5,
            $"Distinct HTML paragraph blocks must retain their normal spacing; the visible text gap was {visibleTextGap:F2} DIPs.");
    }

    [Fact]
    public void InlineDivAndFirstBadgeRowInSameHtmlBlockJoinFollowingFragment()
    {
        const string source = """
            <div align="center"><a><img src="https://example.test/same-block-first.png" width="40" height="20" alt="first"></a>

            <br><a><img src="https://example.test/same-block-second.png" width="40" height="20" alt="second"></a>

            </div>
            """;

        using LayoutSnapshot snapshot = BuildGitHub(source);
        InlineContainerBox firstRow = Assert.Single(
            FlattenBoxes(snapshot).OfType<InlineContainerBox>(),
            static box => box.Runs.OfType<InlineImageRun>()
                .Any(static image => image.Url.EndsWith("/same-block-first.png", StringComparison.Ordinal)));
        InlineContainerBox secondRow = Assert.Single(
            FlattenBoxes(snapshot).OfType<InlineContainerBox>(),
            static box => box.Runs.OfType<InlineImageRun>()
                .Any(static image => image.Url.EndsWith("/same-block-second.png", StringComparison.Ordinal)));
        Rect firstImage = Assert.Single(firstRow.EnumerateInlineImageRects()).Rect;
        Rect secondImage = Assert.Single(secondRow.EnumerateInlineImageRects()).Rect;
        BlockBox firstFlowBlock = Assert.Single(
            snapshot.Blocks,
            static block => FlattenRuns(block).OfType<InlineImageRun>()
                .Any(static image => image.Url.EndsWith("/same-block-first.png", StringComparison.Ordinal)));
        BlockBox secondFlowBlock = Assert.Single(
            snapshot.Blocks,
            static block => FlattenRuns(block).OfType<InlineImageRun>()
                .Any(static image => image.Url.EndsWith("/same-block-second.png", StringComparison.Ordinal)));

        Assert.True(StackBox.SharesSafeHtmlInlineFlow(firstFlowBlock, secondFlowBlock));
        Assert.True(
            secondImage.Top - firstImage.Bottom <= 6,
            "An inline div opener and its first row in one HtmlBlock must join its next split fragment.");
    }

    [Fact]
    public void ClosingDivBeforeInlineContentDoesNotReuseThePreviousFlowMarker()
    {
        const string source = """
            <div align="center">

            <a><img src="https://example.test/inside.png" width="40" height="20" alt="inside"></a>

            </div><br><a><img src="https://example.test/outside.png" width="40" height="20" alt="outside"></a>
            """;

        using LayoutSnapshot snapshot = BuildGitHub(source);
        InlineContainerBox inside = Assert.Single(
            FlattenBoxes(snapshot).OfType<InlineContainerBox>(),
            static box => box.Runs.OfType<InlineImageRun>()
                .Any(static image => image.Url.EndsWith("/inside.png", StringComparison.Ordinal)));
        InlineContainerBox outside = Assert.Single(
            FlattenBoxes(snapshot).OfType<InlineContainerBox>(),
            static box => box.Runs.OfType<InlineImageRun>()
                .Any(static image => image.Url.EndsWith("/outside.png", StringComparison.Ordinal)));

        Assert.NotNull(inside.SafeHtmlInlineFlowScopeId);
        Assert.Null(outside.SafeHtmlInlineFlowScopeId);
        Assert.False(StackBox.SharesSafeHtmlInlineFlow(inside, outside));
    }

    [Fact]
    public void MarkdownEnabledHtmlContainerInheritsAlignmentAtChildEntry()
    {
        const string source = """
            <div align="center" markdown="1">
            ## Centered nested heading

            Centered nested paragraph.

            - Centered nested list item
            </div>
            """;

        using LayoutSnapshot snapshot = BuildGitHub(source);
        InlineContainerBox heading = Assert.Single(
            FlattenBoxes(snapshot).OfType<InlineContainerBox>(),
            static box => box.ElementKey == MarkdownElementKeys.Heading2 &&
                          box.Runs.Any(static run => run.Text == "Centered nested heading"));
        InlineContainerBox paragraph = Assert.Single(
            FlattenBoxes(snapshot).OfType<InlineContainerBox>(),
            static box => box.Runs.Any(static run => run.Text == "Centered nested paragraph."));

        Assert.Equal(CanvasHorizontalAlignment.Center, heading.TextAlignment);
        Assert.Equal(CanvasHorizontalAlignment.Center, paragraph.TextAlignment);
    }

    [Fact]
    public void PersistentCenteredHtmlBlockKeepsNestedRightHeadingAndCenteredSibling()
    {
        const string source = """
            <div align="center">
            <h3 align="right">Persistent nested right heading</h3>
            <p>Persistent centered sibling</p>
            """;

        using LayoutSnapshot snapshot = BuildGitHub(source);
        InlineContainerBox heading = Assert.Single(
            FlattenBoxes(snapshot).OfType<InlineContainerBox>(),
            static box => box.ElementKey == MarkdownElementKeys.Heading3 &&
                          box.Runs.Any(run => run.Text == "Persistent nested right heading"));
        InlineContainerBox paragraph = Assert.Single(
            FlattenBoxes(snapshot).OfType<InlineContainerBox>(),
            static box => box.ElementKey == MarkdownElementKeys.Body &&
                          box.Runs.Any(run => run.Text == "Persistent centered sibling"));

        Assert.Equal(CanvasHorizontalAlignment.Right, heading.TextAlignment);
        Assert.Equal(CanvasHorizontalAlignment.Center, paragraph.TextAlignment);
    }

    [Fact]
    public void CenteredLargeHtmlImageReservesFlowBeforeFollowingHeadingAndProse()
    {
        const string source = """
            免费的编程中文书籍索引
            ============================

            [![](https://img.shields.io/github/issues/justjavac/free-programming-books-zh_CN.svg)](https://github.com/justjavac/free-programming-books-zh_CN/issues)  [![](https://img.shields.io/github/forks/justjavac/free-programming-books-zh_CN.svg)](https://github.com/justjavac/free-programming-books-zh_CN/network) [![](https://img.shields.io/github/stars/justjavac/free-programming-books-zh_CN.svg)](https://github.com/justjavac/free-programming-books-zh_CN/stargazers) [![](https://travis-ci.org/justjavac/free-programming-books-zh_CN.svg?branch=master)](https://travis-ci.org/justjavac/free-programming-books-zh_CN) [![](https://img.shields.io/github/release/justjavac/free-programming-books-zh_CN.svg)](https://github.com/justjavac/free-programming-books-zh_CN/releases)

            免费的编程中文书籍索引，欢迎投稿。

            - 国外程序员在 [stackoverflow](http://stackoverflow.com/questions/1711/what-is-the-single-most-influential-book-every-programmer-should-read/1713%231713) 推荐的程序员必读书籍，[中文版](http://justjavac.com/other/2012/05/15/qualified-programmer-should-read-what-books.html "一个合格的程序员应该读过哪些书")。
            - 程序员应该阅读的非编程类书籍有哪些？ [中文版](what-non-programming-books-should-programmers-read.md)（原帖为 stackoverflow 上已删除的[问题 38210](https://web.archive.org/web/20170103152138/http://stackoverflow.com/questions/38210/what-non-programming-books-should-programmers-read)）
            - [github](https://github.com/EbookFoundation/free-programming-books) 上的一个流行的编程书籍索引  [中文版](https://github.com/EbookFoundation/free-programming-books/blob/main/books/free-programming-books-zh.md)

            如果这个仓库对你有帮助，欢迎 star。如果这个仓库帮你提升了技能找到了工作，可以请我喝杯咖啡：

            <p align="center"><img src="https://example.test/buy-me-a-coffee-wechat.png" width="320" height="320" alt="" /></p>

            ## 参与交流

            欢迎大家将珍藏已久的经典免费书籍共享出来，您可以：
            """;

        using MarkdownEngine engine = new MarkdownEngineBuilder()
            .UseGitHubReadme()
            .Build();
        var registry = Assert.IsType<MarkdownExtensionRegistry>(engine.PresentationConfiguration);
        using LayoutSnapshot snapshot = Build(
            source,
            registry,
            lazy: true,
            availableWidth: 660,
            measureAllLazyViewports: false);
        InlineContainerBox imageHost = Assert.Single(
            FlattenBoxes(snapshot).OfType<InlineContainerBox>(),
            static box => box.Runs.OfType<InlineImageRun>()
                .Any(static image => image.Url == "https://example.test/buy-me-a-coffee-wechat.png"));
        InlineImageRun image = Assert.Single(imageHost.Runs.OfType<InlineImageRun>());
        Rect imageBounds = Assert.Single(imageHost.EnumerateInlineImageRects()).Rect;
        InlineContainerBox heading = Assert.Single(
            FlattenBoxes(snapshot).OfType<InlineContainerBox>(),
            static box => box.Runs.OfType<TextRun>()
                .Any(static run => run.Text.Contains("参与交流", StringComparison.Ordinal)));
        InlineContainerBox prose = Assert.Single(
            FlattenBoxes(snapshot).OfType<InlineContainerBox>(),
            static box => box.Runs.OfType<TextRun>()
                .Any(static run => run.Text.Contains("欢迎大家将珍藏已久的经典免费书籍共享出来", StringComparison.Ordinal)));

        Assert.Equal(320f, image.DesiredWidth, precision: 1);
        Assert.Equal(320f, image.DesiredHeight, precision: 1);
        Assert.Equal(320d, imageBounds.Width, precision: 1);
        Assert.Equal(320d, imageBounds.Height, precision: 1);
        Assert.True(
            imageBounds.Bottom <= heading.Bounds.Top + 0.5,
            $"The following heading at y={heading.Bounds.Top:F2} overlaps the painted 320-DIP image ending at y={imageBounds.Bottom:F2}; image paragraph flow ends at y={imageHost.Bounds.Bottom:F2}.");
        Assert.True(
            heading.Bounds.Bottom <= prose.Bounds.Top + 0.5,
            $"The following prose at y={prose.Bounds.Top:F2} overlaps its heading ending at y={heading.Bounds.Bottom:F2}.");
        Assert.True(
            imageHost.Bounds.Bottom <= heading.Bounds.Top + 0.5,
            $"The image paragraph's flow box ends at y={imageHost.Bounds.Bottom:F2}, after the following heading starts at y={heading.Bounds.Top:F2}.");
    }

    [Fact]
    public async Task LateCenteredHtmlImageDecodeKeepsFollowingContentOutOfPublishedImageBounds()
    {
        const string source = """
            If this repository helps you, you can buy me a coffee:

            <p align="center"><img src="https://example.test/buy-me-a-coffee-wechat.png" width="320" height="320" alt="" /></p>

            ## 参与交流

            欢迎大家将珍藏已久的经典免费书籍共享出来，您可以：
            """;
        var resolver = new DeferredImageResolver();
        using MarkdownEngine engine = new MarkdownEngineBuilder()
            .UseGitHubReadme()
            .Build();
        var registry = Assert.IsType<MarkdownExtensionRegistry>(engine.PresentationConfiguration);
        using LayoutSnapshot snapshot = Build(
            source,
            registry,
            lazy: true,
            availableWidth: 660,
            measureAllLazyViewports: false,
            imageResolver: resolver);

        InlineContainerBox imageHost = Assert.Single(
            FlattenBoxes(snapshot).OfType<InlineContainerBox>(),
            static box => box.Runs.OfType<InlineImageRun>().Any());
        InlineImageRun image = Assert.Single(imageHost.Runs.OfType<InlineImageRun>());
        InlineContainerBox heading = Assert.Single(
            FlattenBoxes(snapshot).OfType<InlineContainerBox>(),
            static box => box.Runs.OfType<TextRun>()
                .Any(static run => run.Text.Contains("参与交流", StringComparison.Ordinal)));
        InlineContainerBox prose = Assert.Single(
            FlattenBoxes(snapshot).OfType<InlineContainerBox>(),
            static box => box.Runs.OfType<TextRun>()
                .Any(static run => run.Text.Contains("欢迎大家将珍藏已久的经典免费书籍共享出来", StringComparison.Ordinal)));
        Rect initialImageRect = Assert.Single(imageHost.EnumerateInlineImageRects()).Rect;
        Assert.True(initialImageRect.Bottom <= heading.Bounds.Top + 0.5,
            "The initial lazy measurement must reserve the explicit 320-DIP image height.");

        var imagePublished = new TaskCompletionSource<(bool LayoutInvalidated, double HostBottom, double HeadingTop, bool HasBitmap)>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        EventHandler<LoadCompletedEventArgs> onLoadCompleted = (_, args) =>
            imagePublished.TrySetResult((
                args.LayoutInvalidated,
                imageHost.Bounds.Bottom,
                heading.Bounds.Top,
                image.Image.Bitmap is not null));
        image.Image.LoadCompleted += onLoadCompleted;
        try
        {
            image.Image.EnsureLoading();
            await resolver.ResolveStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
            resolver.Complete(MarkdownImageResolution.Resolved(new MarkdownImageAsset(
                Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR4nGP6zwAAAgcBApocMXEAAAAASUVORK5CYII="),
                ContentType: "image/png",
                CacheKey: $"safe-html-late-image-{Guid.NewGuid():N}")));

            // Observe the exact first publication point, before the control's
            // queued block relayout can run. This is where a newly available
            // CanvasBitmap can first be painted over neighboring content.
            (bool layoutInvalidated, double hostBottomAtPublish, double headingTopAtPublish, bool hasBitmap) =
                await imagePublished.Task.WaitAsync(TimeSpan.FromSeconds(10));

            Assert.True(hasBitmap, "The resolver/decode path must publish the raster before signaling completion.");
            Assert.True(layoutInvalidated, "The first raster publication must request the host's normal relayout path.");
            Assert.True(
                hostBottomAtPublish <= headingTopAtPublish + 0.5,
                $"At bitmap publication the image paragraph ends at y={hostBottomAtPublish:F2}, after the heading starts at y={headingTopAtPublish:F2}.");

            // Exercise the same changed-block relayout requested by the host
            // after the asynchronous publication.
            snapshot.RelayoutChangedBlocks(
                [imageHost.BlockIndex],
                660,
                CancellationToken.None);

            Rect loadedImageRect = Assert.Single(imageHost.EnumerateInlineImageRects()).Rect;
            Assert.NotNull(image.Image.Bitmap);
            Assert.Equal(320d, loadedImageRect.Width, precision: 1);
            Assert.Equal(320d, loadedImageRect.Height, precision: 1);
            Assert.True(
                loadedImageRect.Bottom <= heading.Bounds.Top + 0.5,
                $"After async relayout the heading at y={heading.Bounds.Top:F2} overlaps the image ending at y={loadedImageRect.Bottom:F2}.");
            Assert.True(
                heading.Bounds.Bottom <= prose.Bounds.Top + 0.5,
                $"The prose at y={prose.Bounds.Top:F2} overlaps the heading ending at y={heading.Bounds.Bottom:F2}.");
        }
        finally
        {
            // Always release the deferred source so a failed assertion cannot
            // leave an image-load task waiting after the snapshot is disposed.
            resolver.Complete(MarkdownImageResolution.Unavailable);
            image.Image.LoadCompleted -= onLoadCompleted;
        }
    }

    [Fact]
    public void MissingAltAnimatedImageUsesItsFileNameLikeGitHub()
    {
        const string source = "<p><img src='/art/android-PullRefreshLayout.gif' width='49%'></p>";

        using LayoutSnapshot snapshot = Build(source, SafeHtmlOptions.Default);
        InlineImageRun image = Assert.Single(FlattenRuns(snapshot).OfType<InlineImageRun>());

        Assert.Equal("android-PullRefreshLayout.gif", image.AltText);
        Assert.Contains("android-PullRefreshLayout.gif", snapshot.SemanticDocument.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void MissingAltStaticImageKeepsTheLocalizedGenericName()
    {
        const string source = "<p><img src='/art/preview.webp'></p>";

        using LayoutSnapshot snapshot = Build(source, SafeHtmlOptions.Default);
        InlineImageRun image = Assert.Single(FlattenRuns(snapshot).OfType<InlineImageRun>());

        Assert.Equal("Image", image.AltText);
    }

    [Fact]
    public void GitHubStandaloneHtmlCommentsAreNotRendered()
    {
        const string source = """
            Before

            <!-- generated-section:start -->

            After
            """;

        using LayoutSnapshot snapshot = Build(source, SafeHtmlOptions.Default);
        string rendered = string.Concat(FlattenRuns(snapshot).Select(static run => run.Text));

        Assert.Contains("Before", rendered, StringComparison.Ordinal);
        Assert.Contains("After", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("generated-section", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("<!--", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public void GitHubMultilineHtmlCommentsSuppressContainedMarkdownAndImages()
    {
        const string source = """
            Visible before

            <!-- hidden:start
            ![must not load](https://example.test/hidden.png)
            **hidden contributor**
            hidden:end -->

            Visible after
            """;

        using LayoutSnapshot snapshot = Build(source, SafeHtmlOptions.Default);
        InlineRun[] runs = FlattenRuns(snapshot).ToArray();
        string rendered = string.Concat(runs.Select(static run => run.Text));

        Assert.Contains("Visible before", rendered, StringComparison.Ordinal);
        Assert.Contains("Visible after", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("hidden contributor", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain(runs, static run => run is InlineImageRun);
    }

    [Fact]
    public void GitHubPictureMarkupUsesThemeMatchedSourceWithoutLeakingHtml()
    {
        const string source = """
            <picture>
              <source media="(prefers-color-scheme: dark)" srcset="dark.svg">
              <source media="(prefers-color-scheme: light)" srcset="light.svg">
              <img src="fallback.svg" alt="Project logo" width="240">
            </picture>
            """;

        using LayoutSnapshot snapshot = Build(source, SafeHtmlOptions.Default);
        InlineRun[] runs = FlattenRuns(snapshot).ToArray();

        InlineImageRun image = Assert.Single(runs.OfType<InlineImageRun>());
        Assert.Equal("light.svg", image.Url);
        Assert.Equal("Project logo", image.AltText);
        Assert.DoesNotContain("<picture", string.Concat(runs.Select(static run => run.Text)));
    }

    [Fact]
    public void GitHubPictureMarkupUsesSingleTypedRasterSource()
    {
        const string source = """
            <picture>
              <source type="image/gif" srcset="animation.gif">
              <img src="poster.jpg" alt="Animated preview">
            </picture>
            """;

        SafeHtmlDocument parsed = SafeHtmlParser.Parse(source);
        SafeHtmlElement picture = Assert.IsType<SafeHtmlElement>(Assert.Single(parsed.Root.Children));
        SafeHtmlElement typedSource = Assert.Single(
            picture.Children.OfType<SafeHtmlElement>(),
            static element => element.Name == "source");
        Assert.Equal("image/gif", typedSource.Attributes["type"]);
        Assert.Equal("animation.gif", typedSource.Attributes["srcset"]);

        using LayoutSnapshot snapshot = Build(source, SafeHtmlOptions.Default);
        InlineImageRun image = Assert.Single(FlattenRuns(snapshot).OfType<InlineImageRun>());

        Assert.Equal("animation.gif", image.Url);
        Assert.Equal("Animated preview", image.AltText);
    }

    [Theory]
    [InlineData("<source type='image/avif' srcset='candidate.avif'>")]
    [InlineData("<source type='image/gif' srcset='one.gif 1x, two.gif 2x'>")]
    [InlineData("<source type='image/gif' srcset='one.gif 2x'>")]
    [InlineData("<source type='image/gif' media='(min-width: 500px)' srcset='candidate.gif'>")]
    [InlineData("<source media='(prefers-color-scheme: light) and (min-width: 500px)' srcset='candidate.gif'>")]
    [InlineData("<source type='image/avif' media='(prefers-color-scheme: light)' srcset='candidate.avif'>")]
    [InlineData("<source type='image/gif' sizes='50vw' srcset='candidate.gif'>")]
    [InlineData("<source srcset='candidate.gif'>")]
    public void GitHubPictureMarkupUsesFallbackWhenSourceSelectionIsAmbiguousOrUnsupported(string sourceMarkup)
    {
        string source = "<picture>\n" + sourceMarkup +
            "\n<img src='poster.jpg' alt='Fallback preview'>\n</picture>";

        using LayoutSnapshot snapshot = Build(source, SafeHtmlOptions.Default);
        InlineImageRun image = Assert.Single(FlattenRuns(snapshot).OfType<InlineImageRun>());

        Assert.Equal("poster.jpg", image.Url);
        Assert.Equal("Fallback preview", image.AltText);
    }

    [Fact]
    public void ExplicitEmptyImageAltRemainsDecorative()
    {
        const string source = "<img src='decoration.png' alt='' width='16' height='1'>";

        using LayoutSnapshot snapshot = Build(source, SafeHtmlOptions.Default);
        InlineImageRun image = Assert.Single(FlattenRuns(snapshot).OfType<InlineImageRun>());

        Assert.Empty(image.AltText);
        Assert.Empty(image.AccessibleText);
        Assert.Empty(FlattenRuns(snapshot).OfType<InlineSpacerRun>());
    }

    [Fact]
    public void ExplicitlySizedSourceLessEmptyAltImageUsesInvisibleMappedSpacer()
    {
        const string source = "before<img width='16' height='1' alt=''>after";

        using LayoutSnapshot snapshot = Build(source, SafeHtmlOptions.Default);
        InlineSpacerRun spacer = Assert.Single(FlattenRuns(snapshot).OfType<InlineSpacerRun>());
        InlineContainerBox owner = Assert.Single(
            FlattenBoxes(snapshot).OfType<InlineContainerBox>(),
            box => box.Runs.Contains(spacer));

        Assert.Equal(16f, spacer.Width);
        Assert.Equal(1f, spacer.Height);
        Assert.Equal(1, spacer.TextLength);
        Assert.Equal("\uFFFC", spacer.Text);
        Assert.Empty(spacer.AccessibleText);
        Assert.True(PrivateTextRunPlacement.IsSupported(spacer));
        InlineSpacerRun clone = Assert.IsType<InlineSpacerRun>(PrivateTextRunPlacement.Clone(spacer));
        Assert.NotSame(spacer, clone);
        Assert.Equal(spacer.Width, clone.Width);
        Assert.Equal(spacer.Height, clone.Height);
        Assert.Equal(spacer.SourceSpan, clone.SourceSpan);

        Rect spacerBounds = owner.GetRunRect(spacer.InlineIndex);
        Assert.InRange(spacerBounds.Width, 15.5, 16.5);
        Assert.DoesNotContain("\uFFFC", snapshot.SemanticDocument.Text, StringComparison.Ordinal);
        Assert.DoesNotContain(
            snapshot.SemanticDocument.TextSpans,
            span => ReferenceEquals(span.InlineRun, spacer));
        Assert.Contains("before", snapshot.SemanticDocument.Text, StringComparison.Ordinal);
        Assert.Contains("after", snapshot.SemanticDocument.Text, StringComparison.Ordinal);
        int selectionStart = snapshot.SemanticDocument.TextOffsetFromDocumentPosition(
            new MarkdownRenderer.Document.DocumentPosition(owner.BlockIndex, spacer.InlineIndex, 0));
        int selectionEnd = snapshot.SemanticDocument.TextOffsetFromDocumentPosition(
            new MarkdownRenderer.Document.DocumentPosition(owner.BlockIndex, spacer.InlineIndex, spacer.TextLength));
        Assert.Equal(selectionStart, selectionEnd);
    }

    [Theory]
    [InlineData(1f)]
    [InlineData(1.75f)]
    public void DecorativeSpacerPreservesFractionalAuthoredGeometryAcrossTextScaling(float textScaleFactor)
    {
        const string source = "before<img width='16.25' height='1.5' alt=''>after";

        using LayoutSnapshot snapshot = Build(
            source,
            SafeHtmlOptions.Default,
            textScaleFactor: textScaleFactor);
        InlineSpacerRun spacer = Assert.Single(FlattenRuns(snapshot).OfType<InlineSpacerRun>());
        InlineContainerBox owner = Assert.Single(
            FlattenBoxes(snapshot).OfType<InlineContainerBox>(),
            box => box.Runs.Contains(spacer));
        Rect bounds = owner.GetRunRect(spacer.InlineIndex);

        Assert.Equal(16.25f, spacer.Width);
        Assert.Equal(1.5f, spacer.Height);
        Assert.InRange(bounds.Width, 16.2, 16.3);
        Assert.DoesNotContain("\uFFFC", snapshot.SemanticDocument.Text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("before<img width='16' height='1' alt=''>after", "after", 0)]
    [InlineData("<img width='16' height='1' alt=''>after", "after", 0)]
    [InlineData("before<img width='16' height='1' alt=''>", "before", 6)]
    [InlineData("before<strong>middle</strong><img width='16' height='1' alt=''>", "middle", 6)]
    public void DecorativeSpacerEndpointsMapToAdjacentSemanticBoundaryAndRoundTrip(
        string source,
        string boundaryText,
        int boundaryOffset)
    {
        using LayoutSnapshot snapshot = Build(source, SafeHtmlOptions.Default);
        InlineSpacerRun spacer = Assert.Single(FlattenRuns(snapshot).OfType<InlineSpacerRun>());
        InlineContainerBox owner = Assert.Single(
            FlattenBoxes(snapshot).OfType<InlineContainerBox>(),
            box => box.Runs.Contains(spacer));
        MarkdownRenderer.Accessibility.MarkdownSemanticDocument semantics = snapshot.SemanticDocument;
        int boundaryStart = semantics.Text.IndexOf(boundaryText, StringComparison.Ordinal);
        Assert.True(boundaryStart >= 0);
        int expected = boundaryStart + boundaryOffset;
        DocumentPosition start = new(owner.BlockIndex, spacer.InlineIndex, 0);
        DocumentPosition end = new(owner.BlockIndex, spacer.InlineIndex, spacer.TextLength);

        Assert.Equal(expected, semantics.TextOffsetFromDocumentPosition(start));
        Assert.Equal(expected, semantics.TextOffsetFromDocumentPosition(end));
        Assert.DoesNotContain("\uFFFC", semantics.Text, StringComparison.Ordinal);

        DocumentPosition? roundTrip = semantics.PositionFromTextOffset(expected);
        Assert.NotNull(roundTrip);
        Assert.Equal(expected, semantics.TextOffsetFromDocumentPosition(roundTrip.Value));
    }

    // An HTML entity inside link text, image alt text or emphasis was once dropped ("Q &amp; A" showed
    // as "Q  A", "std::hash&lt;T&gt;" as "std::hashT").
    [Theory]
    [InlineData("[DevOps Q &amp; A](https://example.test/a)", "DevOps Q & A")]
    [InlineData("**[Account &amp; usage](https://example.test/a)** more", "Account & usage more")]
    [InlineData("[std::hash&lt;basic_json&gt;](https://example.test/a), x", "std::hash<basic_json>, x")]
    [InlineData("[LINUX&nbsp;DO](https://linux.do)", "LINUX DO")]
    [InlineData("~~old &amp; new~~", "old & new")]
    [InlineData("![Q &amp; A](https://example.test/a.png)", "Q & A")]
    public void HtmlEntityInsideFlattenedInlineKeepsItsCharacter(string source, string expected)
    {
        using LayoutSnapshot snapshot = BuildGitHub(source);

        Assert.Equal(expected, snapshot.SemanticDocument.Text);
    }

    // An autolink inside emphasis or inside the first paragraph of a GitHub
    // alert once rendered as nothing ("repo ()").
    [Theory]
    [InlineData("**bold (<https://a.test/b>) x**", "bold (https://a.test/b) x")]
    [InlineData("*em <https://a.test/b>* y", "em https://a.test/b y")]
    public void AutolinkInsideEmphasisIsALink(string source, string expected)
    {
        using LayoutSnapshot snapshot = BuildGitHub(source);

        Assert.Equal(expected, snapshot.SemanticDocument.Text);
        LinkRun link = Assert.Single(FlattenRuns(snapshot).OfType<LinkRun>());
        Assert.Equal("https://a.test/b", link.Url);
    }

    // The first paragraph of an alert must not be flattened into one plain
    // text run, which loses its links, autolinks, bold, code and entities.
    [Fact]
    public void AlertFirstParagraphKeepsItsInlineStructure()
    {
        const string source =
            "> [!WARNING]\n" +
            "> **This repo (<https://github.com/a/b>) is official.** See [docs](https://a.test/d), `code` &amp; more.\n" +
            ">\n" +
            "> second [two](https://b.test)";

        using LayoutSnapshot snapshot = BuildGitHub(source);
        InlineRun[] runs = FlattenRuns(snapshot).ToArray();

        Assert.Equal(
            "⚠ Warning\nThis repo (https://github.com/a/b) is official. See docs, code & more.\nsecond two",
            snapshot.SemanticDocument.Text);
        Assert.Equal(
            new[] { "https://github.com/a/b", "https://a.test/d", "https://b.test" },
            runs.OfType<LinkRun>().Select(static link => link.Url).ToArray());
        Assert.Contains(runs, static run => run is CodeInlineRun { Text: "code" });
        Assert.DoesNotContain(runs, static run => run.Text.Contains("[!WARNING]", StringComparison.Ordinal));
    }

    [Fact]
    public void AlertTagOnTheSameLineIsNotRendered()
    {
        using LayoutSnapshot snapshot = BuildGitHub("> [!NOTE] Read [this](https://a.test).");

        Assert.Equal("ℹ Note\nRead this.", snapshot.SemanticDocument.Text);
        Assert.Equal("https://a.test", Assert.Single(FlattenRuns(snapshot).OfType<LinkRun>()).Url);
    }

    // Markdig also accepts "[X](" as a task marker in the middle of a list item, so
    // "Follow us on [X](url)" lost its link and reported a false task item.
    [Theory]
    [InlineData("- Follow us on [X](https://x.com/a) now", "Follow us on X now")]
    [InlineData("* [Bluesky](https://b.test) and [X](https://x.com/a) for news", "Bluesky and X for news")]
    public async Task MidParagraphTaskMarkerFollowedByLinkIsALink(string source, string expected)
    {
        using MarkdownEngine engine = new MarkdownEngineBuilder().UseGitHubReadme().Build();
        MarkdownRenderer.Document.MarkdownDocument document = await engine.ParseAsync(source);
        Assert.Empty(Markdig.Syntax.MarkdownObjectExtensions.Descendants<Markdig.Extensions.TaskLists.TaskList>(document.ParsedDocument!));
        var registry = Assert.IsType<MarkdownExtensionRegistry>(engine.PresentationConfiguration);
        using LayoutSnapshot snapshot = Build(source, registry, parsedDocument: document.ParsedDocument);

        Assert.EndsWith(expected, snapshot.SemanticDocument.Text, StringComparison.Ordinal);
        Assert.Contains(FlattenRuns(snapshot).OfType<LinkRun>(), static link => link.Url == "https://x.com/a");
    }

    // A README that starts with a block that has no accessible text (an alt=""
    // logo, a <a name> anchor) once left a separator at offset 0 that no block owns, so UIA "select the
    // whole document" started at offset 1 and missed the first character.
    [Theory]
    [InlineData("<div align=\"center\"><img src=\"https://example.test/logo.svg\" alt=\"\" height=\"150\"></div>\n\n# Flask\n\nFlask is a framework.", "Flask")]
    [InlineData("<a name=\"readme-top\"></a>\n\n<p align=\"center\"><img src=\"https://example.test/w.png\" alt=\"Logo\"></p>\n\nBody text.", "Logo")]
    [InlineData("<div><img width='16' height='1' alt=''></div>\n\nFirst words.", "First")]
    public void LeadingTextlessBlockDoesNotLeaveAnUnownedSeparator(string source, string firstText)
    {
        using LayoutSnapshot snapshot = BuildGitHub(source);
        MarkdownRenderer.Accessibility.MarkdownSemanticDocument semantics = snapshot.SemanticDocument;

        Assert.StartsWith(firstText, semantics.Text, StringComparison.Ordinal);
        Assert.True(semantics.TryGetDocumentRange(0, semantics.Text.Length, out DocumentRange range));
        DocumentRange normalized = range.Normalized();
        Assert.Equal(0, semantics.TextOffsetFromDocumentPosition(normalized.Start));
        Assert.Equal(semantics.Text.Length, semantics.TextOffsetFromDocumentPosition(normalized.End));
    }

    [Fact]
    public void SpacerOnlyParagraphMapsToItsZeroLengthSemanticNode()
    {
        const string source = "first paragraph\n\n<img width='16' height='1' alt=''>\n\nlast paragraph";

        using LayoutSnapshot snapshot = Build(source, SafeHtmlOptions.Default);
        InlineSpacerRun spacer = Assert.Single(FlattenRuns(snapshot).OfType<InlineSpacerRun>());
        InlineContainerBox owner = Assert.Single(
            FlattenBoxes(snapshot).OfType<InlineContainerBox>(),
            box => box.Runs.Contains(spacer));
        MarkdownRenderer.Accessibility.MarkdownSemanticDocument semantics = snapshot.SemanticDocument;
        Assert.True(semantics.TryGetInlineContainerNode(owner, out var semanticNode));
        Assert.Equal(semanticNode.TextStart, semanticNode.TextEnd);

        DocumentPosition start = new(owner.BlockIndex, spacer.InlineIndex, 0);
        DocumentPosition end = new(owner.BlockIndex, spacer.InlineIndex, spacer.TextLength);
        Assert.Equal(semanticNode.TextStart, semantics.TextOffsetFromDocumentPosition(start));
        Assert.Equal(semanticNode.TextEnd, semantics.TextOffsetFromDocumentPosition(end));
        Assert.Contains("first paragraph", semantics.Text, StringComparison.Ordinal);
        Assert.Contains("last paragraph", semantics.Text, StringComparison.Ordinal);

        DocumentPosition? roundTrip = semantics.PositionFromTextOffset(semanticNode.TextStart);
        Assert.NotNull(roundTrip);
        Assert.Equal(semanticNode.TextStart, semantics.TextOffsetFromDocumentPosition(roundTrip.Value));
    }

    [Fact]
    public void HtmlBlockImageSpacerUsesTheSameAdmissionPolicy()
    {
        const string source = "<div><img width='16' height='1' alt=''></div>";

        using LayoutSnapshot snapshot = Build(source, SafeHtmlOptions.Default);

        InlineSpacerRun spacer = Assert.Single(FlattenRuns(snapshot).OfType<InlineSpacerRun>());
        Assert.Equal(16f, spacer.Width);
        Assert.Equal(1f, spacer.Height);
        Assert.DoesNotContain(FlattenRuns(snapshot), static run => run is InlineImageRun);
    }

    [Theory]
    [InlineData("<img width='16' height='1'>")]
    [InlineData("<img alt=' ' width='16' height='1'>")]
    [InlineData("<img alt='' width='16'>")]
    [InlineData("<img alt='' height='1'>")]
    [InlineData("<img alt='' width='0' height='1'>")]
    [InlineData("<img alt='' width='16' height='0'>")]
    [InlineData("<img alt='' width='100%' height='1'>")]
    [InlineData("<img alt='' width='16' height='25%'>")]
    [InlineData("<img alt='' width='16' height='1' src=''>")]
    [InlineData("<img alt='' width='16' height='1' src='javascript:alert(1)'>")]
    [InlineData("<img alt='' width='16' height='1' srcset='one.png 1x'>")]
    [InlineData("<img alt='' width='16' height='1' data-src='one.png'>")]
    [InlineData("<img alt='' width='16' height='1' data-srcset='one.png 1x'>")]
    [InlineData("<img alt='' width='16' height='1' data-lazy-srcset='one.png 1x'>")]
    [InlineData("<img alt='' width='16' height='1' data-original='one.png'>")]
    [InlineData("<img alt='' width='16' height='1' data-original-src='one.png'>")]
    [InlineData("<img alt='' width='16' height='1' data-original-srcset='one.png 1x'>")]
    [InlineData("<img alt='' width='16' height='1' data-lazy-src='one.png'>")]
    [InlineData("<img alt='' width='NaN' height='1'>")]
    [InlineData("<img alt='' width='Infinity' height='1'>")]
    [InlineData("<img alt='' width='16' height='Infinity'>")]
    [InlineData("<img alt='' width='16' height='-Infinity'>")]
    [InlineData("<img alt='' style='width:NaNpx;height:1px'>")]
    [InlineData("<picture><img alt='' width='16' height='1'></picture>")]
    [InlineData("<a href='https://example.test/'><img alt='' width='16' height='1'></a>")]
    public void InertSpacerAdmissionRejectsIncompleteSourcedOrInteractiveImages(string source)
    {
        using LayoutSnapshot snapshot = Build(source, SafeHtmlOptions.Default);

        Assert.Empty(FlattenRuns(snapshot).OfType<InlineSpacerRun>());
    }

    [Fact]
    public void InertSpacerAdmissionRespectsDisabledImageFeature()
    {
        const string source = "<img alt='' width='16' height='1'>";
        var options = new SafeHtmlOptions(enableImages: false);

        using LayoutSnapshot snapshot = Build(source, options);

        Assert.Empty(FlattenRuns(snapshot).OfType<InlineSpacerRun>());
    }

    [Theory]
    [InlineData("<p><a href='https://example.test/'><img alt='' width='16' height='1'></a></p>")]
    [InlineData("<p><a href='javascript:alert(1)'><img alt='' width='16' height='1'></a></p>")]
    [InlineData("<div><a href='https://example.test/'><img alt='' width='16' height='1'></a></div>")]
    public void InertSpacerAdmissionRejectsAnchorContentWhenLinksAreDisabled(string source)
    {
        var options = new SafeHtmlOptions(enableLinks: false, enableImages: true);

        using LayoutSnapshot snapshot = Build(source, options);

        Assert.Empty(FlattenRuns(snapshot).OfType<InlineSpacerRun>());
        Assert.DoesNotContain(FlattenRuns(snapshot), static run => run is LinkRun);
    }

    [Fact]
    public void InertSpacerDimensionsUseTheExistingAbsoluteLengthSafetyCap()
    {
        const string source = "<img alt='' width='9000' height='1'>";

        using LayoutSnapshot snapshot = Build(source, SafeHtmlOptions.Default);

        InlineSpacerRun spacer = Assert.Single(FlattenRuns(snapshot).OfType<InlineSpacerRun>());
        Assert.Equal(SafeHtmlParser.MaximumAbsoluteLength, spacer.Width);
        Assert.Equal(1f, spacer.Height);
    }

    [Fact]
    public void LargeDecorativeSpacerShrinksProportionallyWithoutPinningTableMinContent()
    {
        const string source = """
            | Preview | Description |
            | --- | --- |
            | <img alt='' width='8192' height='8192'> | A compact label |
            """;

        using LayoutSnapshot snapshot = BuildGitHub(source, availableWidth: 180);
        TableBox table = Assert.Single(FlattenBoxes(snapshot).OfType<TableBox>());
        InlineSpacerRun spacer = Assert.Single(
            table.GetCellBoxes().SelectMany(static box => box.Runs).OfType<InlineSpacerRun>());
        InlineContainerBox owner = Assert.Single(
            table.GetCellBoxes(),
            box => box.Runs.Contains(spacer));
        Rect spacerBounds = owner.GetRunRect(spacer.InlineIndex);

        // Authored geometry remains immutable; only the layout projection
        // follows max-width:100% in the narrow table cell.
        Assert.Equal(8192f, spacer.Width);
        Assert.Equal(8192f, spacer.Height);
        Assert.False(table.CanScrollHorizontally);
        Assert.True(table.HorizontalExtent <= table.HorizontalViewport + 0.5);
        Assert.InRange(spacerBounds.Width, 1, owner.Bounds.Width + 0.5);
        Assert.InRange(table.Bounds.Height, 1, 256);
    }

    [Fact]
    public void GitHubVideoAttachmentUsesAtomicSafePlaceholderWithoutLeakingSignedMarkup()
    {
        const string source =
            "<video src='https://private-user-images.githubusercontent.com/demo.mp4?jwt=secret' " +
            "width='640' height='360' controls muted></video>";

        using LayoutSnapshot snapshot = Build(source, SafeHtmlOptions.Default);
        InlineImageRun media = Assert.Single(FlattenRuns(snapshot).OfType<InlineImageRun>());
        string rendered = string.Concat(FlattenRuns(snapshot).Select(static run => run.Text));

        Assert.StartsWith("data:image/svg+xml;base64,", media.Url, StringComparison.Ordinal);
        Assert.Null(media.LinkUrl);
        media.Measure(800, 20);
        Assert.InRange(media.DesiredWidth, 639, 641);
        Assert.InRange(media.DesiredHeight, 359, 361);
        Assert.DoesNotContain("jwt", rendered, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<video", rendered, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void GitHubRenderedTaskInputUsesNativeReadOnlyCheckboxSemantics()
    {
        const string source = "<ul><li><input type='checkbox' checked disabled> complete</li></ul>";

        using LayoutSnapshot snapshot = Build(source, SafeHtmlOptions.Default);
        InlineEmbedRun task = Assert.Single(FlattenRuns(snapshot).OfType<InlineEmbedRun>());

        Assert.NotNull(task.AutomationMetadata);
        Assert.True(task.AutomationMetadata!.IsChecked);
        Assert.False(task.AutomationMetadata.CanToggle);
    }

    [Fact]
    public void HeadingInsideDisclosureSummaryRetainsHeadingSemantics()
    {
        const string source = "<details><summary><div class='markdown-heading'><h3>Browser &amp; Automation</h3><a href='#browser'>Permalink</a></div></summary><p>Body</p></details>";

        using LayoutSnapshot snapshot = Build(source, SafeHtmlOptions.Default);
        MarkdownRenderer.Accessibility.MarkdownSemanticNode heading = Assert.Single(
            MarkdownRenderer.Accessibility.MarkdownSemanticDocument
                .EnumerateDepthFirst(snapshot.SemanticDocument.Root),
            static node => node.Role == MarkdownRenderer.Accessibility.MarkdownSemanticRole.Heading);

        Assert.Equal(3, heading.HeadingLevel);
        Assert.Contains("Browser & Automation", snapshot.SemanticDocument.GetText(heading), StringComparison.Ordinal);
    }

    [Fact]
    public void CollapsedDisclosureSummaryRetainsPreformattedCodeWithoutDuplicatingItsText()
    {
        const string source = "<details><summary>What does this print?<div><pre><code>echo one\necho two</code></pre></div></summary><p>Hidden answer</p></details>";

        using LayoutSnapshot snapshot = Build(source, SafeHtmlOptions.Default);
        LinkRun disclosure = Assert.Single(
            FlattenRuns(snapshot).OfType<LinkRun>(),
            static run => run.DisclosureId is not null);
        CodeBlockBox code = Assert.Single(FlattenBoxes(snapshot).OfType<CodeBlockBox>());
        MarkdownRenderer.Accessibility.MarkdownSemanticNode semantic = Assert.Single(
            MarkdownRenderer.Accessibility.MarkdownSemanticDocument
                .EnumerateDepthFirst(snapshot.SemanticDocument.Root),
            static node => node.Role == MarkdownRenderer.Accessibility.MarkdownSemanticRole.CodeBlock);
        string rendered = snapshot.SemanticDocument.Text;

        Assert.Equal("What does this print?", disclosure.AccessibilityName);
        Assert.False(disclosure.IsExpanded);
        Assert.Equal("echo one\necho two", code.CodeText);
        Assert.Equal("echo one\necho two", snapshot.SemanticDocument.GetText(semantic));
        Assert.Equal(1, rendered.Split("echo one", StringSplitOptions.None).Length - 1);
        Assert.DoesNotContain("Hidden answer", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public void PercentageImagesThatFitShareTheSameTableCellLine()
    {
        const string source = """
            | Name | License | Demo |
            | --- | --- | --- |
            | Example | MIT | <img src="first.gif" alt="" width="46%"> <img src="second.gif" alt="" width="46%"> |
            """;

        using LayoutSnapshot snapshot = BuildGitHub(source);
        InlineContainerBox cell = FlattenBoxes(snapshot)
            .OfType<InlineContainerBox>()
            .Single(box => box.Runs.OfType<InlineImageRun>().Count() == 2);
        var images = cell.EnumerateInlineImageRects().ToArray();

        Assert.Equal(2, images.Length);
        double verticalDelta = System.Math.Abs(images[0].Rect.Y - images[1].Rect.Y);
        Assert.True(
            verticalDelta <= 0.5,
            $"Expected same line; cell={cell.Bounds}, first={images[0].Rect}, second={images[1].Rect}.");
        Assert.True(images[0].Rect.Right <= images[1].Rect.Left);
        Assert.True(images[1].Rect.Right <= cell.Bounds.Right + 0.5);
    }

    [Fact]
    public void GitHubImageEmojiUsesTheNormalSizedInlineImagePipeline()
    {
        const string source = "Native :rocket: emoji and custom :octocat: image.";

        using LayoutSnapshot snapshot = BuildGitHub(source);
        InlineContainerBox paragraph = Assert.Single(snapshot.Blocks.OfType<InlineContainerBox>());
        InlineImageRun image = Assert.Single(paragraph.Runs.OfType<InlineImageRun>());
        var placement = Assert.Single(paragraph.EnumerateInlineImageRects());

        Assert.Equal(":octocat:", image.AltText);
        Assert.Equal(
            "https://github.githubassets.com/images/icons/emoji/octocat.png",
            image.Url);
        Assert.Equal(20, placement.Rect.Width, precision: 1);
        Assert.Equal(20, placement.Rect.Height, precision: 1);
        Assert.Contains(
            paragraph.Runs.OfType<TextRun>(),
            static run => run.Text.Contains("🚀", StringComparison.Ordinal));
    }

    [Fact]
    public void GitHubInlineSvgUsesTheIsolatedSvgImagePipeline()
    {
        const string source = """
            <a href="https://applitools.example/">
            <svg width="170" height="32" viewBox="0 0 170 32" xmlns="http://www.w3.org/2000/svg">
              <title>Applitools</title>
              <path d="M0 0h170v32H0z" fill="#00A298"></path>
            </svg>
            </a>
            """;

        using LayoutSnapshot snapshot = Build(source, SafeHtmlOptions.Default);
        InlineRun[] runs = FlattenRuns(snapshot).ToArray();

        InlineImageRun image = Assert.Single(runs.OfType<InlineImageRun>());
        Assert.StartsWith("data:image/svg+xml;base64,", image.Url, StringComparison.Ordinal);
        Assert.Equal("Applitools", image.AltText);
        Assert.Equal("https://applitools.example/", image.LinkUrl);
        Assert.Equal(170, image.Image.MeasuredImageWidth);
        string markup = System.Text.Encoding.UTF8.GetString(
            Convert.FromBase64String(image.Url[(image.Url.IndexOf(',') + 1)..]));
        Assert.Contains("<path", markup, StringComparison.Ordinal);
        Assert.DoesNotContain("<svg", string.Concat(runs.Select(static run => run.Text)));
    }

    [Fact]
    public void GitHubDetailsAndSummaryKeepNestedMarkdownReadable()
    {
        const string source = """
            <details open>
            <summary>Installation notes</summary>

            Use **stable** packages and review the table below.

            | Channel | Status |
            | --- | --- |
            | Stable | Ready |

            </details>
            """;

        using LayoutSnapshot snapshot = Build(source, SafeHtmlOptions.Default);
        string rendered = string.Concat(FlattenRuns(snapshot).Select(static run => run.Text));

        Assert.Contains("Installation notes", rendered, StringComparison.Ordinal);
        Assert.Contains("stable", rendered, StringComparison.Ordinal);
        Assert.Contains("Stable", rendered, StringComparison.Ordinal);
        Assert.Contains("Ready", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("<details", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("<summary", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public void GitHubProfileParsesMarkdownInsidePresentationDiv()
    {
        const string source = """
            <div align="center">

            ### Homepage - [example](https://example.test)

            [![Community](badge.png)](https://example.test/community)

            </div>
            """;

        using LayoutSnapshot snapshot = BuildGitHub(source);
        InlineRun[] runs = FlattenRuns(snapshot).ToArray();

        Assert.Contains(runs, static run => run is LinkRun { Text: "example" });
        InlineImageRun image = Assert.Single(runs.OfType<InlineImageRun>());
        Assert.Equal("badge.png", image.Url);
        Assert.Equal("https://example.test/community", image.LinkUrl);
        Assert.DoesNotContain("[![", string.Concat(runs.Select(static run => run.Text)));
    }

    [Fact]
    public void GitHubProfileResolvesReferenceImagesInsidePresentationDiv()
    {
        const string source = """
            <div align="center">

            [![Community]][community-destination]
            [![Source]][source-destination]

            [Community]: community.png (Community)
            [Source]: source.png (Source)
            [community-destination]: https://example.test/community
            [source-destination]: https://example.test/source

            </div>
            """;

        using LayoutSnapshot snapshot = BuildGitHub(source);
        InlineImageRun[] images = FlattenRuns(snapshot).OfType<InlineImageRun>().ToArray();

        Assert.Equal(2, images.Length);
        Assert.Equal("community.png", images[0].Url);
        Assert.Equal("https://example.test/community", images[0].LinkUrl);
        Assert.Equal("source.png", images[1].Url);
        Assert.Equal("https://example.test/source", images[1].LinkUrl);
    }

    [Fact]
    public void GitHubProfileNestedPresentationDivPreservesOuterReferenceDefinitionOrder()
    {
        const string source = """
            [Shared]: before.png

            <div align="center">
            [![Shared]][shared-link]
            [![Late]][late-link]
            </div>

            [Shared]: inside.png
            [shared-link]: https://example.test/shared
            [Late]: after.png
            [late-link]: https://example.test/late
            """;

        using LayoutSnapshot snapshot = BuildGitHub(source);
        InlineImageRun[] images = FlattenRuns(snapshot).OfType<InlineImageRun>().ToArray();

        Assert.Equal(2, images.Length);
        Assert.Equal("before.png", images[0].Url);
        Assert.Equal("https://example.test/shared", images[0].LinkUrl);
        Assert.Equal("after.png", images[1].Url);
        Assert.Equal("https://example.test/late", images[1].LinkUrl);
    }

    [Theory]
    [InlineData("[![Badge]][Badge Link]", "badge", true)]
    [InlineData("[![Badge]][Badge Link]", "badge link", true)]
    [InlineData("[![Badge]][Badge Link]", "unrelated", false)]
    [InlineData("[Badge  Link]", "Badge Link", true)]
    [InlineData("[Badge\\ Link]", "Badge Link", true)]
    [InlineData("[Badge&amp;Link]", "Badge&Link", true)]
    [InlineData("[Étiquette]", "étiquette", true)]
    public void NestedHtmlReferenceFilterExcludesOnlyProvablyAbsentSimpleLabels(
        string fragment,
        string label,
        bool expected)
    {
        bool simpleFragment = MarkdownRenderer.Html.Renderers.HtmlBlockRenderer
            .HasSimpleAsciiReferenceLabels(fragment);
        Assert.Equal(expected,
            MarkdownRenderer.Html.Renderers.HtmlBlockRenderer.MayUseReferenceLabel(
                fragment, label, simpleFragment));
    }

    [Fact]
    public void NestedHtmlReferenceWithManyUnrelatedDefinitionsStillResolves()
    {
        var source = new System.Text.StringBuilder(
            "<div>\n[![Used]][target]\n</div>\n\n" +
            "[Used]: badge.png\n[target]: https://example.test/target\n");
        for (int index = 0; index < 512; index++)
            source.Append("[Unrelated").Append(index).Append("]: https://example.test/")
                .Append(index).Append('\n');

        using LayoutSnapshot snapshot = BuildGitHub(source.ToString());
        InlineImageRun image = Assert.Single(FlattenRuns(snapshot).OfType<InlineImageRun>());
        Assert.Equal("badge.png", image.Url);
        Assert.Equal("https://example.test/target", image.LinkUrl);
    }

    [Fact]
    public void VoidHtmlBlockBlankLineMarkdownTailRetainsHeadingAndSourceSpan()
    {
        const string source = "<hr>\r\n  \r\n## Following heading\r\n";
        using MarkdownEngine engine = new MarkdownEngineBuilder().UseGitHubReadme().Build();
        var registry = Assert.IsType<MarkdownExtensionRegistry>(engine.PresentationConfiguration);
        Markdig.Syntax.MarkdownDocument parsed = Markdown.Parse(
            source,
            registry.BuildPipeline());
        Assert.Contains(parsed.OfType<Markdig.Syntax.HtmlBlock>(),
            static block => block.Lines.ToString().Contains("## Following heading", StringComparison.Ordinal));

        using LayoutSnapshot snapshot = Build(source, registry, parsedDocument: parsed);
        MarkdownRenderer.Accessibility.MarkdownSemanticNode heading = Assert.Single(
            MarkdownRenderer.Accessibility.MarkdownSemanticDocument
                .EnumerateDepthFirst(snapshot.SemanticDocument.Root),
            static node => node.Role == MarkdownRenderer.Accessibility.MarkdownSemanticRole.Heading);
        Assert.Equal(2, heading.HeadingLevel);
        Assert.Equal("Following heading", snapshot.SemanticDocument.GetText(heading));
        TextRun text = Assert.Single(FlattenRuns(snapshot).OfType<TextRun>(),
            static run => run.Text.Contains("Following heading", StringComparison.Ordinal));
        Assert.Equal(source.IndexOf("Following heading", StringComparison.Ordinal), text.SourceSpan.Start);
    }

    [Fact]
    public async Task GitHubProfileResolvesNumericReferenceImagesInLongPresentationDiv()
    {
        string source = """
            <p align="center"><img src="https://massgrave.dev/img/logo_small.png" alt="MAS Logo"></p>

            <h1 align="center">Microsoft  Activation  Scripts (MAS)</h1>

            <p align="center">Open-source Windows and Office activator featuring HWID, Ohook, TSforge, and Online KMS activation methods, along with advanced troubleshooting.</p>

            <hr>
            {{SPACE2}}
            ## How to Activate Windows / Office / Extended Security Updates (ESU)?

            ### Method 1 - PowerShell ❤️

            1. Click the **Start Menu**, type `PowerShell`, and open it.

            2. Copy and paste the code below and press **Enter.**{{SPACE2}}
               - For **Windows 8.1, 10 and 11**:
                 ```
                 irm https://get.activated.win | iex
                 ```
            {{TAB}} If the above is blocked (by ISP/DNS), try this (needs updated Windows 10 or 11):{{SPACE2}}
            {{TAB}} ```
            {{TAB}} iex (curl.exe -s --doh-url https://1.1.1.1/dns-query https://get.activated.win | Out-String)
            {{TAB}} ```
            {{TAB}}- **Script not launching? Use the below-listed Method 2.**

            3. In the menu that appears, type the number corresponding to one of the **Green** options.

            ---

            ### Method 2 - Traditional (Windows Vista and later)

            1.   Download the script:
                  *   [**MAS_AIO.cmd**](https://dev.azure.com/massgrave/Microsoft-Activation-Scripts/_apis/git/repositories/Microsoft-Activation-Scripts/items?path=/MAS/All-In-One-Version-KL/MAS_AIO.cmd&download=true) (Direct script)
                  *   [**MAS_AIO.zip**](https://dev.azure.com/massgrave/Microsoft-Activation-Scripts/_apis/git/repositories/Microsoft-Activation-Scripts/items?$format=zip) (If the direct script is blocked by your browser)
            2.   Run the `MAS_AIO.cmd` file.
            3.   In the menu that appears, type the number corresponding to one of the **Green** options.

            ---

            > [!TIP]
            > - Some ISPs/DNS providers block access to our domains. You can bypass this by enabling [DNS-over-HTTPS (DoH)](https://developers.cloudflare.com/1.1.1.1/encryption/dns-over-https/encrypted-dns-browsers/) in your browser.{{SPACE1}}
            > - **Having trouble**? Visit our [troubleshooting page](https://massgrave.dev/troubleshoot) or raise an issue on [GitHub](https://github.com/massgravel/Microsoft-Activation-Scripts/issues).

            > [!NOTE]
            >
            > - The `irm` command in PowerShell downloads a script from a specified URL, and the `iex` command executes it.
            > - Always double-check the URL before executing the command and verify the source is trustworthy when manually downloading files.
            > - Be cautious of third parties spreading malware disguised as MAS by altering the URL in the PowerShell command.

            ---

            <div align="center">
            {{TAB}}
            ### Homepage - [https://massgrave.dev/](https://massgrave.dev/)
            {{SPACE2}}
            [![1.1]][1]
            [![1.2]][2]
            [![1.3]][3]
            [![1.4]][4]
            [![1.5]][5]
            [![1.6]][6]
            [![1.7]][7]

            [1.1]: https://massgrave.dev/img/logo_discord.png (Chat with us without signup)
            [1.2]: https://massgrave.dev/img/logo_reddit.png (Reddit)
            [1.3]: https://massgrave.dev/img/logo_bluesky.png (Bluesky)
            [1.4]: https://massgrave.dev/img/logo_x.png (Twitter)

            [1.5]: https://massgrave.dev/img/logo_github.png (GitHub)
            [1.6]: https://massgrave.dev/img/logo_azuredevops.png (AzureDevOps)
            [1.7]: https://massgrave.dev/img/logo_gitea.png (Self-hosted Git)

            [1]: https://discord.gg/j2yFsV5ZVC
            [2]: https://www.reddit.com/r/MAS_Activator
            [3]: https://bsky.app/profile/massgrave.dev
            [4]: https://twitter.com/massgravel
            [5]: https://github.com/massgravel/Microsoft-Activation-Scripts
            [6]: https://dev.azure.com/massgrave/_git/Microsoft-Activation-Scripts
            [7]: https://git.activated.win/Microsoft-Activation-Scripts

            ---

            Latest Version: 3.12{{SPACE2}}
            Release date: 04-Jul-2026
            """;

        source = source
            .Replace("{{TAB}}", "\t", StringComparison.Ordinal)
            .Replace("{{SPACE1}}", " ", StringComparison.Ordinal)
            .Replace("{{SPACE2}}", "  ", StringComparison.Ordinal)
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Replace("\n", "\r\n", StringComparison.Ordinal) + "\r\n";
        Assert.Equal(3559, System.Text.Encoding.UTF8.GetByteCount(source));
        Assert.Equal(
            "1C0839E4B5CFBC5E02C6192F81227BAA6B3DE334F0DEE32D727C37D3BC5F7B38",
            Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(source))));

        using MarkdownEngine engine = new MarkdownEngineBuilder()
            .UseGitHubReadme()
            .UseMathematics()
            .UseMermaid()
            .Build();
        using MarkdownPerformanceSession session =
            new(MarkdownPerformanceOptions.Progressive);
        MarkdownRenderer.Document.MarkdownDocument parsed = Assert.IsType<MarkdownRenderer.Document.MarkdownDocument>(
            await ((IMarkdownPerformanceSessionInternal)session).ParseAndPrepareDocumentAsync(
                engine,
                null,
                source,
                new MarkdownExtensionRegistry(),
                new object(),
                CancellationToken.None));
        string presentationHtmlBlock = Assert.Single(
            parsed.ParsedDocument!
                .OfType<Markdig.Syntax.HtmlBlock>()
                .Select(static block => block.Lines.ToString()),
            static html => html.StartsWith("<div align=\"center\">", StringComparison.Ordinal));
        Assert.Contains("### Homepage - [https://massgrave.dev/](https://massgrave.dev/)", presentationHtmlBlock);
        Assert.Contains("[![1.1]][1]", presentationHtmlBlock);
        Assert.DoesNotContain("[1.1]: https://massgrave.dev/img/logo_discord.png", presentationHtmlBlock);

        var registry = Assert.IsType<MarkdownExtensionRegistry>(engine.PresentationConfiguration);
        using LayoutSnapshot snapshot = Build(source, registry, parsedDocument: parsed.ParsedDocument);
        InlineImageRun[] images = FlattenRuns(snapshot).OfType<InlineImageRun>().ToArray();
        string[] headings = MarkdownRenderer.Accessibility.MarkdownSemanticDocument
            .EnumerateDepthFirst(snapshot.SemanticDocument.Root)
            .Where(static node => node.Role == MarkdownRenderer.Accessibility.MarkdownSemanticRole.Heading)
            .Select(snapshot.SemanticDocument.GetText)
            .ToArray();

        Assert.Equal(8, images.Length);
        Assert.Equal(5, headings.Length);
        Assert.Contains("How to Activate Windows / Office / Extended Security Updates (ESU)?", headings);
        Assert.Equal("https://massgrave.dev/img/logo_small.png", images[0].Url);
        string[] expectedBadgeImages =
        [
            "https://massgrave.dev/img/logo_discord.png",
            "https://massgrave.dev/img/logo_reddit.png",
            "https://massgrave.dev/img/logo_bluesky.png",
            "https://massgrave.dev/img/logo_x.png",
            "https://massgrave.dev/img/logo_github.png",
            "https://massgrave.dev/img/logo_azuredevops.png",
            "https://massgrave.dev/img/logo_gitea.png",
        ];
        string[] expectedBadgeLinks =
        [
            "https://discord.gg/j2yFsV5ZVC",
            "https://www.reddit.com/r/MAS_Activator",
            "https://bsky.app/profile/massgrave.dev",
            "https://twitter.com/massgravel",
            "https://github.com/massgravel/Microsoft-Activation-Scripts",
            "https://dev.azure.com/massgrave/_git/Microsoft-Activation-Scripts",
            "https://git.activated.win/Microsoft-Activation-Scripts",
        ];
        for (int index = 0; index < 7; index++)
        {
            Assert.Equal(expectedBadgeImages[index], images[index + 1].Url);
            Assert.Equal(expectedBadgeLinks[index], images[index + 1].LinkUrl);
        }

        using LayoutSnapshot lazySnapshot = Build(
            source,
            registry,
            parsedDocument: parsed.ParsedDocument,
            lazy: true,
            availableWidth: 623);
        Assert.Equal(
            8,
            lazySnapshot.GetMeasuredTopLevelBlocks()
                .SelectMany(FlattenRuns)
                .OfType<InlineImageRun>()
                .Count());
    }

    [Fact]
    public void GitHubProfileKeepsCollapsedDetailsBodiesOutOfLayout()
    {
        string source = string.Join(
            "\n\n",
            Enumerable.Range(0, 200).Select(index => $$"""
                <details>
                <summary>Section {{index}}</summary>

                Hidden body {{index}} with ![deferred](hidden-{{index}}.png).

                </details>
                """));

        using LayoutSnapshot snapshot = BuildGitHub(source);
        InlineRun[] runs = FlattenRuns(snapshot).ToArray();

        Assert.Equal(200, runs.OfType<LinkRun>().Count(static run => run.DisclosureId is not null));
        Assert.DoesNotContain(runs, static run => run is InlineImageRun);
        Assert.DoesNotContain("Hidden body", string.Concat(runs.Select(static run => run.Text)));
    }

    [Fact]
    public void GitHubProfileClosesBlockDetailsFromInlineFormattedClosingTags()
    {
        string source = string.Join(
            "\n\n",
            Enumerable.Range(0, 32).Select(index => $$"""
                <details>
                <summary>Section {{index}}</summary><br><b>

                Hidden body {{index}} with ![deferred](hidden-{{index}}.png).

                </b></details>
                """));
        var options = new SafeHtmlOptions(
            budgets: new SafeHtmlBudgets(maxNestingDepth: 16));

        using LayoutSnapshot snapshot = Build(source, options, new TestStringProvider());
        InlineRun[] runs = FlattenRuns(snapshot).ToArray();
        string rendered = string.Concat(runs.Select(static run => run.Text));

        Assert.Equal(32, runs.OfType<LinkRun>().Count(static run => run.DisclosureId is not null));
        Assert.DoesNotContain(runs, static run => run is InlineImageRun);
        Assert.DoesNotContain("Hidden body", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("omitted", rendered, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void GitHubProfileClosesDetailsFromTagsNestedInsideListContainers()
    {
        string source = string.Join(
            "\n\n",
            Enumerable.Range(0, 32).Select(index => $$"""
                <details>
                <summary>Section {{index}}:

                  * First item
                  * Second item</summary><br><b>

                Hidden answer
                </b></details>
                """));
        var options = new SafeHtmlOptions(
            budgets: new SafeHtmlBudgets(maxNestingDepth: 16));

        using LayoutSnapshot snapshot = Build(source, options, new TestStringProvider());
        InlineRun[] runs = FlattenRuns(snapshot).ToArray();
        string rendered = string.Concat(runs.Select(static run => run.Text));

        Assert.Equal(32, runs.OfType<LinkRun>().Count(static run => run.DisclosureId is not null));
        Assert.Contains("First item", rendered, StringComparison.Ordinal);
        Assert.Contains("Second item", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("Hidden answer", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("omitted", rendered, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void GitHubProfileKeepsSummaryImagesVisibleWhenDetailsAreCollapsed()
    {
        const string source = """
            <details><summary>Inspect this diagram<br>
            <img src="diagram.png" alt="Architecture" width="500" height="350">
            </summary><p>Hidden explanation</p></details>
            """;

        SafeHtmlDocument parsed = SafeHtmlParser.Parse(source);
        SafeHtmlElement details = Assert.IsType<SafeHtmlElement>(Assert.Single(parsed.Root.Children));
        SafeHtmlElement summary = Assert.Single(
            details.Children.OfType<SafeHtmlElement>(),
            static element => element.Name == "summary");
        Assert.Contains(
            summary.Children.OfType<SafeHtmlElement>(),
            static element => element.Name == "img");

        using LayoutSnapshot snapshot = BuildGitHub(source);
        InlineRun[] runs = FlattenRuns(snapshot).ToArray();

        LinkRun disclosure = Assert.Single(
            runs.OfType<LinkRun>(),
            static run => run.DisclosureId is not null);
        Assert.Equal("Inspect this diagram", disclosure.AccessibilityName);
        InlineImageRun image = Assert.Single(runs.OfType<InlineImageRun>());
        Assert.Equal("diagram.png", image.Url);
        Assert.Equal("Architecture", image.AltText);
        Assert.DoesNotContain(
            "Hidden explanation",
            string.Concat(runs.Select(static run => run.Text)),
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DetailsSummaryTransfersDecorativeSpacersWithoutAddingAccessibleText(bool githubProfile)
    {
        const string source = """
            <details><summary>Before<img alt='' width='16' height='1'>After</summary>
            <p>Hidden body</p></details>
            """;

        using LayoutSnapshot snapshot = githubProfile
            ? BuildGitHub(source)
            : Build(source, SafeHtmlOptions.Default);
        InlineSpacerRun spacer = Assert.Single(FlattenRuns(snapshot).OfType<InlineSpacerRun>());
        LinkRun disclosure = Assert.Single(
            FlattenRuns(snapshot).OfType<LinkRun>(),
            static run => run.DisclosureId is not null);
        InlineContainerBox owner = Assert.Single(
            FlattenBoxes(snapshot).OfType<InlineContainerBox>(),
            box => box.Runs.Contains(spacer));

        Assert.Equal("BeforeAfter", disclosure.AccessibilityName);
        Assert.Empty(FlattenRuns(snapshot).OfType<InlineImageRun>());
        Assert.Equal(16f, spacer.Width);
        Assert.InRange(owner.GetRunRect(spacer.InlineIndex).Width, 15.5, 16.5);
        Assert.DoesNotContain("\uFFFC", snapshot.SemanticDocument.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("Hidden body", snapshot.SemanticDocument.Text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("\n", false)]
    [InlineData("\r\n", false)]
    [InlineData("\n", true)]
    [InlineData("\r\n", true)]
    public void GitHubSummaryImageMapsToItsExactAuthoredTag(string newline, bool multiline)
    {
        const string imageTag = "<img src=\"diagram.png\" alt=\"Architecture\" width=\"32\" height=\"16\">";
        string source = "# Prefix 👩‍💻" + newline + newline +
            (multiline
                ? "<details>" + newline + "<summary>Inspect this diagram" + newline +
                  "  " + imageTag + newline + "</summary>"
                : "<details><summary>Inspect this diagram " + imageTag + "</summary>") +
            newline + newline + "Hidden body." + newline + newline + "</details>";

        using LayoutSnapshot snapshot = BuildGitHub(source);
        InlineImageRun image = Assert.Single(FlattenRuns(snapshot).OfType<InlineImageRun>());

        Assert.Equal("diagram.png", image.Url);
        Assert.Equal("Architecture", image.AltText);
        Assert.Equal(new SourceSpan(source.IndexOf(imageTag, StringComparison.Ordinal), imageTag.Length),
            image.SourceSpan);
        Assert.Equal(imageTag, source.Substring(image.SourceSpan.Start, image.SourceSpan.Length));
        AssertSummaryImageSourceCopy(snapshot, image, imageTag);
    }

    [Theory]
    [InlineData("\n", false)]
    [InlineData("\r\n", false)]
    [InlineData("\n", true)]
    [InlineData("\r\n", true)]
    public void GitHubSummaryRepeatedImagesKeepTheirOwnSourcesInsideMultilineHtml(
        string newline, bool quoted)
    {
        const string imageTag = "<img src='diagram.png' alt='Repeated' width='32' height='16'>";
        string details = string.Join(newline,
            "<details>", "<summary>Images", "<div>", "  " + imageTag,
            "    " + imageTag, "</div>", "</summary>", "", "Hidden body.", "", "</details>");
        if (quoted)
            details = string.Join(newline, details.Split(newline).Select(static line => "> " + line));
        string source = "# Prefix 👩‍💻" + newline + newline + details;

        using LayoutSnapshot snapshot = BuildGitHub(source);
        InlineImageRun[] images = FlattenRuns(snapshot).OfType<InlineImageRun>().ToArray();
        Assert.Equal(2, images.Length);
        int first = source.IndexOf(imageTag, StringComparison.Ordinal);
        int second = source.LastIndexOf(imageTag, StringComparison.Ordinal);
        Assert.True(second > first);
        Assert.Equal(new[] { new SourceSpan(first, imageTag.Length), new SourceSpan(second, imageTag.Length) },
            images.Select(static image => image.SourceSpan).ToArray());
        foreach (InlineImageRun image in images)
            AssertSummaryImageSourceCopy(snapshot, image, imageTag);
    }

    [Theory]
    [InlineData("\n", false)]
    [InlineData("\r\n", false)]
    [InlineData("\n", true)]
    [InlineData("\r\n", true)]
    public void GitHubSummaryMultilineImageTagCopiesItsExactAuthoredSource(
        string newline, bool quoted)
    {
        string details = string.Join(newline,
            "<details>", "<summary>Images", "<div>", "  <img",
            "    src='diagram.png'", "    alt='Multiline'", "    width='32' height='16'>",
            "</div>", "</summary>", "", "Hidden body.", "", "</details>");
        if (quoted)
            details = string.Join(newline, details.Split(newline).Select(static line => "> " + line));
        string source = "# Prefix 👩‍💻" + newline + newline + details;
        int imageStart = source.IndexOf("<img", StringComparison.Ordinal);
        const string imageEnding = "width='32' height='16'>";
        int imageEnd = source.IndexOf(imageEnding, imageStart, StringComparison.Ordinal) + imageEnding.Length;
        string expectedSource = source[imageStart..imageEnd];

        using LayoutSnapshot snapshot = BuildGitHub(source);
        InlineImageRun image = Assert.Single(FlattenRuns(snapshot).OfType<InlineImageRun>());

        Assert.Equal("diagram.png", image.Url);
        Assert.Equal("Multiline", image.AltText);
        Assert.Equal(new SourceSpan(imageStart, imageEnd - imageStart), image.SourceSpan);
        AssertSummaryImageSourceCopy(snapshot, image, expectedSource);
    }

    private static void AssertSummaryImageSourceCopy(
        LayoutSnapshot snapshot, InlineImageRun image, string expectedSource)
    {
        InlineContainerBox owner = Assert.Single(FlattenBoxes(snapshot).OfType<InlineContainerBox>(),
            candidate => candidate.Runs.Contains(image));
        var range = new DocumentRange(
            new DocumentPosition(owner.BlockIndex, image.InlineIndex, 0),
            new DocumentPosition(owner.BlockIndex, image.InlineIndex, 1));
        Assert.Equal(expectedSource, snapshot.SourceMap.Slice(range));
    }

    [Fact]
    public void CollapsedDetailsInsideListItemsDoNotLayoutOrLoadTheirBodies()
    {
        const string source = """
            - [AutoMute](https://example.test/automute) - Automatically mute audio.

              **Languages:** Objective-C

              <details>
              <summary>Screenshots</summary>
              <p>

              Hidden body marker.

              <img src="https://example.test/hidden.png" alt="Hidden screenshot" width="400">

              </p>
              </details>

            - Next visible item
            """;

        using LayoutSnapshot snapshot = BuildGitHub(source);
        InlineRun[] runs = FlattenRuns(snapshot).ToArray();
        LinkRun disclosure = Assert.Single(
            runs.OfType<LinkRun>(),
            static run => run.DisclosureId is not null);

        Assert.Equal("Screenshots", disclosure.AccessibilityName);
        Assert.False(disclosure.IsExpanded);
        Assert.DoesNotContain(runs, static run => run is InlineImageRun);
        string text = string.Concat(runs.Select(static run => run.Text));
        Assert.DoesNotContain("Hidden body marker", text, StringComparison.Ordinal);
        Assert.Contains("Next visible item", text, StringComparison.Ordinal);
    }

    [Fact]
    public void DerivedEngineSafeHtmlOptionsReplaceTheExistingFeatureRenderer()
    {
        const string source =
            "<p><a href='https://example.com'>linked</a> " +
            "<img src='https://example.com/image.png' alt='diagram'></p>";
        using MarkdownEngine original = new MarkdownEngineBuilder()
            .UseGitHubReadme()
            .Build();
        using MarkdownEngine derived = original.ToBuilder()
            .UseSafeHtml(new SafeHtmlOptions(enableLinks: false, enableImages: false))
            .Build();
        var registry = Assert.IsType<MarkdownExtensionRegistry>(derived.PresentationConfiguration);

        using LayoutSnapshot snapshot = Build(source, registry);

        AssertDisabledLinkAndImageFallback(snapshot);
    }

    [Fact]
    public void PerViewSafeHtmlOptionsReplaceTheExistingGitHubFeatureRenderer()
    {
        const string source =
            "<p><a href='https://example.com'>linked</a> " +
            "<img src='https://example.com/image.png' alt='diagram'></p>";
        using MarkdownEngine engine = new MarkdownEngineBuilder()
            .UseGitHubReadme()
            .Build();
        MarkdownExtensionRegistry registry = GitHubReadmeExtensions.GetGitHubReadmeRegistry(
            engine,
            viewRegistry: null,
            new SafeHtmlOptions(enableLinks: false, enableImages: false));

        using LayoutSnapshot snapshot = Build(source, registry);

        AssertDisabledLinkAndImageFallback(snapshot);
    }

    [Fact]
    public void ExistingStrictSafeHtmlOptionsSurviveFollowingGitHubDefaults()
    {
        const string source =
            "<p><a href='https://example.com'>linked</a> " +
            "<img src='https://example.com/image.png' alt='diagram'></p>";
        using MarkdownEngine engine = new MarkdownEngineBuilder()
            .UseSafeHtml(new SafeHtmlOptions(enableLinks: false, enableImages: false))
            .UseGitHubReadme()
            .Build();
        var registry = Assert.IsType<MarkdownExtensionRegistry>(engine.PresentationConfiguration);

        using LayoutSnapshot snapshot = Build(source, registry);

        AssertDisabledLinkAndImageFallback(snapshot);
        SafeHtmlRenderPolicy policy = Assert.IsType<SafeHtmlRenderPolicy>(registry.SafeHtmlPolicy);
        Assert.False(policy.EnableLinks);
        Assert.False(policy.EnableImages);
    }

    [Fact]
    public void DisabledLinksAndImagesRemainReadableButNonInteractive()
    {
        const string source =
            "<p><a href='https://example.com'>linked</a> " +
            "<img src='https://example.com/image.png' alt='diagram'></p>";
        var options = new SafeHtmlOptions(enableLinks: false, enableImages: false);

        using LayoutSnapshot snapshot = Build(source, options);
        InlineRun[] runs = FlattenRuns(snapshot).ToArray();

        Assert.DoesNotContain(runs, static run => run is LinkRun);
        Assert.DoesNotContain(runs, static run => run is InlineImageRun);
        Assert.Contains("linked", string.Concat(runs.Select(static run => run.Text)), StringComparison.Ordinal);
        Assert.Contains("diagram", string.Concat(runs.Select(static run => run.Text)), StringComparison.Ordinal);
    }

    [Fact]
    public void InlineHtmlUsesTheSameLinkImageAndClassPolicyAsHtmlBlocks()
    {
        const string source =
            "before <a class='approved rejected' href='https://example.com'>linked</a> " +
            "<img src='https://example.com/image.png' alt='diagram'> after";
        var options = new SafeHtmlOptions(
            enableLinks: false,
            enableImages: false,
            allowedStyleClasses: ["approved"]);

        using LayoutSnapshot snapshot = Build(source, options);
        InlineRun[] runs = FlattenRuns(snapshot).ToArray();
        InlineRun linked = Assert.Single(runs, static run => run.Text == "linked");

        Assert.DoesNotContain(runs, static run => run is LinkRun or InlineImageRun);
        Assert.Contains(MarkdownElementKeys.Class("approved"), linked.StyleAliases);
        Assert.DoesNotContain(MarkdownElementKeys.Class("rejected"), linked.StyleAliases);
        Assert.Contains("diagram", string.Concat(runs.Select(static run => run.Text)), StringComparison.Ordinal);
    }

    [Fact]
    public void HtmlBlockCollapsesWhitespaceAcrossInlineFormattingBoundaries()
    {
        const string source =
            "<p align='center'><strong>Codex CLI</strong> is a coding agent. " +
            "For an IDE, <a href='https://example.test/install'>install the extension</a>." +
            "<br>If you prefer, run <code>codex app</code> or visit " +
            "<a href='https://example.test/app'>the Codex App page</a>.</p>";

        using LayoutSnapshot snapshot = Build(source, SafeHtmlOptions.Default);
        string rendered = string.Concat(FlattenRuns(snapshot).Select(static run => run.Text));

        Assert.Equal(
            "Codex CLI is a coding agent. For an IDE, install the extension.\n" +
            "If you prefer, run codex app or visit the Codex App page.",
            rendered);
        Assert.Equal(2, FlattenRuns(snapshot).OfType<LinkRun>().Count());
    }

    [Fact]
    public void GitHubHeadingPermalinkAriaHiddenSvgIsNotRenderedAsLinkedImage()
    {
        const string source = """
            <div class="markdown-heading"><h2 id="user-content-help">Help</h2><a class="anchor" aria-label="Permalink: Help" href="#help"><svg viewBox="0 0 16 16" width="16" height="16" aria-hidden="true"><path d="M0 0h1v1z"></path></svg></a></div>
            """;

        using LayoutSnapshot snapshot = Build(source, SafeHtmlOptions.Default);
        InlineRun[] runs = FlattenRuns(snapshot).ToArray();

        Assert.Equal("Help", string.Concat(runs.Select(static run => run.Text)).Trim());
        Assert.DoesNotContain(runs, static run => run is InlineImageRun or LinkRun);
    }

    [Fact]
    public void MarkdownLinkContainingInlineHtmlImageRemainsAVisibleLinkedImage()
    {
        const string source =
            "[<img src=\"https://run.pstmn.io/button.svg\" alt=\"Run In Postman\" " +
            "style=\"width: 128px; height: 32px;\">](https://example.test/collection)";

        using LayoutSnapshot snapshot = Build(source, SafeHtmlOptions.Default);
        InlineImageRun image = Assert.Single(FlattenRuns(snapshot).OfType<InlineImageRun>());

        Assert.Equal("https://run.pstmn.io/button.svg", image.Url);
        Assert.Equal("Run In Postman", image.AltText);
        Assert.Equal("https://example.test/collection", image.LinkUrl);
        Assert.True(image.IsLinked);
        Assert.Equal(new SourceSpan(0, source.Length), image.SourceSpan);
    }

    [Fact]
    public void MarkdownLinkContainingHtmlImageAndTextPreservesBothLinkedContents()
    {
        const string source =
            "[<img src=\"doc/images/icons/AdvancedPaste.png\" alt=\"Advanced Paste icon\" width=\"32\"> " +
            "Advanced Paste](https://learn.microsoft.com/windows/powertoys/advanced-paste)";

        using LayoutSnapshot snapshot = Build(source, SafeHtmlOptions.Default);
        InlineRun[] runs = FlattenRuns(snapshot).ToArray();

        InlineImageRun image = Assert.Single(runs.OfType<InlineImageRun>());
        LinkRun label = Assert.Single(runs.OfType<LinkRun>());
        Assert.Equal("doc/images/icons/AdvancedPaste.png", image.Url);
        Assert.Equal("Advanced Paste icon", image.AltText);
        Assert.Equal("https://learn.microsoft.com/windows/powertoys/advanced-paste", image.LinkUrl);
        Assert.Equal(" Advanced Paste", label.Text);
        Assert.Equal(image.LinkUrl, label.Url);
    }

    [Fact]
    public void UnknownMarkupUsesExactLiteralFallbackIncludingSuppressedChildren()
    {
        const string source = "<custom DATA-x='v'>a<script>alert(1)</script></custom>";

        using LayoutSnapshot snapshot = Build(source, SafeHtmlOptions.Default);

        Assert.Equal(source, string.Concat(FlattenRuns(snapshot).Select(static run => run.Text)));
    }

    [Fact]
    public void StandaloneScriptIsVisibleAsExactSelectableInertSource()
    {
        const string source = "<script data-note='literal'>unsafe-script-sentinel()</script>";

        using LayoutSnapshot snapshot = Build(source, SafeHtmlOptions.Default);
        InlineRun[] runs = FlattenRuns(snapshot).ToArray();

        Assert.Equal(source, string.Concat(runs.Select(static run => run.Text)));
        Assert.All(runs, static run => Assert.IsType<TextRun>(run));
        Assert.Equal(source, string.Concat(runs.Select(run =>
            source.Substring(run.SourceSpan.Start, run.SourceSpan.Length))));
    }

    [Fact]
    public void ScriptLiteralStaysInOrderWithSurroundingLinkedHtmlWithoutBecomingALink()
    {
        const string rawScript = "<script>unsafe-script-sentinel()</script>";
        const string source = "<p><a href='https://example.test'>before" + rawScript + "after</a></p>";

        using LayoutSnapshot snapshot = Build(source, SafeHtmlOptions.Default);
        InlineRun[] runs = FlattenRuns(snapshot).ToArray();
        string rendered = string.Concat(runs.Select(static run => run.Text));

        Assert.Equal("before" + rawScript + "after", rendered);
        Assert.Contains(runs, static run => run is LinkRun { Text: "before" });
        Assert.Contains(runs, static run => run is LinkRun { Text: "after" });

        InlineRun[] scriptRuns = runs.Where(run =>
                run.SourceSpan.Start >= source.IndexOf(rawScript, StringComparison.Ordinal) &&
                run.SourceSpan.Start < source.IndexOf(rawScript, StringComparison.Ordinal) + rawScript.Length)
            .ToArray();
        Assert.NotEmpty(scriptRuns);
        Assert.All(scriptRuns, static run => Assert.IsType<TextRun>(run));
        Assert.Equal(rawScript, string.Concat(scriptRuns.Select(static run => run.Text)));
    }

    [Fact]
    public void ScriptLiteralBetweenHtmlBlocksDoesNotDropOrReorderSiblingText()
    {
        const string rawScript = "<script>unsafe-script-sentinel()</script>";
        const string source = "<p>before block</p>" + rawScript + "<p>after block</p>";

        using LayoutSnapshot snapshot = Build(source, SafeHtmlOptions.Default);
        string rendered = string.Concat(FlattenRuns(snapshot).Select(static run => run.Text));

        Assert.Equal("before block" + rawScript + "after block", rendered);
    }

    [Fact]
    public void UnknownMarkupCanKeepOnlySafeTextContent()
    {
        const string source = "<custom>visible<script>hidden()</script></custom>";
        var options = new SafeHtmlOptions(
            unknownElementBehavior: SafeHtmlUnknownElementBehavior.KeepTextContent);

        using LayoutSnapshot snapshot = Build(source, options);
        string rendered = string.Concat(FlattenRuns(snapshot).Select(static run => run.Text));

        Assert.Equal("visible", rendered);
    }

    [Fact]
    public void OnlyHostAllowlistedHtmlClassesReachStyleSelectors()
    {
        const string source = "<p><span class='approved rejected'>styled</span></p>";
        var options = new SafeHtmlOptions(allowedStyleClasses: ["approved"]);

        using LayoutSnapshot snapshot = Build(source, options);
        InlineRun run = Assert.Single(FlattenRuns(snapshot), static candidate => candidate.Text == "styled");

        Assert.Contains(MarkdownElementKeys.Class("approved"), run.StyleAliases);
        Assert.DoesNotContain(MarkdownElementKeys.Class("rejected"), run.StyleAliases);
    }

    [Fact]
    public void HostLoweredInputBudgetProducesLocalizedFallbackNoticeInsteadOfBlankOutput()
    {
        var options = new SafeHtmlOptions(
            budgets: new SafeHtmlBudgets(
                maxInputLength: 16,
                maxNodeCount: 20,
                maxNestingDepth: 8,
                maxAttributeCount: 4,
                maxAttributeValueLength: 16,
                maxTagLength: 16));

        using LayoutSnapshot snapshot = Build("<p>012345678901234567890123456789</p>", options);
        string rendered = string.Concat(FlattenRuns(snapshot).Select(static run => run.Text));

        Assert.Contains("omitted", rendered, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void HtmlVisibleFallbackTextUsesTheHostStringProvider()
    {
        var options = new SafeHtmlOptions(
            budgets: new SafeHtmlBudgets(
                maxInputLength: 16,
                maxNodeCount: 20,
                maxNestingDepth: 8,
                maxAttributeCount: 4,
                maxAttributeValueLength: 16,
                maxTagLength: 16));

        using LayoutSnapshot snapshot = Build(
            "<p>012345678901234567890123456789</p>",
            options,
            new TestStringProvider());

        Assert.Contains(
            "localized budget notice",
            string.Concat(FlattenRuns(snapshot).Select(static run => run.Text)),
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void InlineHtmlHonorsLoweredInputAndTagLimits(bool limitInput)
    {
        var options = new SafeHtmlOptions(budgets: new SafeHtmlBudgets(
            maxInputLength: limitInput ? 16 : 1024,
            maxTagLength: limitInput ? 1024 : 16));
        using LayoutSnapshot snapshot = Build(
            "prefix <a href='https://example.test/target'>link</a>", options,
            new TestStringProvider());

        InlineRun[] runs = FlattenRuns(snapshot).ToArray();
        Assert.DoesNotContain(runs, static run => run is LinkRun);
        Assert.Contains("localized budget notice", string.Concat(runs.Select(static run => run.Text)));
    }

    [Fact]
    public void InlineHtmlHonorsAttributeLimits()
    {
        var options = new SafeHtmlOptions(budgets: new SafeHtmlBudgets(
            maxAttributeCount: 1, maxAttributeValueLength: 4));
        using LayoutSnapshot snapshot = Build(
            "prefix <a title='long title' href='https://example.test'>link</a>", options);

        Assert.DoesNotContain(FlattenRuns(snapshot), static run => run is LinkRun);
        Assert.Contains("link", string.Concat(FlattenRuns(snapshot).Select(static run => run.Text)));
    }

    [Fact]
    public void DeepInlineHtmlStopsAtTheDefaultDepthCeiling()
    {
        string source = "prefix " + string.Concat(Enumerable.Repeat("<span>", 2_000)) + "unreachable";
        using LayoutSnapshot snapshot = Build(source, SafeHtmlOptions.Default, new TestStringProvider());

        string rendered = string.Concat(FlattenRuns(snapshot).Select(static run => run.Text));
        Assert.Contains("localized budget notice", rendered);
        Assert.DoesNotContain("unreachable", rendered);
    }

    [Fact]
    public void InlineDepthLimitAllowsTheExactBoundaryButRejectsAnotherScope()
    {
        var options = new SafeHtmlOptions(budgets: new SafeHtmlBudgets(maxNestingDepth: 2));
        using LayoutSnapshot allowed = Build("prefix <span><b>visible</b></span>", options);
        Assert.Contains("visible", string.Concat(FlattenRuns(allowed).Select(static run => run.Text)));

        using LayoutSnapshot rejected = Build("prefix <span><b><i>unreachable</i></b></span>", options);
        string rendered = string.Concat(FlattenRuns(rejected).Select(static run => run.Text));
        Assert.Contains("omitted", rendered);
        Assert.DoesNotContain("unreachable", rendered);
    }

    [Fact]
    public void InlineNodeBudgetCannotBeResetWithSiblingTags()
    {
        var options = new SafeHtmlOptions(budgets: new SafeHtmlBudgets(maxNodeCount: 3));
        using LayoutSnapshot snapshot = Build(
            "prefix <span>one</span><a href='https://example.test'>two</a>", options);

        InlineRun[] runs = FlattenRuns(snapshot).ToArray();
        Assert.DoesNotContain(runs, static run => run is LinkRun);
        Assert.Contains("omitted", string.Concat(runs.Select(static run => run.Text)));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void CrossBlockHtmlScopesShareTheConfiguredBudgets(bool limitDepth)
    {
        var options = new SafeHtmlOptions(budgets: new SafeHtmlBudgets(
            maxNestingDepth: limitDepth ? 2 : 64,
            maxInputLength: limitDepth ? 4096 : 32));
        const string source = "<div align='right'>\n\n<div>\n\n<div>\n\n<div>\n\nvisible markdown";
        using LayoutSnapshot snapshot = Build(source, options, new TestStringProvider());

        string rendered = string.Concat(FlattenRuns(snapshot).Select(static run => run.Text));
        Assert.Contains("localized budget notice", rendered);
        Assert.Contains("visible markdown", rendered);
        Assert.Equal(1, rendered.Split("localized budget notice").Length - 1);
    }

    private static LayoutSnapshot Build(
        string source,
        SafeHtmlOptions options,
        IMarkdownStringProvider? stringProvider = null,
        float textScaleFactor = 1f)
    {
        var registry = new MarkdownExtensionRegistry().ConfigureSafeHtmlRegistry(options);
        return Build(source, registry, stringProvider, textScaleFactor: textScaleFactor);
    }

    private static LayoutSnapshot Build(
        string source,
        MarkdownExtensionRegistry registry,
        IMarkdownStringProvider? stringProvider = null,
        Markdig.Syntax.MarkdownDocument? parsedDocument = null,
        bool lazy = false,
        float availableWidth = 800,
        bool measureAllLazyViewports = true,
        IMarkdownImageResolver? imageResolver = null,
        ElementStyle? elementStyle = null,
        IReadOnlyDictionary<string, ElementStyle>? keyedStyles = null,
        float textScaleFactor = 1f)
    {
        Markdig.Syntax.MarkdownDocument document =
            parsedDocument ?? Markdown.Parse(source, registry.BuildPipeline());
        var style = elementStyle ?? new ElementStyle();
        string[] keys =
        [
            MarkdownElementKeys.Body,
            MarkdownElementKeys.Strong,
            MarkdownElementKeys.Emphasis,
            MarkdownElementKeys.Link,
            MarkdownElementKeys.ImageCaption,
            MarkdownElementKeys.CodeInline,
            MarkdownElementKeys.CodeBlock,
            MarkdownElementKeys.CodeBlockHeader,
            MarkdownElementKeys.CodeBlockLanguage,
            MarkdownElementKeys.CodeBlockLanguage,
            MarkdownElementKeys.CodeBlockGutter,
            MarkdownElementKeys.CodeBlockLineNumber,
            MarkdownElementKeys.Strikethrough,
            MarkdownElementKeys.Subscript,
            MarkdownElementKeys.Superscript,
            MarkdownElementKeys.Inserted,
            MarkdownElementKeys.Marked,
            MarkdownElementKeys.ThematicBreak,
            MarkdownElementKeys.Quote,
            MarkdownElementKeys.ListMarker,
            MarkdownElementKeys.Table,
            MarkdownElementKeys.TableHeader,
            MarkdownElementKeys.TableCell,
        ];
        var styles = keys.Distinct(StringComparer.Ordinal)
            .ToDictionary(static key => key, _ => style, StringComparer.Ordinal);
        if (keyedStyles is not null)
        {
            foreach ((string key, ElementStyle keyedStyle) in keyedStyles)
                styles[key] = keyedStyle;
        }
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
            textScaleFactor: textScaleFactor);
        var context = new MarkdownLayoutContext(
            CanvasDevice.GetSharedDevice(),
            theme,
            new MarkdownSourceMap(source),
            registry,
            FlowDirection.LeftToRight)
        {
            StringProvider = stringProvider,
            ImageResolver = imageResolver,
        };
        var builder = new LayoutBuilder(context);
        if (!lazy)
            return builder.Build(document, availableWidth);

        LayoutSnapshot snapshot = builder.BuildLazy(
            document,
            availableWidth,
            viewportTop: 0,
            viewportHeight: 499,
            overscan: 0,
            cancellationToken: CancellationToken.None);
        if (!measureAllLazyViewports)
            return snapshot;
        for (int viewport = 0; viewport < 64; viewport++)
        {
            double top = viewport * 449;
            if (top >= snapshot.Size.Height)
                break;
            snapshot.EnsureMeasuredViewport(top, 499, 0, CancellationToken.None);
        }
        return snapshot;
    }

    private static LayoutSnapshot BuildGitHub(
        string source,
        ElementStyle? elementStyle = null,
        IReadOnlyDictionary<string, ElementStyle>? keyedStyles = null,
        float availableWidth = 800,
        float textScaleFactor = 1f)
    {
        using MarkdownEngine engine = new MarkdownEngineBuilder()
            .UseGitHubReadme()
            .Build();
        var registry = Assert.IsType<MarkdownExtensionRegistry>(engine.PresentationConfiguration);
        return Build(
            source,
            registry,
            availableWidth: availableWidth,
            elementStyle: elementStyle,
            keyedStyles: keyedStyles,
            textScaleFactor: textScaleFactor);
    }

    private static void AssertDisabledLinkAndImageFallback(LayoutSnapshot snapshot)
    {
        InlineRun[] runs = FlattenRuns(snapshot).ToArray();
        Assert.DoesNotContain(runs, static run => run is LinkRun);
        Assert.DoesNotContain(runs, static run => run is InlineImageRun);
        string rendered = string.Concat(runs.Select(static run => run.Text));
        Assert.Contains("linked", rendered, StringComparison.Ordinal);
        Assert.Contains("diagram", rendered, StringComparison.Ordinal);
    }

    private static IEnumerable<InlineRun> FlattenRuns(LayoutSnapshot snapshot)
    {
        foreach (BlockBox block in snapshot.Blocks)
        {
            foreach (InlineRun run in FlattenRuns(block))
                yield return run;
        }
    }

    private static IEnumerable<BlockBox> FlattenBoxes(LayoutSnapshot snapshot)
    {
        foreach (BlockBox block in snapshot.Blocks)
        {
            yield return block;
            if (block is StackBox stack)
            {
                foreach (BlockBox child in FlattenBoxes(stack))
                    yield return child;
            }
            else if (block is TableBox table)
            {
                foreach (InlineContainerBox cell in table.GetCellBoxes())
                    yield return cell;
            }
        }
    }

    private static IEnumerable<BlockBox> FlattenBoxes(StackBox stack)
    {
        foreach (BlockBox child in stack.Children)
        {
            yield return child;
            if (child is StackBox nested)
            {
                foreach (BlockBox descendant in FlattenBoxes(nested))
                    yield return descendant;
            }
            else if (child is TableBox table)
            {
                foreach (InlineContainerBox cell in table.GetCellBoxes())
                    yield return cell;
            }
        }
    }

    private static IEnumerable<InlineRun> FlattenRuns(BlockBox block)
    {
        if (block is InlineContainerBox inline)
        {
            foreach (InlineRun run in inline.Runs)
                yield return run;
        }
        else if (block is StackBox stack)
        {
            foreach (BlockBox child in stack.Children)
            {
                foreach (InlineRun run in FlattenRuns(child))
                    yield return run;
            }
        }
        else if (block is ListItemBox listItem)
        {
            foreach (InlineRun run in FlattenRuns(listItem.Marker))
                yield return run;
            foreach (InlineRun run in FlattenRuns(listItem.Content))
                yield return run;
        }
    }

    private sealed class TestStringProvider : IMarkdownStringProvider
    {
        public string? GetString(string key, System.Globalization.CultureInfo culture) =>
            key == MarkdownStringKeys.HtmlBudgetExceeded ? "localized budget notice" : null;
    }

    private sealed class DeferredImageResolver : IMarkdownImageResolver
    {
        private readonly TaskCompletionSource<MarkdownImageResolution> _resolution = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource ResolveStarted { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public ValueTask<MarkdownImageResolution> ResolveAsync(
            string source,
            MarkdownImageResolveContext context,
            CancellationToken cancellationToken)
        {
            ResolveStarted.TrySetResult();
            return new ValueTask<MarkdownImageResolution>(_resolution.Task.WaitAsync(cancellationToken));
        }

        public void Complete(MarkdownImageResolution resolution) => _resolution.TrySetResult(resolution);
    }
}
