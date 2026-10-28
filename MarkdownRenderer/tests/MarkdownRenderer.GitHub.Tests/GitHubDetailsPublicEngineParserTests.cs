using Markdig.Syntax;
using MarkdownRenderer.Controls;
using MarkdownRenderer.GitHub;
using MarkdownRenderer.Html;
using MarkdownRenderer.Parsing;
using Xunit;

namespace MarkdownRenderer.GitHub.Tests;

public sealed class GitHubDetailsPublicEngineParserTests
{
    private const string MarkdownBearingDetails = """
        <details>
        <summary>Toggle details</summary>

        ### A parsed heading

        **Bold text in a Markdown paragraph.**

        - first item
        - second item

        </details>
        """;

    [Fact]
    public async Task DefaultGitHubReadmeEngineParsesMarkdownBearingDetailsAsContainer()
    {
        using MarkdownEngine engine = new MarkdownEngineBuilder()
            .UseGitHubReadme()
            .Build();

        await AssertMarkdownBearingDetailsAsync(engine);
    }

    [Fact]
    public async Task GitHubReadmeOptionsOverloadParsesMarkdownBearingDetailsAsContainer()
    {
        using MarkdownEngine engine = new MarkdownEngineBuilder()
            .UseGitHubReadme(new SafeHtmlOptions(enableImages: false))
            .Build();

        await AssertMarkdownBearingDetailsAsync(engine);
    }

    [Fact]
    public async Task DerivedGitHubReadmeEngineParsesMarkdownBearingDetailsAsContainer()
    {
        using MarkdownEngine original = new MarkdownEngineBuilder()
            .UseGitHubReadme()
            .Build();
        using MarkdownEngine derived = original.ToBuilder()
            .UseSafeHtml(new SafeHtmlOptions(enableImages: false))
            .Build();

        await AssertMarkdownBearingDetailsAsync(derived);
    }

    [Fact]
    public async Task GitHubReadmeControlBuilderParsesMarkdownBearingDetailsAsContainer()
    {
        using MarkdownEngine engine = new MarkdownRendererControlBuilder()
            .UseGitHubReadme()
            .CreateEngineSnapshotForTesting();

        await AssertMarkdownBearingDetailsAsync(engine);
    }

    [Fact]
    public async Task GitHubReadmeControlBuilderOptionsOverloadParsesMarkdownBearingDetailsAsContainer()
    {
        using MarkdownEngine engine = new MarkdownRendererControlBuilder()
            .UseGitHubReadme(new SafeHtmlOptions(enableImages: false))
            .CreateEngineSnapshotForTesting();

        await AssertMarkdownBearingDetailsAsync(engine);
    }

    [Fact]
    public async Task ExplicitSafeHtmlAfterGitHubReadmePreservesItsDetailsParser()
    {
        using MarkdownEngine engine = new MarkdownRendererControlBuilder()
            .UseGitHubReadme()
            .UseSafeHtml(new SafeHtmlOptions(enableImages: false))
            .CreateEngineSnapshotForTesting();

        await AssertMarkdownBearingDetailsAsync(engine);
    }

    [Fact]
    public async Task RepeatedGitHubReadmeControlBuilderCompositionInstallsOneDetailsParser()
    {
        using MarkdownEngine engine = new MarkdownRendererControlBuilder()
            .UseGitHubReadme()
            .UseSafeHtml(new SafeHtmlOptions(enableImages: false))
            .UseGitHubReadme(new SafeHtmlOptions(enableImages: false))
            .UseGitHubReadme()
            .CreateEngineSnapshotForTesting();

        await AssertMarkdownBearingDetailsAsync(engine);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task LiteralProfileOverrideDoesNotRetainGitHubHtmlParsers(
        bool useGfmStrict,
        bool deriveFromExistingEngine)
    {
        MarkdownProfile literalProfile = useGfmStrict
            ? MarkdownProfiles.GfmStrict
            : MarkdownProfiles.CommonMark;
        using MarkdownEngine? original = deriveFromExistingEngine
            ? new MarkdownEngineBuilder().UseGitHubReadme().Build()
            : null;
        using MarkdownEngine engine = deriveFromExistingEngine
            ? original!.ToBuilder().UseProfile(literalProfile).Build()
            : new MarkdownEngineBuilder()
                .UseGitHubReadme()
                .UseProfile(literalProfile)
                .Build();

        if (original is not null)
            await AssertMarkdownBearingDetailsAsync(original);

        MarkdownRenderer.Document.MarkdownDocument semanticDocument =
            await engine.ParseAsync(MarkdownBearingDetails);
        Markdig.Syntax.MarkdownDocument parsedDocument = Assert.IsType<Markdig.Syntax.MarkdownDocument>(
            semanticDocument.ParsedDocument);

        Assert.DoesNotContain(parsedDocument, static block => block is HtmlBlock or GitHubDetailsBlock);
        Assert.DoesNotContain(
            parsedDocument.Descendants(),
            static node => node is HtmlBlock or GitHubDetailsBlock);
        Assert.NotEmpty(parsedDocument.Descendants().OfType<ParagraphBlock>());
    }

    private static async Task AssertMarkdownBearingDetailsAsync(MarkdownEngine engine)
    {
        MarkdownRenderer.Document.MarkdownDocument semanticDocument =
            await engine.ParseAsync(MarkdownBearingDetails);
        Markdig.Syntax.MarkdownDocument parsedDocument = Assert.IsType<Markdig.Syntax.MarkdownDocument>(
            semanticDocument.ParsedDocument);
        GitHubDetailsBlock details = Assert.Single(parsedDocument.OfType<GitHubDetailsBlock>());
        IEnumerable<MarkdownObject> descendants = details.Descendants();

        Assert.Contains(descendants, static node => node is HeadingBlock);
        Assert.Contains(descendants, static node => node is ListBlock);
        Assert.Contains(descendants, static node => node is ParagraphBlock);
        Assert.DoesNotContain(parsedDocument, static block => block is HtmlBlock);
    }
}
