using MarkdownRenderer.Document;
using MarkdownRenderer.Html;
using MarkdownRenderer.Layout;
using MarkdownRenderer.Layout.Boxes;
using MarkdownRenderer.Parsing;
using MarkdownRenderer.Theming;
using Xunit;
using static MarkdownRenderer.GitHub.Tests.SafeHtmlRenderingFixture;

namespace MarkdownRenderer.GitHub.Tests;

/// <summary>
/// Checks that GitHub-rendered safe HTML keeps its document structure:
/// code blocks, lists, tables, headings, alerts, links and task inputs.
/// </summary>
public sealed class SafeHtmlContentStructureTests
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
}
