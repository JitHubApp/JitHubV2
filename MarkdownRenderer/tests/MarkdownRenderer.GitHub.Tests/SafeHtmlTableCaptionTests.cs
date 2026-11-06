using System.Collections.Generic;
using System.Linq;
using System.Xml.Linq;
using MarkdownRenderer.Accessibility;
using Markdig;
using MarkdownRenderer.Document;
using MarkdownRenderer.GitHub;
using MarkdownRenderer.Html;
using MarkdownRenderer.Layout;
using MarkdownRenderer.Layout.Boxes;
using MarkdownRenderer.Parsing;
using MarkdownRenderer.Selection;
using MarkdownRenderer.Theming;
using Microsoft.Graphics.Canvas;
using Microsoft.UI.Xaml;
using Windows.UI;
using Xunit;

namespace MarkdownRenderer.GitHub.Tests;

public sealed class SafeHtmlTableCaptionTests
{
    [Fact]
    public void CaptionContentPrecedesUnchangedTableAndRetainsFormattingAndSource()
    {
        const string source =
            "<table><caption>Native <strong>HTML</strong> table</caption>" +
            "<thead><tr><th>Capability</th></tr></thead>" +
            "<tbody><tr><td>Behavior</td></tr></tbody></table>";

        using LayoutSnapshot snapshot = Build(source);
        TableBox table = Assert.Single(EnumerateBlocks(snapshot).OfType<TableBox>());
        Assert.Equal(1, table.ColumnCount);
        Assert.Equal(1, table.HeaderRowCount);
        Assert.Equal(1, table.BodyRowCount);

        InlineContainerBox caption = Assert.Single(
            EnumerateBlocks(snapshot).OfType<InlineContainerBox>(),
            static box => box.ElementKey == MarkdownElementKeys.Body &&
                box.Runs.Any(static run => run.Text == "Native "));
        Assert.Equal("Native HTML table", string.Concat(caption.Runs.Select(static run => run.Text)));

        InlineRun strong = Assert.Single(caption.Runs, static run => run.Text == "HTML");
        Assert.Equal(MarkdownElementKeys.Strong, strong.ElementKey);
        TextRun first = Assert.Single(caption.Runs.OfType<TextRun>(), static run => run.Text == "Native ");
        int captionTextStart = source.IndexOf("Native ", System.StringComparison.Ordinal);
        Assert.Equal(new SourceSpan(captionTextStart, "Native ".Length), first.SourceSpan);
        Assert.Equal("Native ", source.Substring(first.SourceSpan.Start, first.SourceSpan.Length));
        Assert.Equal(
            new SourceSpan(source.IndexOf("HTML", System.StringComparison.Ordinal), "HTML".Length),
            strong.SourceSpan);
        Assert.Equal("HTML", source.Substring(strong.SourceSpan.Start, strong.SourceSpan.Length));
        Assert.True(caption.Bounds.Width > 0);
        Assert.True(caption.Bounds.Bottom <= table.Bounds.Top);

        string visible = string.Concat(EnumerateRuns(snapshot).Select(static run => run.Text));
        Assert.StartsWith("Native HTML table", visible, System.StringComparison.Ordinal);
        Assert.Contains("Capability", visible, System.StringComparison.Ordinal);
        Assert.Contains("Behavior", visible, System.StringComparison.Ordinal);

        string semanticText = snapshot.SemanticDocument.Text;
        Assert.Contains("Native HTML table", semanticText, StringComparison.Ordinal);
        Assert.Contains("Capability", semanticText, StringComparison.Ordinal);
        Assert.True(
            semanticText.IndexOf("Native HTML table", System.StringComparison.Ordinal) <
                semanticText.IndexOf("Capability", System.StringComparison.Ordinal));

        int captionStart = semanticText.IndexOf("Native HTML table", System.StringComparison.Ordinal);
        int captionEnd = captionStart + "Native HTML table".Length;
        XElement selectedCaption = XElement.Parse(
            MarkdownRenderedHtmlWriter.BuildFragment(snapshot.SemanticDocument, captionStart, captionEnd));
        Assert.Equal("Native HTML table", selectedCaption.Value);
        Assert.Equal("HTML", Assert.Single(selectedCaption.Descendants("strong")).Value);
        Assert.DoesNotContain("Capability", selectedCaption.Value, StringComparison.Ordinal);
        Assert.DoesNotContain("Behavior", selectedCaption.Value, StringComparison.Ordinal);

        XElement fullSelection = XElement.Parse(
            MarkdownRenderedHtmlWriter.BuildFragment(snapshot.SemanticDocument, 0, semanticText.Length));
        XElement copiedTable = Assert.Single(fullSelection.Descendants("table"));
        Assert.Equal("Capability", Assert.Single(copiedTable.Descendants("th")).Value);
        Assert.Equal("Behavior", Assert.Single(copiedTable.Descendants("td")).Value);
        Assert.Equal(1, fullSelection.Value.Split("Native HTML table", StringSplitOptions.None).Length - 1);
        XNode[] copiedNodes = fullSelection.Nodes().ToArray();
        int captionNodeIndex = System.Array.FindIndex(
            copiedNodes,
            static node => node.ToString(SaveOptions.DisableFormatting).Contains("Native", System.StringComparison.Ordinal));
        int tableNodeIndex = System.Array.FindIndex(copiedNodes, static node => node is XElement { Name.LocalName: "table" });
        Assert.True(captionNodeIndex >= 0 && captionNodeIndex < tableNodeIndex);
    }

    [Fact]
    public void CaptionOnlyTableDoesNotDiscardItsSupportedText()
    {
        const string source = "<table><caption>Caption without rows</caption></table>";

        using LayoutSnapshot snapshot = Build(source);

        Assert.Empty(EnumerateBlocks(snapshot).OfType<TableBox>());
        Assert.Contains(
            EnumerateRuns(snapshot),
            static run => run.Text == "Caption without rows");
        Assert.Contains("Caption without rows", snapshot.SemanticDocument.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void RowsNestedInsideCaptionDoNotBecomeRowsOfTheOuterTable()
    {
        const string source =
            "<table><caption>Outer caption<table><tr><th>Nested heading</th></tr>" +
            "<tr><td>Nested cell</td></tr></table></caption>" +
            "<tr><td>Outer cell</td></tr></table>";

        using LayoutSnapshot snapshot = Build(source);

        TableBox[] tables = EnumerateBlocks(snapshot).OfType<TableBox>().ToArray();
        Assert.Equal(2, tables.Length);
        Assert.Contains(tables, static table => table.ColumnCount == 1 && table.HeaderRowCount == 0 && table.BodyRowCount == 1);
        Assert.Contains(tables, static table => table.ColumnCount == 1 && table.HeaderRowCount == 1 && table.BodyRowCount == 1);

        string visible = string.Concat(EnumerateRuns(snapshot).Select(static run => run.Text));
        Assert.Contains("Outer caption", visible, StringComparison.Ordinal);
        Assert.Contains("Nested heading", visible, StringComparison.Ordinal);
        Assert.Contains("Outer cell", visible, StringComparison.Ordinal);
    }

    private static LayoutSnapshot Build(string source)
    {
        var registry = new MarkdownExtensionRegistry().ConfigureSafeHtmlRegistry(SafeHtmlOptions.Default);
        Markdig.Syntax.MarkdownDocument document = Markdown.Parse(source, registry.BuildPipeline());
        var style = new ElementStyle();
        string[] keys =
        [
            MarkdownElementKeys.Body,
            MarkdownElementKeys.Strong,
            MarkdownElementKeys.Table,
            MarkdownElementKeys.TableHeader,
            MarkdownElementKeys.TableCell,
        ];
        var styles = keys.Distinct(System.StringComparer.Ordinal)
            .ToDictionary(static key => key, _ => style, System.StringComparer.Ordinal);
        Color transparent = Color.FromArgb(0, 0, 0, 0);
        var theme = new ThemeSnapshot(
            styles,
            new Dictionary<string, ElementStyleOverride>(),
            transparent,
            transparent,
            transparent,
            transparent,
            isDark: false,
            isHighContrast: false,
            textScaleFactor: 1);
        var context = new MarkdownLayoutContext(
            CanvasDevice.GetSharedDevice(),
            theme,
            new MarkdownSourceMap(source),
            registry,
            FlowDirection.LeftToRight);
        return new LayoutBuilder(context).Build(document, availableWidth: 800);
    }

    private static IEnumerable<BlockBox> EnumerateBlocks(LayoutSnapshot snapshot)
    {
        foreach (BlockBox block in snapshot.Blocks)
        {
            foreach (BlockBox descendant in EnumerateBlocks(block))
                yield return descendant;
        }
    }

    private static IEnumerable<BlockBox> EnumerateBlocks(BlockBox block)
    {
        yield return block;
        if (block is StackBox stack)
        {
            foreach (BlockBox child in stack.Children)
            {
                foreach (BlockBox descendant in EnumerateBlocks(child))
                    yield return descendant;
            }
        }
        else if (block is TableBox table)
        {
            foreach (InlineContainerBox cell in table.GetCellBoxes())
                yield return cell;
        }
    }

    private static IEnumerable<InlineRun> EnumerateRuns(LayoutSnapshot snapshot)
    {
        foreach (BlockBox block in EnumerateBlocks(snapshot))
        {
            if (block is InlineContainerBox inline)
            {
                foreach (InlineRun run in inline.Runs)
                    yield return run;
            }
        }
    }
}
