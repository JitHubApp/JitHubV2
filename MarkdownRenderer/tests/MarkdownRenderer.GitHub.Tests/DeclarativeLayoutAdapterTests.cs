using MarkdownRenderer.Document;
using MarkdownRenderer.Extensions;
using MarkdownRenderer.Layout.Internal.Boxes;
using MarkdownRenderer.Layout.Internal.CodeBlocks;
using MarkdownRenderer.Layout.Internal.Images;
using MarkdownRenderer.Layout.Internal.Inlines;
using MarkdownRenderer.Layout.Internal.Inlines.Runs;
using MarkdownRenderer.Layout.Internal.Snapshots;
using MarkdownRenderer.Layout.Internal.Tables;
using MarkdownRenderer.Theming;
using Markdig.Syntax;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.UI.Xaml;
using Windows.Foundation;
using Windows.UI;
using Xunit;
using static MarkdownRenderer.GitHub.Tests.DeclarativeLayoutAdapterFixture;

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

    [Theory]
    [InlineData(FlowDirection.LeftToRight)]
    [InlineData(FlowDirection.RightToLeft)]
    public async Task DeclarativeListCentersOnlyUnorderedMarkersWithoutMovingContent(
        FlowDirection flowDirection)
    {
        var engine = CreateEngine(builder =>
            builder.RegisterBlock(MarkdownSyntaxKinds.Block.Paragraph, (context, content) =>
                AddNestedAndOrderedLists(context.Node.SourceSpan, content)));
        MarkdownRenderer.Document.MarkdownDocument document = await engine.ParseAsync("lists");

        using LayoutSnapshot snapshot = Build(
            document,
            width: 600,
            flowDirection: flowDirection,
            listMarkerStyle: new ElementStyle { ListIndent = 32, NestedListIndent = 8 });

        ListItemBox[] items = EnumerateListItems(snapshot.Blocks).ToArray();
        Assert.Equal(5, items.Length);
        Assert.Equal(new[] { "•", "•", "•" }, items
            .Where(static item => Assert.Single(item.Marker.Runs).Text == "•")
            .Select(static item => Assert.Single(item.Marker.Runs).Text));
        Assert.Equal(new[] { "7.", "8." }, items
            .Where(static item => Assert.Single(item.Marker.Runs).Text != "•")
            .Select(static item => Assert.Single(item.Marker.Runs).Text));

        ListItemBox[] unordered = items
            .Where(static item => Assert.Single(item.Marker.Runs).Text == "•")
            .ToArray();
        ListItemBox[] ordered = items
            .Where(static item => Assert.Single(item.Marker.Runs).Text != "•")
            .ToArray();
        Assert.All(unordered, item =>
            Assert.Equal(CanvasHorizontalAlignment.Center, item.Marker.TextAlignment));
        Assert.All(ordered, item =>
            Assert.NotEqual(CanvasHorizontalAlignment.Center, item.Marker.TextAlignment));
        Assert.Equal(new[] { 32f, 40f, 32f, 32f, 32f }, items
            .Select(static item => item.MarkerWidth));

        foreach (ListItemBox item in items)
        {
            AssertClose(item.MarkerWidth, item.Marker.Bounds.Width);
            AssertClose(item.Bounds.Width, item.MarkerWidth + item.Content.Bounds.Width);
            if (flowDirection == FlowDirection.RightToLeft)
                AssertClose(item.Content.Bounds.Right, item.Marker.Bounds.Left);
            else
                AssertClose(item.Marker.Bounds.Right, item.Content.Bounds.Left);
        }
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

    [Theory]
    [InlineData(1d)]
    [InlineData(1.5d)]
    [InlineData(2d)]
    public async Task ResponsiveImageColumnShrinksAndKeepsSiblingColumnsVisible(double rasterizationScale)
    {
        const string onePixelPng =
            "data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=";
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
                                cell => cell.AddText("Lists", span));
                            row.AddContainer(
                                MarkdownContentKind.TableCell,
                                MarkdownStyleRole.TableCell,
                                span,
                                MarkdownAccessibilityRole.Cell,
                                cell => cell.AddImage(
                                    onePixelPng,
                                    "List rendering test sheet",
                                    span,
                                    attributes: new Dictionary<string, string>
                                    {
                                        [MarkdownContentAttributes.ImageWidth] = "1280",
                                        [MarkdownContentAttributes.ImageHeight] = "1024",
                                    }));
                            row.AddContainer(
                                MarkdownContentKind.TableCell,
                                MarkdownStyleRole.TableCell,
                                span,
                                MarkdownAccessibilityRole.Cell,
                                cell => cell.AddText(
                                    "Unordered, ordered, definition, nested, and mixed lists",
                                    span));
                        }));
            }));
        MarkdownRenderer.Document.MarkdownDocument document = await engine.ParseAsync("image table");

        using LayoutSnapshot snapshot = Build(
            document,
            width: 734,
            rasterizationScale: rasterizationScale);

        var table = Assert.IsType<TableBox>(Assert.Single(snapshot.Blocks));
        TableBox.CellInfo[] cells = [.. table.GetCellInfos()];
        Assert.Equal(3, cells.Length);
        Assert.False(table.CanScrollHorizontally);
        Assert.InRange(table.HorizontalExtent, 1, table.HorizontalViewport + 0.5);

        var image = Assert.IsType<InlineImageRun>(Assert.Single(cells[1].Box.Runs));
        Assert.InRange(image.DesiredWidth, 1, cells[1].Box.Bounds.Width + 0.5);
        Assert.True(image.DesiredWidth < 720);
        Assert.InRange(image.DesiredWidth / image.DesiredHeight, 1.249f, 1.251f);
        Assert.InRange(
            cells[2].Box.Bounds.Right,
            table.HorizontalViewportBounds.Left,
            table.HorizontalViewportBounds.Right + 0.5);
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
}
