using MarkdownRenderer.CodeBlocks;
using MarkdownRenderer.Document;
using MarkdownRenderer.Layout.Internal.Boxes;
using MarkdownRenderer.Layout.Internal.CodeBlocks;
using MarkdownRenderer.Layout.Internal.Context;
using MarkdownRenderer.Layout.Internal.Inlines;
using MarkdownRenderer.Layout.Internal.Inlines.Runs;
using MarkdownRenderer.Layout.Internal.RenderResources;
using MarkdownRenderer.Layout.Internal.Snapshots;
using MarkdownRenderer.Parsing.Internal;
using MarkdownRenderer.Selection;
using MarkdownRenderer.Selection.Internal;
using MarkdownRenderer.Theming;
using MarkdownRenderer.Theming.Internal;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.UI.Xaml;
using Windows.UI;
using Xunit;

namespace MarkdownRenderer.GitHub.Tests.Selection.Internal;

/// <summary>
/// A native, line-numbered C# code block laid out alone at a fractional origin,
/// with a selection spanning every chunk. Used by
/// <see cref="SelectionAdornerCodeChunkParityTests"/> to compare code selection
/// geometry with DirectWrite in both flow directions.
/// </summary>
internal sealed class RtlNativeCodeFixture(
    CanvasDevice device,
    MarkdownLayoutContext context,
    LayoutSnapshot snapshot,
    CodeBlockBox code,
    SelectionController selection) : IAsyncDisposable
{
    private const float Width = 208.25f;

    internal CanvasDevice Device { get; } = device;
    internal MarkdownLayoutContext Context { get; } = context;
    internal LayoutSnapshot Snapshot { get; } = snapshot;
    internal CodeBlockBox Code { get; } = code;
    internal SelectionController Selection { get; } = selection;

    internal static RtlNativeCodeFixture Create(
        CanvasDevice device,
        MarkdownRenderResources renderResources,
        float rasterizationScale,
        CanvasWordWrapping wrapping,
        bool includeUnbreakableToken,
        FlowDirection flowDirection = FlowDirection.RightToLeft)
    {
        string[] lines = CreateLines(includeUnbreakableToken);
        string codeText = string.Join("\n", lines);
        var context = new MarkdownLayoutContext(
            device,
            CreateTheme(),
            new MarkdownSourceMap(codeText),
            new MarkdownExtensionRegistry(),
            flowDirection,
            language: "en-US")
        {
            RenderResources = renderResources,
            RasterizationScale = rasterizationScale,
            IsCodeBlockCopyEnabled = true,
        };
        CodeBlockBox code = CreateCodeBlock(context, codeText, lines, wrapping);

        code.Measure(Width);
        code.Arrange(11.25f, 37.375f, Width);
        var snapshot = new LayoutSnapshot(
            new BlockBox[] { code },
            context.SourceMap,
            Width,
            (float)code.Bounds.Bottom + 12);
        var selection = new SelectionController();
        selection.SetAnchor(new DocumentPosition(code.Chunks[0].BlockIndex, 0, 0));
        selection.ExtendTo(new DocumentPosition(code.Chunks[^1].BlockIndex, 0, int.MaxValue));
        return new RtlNativeCodeFixture(device, context, snapshot, code, selection);
    }

    public async ValueTask DisposeAsync()
    {
        await Snapshot.Retire().WaitAsync(TimeSpan.FromSeconds(10));
        await Context.DrainOwnedRetirementsAsync().WaitAsync(TimeSpan.FromSeconds(10));
    }

    private static string[] CreateLines(bool includeUnbreakableToken)
    {
        string longToken = new('W', 220);
        return includeUnbreakableToken
            ?
            [
                "x",
                "var mediumName = 17;",
                "var bidiValue = \"שלום אבג 123\";",
                "var token = " + longToken + ";",
                "tail;",
            ]
            :
            [
                "var firstValue = \"ordinary words wrap within the view\";",
                "var secondValue = \"more words for a natural wrap\";",
                "int finalColumn = 42;",
            ];
    }

    private static ThemeSnapshot CreateTheme()
    {
        var codeStyle = new ElementStyle
        {
            FontFamily = "Consolas",
            FontSize = 13,
            Foreground = Color.FromArgb(255, 24, 25, 26),
            Background = Color.FromArgb(255, 246, 247, 249),
            Padding = new Thickness(4, 2, 6, 2),
        };
        var lineNumberStyle = new ElementStyle
        {
            FontFamily = "Consolas",
            FontSize = 11,
            Foreground = Color.FromArgb(255, 86, 93, 101),
        };
        Color transparent = Color.FromArgb(0, 0, 0, 0);
        return new ThemeSnapshot(
            new Dictionary<string, ElementStyle>
            {
                [MarkdownElementKeys.Body] = codeStyle,
                [MarkdownElementKeys.CodeBlock] = codeStyle,
                [MarkdownElementKeys.CodeBlockHeader] = new ElementStyle(),
                [MarkdownElementKeys.CodeBlockLanguage] = codeStyle,
                [MarkdownElementKeys.CodeBlockGutter] = new ElementStyle(),
                [MarkdownElementKeys.CodeBlockLineNumber] = lineNumberStyle,
            },
            new Dictionary<string, ElementStyleOverride>(),
            transparent,
            transparent,
            transparent,
            transparent,
            isDark: false,
            isHighContrast: false,
            textScaleFactor: 1);
    }

    private static CodeBlockBox CreateCodeBlock(
        MarkdownLayoutContext context,
        string codeText,
        string[] lines,
        CanvasWordWrapping wrapping)
    {
        var metadata = CodeBlockMetadata.FromDeclarative(
            new SourceSpan(0, codeText.Length),
            codeText,
            "csharp",
            new Dictionary<string, string> { ["startline"] = "7" });
        var code = new CodeBlockBox(context, metadata, codeText, isCopyButtonEnabled: true, showLineNumbers: true)
        {
            BlockIndex = 0,
        };
        int nextBlockIndex = 1;
        int textOffset = 0;
        foreach (string line in lines)
        {
            string chunkText = textOffset + line.Length < codeText.Length ? line + "\n" : line;
            var chunk = new InlineContainerBox(context, MarkdownElementKeys.CodeBlock)
            {
                BlockIndex = nextBlockIndex++,
                CodeLanguage = metadata.Language,
                CodeBlockTextOffset = textOffset,
                CodeBlockTextLength = chunkText.Length,
                WordWrapping = wrapping,
            };
            chunk.Add(new TextRun(chunkText));
            code.AddChunk(chunk);
            textOffset += chunkText.Length;
        }
        Assert.Equal(codeText.Length, textOffset);
        return code;
    }
}
