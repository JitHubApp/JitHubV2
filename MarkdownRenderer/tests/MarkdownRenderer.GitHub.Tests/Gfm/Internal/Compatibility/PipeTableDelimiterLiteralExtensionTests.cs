using Markdig.Extensions.Tables;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using MarkdownRenderer.GitHub;
using MarkdownRenderer.Layout.Internal.Inlines;
using MarkdownRenderer.Layout.Internal.Inlines.Runs;
using MarkdownRenderer.Layout.Internal.Snapshots;
using MarkdownRenderer.Layout.Internal.Tables;
using Xunit;
using static MarkdownRenderer.GitHub.Tests.TestSupport.Fixtures.GitHubReadmeEngineLayoutFixture;

namespace MarkdownRenderer.GitHub.Tests.Gfm.Internal.Compatibility;

/// <summary>
/// A line with <c>|</c> characters but no <c>---|---</c> delimiter row is a
/// paragraph, and its pipes are literal text. Markdig leaves each such pipe in
/// the syntax tree as a <see cref="PipeTableDelimiterInline"/> (its own HTML
/// renderer writes the delimiter's literal); the renderer must keep them too.
/// </summary>
public sealed class PipeTableDelimiterLiteralExtensionTests
{
    // The head of rust-lang/rust's README, verbatim.
    private const string RustReadmeHead = """
        <div align="center">
          <picture>
            <source media="(prefers-color-scheme: dark)" srcset="https://raw.githubusercontent.com/rust-lang/www.rust-lang.org/master/static/images/rust-social-wide-dark.svg">
            <source media="(prefers-color-scheme: light)" srcset="https://raw.githubusercontent.com/rust-lang/www.rust-lang.org/master/static/images/rust-social-wide-light.svg">
            <img alt="The Rust Programming Language: A language empowering everyone to build reliable and efficient software"
                 src="https://raw.githubusercontent.com/rust-lang/www.rust-lang.org/master/static/images/rust-social-wide-light.svg"
                 width="50%">
          </picture>

        [Website][Rust] | [Getting started] | [Learn] | [Documentation] | [Contributing]
        </div>

        This is the main source code repository for [Rust].

        [Rust]: https://www.rust-lang.org/
        [Getting Started]: https://www.rust-lang.org/learn/get-started
        [Learn]: https://www.rust-lang.org/learn
        [Documentation]: https://www.rust-lang.org/learn#learn-use
        [Contributing]: CONTRIBUTING.md
        """;

    [Fact]
    public async Task RustReadmeNavigationLineInsideCenteredDivKeepsItsPipes()
    {
        using LayoutSnapshot snapshot = await BuildAsync(RustReadmeHead);

        InlineContainerBox navigation = Assert.Single(
            EnumerateInlineBoxes(snapshot),
            static box => GetText(box).Contains("Website", StringComparison.Ordinal));
        Assert.Equal(
            "Website | Getting started | Learn | Documentation | Contributing",
            GetText(navigation));
        Assert.Equal(5, navigation.Runs.OfType<LinkRun>().Count());
        Assert.DoesNotContain(snapshot.Blocks, static block => block is TableBox);
        Assert.Contains(
            "Website | Getting started | Learn | Documentation | Contributing",
            snapshot.SemanticDocument.Text,
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(
        "[Website][Rust] | [Learn]\n\n[Rust]: https://www.rust-lang.org/\n[Learn]: https://www.rust-lang.org/learn",
        "Website | Learn")]
    [InlineData("a | b | c", "a | b | c")]
    [InlineData("| leading and trailing |", "| leading and trailing |")]
    [InlineData("*a | b* and **c | d**", "a | b and c | d")]
    [InlineData("- item a | b", "item a | b")]
    // One HTML block: GitHub reparses the div's text as Markdown.
    [InlineData(
        "<div align=\"center\">\n[Website](https://www.rust-lang.org/) | [Learn](https://www.rust-lang.org/learn)\n</div>",
        "Website | Learn")]
    public async Task PipeLineWithoutDelimiterRowIsAParagraphWithLiteralPipes(string source, string expected)
    {
        using LayoutSnapshot snapshot = await BuildAsync(source);

        Assert.Contains(EnumerateInlineBoxes(snapshot), box => GetText(box) == expected);
        Assert.DoesNotContain(snapshot.Blocks, static block => block is TableBox);
    }

    [Fact]
    public async Task ParsedDocumentHoldsEachNonTablePipeAsASourceMappedLiteral()
    {
        const string source = "a | b";
        using MarkdownEngine engine = new MarkdownEngineBuilder().UseGitHubReadme().Build();
        MarkdownRenderer.Document.MarkdownDocument document = await engine.ParseAsync(source);
        MarkdownDocument parsed = Assert.IsType<MarkdownDocument>(document.ParsedDocument);

        ParagraphBlock paragraph = Assert.IsType<ParagraphBlock>(Assert.Single(parsed));
        Assert.Empty(parsed.Descendants<PipeTableDelimiterInline>());
        LiteralInline pipe = Assert.Single(
            paragraph.Inline!.OfType<LiteralInline>(),
            static literal => literal.Content.ToString() == "|");
        Assert.Equal(2, pipe.Span.Start);
        Assert.Equal(1, pipe.Span.Length);
        Assert.Equal(
            "a | b",
            string.Concat(paragraph.Inline!.OfType<LiteralInline>().Select(static literal => literal.Content.ToString())));
    }

    [Fact]
    public async Task DelimiterRowStillBuildsATableWithEscapedPipeInCell()
    {
        const string source = "a | b | c\n\n| h1 | h2 |\n|----|----|\n| x \\| y | z |";
        using LayoutSnapshot snapshot = await BuildAsync(source);

        Assert.Contains(EnumerateInlineBoxes(snapshot), static box => GetText(box) == "a | b | c");
        TableBox table = Assert.Single(snapshot.Blocks.OfType<TableBox>());
        Assert.Equal(
            ["h1", "h2", "x | y", "z"],
            table.GetCellBoxes().Select(GetText).ToArray());
    }
}
