using Markdig;
using Markdig.Extensions.Footnotes;
using Markdig.Extensions.Tables;
using Markdig.Extensions.TaskLists;
using Markdig.Renderers.Html;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using Xunit;

namespace MarkdownRenderer.Tests;

public class GfmIntegrationTests
{
    private static MarkdownPipeline GfmPipeline() =>
        new MarkdownPipelineBuilder()
            .UsePipeTables()
            .UseTaskLists()
            .UseAutoLinks()
            .UseEmphasisExtras()
            .UseEmojiAndSmiley()
            .UseFootnotes()
            .UseGenericAttributes()
            .Build();

    private static MarkdownDocument Parse(string md, MarkdownPipeline? pipeline = null) =>
        Markdown.Parse(md, pipeline ?? GfmPipeline());

    // 1. Alert syntax — Markdig has no native GitHub-alert transform;
    //    "> [!NOTE]" renders as a plain QuoteBlock.
    [Fact]
    public void Alert_NoteBlock_ParsesAsQuoteBlock()
    {
        var md = "> [!NOTE]\n> Content here.";
        var doc = Parse(md);
        var quote = Assert.Single(doc.OfType<QuoteBlock>());
        Assert.NotNull(quote);
    }

    [Fact]
    public void Alert_NoteBlock_ContentPreservedInHtml()
    {
        var md = "> [!NOTE]\n> Content here.";
        var html = Markdown.ToHtml(md, GfmPipeline());
        Assert.Contains("[!NOTE]", html);
        Assert.Contains("Content here", html);
    }

    // 2. Table — rows and columns
    [Fact]
    public void Table_HasCorrectRowCount()
    {
        var md = "| H1 | H2 |\n|---|---|\n| R1C1 | R1C2 |\n| R2C1 | R2C2 |";
        var doc = Parse(md);
        var table = Assert.Single(doc.OfType<Table>());
        // header row + 2 data rows = 3
        Assert.Equal(3, table.OfType<TableRow>().Count());
    }

    [Fact]
    public void Table_HeaderRow_IsMarkedAsHeader()
    {
        var md = "| A | B |\n|---|---|\n| 1 | 2 |";
        var doc = Parse(md);
        var table = Assert.Single(doc.OfType<Table>());
        var firstRow = table.OfType<TableRow>().First();
        Assert.True(firstRow.IsHeader);
    }

    [Fact]
    public void Table_DataRow_IsNotMarkedAsHeader()
    {
        var md = "| A | B |\n|---|---|\n| 1 | 2 |";
        var doc = Parse(md);
        var table = Assert.Single(doc.OfType<Table>());
        var dataRow = table.OfType<TableRow>().Last();
        Assert.False(dataRow.IsHeader);
    }

    [Fact]
    public void Table_ColumnDefinitions_PreserveAlignment()
    {
        var md = "| Left | Center | Right |\n|:---|:---:|---:|\n| a | b | c |";
        var doc = Parse(md);
        var table = Assert.Single(doc.OfType<Table>());

        Assert.Equal(TableColumnAlign.Left, table.ColumnDefinitions[0].Alignment);
        Assert.Equal(TableColumnAlign.Center, table.ColumnDefinitions[1].Alignment);
        Assert.Equal(TableColumnAlign.Right, table.ColumnDefinitions[2].Alignment);
    }

    // 3. Task list — checked and unchecked
    [Fact]
    public void TaskList_CheckedItem_HasCheckedTrue()
    {
        var md = "- [x] done\n- [ ] todo";
        var doc = Parse(md);
        var list = Assert.Single(doc.OfType<ListBlock>());
        var items = list.OfType<ListItemBlock>().ToList();
        Assert.Equal(2, items.Count);

        var firstPara = items[0].OfType<ParagraphBlock>().First();
        var checkedTask = firstPara.Inline!.OfType<TaskList>().First();
        Assert.True(checkedTask.Checked);
    }

    [Fact]
    public void TaskList_UncheckedItem_HasCheckedFalse()
    {
        var md = "- [x] done\n- [ ] todo";
        var doc = Parse(md);
        var list = Assert.Single(doc.OfType<ListBlock>());
        var items = list.OfType<ListItemBlock>().ToList();

        var secondPara = items[1].OfType<ParagraphBlock>().First();
        var uncheckedTask = secondPara.Inline!.OfType<TaskList>().First();
        Assert.False(uncheckedTask.Checked);
    }

    // 4. Footnote — reference and definition
    [Fact]
    public void Footnote_Reference_ProducesFootnoteGroup()
    {
        var md = "Text with footnote[^1].\n\n[^1]: Footnote text.";
        var doc = Parse(md);
        var footnoteGroup = doc.OfType<FootnoteGroup>().FirstOrDefault();
        Assert.NotNull(footnoteGroup);
    }

    [Fact]
    public void Footnote_InlineParagraph_ContainsFootnoteLink()
    {
        var md = "Text[^fn].\n\n[^fn]: The footnote.";
        var doc = Parse(md);
        var para = doc.OfType<ParagraphBlock>().First();
        var footnoteLink = para.Inline!.Descendants().OfType<FootnoteLink>().FirstOrDefault();
        Assert.NotNull(footnoteLink);
    }

    // 5. Strikethrough
    [Fact]
    public void Strikethrough_ProducesEmphasisWithTildeDelimiter()
    {
        var md = "~~struck~~";
        var doc = Parse(md);
        var para = Assert.Single(doc.OfType<ParagraphBlock>());
        var strikeEmphasis = para.Inline!
            .OfType<EmphasisInline>()
            .FirstOrDefault(e => e.DelimiterChar == '~');
        Assert.NotNull(strikeEmphasis);
        Assert.Equal(2, strikeEmphasis.DelimiterCount);
    }

    // 6. Autolinks — bare URL → HTML anchor
    [Fact]
    public void AutoLink_BareUrl_ProducesHtmlAnchor()
    {
        var md = "Visit https://example.com for info.";
        var html = Markdown.ToHtml(md, GfmPipeline());
        Assert.Contains("<a href=\"https://example.com\"", html);
    }

    [Fact]
    public void AutoLink_AngleBracketUrl_RendersAsHtmlAnchor()
    {
        var md = "See <https://example.com>.";
        var html = Markdown.ToHtml(md, GfmPipeline());
        Assert.Contains("href=\"https://example.com\"", html);
        Assert.Contains("<a ", html);
    }

    // 7. Emoji — :smile: colons are consumed by the extension
    [Fact]
    public void Emoji_ColonSyntax_IsReplacedInHtml()
    {
        var md = "Hello :smile: world";
        var html = Markdown.ToHtml(md, GfmPipeline());
        Assert.DoesNotContain(":smile:", html);
    }

    [Fact]
    public void Emoji_ColonSyntax_ProducesNonEmptyOutput()
    {
        var md = ":+1:";
        var html = Markdown.ToHtml(md, GfmPipeline());
        Assert.False(string.IsNullOrWhiteSpace(html));
        Assert.DoesNotContain(":+1:", html);
    }

    // 8. Math NOT in pipeline — dollar signs treated as plain text
    [Fact]
    public void Math_WithoutExtension_NotParsedAsMathNode()
    {
        var md = "Equation: $x^2 + y^2 = z^2$";
        var html = Markdown.ToHtml(md, GfmPipeline());
        Assert.DoesNotContain("<math", html.ToLowerInvariant());
        Assert.DoesNotContain("katex", html.ToLowerInvariant());
    }

    [Fact]
    public void Math_InlineDollar_RemainsLiteralText()
    {
        var md = "Price: $42";
        var doc = Parse(md);
        var para = Assert.Single(doc.OfType<ParagraphBlock>());
        Assert.NotNull(para);
        // Without math extension the paragraph is a plain ParagraphBlock
        var html = Markdown.ToHtml(md, GfmPipeline());
        Assert.Contains("$42", html);
    }

    // 9. Nested lists — 3 levels deep
    [Fact]
    public void NestedList_ThreeLevels_ParsesCorrectly()
    {
        var md = "- Level 1\n  - Level 2\n    - Level 3";
        var doc = Parse(md);
        var topList = Assert.Single(doc.OfType<ListBlock>());
        var level1Item = topList.OfType<ListItemBlock>().First();

        var level2List = level1Item.OfType<ListBlock>().FirstOrDefault();
        Assert.NotNull(level2List);

        var level2Item = level2List.OfType<ListItemBlock>().First();
        var level3List = level2Item.OfType<ListBlock>().FirstOrDefault();
        Assert.NotNull(level3List);

        Assert.Single(level3List.OfType<ListItemBlock>());
    }

    [Fact]
    public void NestedList_Mixed_OrderedAndUnordered()
    {
        var md = "1. Ordered\n   - Nested unordered\n   - Another";
        var doc = Parse(md);
        var outerList = Assert.Single(doc.OfType<ListBlock>());
        Assert.True(outerList.IsOrdered);

        var outerItem = outerList.OfType<ListItemBlock>().First();
        var innerList = outerItem.OfType<ListBlock>().FirstOrDefault();
        Assert.NotNull(innerList);
        Assert.False(innerList.IsOrdered);
    }

    [Fact]
    public void GenericAttributes_AttachClassesAndIdsToMarkdownObjects()
    {
        var md = "## Heading {#intro .warning .wide}\n\nParagraph with [link](https://example.com){.cta}.";
        var doc = Parse(md);

        var heading = Assert.Single(doc.OfType<HeadingBlock>());
        var headingAttrs = HtmlAttributesExtensions.TryGetAttributes(heading);
        Assert.NotNull(headingAttrs);
        Assert.Equal("intro", headingAttrs!.Id);
        Assert.Equal(new[] { "warning", "wide" }, headingAttrs.Classes);

        var paragraph = Assert.Single(doc.OfType<ParagraphBlock>());
        var link = paragraph.Inline!.Descendants().OfType<LinkInline>().Single();
        var linkAttrs = HtmlAttributesExtensions.TryGetAttributes(link);
        Assert.NotNull(linkAttrs);
        Assert.Equal(new[] { "cta" }, linkAttrs!.Classes);
    }

    // 10. Fenced code block language tag
    [Fact]
    public void FencedCode_WithLanguage_InfoPropertyIsSet()
    {
        var md = "```typescript\nconst x: number = 42;\n```";
        var doc = Parse(md);
        var code = Assert.Single(doc.OfType<FencedCodeBlock>());
        Assert.Equal("typescript", code.Info);
    }

    [Fact]
    public void FencedCode_NoLanguage_InfoIsNullOrEmpty()
    {
        var md = "```\nplain code\n```";
        var doc = Parse(md);
        var code = Assert.Single(doc.OfType<FencedCodeBlock>());
        Assert.True(string.IsNullOrEmpty(code.Info));
    }

    [Fact]
    public void FencedCode_CSharp_CodeContentPreserved()
    {
        var md = "```csharp\nint x = 1;\n```";
        var doc = Parse(md);
        var code = Assert.Single(doc.OfType<FencedCodeBlock>());
        Assert.Equal("csharp", code.Info);
        var lines = code.Lines.ToString();
        Assert.Contains("int x = 1;", lines);
    }
}
