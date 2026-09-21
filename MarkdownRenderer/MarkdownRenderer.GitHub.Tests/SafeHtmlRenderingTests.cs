using Markdig;
using MarkdownRenderer.Document;
using MarkdownRenderer.GitHub;
using MarkdownRenderer.Html;
using MarkdownRenderer.Hosting;
using MarkdownRenderer.Layout;
using MarkdownRenderer.Layout.Boxes;
using MarkdownRenderer.Parsing;
using MarkdownRenderer.Theming;
using Microsoft.Graphics.Canvas;
using Microsoft.UI.Xaml;
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
    public void ExplicitEmptyImageAltRemainsDecorative()
    {
        const string source = "<img src='decoration.png' alt=''>";

        using LayoutSnapshot snapshot = Build(source, SafeHtmlOptions.Default);
        InlineImageRun image = Assert.Single(FlattenRuns(snapshot).OfType<InlineImageRun>());

        Assert.Empty(image.AltText);
        Assert.Empty(image.AccessibleText);
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
        IMarkdownStringProvider? stringProvider = null)
    {
        var registry = new MarkdownExtensionRegistry().ConfigureSafeHtmlRegistry(options);
        return Build(source, registry, stringProvider);
    }

    private static LayoutSnapshot Build(
        string source,
        MarkdownExtensionRegistry registry,
        IMarkdownStringProvider? stringProvider = null)
    {
        Markdig.Syntax.MarkdownDocument document = Markdown.Parse(source, registry.BuildPipeline());
        var style = new ElementStyle();
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
            FlowDirection.LeftToRight)
        {
            StringProvider = stringProvider,
        };
        return new LayoutBuilder(context).Build(document, 800);
    }

    private static LayoutSnapshot BuildGitHub(string source)
    {
        using MarkdownEngine engine = new MarkdownEngineBuilder()
            .UseGitHubReadme()
            .Build();
        var registry = Assert.IsType<MarkdownExtensionRegistry>(engine.PresentationConfiguration);
        return Build(source, registry);
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
}
