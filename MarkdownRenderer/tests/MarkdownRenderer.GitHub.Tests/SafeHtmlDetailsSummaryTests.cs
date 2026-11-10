using MarkdownRenderer.Document;
using MarkdownRenderer.Html;
using MarkdownRenderer.Layout;
using MarkdownRenderer.Layout.Boxes;
using MarkdownRenderer.Parsing.Internal.SafeHtml;
using Xunit;
using static MarkdownRenderer.GitHub.Tests.SafeHtmlRenderingFixture;

namespace MarkdownRenderer.GitHub.Tests;

/// <summary>
/// Checks details and summary disclosure markup: collapsed bodies stay out
/// of layout, summaries keep their headings, code and images, and summary
/// images copy their exact authored source.
/// </summary>
public sealed class SafeHtmlDetailsSummaryTests
{
    [Fact]
    public void HeadingInsideDisclosureSummaryRetainsHeadingSemantics()
    {
        const string source = "<details><summary><div class='markdown-heading'><h3>Browser &amp; Automation</h3><a href='#browser'>Permalink</a></div></summary><p>Body</p></details>";

        using LayoutSnapshot snapshot = Build(source, SafeHtmlOptions.Default);
        MarkdownRenderer.Accessibility.Internal.Semantics.MarkdownSemanticNode heading = Assert.Single(
            MarkdownRenderer.Accessibility.Internal.Semantics.MarkdownSemanticDocument
                .EnumerateDepthFirst(snapshot.SemanticDocument.Root),
            static node => node.Role == MarkdownRenderer.Accessibility.Internal.Semantics.MarkdownSemanticRole.Heading);

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
        MarkdownRenderer.Accessibility.Internal.Semantics.MarkdownSemanticNode semantic = Assert.Single(
            MarkdownRenderer.Accessibility.Internal.Semantics.MarkdownSemanticDocument
                .EnumerateDepthFirst(snapshot.SemanticDocument.Root),
            static node => node.Role == MarkdownRenderer.Accessibility.Internal.Semantics.MarkdownSemanticRole.CodeBlock);
        string rendered = snapshot.SemanticDocument.Text;

        Assert.Equal("What does this print?", disclosure.AccessibilityName);
        Assert.False(disclosure.IsExpanded);
        Assert.Equal("echo one\necho two", code.CodeText);
        Assert.Equal("echo one\necho two", snapshot.SemanticDocument.GetText(semantic));
        Assert.Equal(1, rendered.Split("echo one", StringSplitOptions.None).Length - 1);
        Assert.DoesNotContain("Hidden answer", rendered, StringComparison.Ordinal);
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
}
