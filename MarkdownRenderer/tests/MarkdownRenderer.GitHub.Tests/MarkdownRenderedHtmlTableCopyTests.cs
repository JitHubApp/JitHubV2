using System.Xml.Linq;
using MarkdownRenderer.Accessibility;
using MarkdownRenderer.Document;
using MarkdownRenderer.Extensions;
using MarkdownRenderer.GitHub;
using MarkdownRenderer.Html;
using MarkdownRenderer.Layout;
using MarkdownRenderer.Parsing;
using MarkdownRenderer.Selection;
using MarkdownRenderer.Theming;
using Microsoft.Graphics.Canvas;
using Microsoft.UI.Xaml;
using Windows.UI;
using Xunit;

namespace MarkdownRenderer.GitHub.Tests;

public sealed class MarkdownRenderedHtmlTableCopyTests
{
    [Theory]
    [InlineData("| Name | Value |\n| --- | --- |\n| alpha | one |")]
    [InlineData("<table><thead><tr><th>Name</th><th>Value</th></tr></thead><tbody><tr><td>alpha</td><td>one</td></tr></tbody></table>")]
    public async Task FullTableCopyPreservesRowsColumnsAndHeaderCells(string source)
    {
        using LayoutSnapshot snapshot = await Build(source);
        MarkdownSemanticDocument semantic = snapshot.SemanticDocument;
        MarkdownSemanticNode table = Assert.Single(
            MarkdownSemanticDocument.EnumerateDepthFirst(semantic.Root),
            static node => node.Role == MarkdownSemanticRole.Table);
        Assert.Equal(2, table.RowCount);
        Assert.Equal(2, table.ColumnCount);
        Assert.Equal(new[] { "Name", "Value", "alpha", "one" },
            table.Children.Select(semantic.GetText));

        XElement fragment = XElement.Parse(MarkdownRenderedHtmlWriter.BuildFragment(
            semantic, table.TextStart, table.TextEnd));
        XElement copiedTable = Assert.Single(fragment.Descendants("table"));
        XElement[] rows = copiedTable.Descendants("tr").ToArray();
        Assert.Equal(2, rows.Length);
        Assert.Equal(new[] { "Name", "Value" }, rows[0].Elements("th").Select(cell => cell.Value));
        Assert.Equal(new[] { "alpha", "one" }, rows[1].Elements("td").Select(cell => cell.Value));

        using LayoutSnapshot pasted = await Build(fragment.ToString(SaveOptions.DisableFormatting));
        MarkdownSemanticDocument pastedSemantic = pasted.SemanticDocument;
        MarkdownSemanticNode pastedTable = Assert.Single(
            MarkdownSemanticDocument.EnumerateDepthFirst(pastedSemantic.Root),
            static node => node.Role == MarkdownSemanticRole.Table);
        Assert.Equal(2, pastedTable.RowCount);
        Assert.Equal(2, pastedTable.ColumnCount);
        Assert.Equal(new[] { "Name", "Value", "alpha", "one" },
            pastedTable.Children.Select(pastedSemantic.GetText));
    }

    [Fact]
    public async Task PartialRowCopiesOnlySelectedCharactersWithoutNeighboringLabels()
    {
        using LayoutSnapshot snapshot = await Build(
            "| Heading one | Heading two |\n| --- | --- |\n| prefixABC | DEFsuffix |\n| next one | next two |");
        MarkdownSemanticDocument semantic = snapshot.SemanticDocument;
        int start = semantic.Text.IndexOf("ABC", StringComparison.Ordinal);
        int end = semantic.Text.IndexOf("DEF", StringComparison.Ordinal) + 3;
        XElement fragment = XElement.Parse(MarkdownRenderedHtmlWriter.BuildFragment(semantic, start, end));
        XElement row = Assert.Single(Assert.Single(fragment.Descendants("table")).Descendants("tr"));

        Assert.Equal(new[] { "ABC", "DEF" }, row.Elements("td").Select(cell => cell.Value));
        Assert.Equal("ABCDEF", fragment.Value);
    }

    [Fact]
    public async Task CrossRowSelectionRetainsEmptyColumnPositionsWithoutUnselectedContent()
    {
        using LayoutSnapshot snapshot = await Build("| First | secondTAIL |\n| --- | --- |\n| HEADthird | fourth |");
        MarkdownSemanticDocument semantic = snapshot.SemanticDocument;
        int start = semantic.Text.IndexOf("TAIL", StringComparison.Ordinal);
        int end = semantic.Text.IndexOf("HEAD", StringComparison.Ordinal) + 4;
        XElement fragment = XElement.Parse(MarkdownRenderedHtmlWriter.BuildFragment(semantic, start, end));
        XElement[] rows = Assert.Single(fragment.Descendants("table")).Descendants("tr").ToArray();

        Assert.Equal(2, rows.Length);
        Assert.Equal(new[] { "", "TAIL" }, rows[0].Elements("th").Select(cell => cell.Value));
        Assert.Equal(new[] { "HEAD", "" }, rows[1].Elements("td").Select(cell => cell.Value));
        Assert.Equal("TAILHEAD", fragment.Value);
    }

    [Fact]
    public async Task FullTablePreservesAuthoredEmptyCellsAndRows()
    {
        using LayoutSnapshot snapshot = await Build("| | Header |\n| --- | --- |\n| | |\n| value | |");
        MarkdownSemanticDocument semantic = snapshot.SemanticDocument;
        XElement fragment = XElement.Parse(MarkdownRenderedHtmlWriter.BuildFragment(semantic, 0, semantic.Text.Length));
        XElement[] rows = Assert.Single(fragment.Descendants("table")).Descendants("tr").ToArray();

        Assert.Equal(3, rows.Length);
        Assert.Equal(new[] { "", "Header" }, rows[0].Elements("th").Select(cell => cell.Value));
        Assert.Equal(new[] { "", "" }, rows[1].Elements("td").Select(cell => cell.Value));
        Assert.Equal(new[] { "value", "" }, rows[2].Elements("td").Select(cell => cell.Value));
    }

    [Fact]
    public async Task CellFormattingAndEscapingRemainSafeAndBalanced()
    {
        using LayoutSnapshot snapshot = await Build(
            "| Header | Value |\n| --- | --- |\n| **strong** and `left  right` | [safe](https://example.test/?a=1&b=2) &lt;script&gt; |");
        MarkdownSemanticDocument semantic = snapshot.SemanticDocument;
        XElement fragment = XElement.Parse(MarkdownRenderedHtmlWriter.BuildFragment(semantic, 0, semantic.Text.Length));

        Assert.Equal("strong", Assert.Single(fragment.Descendants("strong")).Value);
        XElement code = Assert.Single(fragment.Descendants("code"));
        Assert.Equal("left  right", code.Value);
        Assert.Equal("white-space: pre-wrap", code.Attribute("style")?.Value);
        XElement link = Assert.Single(fragment.Descendants("a"));
        Assert.Equal("safe", link.Value);
        Assert.Equal("https://example.test/?a=1&b=2", link.Attribute("href")?.Value);
        Assert.Contains("<script>", fragment.Value, StringComparison.Ordinal);
        Assert.Empty(fragment.Descendants("script"));
    }

    [Fact]
    public async Task TableDoesNotDuplicateCellTextOrAbsorbNeighboringCodeBlocks()
    {
        using LayoutSnapshot snapshot = await Build(
            "before\n\n```text\n  code before\n```\n\n| H1 | H2 |\n| --- | --- |\n| cell-one | cell-two |\n\n```text\n\tcode after\n```\n\nafter");
        MarkdownSemanticDocument semantic = snapshot.SemanticDocument;
        XElement fragment = XElement.Parse(MarkdownRenderedHtmlWriter.BuildFragment(semantic, 0, semantic.Text.Length));

        XElement table = Assert.Single(fragment.Elements("table"));
        Assert.Equal(new[] { "cell-one", "cell-two" }, table.Descendants("td").Select(cell => cell.Value));
        Assert.Equal(new[] { "  code before", "\tcode after" }, fragment.Elements("pre").Select(pre => pre.Value));
        Assert.Equal(1, fragment.Value.Split("cell-one", StringSplitOptions.None).Length - 1);
        Assert.StartsWith("before", fragment.Value, StringComparison.Ordinal);
        Assert.EndsWith("after", fragment.Value, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SelectingOnlySyntheticCellSeparatorKeepsTheSelectedLineBreak()
    {
        using LayoutSnapshot snapshot = await Build("| First | Second |\n| --- | --- |\n| third | fourth |");
        MarkdownSemanticDocument semantic = snapshot.SemanticDocument;
        int start = semantic.Text.IndexOf('\n');
        Assert.True(start >= 0);
        XElement fragment = XElement.Parse(MarkdownRenderedHtmlWriter.BuildFragment(semantic, start, start + 1));

        Assert.Empty(fragment.Descendants("table"));
        Assert.Single(fragment.Elements("br"));
        Assert.Empty(fragment.Value);
    }

    [Fact]
    public async Task PartialPreformattedCellCopyDoesNotEmitAnExtraBlockOutsideTheTable()
    {
        using LayoutSnapshot snapshot = await Build(
            "<table><tr><th>Header</th></tr><tr><td><pre>  prefixTARGETsuffix\n\tmore</pre></td></tr></table>");
        MarkdownSemanticDocument semantic = snapshot.SemanticDocument;
        Assert.Contains(
            MarkdownSemanticDocument.EnumerateDepthFirst(semantic.Root),
            static node => node.Role == MarkdownSemanticRole.CodeBlock &&
                node.Parent?.Role == MarkdownSemanticRole.TableCell);
        int start = semantic.Text.IndexOf("TARGET", StringComparison.Ordinal);
        Assert.True(start >= 0);
        XElement fragment = XElement.Parse(MarkdownRenderedHtmlWriter.BuildFragment(semantic, start, start + 6));

        XElement code = Assert.Single(fragment.Descendants("code"));
        Assert.Equal("TARGET", code.Value);
        Assert.Equal("td", code.Parent?.Parent?.Name.LocalName);
        Assert.Empty(fragment.Elements("pre"));
        Assert.Equal("TARGET", fragment.Value);
    }

    [Theory]
    [InlineData(FlowDirection.LeftToRight)]
    [InlineData(FlowDirection.RightToLeft)]
    public async Task UnicodeTableCopyKeepsLogicalColumnOrderInEitherVisualDirection(FlowDirection direction)
    {
        using LayoutSnapshot snapshot = await Build(
            "| שם | ערך |\n| --- | --- |\n| ראשון 😀 | שני e\u0301 |", direction);
        MarkdownSemanticDocument semantic = snapshot.SemanticDocument;
        XElement fragment = XElement.Parse(MarkdownRenderedHtmlWriter.BuildFragment(semantic, 0, semantic.Text.Length));
        XElement table = Assert.Single(fragment.Elements("table"));

        Assert.Equal(new[] { "שם", "ערך" }, table.Descendants("th").Select(cell => cell.Value));
        Assert.Equal(new[] { "ראשון 😀", "שני e\u0301" }, table.Descendants("td").Select(cell => cell.Value));
    }

    [Fact]
    public async Task AuthoredCellHardBreakIsNotConfusedWithGeneratedCellSeparator()
    {
        using LayoutSnapshot snapshot = await Build("<table><tr><td>Tail<br></td></tr></table>");
        MarkdownSemanticDocument semantic = snapshot.SemanticDocument;
        Assert.Equal("Tail\n", semantic.Text);
        XElement fragment = XElement.Parse(MarkdownRenderedHtmlWriter.BuildFragment(semantic, 0, semantic.Text.Length));
        XElement cell = Assert.Single(fragment.Descendants("td"));

        Assert.Equal("Tail", cell.Value);
        Assert.Single(cell.Elements("br"));
        Assert.Empty(fragment.Elements("br"));
    }

    private static async Task<LayoutSnapshot> Build(
        string source,
        FlowDirection direction = FlowDirection.LeftToRight)
    {
        using MarkdownEngine engine = new MarkdownEngineBuilder()
            .UseGitHubReadme(SafeHtmlOptions.Default)
            .Build();
        MarkdownDocument document = await engine.ParseAsync(source);
        MarkdownExtensionRegistry registry = Assert.IsType<MarkdownExtensionRegistry>(
            engine.PresentationConfiguration);
        var style = new ElementStyle();
        var theme = new ThemeSnapshot(
            new Dictionary<string, ElementStyle> { [MarkdownElementKeys.Body] = style },
            new Dictionary<string, ElementStyleOverride>(),
            default(Color), default, default, default,
            isDark: false, isHighContrast: false, textScaleFactor: 1);
        var context = new MarkdownLayoutContext(
            CanvasDevice.GetSharedDevice(forceSoftwareRenderer: true),
            theme,
            new MarkdownSourceMap(source),
            registry,
            direction);
        return new LayoutBuilder(context, semanticDocument: document)
            .Build(document.ParsedDocument!, 600);
    }
}
