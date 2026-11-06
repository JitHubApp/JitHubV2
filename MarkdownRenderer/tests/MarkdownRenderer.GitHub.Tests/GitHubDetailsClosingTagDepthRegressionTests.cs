using System.Text;
using Markdig.Extensions.Tables;
using Markdig.Syntax;
using MarkdownRenderer.GitHub;
using Xunit;

namespace MarkdownRenderer.GitHub.Tests;

public sealed class GitHubDetailsClosingTagDepthRegressionTests
{
    [Fact]
    public async Task HundredsOfSiblingDetailsCloseAfterFormattingEndTags()
    {
        const int detailsCount = 444;
        var source = new StringBuilder(detailsCount * 112);
        for (int index = 0; index < detailsCount; index++)
        {
            source.AppendLine("<details>");
            source.Append("<summary>Question ").Append(index).AppendLine("</summary><br><b>");
            source.Append("Answer ").Append(index).AppendLine();
            source.AppendLine("</b></details>");
            source.AppendLine();
        }

        using MarkdownEngine engine = new MarkdownEngineBuilder()
            .UseGitHubReadme()
            .Build();
        MarkdownRenderer.Document.MarkdownDocument document =
            await engine.ParseAsync(source.ToString());
        Markdig.Syntax.MarkdownDocument parsed = Assert.IsType<Markdig.Syntax.MarkdownDocument>(
            document.ParsedDocument);

        GitHubDetailsBlock[] topLevelDetails = parsed.OfType<GitHubDetailsBlock>().ToArray();
        Assert.Equal(detailsCount, topLevelDetails.Length);
        Assert.Equal(detailsCount, parsed.Descendants().OfType<GitHubDetailsBlock>().Count());
        Assert.All(topLevelDetails, static details =>
            Assert.Single(details.OfType<ParagraphBlock>()));
    }

    [Fact]
    public async Task NestedDetailsCloseIndependentlyAfterChildFormattingEndTags()
    {
        const int nestingDepth = 24;
        var source = new StringBuilder(nestingDepth * 80);
        for (int index = 0; index < nestingDepth; index++)
        {
            source.AppendLine("<details>");
            source.Append("<summary>Level ").Append(index).AppendLine("</summary><br><b>");
        }

        source.AppendLine("Deepest body.");
        for (int index = nestingDepth - 1; index > 0; index--)
        {
            source.AppendLine("</b></details>");
            source.AppendLine();
            source.Append("Parent body ").Append(index - 1).AppendLine(".");
            source.AppendLine();
        }
        source.AppendLine("</b></details>");

        using MarkdownEngine engine = new MarkdownEngineBuilder()
            .UseGitHubReadme()
            .Build();
        MarkdownRenderer.Document.MarkdownDocument document =
            await engine.ParseAsync(source.ToString());
        Markdig.Syntax.MarkdownDocument parsed = Assert.IsType<Markdig.Syntax.MarkdownDocument>(
            document.ParsedDocument);

        GitHubDetailsBlock outer = Assert.Single(parsed.OfType<GitHubDetailsBlock>());
        GitHubDetailsBlock[] detailsBlocks = EnumerateDetails(outer).ToArray();
        Assert.Equal(nestingDepth, detailsBlocks.Length);
        Assert.Empty(parsed.OfType<HtmlBlock>());
        Assert.All(detailsBlocks, static details =>
            Assert.Equal(0, details.OpenDetailsDescendantCount));

        for (int index = 0; index < detailsBlocks.Length; index++)
        {
            Assert.True(
                detailsBlocks[index].OfType<ParagraphBlock>().Count() == 1,
                $"Expected one direct paragraph at nesting level {index}.");
            Assert.Equal(
                index + 1 < nestingDepth ? 1 : 0,
                detailsBlocks[index].OfType<GitHubDetailsBlock>().Count());
        }
    }

    [Fact]
    public async Task DetailsCloseTextInsideInlineCodeDoesNotCloseContainer()
    {
        const string source = """
            <details>
            <summary>Example</summary>

            The literal `</details>` text is part of this answer.

            </details>
            """;

        using MarkdownEngine engine = new MarkdownEngineBuilder()
            .UseGitHubReadme()
            .Build();
        MarkdownRenderer.Document.MarkdownDocument document =
            await engine.ParseAsync(source);
        Markdig.Syntax.MarkdownDocument parsed = Assert.IsType<Markdig.Syntax.MarkdownDocument>(
            document.ParsedDocument);

        GitHubDetailsBlock details = Assert.Single(parsed.OfType<GitHubDetailsBlock>());
        Assert.Single(details.OfType<ParagraphBlock>());
    }

    [Fact]
    public async Task ClosingTagsInsideFencedAndIndentedCodeRemainContent()
    {
        const string source = """
            <details>
            <summary>Fenced</summary>

            ```text
            </details>
            ```

            Fenced body remains in the container.

            </details>

            <details>
            <summary>Close immediately after a fenced block</summary>

            ```text
            fenced literal close: </details>
            ```
            </details>

            <details>
            <summary>Indented</summary>

                </details>

            Indented body remains in the container.

            </details>
            """;

        using MarkdownEngine engine = new MarkdownEngineBuilder()
            .UseGitHubReadme()
            .Build();
        MarkdownRenderer.Document.MarkdownDocument document =
            await engine.ParseAsync(source);
        Markdig.Syntax.MarkdownDocument parsed = Assert.IsType<Markdig.Syntax.MarkdownDocument>(
            document.ParsedDocument);

        GitHubDetailsBlock[] detailsBlocks = parsed.OfType<GitHubDetailsBlock>().ToArray();
        Assert.Equal(3, detailsBlocks.Length);
        Assert.Single(detailsBlocks[0].Descendants().OfType<FencedCodeBlock>());
        Assert.Single(detailsBlocks[0].OfType<ParagraphBlock>());
        Assert.Single(detailsBlocks[1].Descendants().OfType<FencedCodeBlock>());
        Assert.Empty(detailsBlocks[1].OfType<ParagraphBlock>());
        Assert.Single(detailsBlocks[2].Descendants().OfType<CodeBlock>());
        Assert.Single(detailsBlocks[2].OfType<ParagraphBlock>());
    }

    [Theory]
    [InlineData("\n", "</details>")]
    [InlineData("\r\n", "</details>")]
    [InlineData("\n", "</b></details>")]
    [InlineData("\r\n", "</b></details>")]
    public async Task LiteralDetailsCloserAtEofInsideUnclosedFenceIsIncludedInSpan(
        string newline,
        string literalCloser)
    {
        string source = string.Join(newline,
            "<details>",
            "<summary>Code sample</summary>",
            "```text",
            literalCloser);

        using MarkdownEngine engine = new MarkdownEngineBuilder()
            .UseGitHubReadme()
            .Build();
        MarkdownRenderer.Document.MarkdownDocument document =
            await engine.ParseAsync(source);
        Markdig.Syntax.MarkdownDocument parsed = Assert.IsType<Markdig.Syntax.MarkdownDocument>(
            document.ParsedDocument);

        GitHubDetailsBlock details = Assert.Single(parsed.OfType<GitHubDetailsBlock>());
        FencedCodeBlock fenced = Assert.Single(details.OfType<FencedCodeBlock>());
        string[] codeLines = fenced.Lines.Lines
            .Select(static line => line.Slice.ToString())
            .ToArray();
        Assert.Contains(literalCloser, codeLines);
        Assert.Equal(source.Length - 1, details.Span.End);
    }

    [Fact]
    public async Task EofInNestedDetailsDoesNotAffectSubsequentParse()
    {
        using MarkdownEngine engine = new MarkdownEngineBuilder()
            .UseGitHubReadme()
            .Build();

        MarkdownRenderer.Document.MarkdownDocument incomplete =
            await engine.ParseAsync("""
                <details>
                <summary>Outer</summary>
                <details>
                <summary>Inner</summary>
                Unclosed body.
                """);
        Markdig.Syntax.MarkdownDocument incompleteParsed =
            Assert.IsType<Markdig.Syntax.MarkdownDocument>(incomplete.ParsedDocument);
        Assert.Equal(2, incompleteParsed.Descendants().OfType<GitHubDetailsBlock>().Count());
        Assert.All(incompleteParsed.Descendants().OfType<GitHubDetailsBlock>(),
            static details => Assert.Equal(0, details.OpenDetailsDescendantCount));

        const int siblingCount = 64;
        string source = CreateSiblingDetailsSource(siblingCount);
        MarkdownRenderer.Document.MarkdownDocument subsequent =
            await engine.ParseAsync(source);
        Assert.Equal(
            siblingCount,
            Assert.IsType<Markdig.Syntax.MarkdownDocument>(subsequent.ParsedDocument)
                .OfType<GitHubDetailsBlock>().Count());
    }

    [Fact]
    public async Task ParallelDocumentsKeepDetailsDescendantCountsIndependent()
    {
        using MarkdownEngine engine = new MarkdownEngineBuilder()
            .UseGitHubReadme()
            .WithParseLimits(new MarkdownParseLimits(
                maximumConcurrentParseCount: 2,
                maximumOutstandingParseCount: 2))
            .WithParseCacheBudgetBytes(0)
            .Build();

        Task<MarkdownRenderer.Document.MarkdownDocument> firstTask =
            engine.ParseAsync(CreateSiblingDetailsSource(96));
        Task<MarkdownRenderer.Document.MarkdownDocument> secondTask =
            engine.ParseAsync(CreateSiblingDetailsSource(144));
        MarkdownRenderer.Document.MarkdownDocument[] documents =
            await Task.WhenAll(firstTask, secondTask);

        Assert.Equal(
            96,
            Assert.IsType<Markdig.Syntax.MarkdownDocument>(documents[0].ParsedDocument)
                .OfType<GitHubDetailsBlock>().Count());
        Assert.Equal(
            144,
            Assert.IsType<Markdig.Syntax.MarkdownDocument>(documents[1].ParsedDocument)
                .OfType<GitHubDetailsBlock>().Count());
    }

    [Fact]
    public async Task DescendantCountsBalanceWhenQuoteAndListContainersExit()
    {
        const string source = """
            <details>
            <summary>Quote parent</summary>

            > <details>
            > <summary>Quoted child</summary>
            > Quoted child body.

            Quote parent body.

            </details>

            <details>
            <summary>List parent</summary>

            - item

              <details>
              <summary>List child</summary>
              List child body.
              </details>

            # List parent heading

            List parent body.

            </details>
            """;

        using MarkdownEngine engine = new MarkdownEngineBuilder()
            .UseGitHubReadme()
            .Build();
        MarkdownRenderer.Document.MarkdownDocument document =
            await engine.ParseAsync(source);
        Markdig.Syntax.MarkdownDocument parsed = Assert.IsType<Markdig.Syntax.MarkdownDocument>(
            document.ParsedDocument);
        GitHubDetailsBlock[] topLevelDetails = parsed.OfType<GitHubDetailsBlock>().ToArray();
        GitHubDetailsBlock[] allDetails = parsed.Descendants().OfType<GitHubDetailsBlock>().ToArray();

        Assert.Equal(2, topLevelDetails.Length);
        Assert.Equal(4, allDetails.Length);
        Assert.All(allDetails, static details =>
            Assert.Equal(0, details.OpenDetailsDescendantCount));
        Assert.Single(topLevelDetails[0].OfType<ParagraphBlock>());
        ListBlock list = Assert.Single(topLevelDetails[1].OfType<ListBlock>());
        ListItemBlock listItem = Assert.Single(list.OfType<ListItemBlock>());
        Assert.Single(listItem.Descendants().OfType<GitHubDetailsBlock>());
        Assert.Single(topLevelDetails[1].OfType<HeadingBlock>());
        Assert.Single(topLevelDetails[1].OfType<ParagraphBlock>());
    }

    [Fact]
    public async Task DetailsOpeningAtListContentIndentRemainsInsideListItem()
    {
        using MarkdownEngine engine = new MarkdownEngineBuilder()
            .UseGitHubReadme()
            .Build();

        foreach (int indentWidth in new[] { 2, 4 })
        {
            string indent = new(' ', indentWidth);
            string standardSource = $"- item\n\n{indent}# Nested heading\n\n# Outside heading\n";
            Markdig.Syntax.MarkdownDocument standard = Markdig.Markdown.Parse(standardSource);
            ListItemBlock standardItem = Assert.Single(
                Assert.Single(standard.OfType<ListBlock>()).OfType<ListItemBlock>());
            Assert.Single(standardItem.Descendants().OfType<HeadingBlock>());

            string detailsSource = $"- item\n\n{indent}<details>\n" +
                $"{indent}<summary>Nested details</summary>\n" +
                $"{indent}Nested body.\n\n" +
                $"{indent}</details>\n\n# Outside heading\n";
            MarkdownRenderer.Document.MarkdownDocument document =
                await engine.ParseAsync(detailsSource);
            Markdig.Syntax.MarkdownDocument parsed = Assert.IsType<Markdig.Syntax.MarkdownDocument>(
                document.ParsedDocument);
            ListBlock list = Assert.Single(parsed.OfType<ListBlock>());
            ListItemBlock item = Assert.Single(list.OfType<ListItemBlock>());
            Assert.Single(parsed.OfType<HeadingBlock>());
            GitHubDetailsBlock? nested = item.Descendants().OfType<GitHubDetailsBlock>().SingleOrDefault();
            Assert.True(
                nested is not null,
                $"Details at the {indentWidth}-column list content indent was not nested.\n" +
                $"Standard Markdig accepted a heading at the same indent.\n" +
                DumpBlocks(parsed));
            Assert.Single(nested!.OfType<ParagraphBlock>());
            Assert.Equal(0, nested.OpenDetailsDescendantCount);

            string wrappedSource = $"<details>\n<summary>Parent</summary>\n\n" +
                $"- item\n\n{indent}<details>\n" +
                $"{indent}<summary>Nested details</summary>\n" +
                $"{indent}Nested body.\n\n" +
                $"{indent}</details>\n\n# Outside heading\n\n</details>\n";
            MarkdownRenderer.Document.MarkdownDocument wrappedDocument =
                await engine.ParseAsync(wrappedSource);
            Markdig.Syntax.MarkdownDocument wrappedParsed =
                Assert.IsType<Markdig.Syntax.MarkdownDocument>(wrappedDocument.ParsedDocument);
            GitHubDetailsBlock parent = Assert.Single(wrappedParsed.OfType<GitHubDetailsBlock>());
            ListItemBlock wrappedItem = Assert.Single(
                Assert.Single(parent.OfType<ListBlock>()).OfType<ListItemBlock>());
            GitHubDetailsBlock? wrappedNested =
                wrappedItem.Descendants().OfType<GitHubDetailsBlock>().SingleOrDefault();
            Assert.True(
                wrappedNested is not null,
                $"Details under an active parent container at the {indentWidth}-column list content " +
                $"indent was not nested.\n{DumpBlocks(wrappedParsed)}");
            Assert.Single(wrappedNested!.OfType<ParagraphBlock>());
            Assert.Equal(0, parent.OpenDetailsDescendantCount);
        }
    }

    [Fact]
    public async Task OpeningSiblingDetailsDoesNotCountAsDescendantOfClosedListDetails()
    {
        const string source = """
            <details>
            <summary>Parent</summary>

            - item

              <details>
              <summary>List child</summary>
              Child body.

            <details>
            <summary>Parent-level sibling</summary>
            Sibling body.
            </details>

            </details>
            """;

        using MarkdownEngine engine = new MarkdownEngineBuilder()
            .UseGitHubReadme()
            .Build();
        MarkdownRenderer.Document.MarkdownDocument document =
            await engine.ParseAsync(source);
        Markdig.Syntax.MarkdownDocument parsed = Assert.IsType<Markdig.Syntax.MarkdownDocument>(
            document.ParsedDocument);

        GitHubDetailsBlock parent = Assert.Single(parsed.OfType<GitHubDetailsBlock>());
        ListItemBlock item = Assert.Single(
            Assert.Single(parent.OfType<ListBlock>()).OfType<ListItemBlock>());
        GitHubDetailsBlock listChild = Assert.Single(item.Descendants().OfType<GitHubDetailsBlock>());
        GitHubDetailsBlock[] parentSiblings = parent.OfType<GitHubDetailsBlock>().ToArray();
        GitHubDetailsBlock parentSibling = Assert.Single(parentSiblings);
        Assert.Equal("Parent-level sibling", parentSibling.SummaryMarkup);
        Assert.Equal(0, listChild.OpenDetailsDescendantCount);
        Assert.Equal(0, parent.OpenDetailsDescendantCount);
        Assert.Equal(0, parentSibling.OpenDetailsDescendantCount);
    }

    [Fact]
    public async Task WideTableBreadthDoesNotBecomeDetailsNesting()
    {
        const int columnCount = 256;
        static string Row(IEnumerable<string> cells) => "| " + string.Join(" | ", cells) + " |";

        string[] headings = Enumerable.Range(0, columnCount)
            .Select(static index => $"Heading {index}")
            .ToArray();
        string[] separators = Enumerable.Repeat("---", columnCount).ToArray();
        string[] cells = Enumerable.Range(0, columnCount)
            .Select(static index => $"Cell {index}")
            .ToArray();
        string source = string.Join('\n', Row(headings), Row(separators), Row(cells));

        using MarkdownEngine engine = new MarkdownEngineBuilder()
            .UseGitHubReadme()
            .Build();
        MarkdownRenderer.Document.MarkdownDocument document =
            await engine.ParseAsync(source);
        Markdig.Syntax.MarkdownDocument parsed = Assert.IsType<Markdig.Syntax.MarkdownDocument>(
            document.ParsedDocument);

        Table table = Assert.Single(parsed.Descendants().OfType<Table>());
        TableRow[] rows = table.OfType<TableRow>().ToArray();
        Assert.Equal(2, rows.Length);
        Assert.Equal(columnCount, rows[0].Count());
        Assert.Equal(columnCount, rows[1].Count());
    }

    private static IEnumerable<GitHubDetailsBlock> EnumerateDetails(GitHubDetailsBlock details)
    {
        yield return details;
        foreach (GitHubDetailsBlock child in details.OfType<GitHubDetailsBlock>())
        {
            foreach (GitHubDetailsBlock descendant in EnumerateDetails(child))
                yield return descendant;
        }
    }

    private static string CreateSiblingDetailsSource(int detailsCount)
    {
        var source = new StringBuilder(detailsCount * 80);
        for (int index = 0; index < detailsCount; index++)
        {
            source.AppendLine("<details>");
            source.Append("<summary>Question ").Append(index).AppendLine("</summary>");
            source.Append("Answer ").Append(index).AppendLine();
            source.AppendLine("</details>");
            source.AppendLine();
        }

        return source.ToString();
    }

    private static string DumpBlocks(ContainerBlock container)
    {
        var output = new StringBuilder();
        AppendBlocks(output, container, 0);
        return output.ToString();
    }

    private static void AppendBlocks(StringBuilder output, ContainerBlock container, int depth)
    {
        foreach (Block block in container)
        {
            output.Append(' ', depth * 2)
                .Append(block.GetType().Name)
                .Append(" span=")
                .Append(block.Span)
                .AppendLine();
            if (block is ContainerBlock child)
                AppendBlocks(output, child, depth + 1);
        }
    }

}
