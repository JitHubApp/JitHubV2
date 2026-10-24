using JitHub.Services.CodeViewer;
using Xunit;

namespace JitHub.WinUI.Tests.Services;

public sealed class GitHubRenderedReadmeHtmlNormalizerTests
{
    [Fact]
    public void NormalizeForMarkdownPipelineRemovesRootTagIndentation()
    {
        const string html = """
            <p>Logo</p>
                <p align="center">
            <a href="https://example.test"><img src="badge.svg" alt="Badge"></a>
            </p>
            <h2>Description</h2>
            """;

        string normalized = GitHubRenderedReadmeHtmlNormalizer.NormalizeForMarkdownPipeline(html);

        Assert.DoesNotContain("\n    <p", normalized, StringComparison.Ordinal);
        Assert.Contains("<p align=\"center\">&#10;<a", normalized, StringComparison.Ordinal);
        Assert.Contains("\n\n<h2>Description</h2>", normalized, StringComparison.Ordinal);
    }

    [Fact]
    public void MultilineQuotedAttributeCannotSplitRenderedHtmlBlock()
    {
        const string source = """
            <div data-snippet-clipboard-copy-content="line one

            line three"><pre><span>line one</span>

            <span>line three</span></pre></div>
            """;

        string normalized = GitHubRenderedReadmeHtmlNormalizer.NormalizeForMarkdownPipeline(source);

        Assert.Contains(
            "data-snippet-clipboard-copy-content=\"line one&#10;&#10;line three\"",
            normalized,
            StringComparison.Ordinal);
        Assert.Contains(
            "<span>line one</span>&#10;&#10;<span>line three</span>",
            normalized,
            StringComparison.Ordinal);
        Assert.DoesNotContain('\n', normalized);
    }

    [Theory]
    [InlineData("")]
    [InlineData("plain text\n\nnext paragraph")]
    [InlineData("<div class='one'>content</div>")]
    public void ContentWithoutTagInternalLineBreaksIsUnchanged(string source) =>
        Assert.Same(source, GitHubRenderedReadmeHtmlNormalizer.NormalizeForMarkdownPipeline(source));

    [Fact]
    public void UnquotedTagLineBreakBecomesSeparatingWhitespace()
    {
        const string source = "<img src='one'\r\nalt='two'>";

        Assert.Equal(
            "<img src='one' alt='two'>",
            GitHubRenderedReadmeHtmlNormalizer.NormalizeForMarkdownPipeline(source));
    }

    [Fact]
    public void RootElementsRemainIndependentBoundedHtmlBlocks()
    {
        const string source = "<h1>First</h1>\n<section><pre>line one\n\nline three</pre></section>\n<p>Last</p>";

        Assert.Equal(
            "<h1>First</h1>\n\n<section><pre>line one&#10;&#10;line three</pre></section>\n\n<p>Last</p>",
            GitHubRenderedReadmeHtmlNormalizer.NormalizeForMarkdownPipeline(source));
    }

    [Fact]
    public void VoidRootElementsDoNotConsumeFollowingBlockBoundary()
    {
        const string source = "<img src='one'>\n<h2>After image</h2>";

        Assert.Equal(
            "<img src='one'>\n\n<h2>After image</h2>",
            GitHubRenderedReadmeHtmlNormalizer.NormalizeForMarkdownPipeline(source));
    }

    [Fact]
    public void GitHubTransportShellIsRemovedBeforeBlockNormalization()
    {
        const string source = "<div id=\"readme\" class=\"md\" data-path=\"README.md\"><article class=\"markdown-body entry-content\" itemprop=\"text\"><h1>First</h1>\n<p>Second</p></article></div>";

        Assert.Equal(
            "<h1>First</h1>\n\n<p>Second</p>",
            GitHubRenderedReadmeHtmlNormalizer.NormalizeForMarkdownPipeline(source));
    }

    [Theory]
    [InlineData("markdown-accessiblity-table")]
    [InlineData("markdown-accessibility-table")]
    public void GitHubAccessibilityTableTransportWrapperIsRemoved(string wrapper)
    {
        string source = $"<{wrapper}><table><tr><td>Value</td></tr></table></{wrapper}>";

        Assert.Equal(
            "<table><tr><td>Value</td></tr></table>",
            GitHubRenderedReadmeHtmlNormalizer.NormalizeForMarkdownPipeline(source));
    }

    [Fact]
    public void GitHubThemedPictureTransportWrapperIsRemovedWithoutLosingSources()
    {
        const string source = "<themed-picture data-catalyst-inline=\"true\"><picture>\n" +
            "<source media=\"(prefers-color-scheme: dark)\" srcset=\"dark.png\">\n" +
            "<img src=\"light.png\" alt=\"Project logo\" width=\"300\">\n" +
            "</picture></themed-picture>";

        string normalized = GitHubRenderedReadmeHtmlNormalizer.NormalizeForMarkdownPipeline(source);

        Assert.DoesNotContain("themed-picture", normalized, StringComparison.Ordinal);
        Assert.Contains("<picture>", normalized, StringComparison.Ordinal);
        Assert.Contains("srcset=\"dark.png\"", normalized, StringComparison.Ordinal);
        Assert.Contains("src=\"light.png\"", normalized, StringComparison.Ordinal);
        Assert.Contains("alt=\"Project logo\"", normalized, StringComparison.Ordinal);
    }

    [Fact]
    public void GitHubEmojiTransportWrapperIsRemovedWithoutLosingItsFallback()
    {
        const string source = "<p>Use <g-emoji class=\"g-emoji\" alias=\"warning\">⚠</g-emoji> carefully.</p>";

        string normalized = GitHubRenderedReadmeHtmlNormalizer.NormalizeForMarkdownPipeline(source);

        Assert.Equal("<p>Use ⚠ carefully.</p>", normalized);
    }

    [Fact]
    public void GitHubHeadingPermalinkOcticonIsRemoved()
    {
        const string source = "<h2>Documentation</h2><a id=\"user-content-documentation\" class=\"anchor\" aria-label=\"Permalink: Documentation\" href=\"#documentation\"><svg data-component=\"Octicon\" aria-hidden=\"true\"><path d=\"M0 0\"></path></svg></a>";

        string normalized = GitHubRenderedReadmeHtmlNormalizer.NormalizeForMarkdownPipeline(source);

        Assert.Equal("<h2>Documentation</h2>", normalized);
    }

    [Fact]
    public void EmptyTransportAnchorIsRemovedButNamedAnchorRemains()
    {
        const string source = "<a href=\"https://example.test/empty\"> \n </a>" +
            "<a href=\"https://example.test/accessible\" aria-label=\"Accessible destination\"> </a>" +
            "<a href=\"https://example.test/docs\">Docs</a>";

        string normalized = GitHubRenderedReadmeHtmlNormalizer.NormalizeForMarkdownPipeline(source);

        Assert.Equal(
            "<a href=\"https://example.test/accessible\" aria-label=\"Accessible destination\"> </a>" +
            "<a href=\"https://example.test/docs\">Docs</a>",
            normalized);
    }

    [Fact]
    public void GitHubGeneratedStaticImageSelfLinkIsUnwrapped()
    {
        const string source = "<a target=\"_blank\" rel=\"noopener noreferrer nofollow\" href=\"https://camo.githubusercontent.com/hash/image\"><img src=\"https://camo.githubusercontent.com/hash/image\" alt=\"Badge\"></a>";

        string normalized = GitHubRenderedReadmeHtmlNormalizer.NormalizeForMarkdownPipeline(source);

        Assert.Equal(
            "<img src=\"https://camo.githubusercontent.com/hash/image\" alt=\"Badge\">",
            normalized);
    }

    [Fact]
    public void AuthoredAndAnimatedImageLinksRemainInteractive()
    {
        const string authored = "<a href=\"https://example.test/docs\"><img src=\"https://example.test/image.png\"></a>";
        const string animated = "<a target=\"_blank\" rel=\"noopener noreferrer\" href=\"https://camo.githubusercontent.com/hash/image\"><img src=\"https://camo.githubusercontent.com/hash/image\" data-animated-image=\"\"></a>";

        Assert.Equal(
            authored,
            GitHubRenderedReadmeHtmlNormalizer.NormalizeForMarkdownPipeline(authored));
        Assert.Equal(
            animated,
            GitHubRenderedReadmeHtmlNormalizer.NormalizeForMarkdownPipeline(animated));
    }

    [Fact]
    public void GitHubMermaidTransportBecomesNativeMermaidFence()
    {
        const string source = """
            <p>Before</p>
            <section class="js-render-needs-enrichment" data-type="mermaid">
              <div class="js-render-enrichment-target" data-plain="flowchart TD
                A[&quot;Start&quot;] --&gt; B[Done]"><pre lang="mermaid">ignored transport copy</pre></div>
            </section>
            <p>After</p>
            """;

        string normalized = GitHubRenderedReadmeHtmlNormalizer.NormalizeForMarkdownPipeline(source);

        Assert.Contains("~~~mermaid\nflowchart TD", normalized, StringComparison.Ordinal);
        Assert.Contains("A[\"Start\"] --> B[Done]", normalized, StringComparison.Ordinal);
        Assert.Contains("\n~~~", normalized, StringComparison.Ordinal);
        Assert.DoesNotContain("ignored transport copy", normalized, StringComparison.Ordinal);
        Assert.DoesNotContain("js-render-needs-enrichment", normalized, StringComparison.Ordinal);
        Assert.Contains("<p>Before</p>", normalized, StringComparison.Ordinal);
        Assert.Contains("<p>After</p>", normalized, StringComparison.Ordinal);
    }

    [Fact]
    public void GitHubGeneratedMediaDisclosureIsUnwrappedButMediaIsRetained()
    {
        const string source = """
            <details open class="details-reset border rounded-2">
              <summary><span>demo.mp4</span></summary>
              <video src="https://example.test/demo.mp4" controls></video>
            </details>
            <details><summary>Authored</summary><p>Body</p></details>
            """;

        string normalized = GitHubRenderedReadmeHtmlNormalizer.NormalizeForMarkdownPipeline(source);

        Assert.DoesNotContain("details-reset", normalized, StringComparison.Ordinal);
        Assert.Contains("<summary><span>demo.mp4</span></summary>", normalized, StringComparison.Ordinal);
        Assert.Contains("<video src=", normalized, StringComparison.Ordinal);
        Assert.Contains("<details><summary>Authored</summary>", normalized, StringComparison.Ordinal);
    }
}
