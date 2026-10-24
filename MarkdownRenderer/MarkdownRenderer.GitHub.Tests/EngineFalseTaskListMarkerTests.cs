using Markdig.Extensions.TaskLists;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using MarkdownRenderer.GitHub;
using Xunit;

namespace MarkdownRenderer.GitHub.Tests;

/// <summary>
/// P3-005: the public GitHub README engine exposed <c>- [X](https://x.com/dify_ai)</c>
/// as a checked task item, while the view (and GitHub) shows a link labelled
/// "X". The engine now applies the same GFM rule as the rendering parser.
/// </summary>
public sealed class EngineFalseTaskListMarkerTests
{
    [Theory]
    [InlineData("- [X](https://x.com/dify_ai): Updates", "X")]
    [InlineData("- [x](https://example.test/a) lower", "x")]
    public async Task GitHubReadmeEngineExposesLinkNotTaskForMarkerFollowedByLink(string source, string label)
    {
        using MarkdownEngine engine = new MarkdownEngineBuilder().UseGitHubReadme().Build();

        MarkdownRenderer.Document.MarkdownDocument document = await engine.ParseAsync(source);

        MarkdownDocument parsed = document.ParsedDocument!;
        Assert.Empty(parsed.Descendants<TaskList>());
        LinkInline link = Assert.Single(parsed.Descendants<LinkInline>());
        Assert.Equal(label, Assert.IsType<LiteralInline>(link.FirstChild).Content.ToString());
        Assert.Equal(2, link.Span.Start);
    }

    [Fact]
    public async Task RealLeadingTaskKeepsItsMarkerWhenALaterLinkLooksLikeOne()
    {
        using MarkdownEngine engine = new MarkdownEngineBuilder().UseGitHubReadme().Build();
        const string source = "- [x] done, follow [X](https://x.com/a) for ~~old~~ news";

        MarkdownRenderer.Document.MarkdownDocument document = await engine.ParseAsync(source);

        MarkdownDocument parsed = document.ParsedDocument!;
        TaskList task = Assert.Single(parsed.Descendants<TaskList>());
        Assert.True(task.Checked);
        ParagraphBlock paragraph = Assert.Single(parsed.Descendants<ParagraphBlock>());
        Assert.Same(task, paragraph.Inline!.FirstChild);
        LinkInline link = Assert.Single(parsed.Descendants<LinkInline>());
        Assert.Equal("https://x.com/a", link.Url);
        Assert.Equal(source.IndexOf("[X]", StringComparison.Ordinal), link.Span.Start);
        // The repair reparses with the engine's own pipeline (strikethrough).
        Assert.Contains(parsed.Descendants<EmphasisInline>(), static emphasis => emphasis.DelimiterChar == '~');
    }

    [Fact]
    public async Task RepairThatWouldSplitAQuotedContinuationLeavesTheParagraph()
    {
        using MarkdownEngine engine = new MarkdownEngineBuilder().UseGitHubReadme().Build();
        const string source = "> - Follow us on\n>   [X](https://x.com/a) now";

        MarkdownRenderer.Document.MarkdownDocument document = await engine.ParseAsync(source);

        // Reparsing the raw slice would read "> " as a new quote and drop
        // text; the guard keeps the parsed paragraph instead of losing it.
        ParagraphBlock paragraph = Assert.Single(document.ParsedDocument!.Descendants<ParagraphBlock>());
        Assert.Contains(paragraph.Inline!.Descendants<LiteralInline>(), static literal => literal.Content.ToString().Contains("now", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("- [x] done")]
    [InlineData("- [ ] todo")]
    [InlineData("- [X] done [link](https://example.test)")]
    public async Task GitHubReadmeEngineKeepsRealTaskItems(string source)
    {
        using MarkdownEngine engine = new MarkdownEngineBuilder().UseGitHubReadme().Build();

        MarkdownRenderer.Document.MarkdownDocument document = await engine.ParseAsync(source);

        Assert.Single(document.ParsedDocument!.Descendants<TaskList>());
    }
}
