using MarkdownRenderer.Document;
using MarkdownRenderer.Extensions;
using MarkdownRenderer.Layout;
using MarkdownRenderer.Layout.Boxes;
using MarkdownRenderer.Parsing;
using MarkdownRenderer.Theming;
using Markdig.Syntax;
using Microsoft.Graphics.Canvas;
using Microsoft.UI.Xaml;
using Windows.Foundation;
using Windows.UI;
using Xunit;

namespace MarkdownRenderer.GitHub.Tests;

public sealed class DeclarativeLayoutAdapterTests
{
    [Fact]
    public async Task DocumentReportsEveryNestedDeclarativeStyleRoleWithoutResourceDiscovery()
    {
        var outerRole = new MarkdownStyleRole("TestOuter");
        var innerRole = new MarkdownStyleRole("TestInner");
        var engine = CreateEngine(builder =>
            builder.RegisterBlock(MarkdownSyntaxKinds.Block.Paragraph, (context, content) =>
                content.AddContainer(
                    MarkdownContentKind.Container,
                    outerRole,
                    context.Node.SourceSpan,
                    MarkdownAccessibilityRole.Group,
                    children => children.AddText(
                        "nested",
                        context.Node.SourceSpan,
                        innerRole))));

        MarkdownRenderer.Document.MarkdownDocument document = await engine.ParseAsync("source");

        Assert.Equal(
            ["TestInner", "TestOuter"],
            document.GetExtensionStyleRoleNames().Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task InlineExtensionOutputReplacesTheExactInlineNode()
    {
        var engine = CreateEngine((builder) =>
            builder.RegisterInline(MarkdownSyntaxKinds.Inline.Text, (context, content) =>
                content.AddText("declarative inline", context.Node.SourceSpan, MarkdownStyleRole.Strong)));
        MarkdownRenderer.Document.MarkdownDocument document = await engine.ParseAsync("original");

        using LayoutSnapshot snapshot = Build(document);

        var paragraph = Assert.IsType<InlineContainerBox>(Assert.Single(snapshot.Blocks));
        var run = Assert.IsType<TextRun>(Assert.Single(paragraph.Runs));
        Assert.Equal("declarative inline", run.Text);
        Assert.Equal(MarkdownElementKeys.Strong, run.ElementKey);
        Assert.Equal(new SourceSpan(0, "original".Length), run.SourceSpan);
    }

    [Theory]
    [InlineData("text", typeof(InlineContainerBox))]
    [InlineData("code", typeof(CodeBlockBox))]
    [InlineData("image", typeof(ImageBox))]
    [InlineData("list", typeof(StackBox))]
    [InlineData("table", typeof(TableBox))]
    public async Task BlockExtensionOutputUsesNativeAdapter(string kind, Type expectedType)
    {
        var engine = CreateEngine(builder =>
            builder.RegisterBlock(MarkdownSyntaxKinds.Block.Paragraph, (context, content) =>
                AddContent(kind, context, content)));
        MarkdownRenderer.Document.MarkdownDocument document = await engine.ParseAsync(kind);

        using LayoutSnapshot snapshot = Build(document);

        Assert.IsType(expectedType, Assert.Single(snapshot.Blocks));
    }

    [Fact]
    public async Task UnsupportedCustomPrimitiveFallsBackAtomicallyToMarkdown()
    {
        var engine = CreateEngine(builder =>
            builder.RegisterBlock(MarkdownSyntaxKinds.Block.Paragraph, (context, content) =>
                content.AddCustom(
                    "tests.unsupported",
                    context.Node.SourceSpan,
                    MarkdownStyleRole.Body,
                    MarkdownAccessibilityRole.Group)));
        MarkdownRenderer.Document.MarkdownDocument document = await engine.ParseAsync("native fallback");

        using LayoutSnapshot snapshot = Build(document);

        var paragraph = Assert.IsType<InlineContainerBox>(Assert.Single(snapshot.Blocks));
        Assert.Equal("native fallback", Assert.Single(paragraph.Runs).Text);
    }

    [Fact]
    public async Task TableUsesIntrinsicColumnsAndOwnsOnlyHorizontalOverflow()
    {
        const string longToken = "ThisIsADeliberatelyLongUnbreakableTableValue0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ";
        var engine = CreateEngine(builder =>
            builder.RegisterBlock(MarkdownSyntaxKinds.Block.Paragraph, (context, content) =>
            {
                SourceSpan span = context.Node.SourceSpan;
                content.AddContainer(
                    MarkdownContentKind.Table,
                    MarkdownStyleRole.Table,
                    span,
                    MarkdownAccessibilityRole.Table,
                    table => table.AddContainer(
                        MarkdownContentKind.TableRow,
                        MarkdownStyleRole.Table,
                        span,
                        MarkdownAccessibilityRole.Row,
                        row =>
                        {
                            row.AddContainer(
                                MarkdownContentKind.TableCell,
                                MarkdownStyleRole.TableCell,
                                span,
                                MarkdownAccessibilityRole.Cell,
                                cell => cell.AddText(longToken, span));
                            row.AddContainer(
                                MarkdownContentKind.TableCell,
                                MarkdownStyleRole.TableCell,
                                span,
                                MarkdownAccessibilityRole.Cell,
                                cell => cell.AddText("x", span));
                        }));
            }));
        MarkdownRenderer.Document.MarkdownDocument document = await engine.ParseAsync("table");

        using LayoutSnapshot snapshot = Build(document, width: 220);

        var table = Assert.IsType<TableBox>(Assert.Single(snapshot.Blocks));
        Assert.True(table.CanScrollHorizontally);
        Assert.True(table.HorizontalExtent > table.HorizontalViewport);
        TableBox.CellInfo[] cells = [.. table.GetCellInfos()];
        Assert.Equal(2, cells.Length);
        Assert.True(cells[0].Box.Bounds.Width > cells[1].Box.Bounds.Width * 2);

        double oldFirstCellX = cells[0].Box.Bounds.X;
        Assert.True(table.SetHorizontalOffset(table.HorizontalExtent));
        Assert.Equal(table.HorizontalExtent - table.HorizontalViewport, table.HorizontalOffset, precision: 3);
        Assert.True(cells[0].Box.Bounds.X < oldFirstCellX);
        Assert.False(table.HorizontalScrollThumbBounds.IsEmpty);
    }

    [Fact]
    public async Task CodeBlockDefaultsToNoWrapWithLocalHorizontalOverflow()
    {
        string code = new('x', 400);
        var engine = CreateEngine(builder =>
            builder.RegisterBlock(MarkdownSyntaxKinds.Block.Paragraph, (context, content) =>
                content.AddCodeBlock(code, "text", context.Node.SourceSpan)));
        MarkdownRenderer.Document.MarkdownDocument document = await engine.ParseAsync("code");

        using LayoutSnapshot snapshot = Build(document, width: 240);

        var codeBlock = Assert.IsType<CodeBlockBox>(Assert.Single(snapshot.Blocks));
        Assert.True(codeBlock.CanScrollHorizontally);
        Assert.True(codeBlock.HorizontalExtent > codeBlock.HorizontalViewport);
        Assert.Equal(Microsoft.Graphics.Canvas.Text.CanvasWordWrapping.NoWrap, Assert.Single(codeBlock.Chunks).WordWrapping);
        double oldChunkX = codeBlock.Chunks[0].Bounds.X;
        Assert.True(codeBlock.ScrollHorizontal(96));
        Assert.Equal(96, codeBlock.HorizontalOffset, precision: 3);
        Assert.True(codeBlock.Chunks[0].Bounds.X < oldChunkX);
        Assert.False(codeBlock.HorizontalScrollThumbBounds.IsEmpty);
    }

    [Fact]
    public async Task GiantCodeBlockIsPartitionedIntoViewportIndexedLineBands()
    {
        string code = string.Concat(Enumerable.Range(0, 4_096).Select(
            static line => $"line-{line:D4}\n"));
        var engine = CreateEngine(builder =>
            builder.RegisterBlock(MarkdownSyntaxKinds.Block.Paragraph, (context, content) =>
                content.AddCodeBlock(code, "text", context.Node.SourceSpan)));
        MarkdownRenderer.Document.MarkdownDocument document = await engine.ParseAsync("code");

        using LayoutSnapshot snapshot = Build(document, width: 480);

        var codeBlock = Assert.IsType<CodeBlockBox>(Assert.Single(snapshot.Blocks));
        Assert.True(codeBlock.Chunks.Count >= 32);
        Assert.True(codeBlock.IndexedVisualLineCount >= 4_096);
        Assert.All(codeBlock.Chunks, chunk =>
        {
            string chunkText = string.Concat(chunk.Runs.Select(static run => run.Text));
            Assert.InRange(chunkText.Count(static character => character == '\n'), 1, 128);
            Assert.Same(chunk.GetLineMetricsSnapshot(), chunk.GetLineMetricsSnapshot());
        });
        var visible = codeBlock.GetVisibleChunkRange(
            codeBlock.Bounds.Bottom - 240,
            codeBlock.Bounds.Bottom - 1);
        Assert.True(visible.First > 0);
        Assert.InRange(visible.EndExclusive - visible.First, 1, 3);
    }

    [Fact]
    public async Task GiantTableResolvesOnlyViewportRowsForWarmPainting()
    {
        const int rowCount = 2_048;
        var engine = CreateEngine(builder =>
            builder.RegisterBlock(MarkdownSyntaxKinds.Block.Paragraph, (context, content) =>
            {
                SourceSpan span = context.Node.SourceSpan;
                content.AddContainer(
                    MarkdownContentKind.Table,
                    MarkdownStyleRole.Table,
                    span,
                    MarkdownAccessibilityRole.Table,
                    table =>
                    {
                        for (int row = 0; row < rowCount; row++)
                        {
                            int capturedRow = row;
                            table.AddContainer(
                                MarkdownContentKind.TableRow,
                                MarkdownStyleRole.Table,
                                span,
                                MarkdownAccessibilityRole.Row,
                                cells => cells.AddContainer(
                                    MarkdownContentKind.TableCell,
                                    MarkdownStyleRole.TableCell,
                                    span,
                                    MarkdownAccessibilityRole.Cell,
                                    cell => cell.AddText($"row {capturedRow}", span)));
                        }
                    });
            }));
        MarkdownRenderer.Document.MarkdownDocument document = await engine.ParseAsync("table");

        using LayoutSnapshot snapshot = Build(document, width: 480);

        var table = Assert.IsType<TableBox>(Assert.Single(snapshot.Blocks));
        Assert.Equal(rowCount, table.RowCount);
        var visible = table.GetVisibleRowRange(
            table.HorizontalViewportBounds.Bottom - 240,
            table.HorizontalViewportBounds.Bottom - 1);
        Assert.True(visible.First > rowCount - 64);
        Assert.InRange(visible.EndExclusive - visible.First, 1, 32);
    }

    private static MarkdownEngine CreateEngine(Action<MarkdownExtensionBuilder> configure) =>
        new MarkdownEngineBuilder()
            .UseExtension(new TestExtension(configure))
            .Build();

    private static void AddContent(
        string kind,
        MarkdownExtensionContext context,
        MarkdownContentBuilder content)
    {
        SourceSpan span = context.Node.SourceSpan;
        switch (kind)
        {
            case "text":
                content.AddText("native text", span);
                break;
            case "code":
                content.AddCodeBlock(
                    "Console.WriteLine();\n",
                    "csharp",
                    span,
                    attributes: new Dictionary<string, string>
                    {
                        [MarkdownContentAttributes.CodeShowLineNumbers] = "true",
                    });
                break;
            case "image":
                content.AddImage(
                    "data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=",
                    "one pixel",
                    span);
                break;
            case "list":
                content.AddContainer(
                    MarkdownContentKind.List,
                    MarkdownStyleRole.Body,
                    span,
                    MarkdownAccessibilityRole.List,
                    list => list.AddContainer(
                        MarkdownContentKind.ListItem,
                        MarkdownStyleRole.Body,
                        span,
                        MarkdownAccessibilityRole.ListItem,
                        item => item.AddText("item", span)));
                break;
            case "table":
                content.AddContainer(
                    MarkdownContentKind.Table,
                    MarkdownStyleRole.Table,
                    span,
                    MarkdownAccessibilityRole.Table,
                    table => table.AddContainer(
                        MarkdownContentKind.TableRow,
                        MarkdownStyleRole.Table,
                        span,
                        MarkdownAccessibilityRole.Row,
                        row => row.AddContainer(
                            MarkdownContentKind.TableCell,
                            MarkdownStyleRole.TableHeader,
                            span,
                            MarkdownAccessibilityRole.Cell,
                            cell => cell.AddText("heading", span)),
                        attributes: new Dictionary<string, string>
                        {
                            [MarkdownContentAttributes.TableHeaderRow] = "true",
                        }));
                break;
            default:
                throw new InvalidOperationException(kind);
        }
    }

    private static LayoutSnapshot Build(
        MarkdownRenderer.Document.MarkdownDocument document,
        float width = 800)
    {
        var style = new ElementStyle();
        string[] keys =
        [
            MarkdownElementKeys.Body,
            MarkdownElementKeys.Strong,
            MarkdownElementKeys.Link,
            MarkdownElementKeys.ImageCaption,
            MarkdownElementKeys.CodeBlock,
            MarkdownElementKeys.CodeBlockHeader,
            MarkdownElementKeys.CodeBlockLanguage,
            MarkdownElementKeys.CodeBlockGutter,
            MarkdownElementKeys.CodeBlockLineNumber,
            MarkdownElementKeys.ListMarker,
            MarkdownElementKeys.Table,
            MarkdownElementKeys.TableHeader,
            MarkdownElementKeys.TableCell,
        ];
        var styles = keys.ToDictionary(static key => key, _ => style, StringComparer.Ordinal);
        Color transparent = Color.FromArgb(0, 0, 0, 0);
        var snapshot = new ThemeSnapshot(
            styles,
            new Dictionary<string, ElementStyleOverride>(),
            transparent,
            transparent,
            transparent,
            transparent,
            isDark: false,
            isHighContrast: false,
            textScaleFactor: 1);
        var sourceMap = new MarkdownSourceMap(document.Source);
        var context = new MarkdownLayoutContext(
            CanvasDevice.GetSharedDevice(),
            snapshot,
            sourceMap,
            new MarkdownExtensionRegistry(),
            FlowDirection.LeftToRight);
        return new LayoutBuilder(
            context,
            enableDeclarativeHostedElements: false,
            semanticDocument: document)
            .Build(document.ParsedDocument!, width);
    }

    private sealed class TestExtension(Action<MarkdownExtensionBuilder> configure) : IMarkdownExtension
    {
        public string Id => "MarkdownRenderer.Tests.DeclarativeLayout";

        public void Configure(MarkdownExtensionBuilder builder) => configure(builder);
    }
}
