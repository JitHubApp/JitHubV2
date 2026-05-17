using Xunit;
using Markdig;
using Markdig.Extensions.Abbreviations;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using Markdig.Extensions.Tables;
using Markdig.Extensions.TaskLists;
using MarkdownRenderer.Parsing;

namespace MarkdownRenderer.Tests;

public class MarkdigParserTests
{
    private static MarkdigParser DefaultParser()
    {
        var pipeline = new MarkdownPipelineBuilder().Build();
        return new MarkdigParser(pipeline);
    }

    private static MarkdigParser GfmParser()
    {
        var pipeline = new MarkdownPipelineBuilder()
            .UsePipeTables()
            .UseTaskLists()
            .UseAutoLinks()
            .UseEmphasisExtras()
            .UseEmojiAndSmiley()
            .UseGenericAttributes()
            .Build();
        return new MarkdigParser(pipeline);
    }

    // ── Constructor ──────────────────────────────────────────────────────────

    [Fact]
    public void Constructor_NullPipeline_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new MarkdigParser(null!));
    }

    // ── Parse (sync) ─────────────────────────────────────────────────────────

    [Fact]
    public void Parse_NullSource_TreatedAsEmpty()
    {
        var result = DefaultParser().Parse(null!);
        Assert.Equal(string.Empty, result.SourceText);
        Assert.NotNull(result.Document);
    }

    [Fact]
    public void Parse_EmptySource_ReturnsEmptyDocument()
    {
        var result = DefaultParser().Parse(string.Empty);
        Assert.Equal(string.Empty, result.SourceText);
        Assert.Empty(result.Document);
    }

    [Fact]
    public void Parse_RetainsSourceText()
    {
        const string md = "# Hello";
        var result = DefaultParser().Parse(md);
        Assert.Equal(md, result.SourceText);
    }

    // ── ParseAsync ───────────────────────────────────────────────────────────

    [Fact]
    public async Task ParseAsync_NullSource_TreatedAsEmpty()
    {
        var result = await DefaultParser().ParseAsync(null!);
        Assert.NotNull(result);
        Assert.Equal(string.Empty, result.SourceText);
    }

    [Fact]
    public async Task ParseAsync_CancellationToken_ReturnsNull()
    {
        var cts = new CancellationTokenSource();
        cts.Cancel();
        var result = await DefaultParser().ParseAsync("# Hello", cts.Token);
        Assert.Null(result);
    }

    // ── Block structure ──────────────────────────────────────────────────────

    [Fact]
    public void Parse_Heading1_ProducesHeadingBlock()
    {
        var doc = DefaultParser().Parse("# Hello").Document;
        var heading = Assert.Single(doc.OfType<HeadingBlock>());
        Assert.Equal(1, heading.Level);
    }

    [Fact]
    public void Parse_Heading3_HasCorrectLevel()
    {
        var doc = DefaultParser().Parse("### Third").Document;
        var heading = Assert.Single(doc.OfType<HeadingBlock>());
        Assert.Equal(3, heading.Level);
    }

    [Fact]
    public void Parse_Paragraph_ProducesParagraphBlock()
    {
        var doc = DefaultParser().Parse("Just a paragraph.").Document;
        Assert.Single(doc.OfType<ParagraphBlock>());
    }

    [Fact]
    public void Parse_FencedCodeBlock_ProducesCodeBlock()
    {
        var md = "```csharp\nvar x = 1;\n```";
        var doc = DefaultParser().Parse(md).Document;
        var code = Assert.Single(doc.OfType<FencedCodeBlock>());
        Assert.Equal("csharp", code.Info);
    }

    [Fact]
    public void Parse_IndentedCode_ProducesCodeBlock()
    {
        var md = "    int x = 1;";
        var doc = DefaultParser().Parse(md).Document;
        Assert.Single(doc.OfType<CodeBlock>());
    }

    [Fact]
    public void Parse_BlockQuote_ProducesQuoteBlock()
    {
        var doc = DefaultParser().Parse("> a quote").Document;
        Assert.Single(doc.OfType<QuoteBlock>());
    }

    [Fact]
    public void Parse_BulletList_ProducesListBlock()
    {
        var md = "- item one\n- item two\n- item three";
        var doc = DefaultParser().Parse(md).Document;
        var list = Assert.Single(doc.OfType<ListBlock>());
        Assert.False(list.IsOrdered);
        Assert.Equal(3, list.Count);
    }

    [Fact]
    public void Parse_OrderedList_IsMarkedOrdered()
    {
        var md = "1. first\n2. second";
        var doc = DefaultParser().Parse(md).Document;
        var list = Assert.Single(doc.OfType<ListBlock>());
        Assert.True(list.IsOrdered);
    }

    [Fact]
    public void Parse_ThematicBreak_ProducesThematicBreak()
    {
        var doc = DefaultParser().Parse("---").Document;
        Assert.Single(doc.OfType<ThematicBreakBlock>());
    }

    // ── Inline structure ─────────────────────────────────────────────────────

    [Fact]
    public void Parse_EmphasisInline_ProducesEmphasisInline()
    {
        var doc = DefaultParser().Parse("*italic*").Document;
        var para = Assert.Single(doc.OfType<ParagraphBlock>());
        Assert.Contains(para.Inline!, i => i is EmphasisInline);
    }

    [Fact]
    public void Parse_InlineCode_ProducesCodeInline()
    {
        var doc = DefaultParser().Parse("Use `code` here.").Document;
        var para = Assert.Single(doc.OfType<ParagraphBlock>());
        Assert.Contains(para.Inline!, i => i is CodeInline);
    }

    [Fact]
    public void Parse_Link_ProducesLinkInline()
    {
        var doc = DefaultParser().Parse("[GitHub](https://github.com)").Document;
        var para = Assert.Single(doc.OfType<ParagraphBlock>());
        var link = para.Inline!.OfType<LinkInline>().FirstOrDefault();
        Assert.NotNull(link);
        Assert.Equal("https://github.com", link.Url);
    }

    [Fact]
    public void Parse_AbbreviationOccurrences_CanBeNestedInsideGenericContainerInline()
    {
        const string md = """
            HTML and SVG expansions should expose abbreviation runs.

            *[HTML]: Hyper Text Markup Language
            *[SVG]: Scalable Vector Graphics
            """;

        var pipeline = new MarkdownPipelineBuilder()
            .UseAbbreviations()
            .Build();
        var doc = new MarkdigParser(pipeline).Parse(md).Document;
        var para = Assert.Single(doc.OfType<ParagraphBlock>());

        Assert.DoesNotContain(para.Inline!, inline => inline is AbbreviationInline);

        var abbreviations = DescendantInlines(para.Inline!)
            .OfType<AbbreviationInline>()
            .ToArray();

        Assert.Collection(
            abbreviations,
            abbreviation =>
            {
                Assert.Equal("HTML", abbreviation.Abbreviation?.Label);
                Assert.Equal("Hyper Text Markup Language", abbreviation.Abbreviation?.Text.ToString());
            },
            abbreviation =>
            {
                Assert.Equal("SVG", abbreviation.Abbreviation?.Label);
                Assert.Equal("Scalable Vector Graphics", abbreviation.Abbreviation?.Text.ToString());
            });
    }

    // ── GFM extensions ───────────────────────────────────────────────────────

    [Fact]
    public void GfmParser_PipeTable_ProducesTableBlock()
    {
        var md = "| A | B |\n|---|---|\n| 1 | 2 |";
        var doc = GfmParser().Parse(md).Document;
        Assert.Single(doc.OfType<Table>());
    }

    [Fact]
    public void GfmParser_Table_HasCorrectColumnCount()
    {
        var md = "| A | B | C |\n|---|---|---|\n| 1 | 2 | 3 |";
        var doc = GfmParser().Parse(md).Document;
        var table = Assert.Single(doc.OfType<Table>());
        var headerRow = table.OfType<TableRow>().First();
        Assert.Equal(3, headerRow.Count);
    }

    [Fact]
    public void GfmParser_TaskList_ProducesTaskListItem()
    {
        var md = "- [x] done\n- [ ] todo";
        var doc = GfmParser().Parse(md).Document;
        var list = Assert.Single(doc.OfType<ListBlock>());
        // Both list items should have a TaskList object in their first paragraph's inline
        int taskCount = 0;
        foreach (var item in list.OfType<ListItemBlock>())
        {
            foreach (var block in item.OfType<ParagraphBlock>())
            {
                if (block.Inline!.OfType<TaskList>().Any())
                    taskCount++;
            }
        }
        Assert.Equal(2, taskCount);
    }

    [Fact]
    public void GfmParser_StrikethroughEmphasis_Parsed()
    {
        var md = "~~strike~~";
        var doc = GfmParser().Parse(md).Document;
        var para = Assert.Single(doc.OfType<ParagraphBlock>());
        Assert.Contains(para.Inline!, i => i is EmphasisInline e && e.DelimiterCount == 2);
    }

    [Fact]
    public void MultipleBlocks_CountMatchesExpected()
    {
        var md = "# Title\n\nParagraph one.\n\n> A quote\n\n- item";
        var doc = DefaultParser().Parse(md).Document;
        Assert.Equal(1, doc.OfType<HeadingBlock>().Count());
        Assert.Equal(1, doc.OfType<ParagraphBlock>().Count());
        Assert.Equal(1, doc.OfType<QuoteBlock>().Count());
        Assert.Equal(1, doc.OfType<ListBlock>().Count());
    }

    private static IEnumerable<Inline> DescendantInlines(ContainerInline container)
    {
        foreach (var inline in container)
        {
            yield return inline;
            if (inline is not ContainerInline nested)
                continue;

            foreach (var descendant in DescendantInlines(nested))
                yield return descendant;
        }
    }
}
