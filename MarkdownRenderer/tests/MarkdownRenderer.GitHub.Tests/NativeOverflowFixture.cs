using MarkdownRenderer.CodeBlocks;
using MarkdownRenderer.Document;
using MarkdownRenderer.Layout;
using MarkdownRenderer.Layout.Boxes;
using MarkdownRenderer.Parsing;
using MarkdownRenderer.Selection;
using MarkdownRenderer.Theming;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.UI.Xaml;
using Windows.Foundation;
using Windows.UI;

namespace MarkdownRenderer.GitHub.Tests;

/// <summary>
/// A narrow document holding a horizontally overflowing three-column table
/// followed by a 36-line no-wrap code block split into nine-line chunks, with a
/// selection from the first table cell to the last code chunk. Used by
/// <see cref="SelectionAdornerViewportParityTests"/> to compare clipped
/// selection geometry with the legacy full-tree oracle.
/// </summary>
internal sealed class NativeOverflowFixture(
    CanvasDevice device,
    MarkdownLayoutContext context,
    LayoutSnapshot snapshot,
    TableBox table,
    CodeBlockBox code,
    SelectionController selection,
    float width) : IAsyncDisposable
{
    private const float LayoutWidth = 176;
    private const int LinesPerChunk = 9;

    internal CanvasDevice Device { get; } = device;
    internal MarkdownLayoutContext Context { get; } = context;
    internal LayoutSnapshot Snapshot { get; } = snapshot;
    internal TableBox Table { get; } = table;
    internal CodeBlockBox Code { get; } = code;
    internal SelectionController Selection { get; } = selection;
    internal float Width { get; } = width;
    internal Rect CodeViewportBounds => Code.HorizontalViewportBounds;

    internal static NativeOverflowFixture Create(
        CanvasDevice device,
        MarkdownRenderResources renderResources,
        FlowDirection flowDirection,
        float rasterizationScale)
    {
        ThemeSnapshot theme = CreateTheme();
        string code = string.Join(
            "\n",
            Enumerable.Range(0, 36).Select(index =>
                $"var horizontalColumn_{index:D2} = sourceValue_{index:D2} + additionalOverflowPayload_{index:D2};"));
        string source = "wide table and code selection\n" + code;
        var context = new MarkdownLayoutContext(
            device,
            theme,
            new MarkdownSourceMap(source),
            new MarkdownExtensionRegistry(),
            flowDirection,
            language: "en-US")
        {
            RenderResources = renderResources,
            RasterizationScale = rasterizationScale,
            IsCodeBlockCopyEnabled = true,
        };

        TableBox table = CreateTable(context);
        table.Measure(LayoutWidth);
        table.Arrange(0, 8, LayoutWidth);

        CodeBlockBox codeBlock = CreateChunkedCodeBlock(context, code, out int lastChunkIndex);
        codeBlock.Measure(LayoutWidth);
        codeBlock.Arrange(0, (float)table.Bounds.Bottom + 20, LayoutWidth);
        var snapshot = new LayoutSnapshot(
            new BlockBox[] { table, codeBlock },
            context.SourceMap,
            LayoutWidth,
            (float)codeBlock.Bounds.Bottom + 16);
        var selection = new SelectionController();
        selection.SetAnchor(new DocumentPosition(1, 0, 0));
        selection.ExtendTo(new DocumentPosition(lastChunkIndex, 0, int.MaxValue));
        return new NativeOverflowFixture(device, context, snapshot, table, codeBlock, selection, LayoutWidth);
    }

    public async ValueTask DisposeAsync()
    {
        await Snapshot.Retire().WaitAsync(TimeSpan.FromSeconds(10));
        await Context.DrainOwnedRetirementsAsync().WaitAsync(TimeSpan.FromSeconds(10));
    }

    private static ThemeSnapshot CreateTheme()
    {
        var codeStyle = new ElementStyle
        {
            FontFamily = "Consolas",
            FontSize = 13,
            Foreground = Color.FromArgb(255, 24, 25, 26),
            Background = Color.FromArgb(255, 246, 247, 249),
        };
        var bodyStyle = new ElementStyle
        {
            FontFamily = "Segoe UI",
            FontSize = 13,
            Foreground = Color.FromArgb(255, 25, 28, 31),
        };
        var tableStyle = new ElementStyle { Padding = new Thickness(2) };
        var styles = new Dictionary<string, ElementStyle>
        {
            [MarkdownElementKeys.Body] = bodyStyle,
            [MarkdownElementKeys.Table] = tableStyle,
            [MarkdownElementKeys.TableHeader] = bodyStyle,
            [MarkdownElementKeys.TableCell] = bodyStyle,
            [MarkdownElementKeys.CodeBlock] = codeStyle,
            [MarkdownElementKeys.CodeBlockHeader] = new ElementStyle(),
            [MarkdownElementKeys.CodeBlockLanguage] = bodyStyle,
            [MarkdownElementKeys.CodeBlockGutter] = new ElementStyle(),
            [MarkdownElementKeys.CodeBlockLineNumber] = new ElementStyle(),
        };
        Color transparent = Color.FromArgb(0, 0, 0, 0);
        return new ThemeSnapshot(
            styles,
            new Dictionary<string, ElementStyleOverride>(),
            transparent,
            transparent,
            transparent,
            transparent,
            isDark: false,
            isHighContrast: false,
            textScaleFactor: 1);
    }

    private static TableBox CreateTable(MarkdownLayoutContext context)
    {
        InlineContainerBox MakeCell(int blockIndex, string text)
        {
            var cell = new InlineContainerBox(context, MarkdownElementKeys.TableCell)
            {
                BlockIndex = blockIndex,
            };
            cell.Add(new TextRun(text));
            return cell;
        }

        InlineContainerBox[][] headers =
        [
            [
                MakeCell(1, "header-column-with-long-content-one"),
                MakeCell(2, "header-column-with-long-content-two"),
                MakeCell(3, "header-column-with-long-content-three"),
            ],
        ];
        InlineContainerBox[][] rows =
        [
            [
                MakeCell(4, "body-column-with-long-content-alpha"),
                MakeCell(5, "body-column-with-long-content-bravo"),
                MakeCell(6, "body-column-with-long-content-charlie"),
            ],
            [
                MakeCell(7, "second-row-long-content-delta"),
                MakeCell(8, "second-row-long-content-echo"),
                MakeCell(9, "second-row-long-content-foxtrot"),
            ],
        ];
        return new TableBox(context, headers, rows) { BlockIndex = 0 };
    }

    private static CodeBlockBox CreateChunkedCodeBlock(
        MarkdownLayoutContext context,
        string code,
        out int lastChunkIndex)
    {
        CodeBlockMetadata metadata = CodeBlockMetadata.FromDeclarative(
            new SourceSpan(0, code.Length),
            code,
            "csharp",
            new Dictionary<string, string> { ["startline"] = "1" });
        var codeBlock = new CodeBlockBox(
            context,
            metadata,
            code,
            isCopyButtonEnabled: true,
            showLineNumbers: true)
        {
            BlockIndex = 10,
        };
        string canonicalCode = codeBlock.CodeText;
        string[] lines = canonicalCode.Split('\n');
        int nextLine = 0;
        int chunkIndex = 11;
        int textOffset = 0;
        while (nextLine < lines.Length)
        {
            int count = global::System.Math.Min(LinesPerChunk, lines.Length - nextLine);
            string chunkText = string.Join("\n", lines.Skip(nextLine).Take(count));
            if (nextLine + count < lines.Length)
                chunkText += "\n";
            var chunk = new InlineContainerBox(context, MarkdownElementKeys.CodeBlock)
            {
                BlockIndex = chunkIndex++,
                CodeLanguage = metadata.Language,
                CodeBlockTextOffset = textOffset,
                CodeBlockTextLength = chunkText.Length,
                WordWrapping = CanvasWordWrapping.NoWrap,
            };
            chunk.Add(new TextRun(chunkText));
            codeBlock.AddChunk(chunk);
            textOffset += chunkText.Length;
            nextLine += count;
        }

        lastChunkIndex = chunkIndex - 1;
        return codeBlock;
    }
}
