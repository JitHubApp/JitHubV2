using Markdig;
using MarkdownRenderer.Layout.Internal.Inlines.Runs;
using MarkdownRenderer.Layout.Internal.Snapshots;
using MarkdownRenderer.Math;
using MarkdownRenderer.Mermaid;
using MarkdownRenderer.Parsing.Internal;
using MarkdownRenderer.Performance;
using Xunit;
using static MarkdownRenderer.GitHub.Tests.SafeHtmlRenderingFixture;

namespace MarkdownRenderer.GitHub.Tests;

/// <summary>
/// Checks that GitHub profile READMEs parse Markdown and resolve reference
/// definitions inside presentation divs and other HTML blocks.
/// </summary>
public sealed class SafeHtmlProfileReferenceTests
{
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
        bool simpleFragment = MarkdownRenderer.Html.Internal.Renderers.HtmlNestedMarkdownSource
            .HasSimpleAsciiReferenceLabels(fragment);
        Assert.Equal(expected,
            MarkdownRenderer.Html.Internal.Renderers.HtmlNestedMarkdownSource.MayUseReferenceLabel(
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
        MarkdownRenderer.Accessibility.Internal.Semantics.MarkdownSemanticNode heading = Assert.Single(
            MarkdownRenderer.Accessibility.Internal.Semantics.MarkdownSemanticDocument
                .EnumerateDepthFirst(snapshot.SemanticDocument.Root),
            static node => node.Role == MarkdownRenderer.Accessibility.Internal.Semantics.MarkdownSemanticRole.Heading);
        Assert.Equal(2, heading.HeadingLevel);
        Assert.Equal("Following heading", snapshot.SemanticDocument.GetText(heading));
        TextRun text = Assert.Single(FlattenRuns(snapshot).OfType<TextRun>(),
            static run => run.Text.Contains("Following heading", StringComparison.Ordinal));
        Assert.Equal(source.IndexOf("Following heading", StringComparison.Ordinal), text.SourceSpan.Start);
    }

    [Fact]
    public async Task GitHubProfileResolvesNumericReferenceImagesInLongPresentationDiv()
    {
        string source = GitHubProfileReadmeSources.CreateMassgraveProfileSource();
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
        string[] headings = MarkdownRenderer.Accessibility.Internal.Semantics.MarkdownSemanticDocument
            .EnumerateDepthFirst(snapshot.SemanticDocument.Root)
            .Where(static node => node.Role == MarkdownRenderer.Accessibility.Internal.Semantics.MarkdownSemanticRole.Heading)
            .Select(snapshot.SemanticDocument.GetText)
            .ToArray();

        Assert.Equal(8, images.Length);
        Assert.Equal(5, headings.Length);
        Assert.Contains("How to Activate Windows / Office / Extended Security Updates (ESU)?", headings);
        Assert.Equal("https://massgrave.dev/img/logo_small.png", images[0].Url);
        AssertMassgraveBadgeImagesAndLinks(images);

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

    private static void AssertMassgraveBadgeImagesAndLinks(InlineImageRun[] images)
    {
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
    }
}
