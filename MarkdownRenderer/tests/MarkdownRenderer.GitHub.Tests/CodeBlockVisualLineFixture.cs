using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MarkdownRenderer.CodeBlocks;
using MarkdownRenderer.Document;
using MarkdownRenderer.Layout;
using MarkdownRenderer.Layout.Boxes;
using MarkdownRenderer.Parsing.Internal;
using MarkdownRenderer.Theming;
using MarkdownRenderer.Theming.Internal;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.UI.Xaml;
using Windows.Foundation;
using Windows.UI;
using Xunit;
using Xunit.Abstractions;

namespace MarkdownRenderer.GitHub.Tests;

/// <summary>A measured, chunked <see cref="CodeBlockBox"/> and the layout context that owns it, for visual line placement tests.</summary>
internal sealed class CodeBlockVisualLineFixture(
    CodeBlockBox box,
    MarkdownLayoutContext context,
    ThemeSnapshot theme,
    ElementStyle style,
    bool ownsContext) : IAsyncDisposable
{
    internal CodeBlockBox Box { get; } = box;
    internal MarkdownLayoutContext Context { get; } = context;
    internal ThemeSnapshot Theme { get; } = theme;
    internal ElementStyle Style { get; } = style;

    public async ValueTask DisposeAsync()
    {
        Box.Dispose();
        if (ownsContext)
            await Context.DrainOwnedRetirementsAsync().WaitAsync(TimeSpan.FromSeconds(10));
    }

    internal static CodeBlockVisualLineFixture Create(
        CanvasDevice device,
        string codeText,
        float rasterizationScale,
        FlowDirection flowDirection,
        CanvasWordWrapping wrapping,
        float availableWidth,
        int chunkCount,
        int startLine,
        bool showLineNumbers,
        CancellationToken cancellationToken = default)
    {
        ElementStyle codeStyle = new()
        {
            FontFamily = "Consolas",
            FontSize = 13,
            Foreground = Color.FromArgb(255, 28, 30, 33),
        };
        ElementStyle lineNumberStyle = new()
        {
            FontFamily = "Consolas",
            FontSize = 11,
            Foreground = Color.FromArgb(255, 86, 93, 101),
        };
        Color transparent = Color.FromArgb(0, 0, 0, 0);
        var styles = new Dictionary<string, ElementStyle>
        {
            [MarkdownElementKeys.Body] = codeStyle,
            [MarkdownElementKeys.CodeBlock] = codeStyle,
            [MarkdownElementKeys.CodeBlockLineNumber] = lineNumberStyle,
            [MarkdownElementKeys.CodeBlockGutter] = new ElementStyle(),
            [MarkdownElementKeys.CodeBlockHeader] = new ElementStyle(),
            [MarkdownElementKeys.CodeBlockLanguage] = new ElementStyle(),
        };
        var theme = new ThemeSnapshot(
            styles,
            new Dictionary<string, ElementStyleOverride>(),
            transparent, transparent, transparent, transparent,
            isDark: false,
            isHighContrast: false,
            textScaleFactor: 1);
        var registry = new MarkdownExtensionRegistry();
        var sourceMap = new MarkdownSourceMap(codeText);
        MarkdownLayoutContext context = CreateContext(
            device, theme, sourceMap, registry, flowDirection, rasterizationScale, cancellationToken);
        var attributes = new Dictionary<string, string>
        {
            ["startline"] = startLine.ToString(CultureInfo.InvariantCulture),
        };
        CodeBlockMetadata metadata = CodeBlockMetadata.FromDeclarative(
            new SourceSpan(0, codeText.Length), codeText, "csharp", attributes);
        var box = new CodeBlockBox(context, metadata, codeText, isCopyButtonEnabled: true, showLineNumbers)
        {
            BlockIndex = context.NextBlockIndex(),
        };

        // CodeBlockBox canonicalizes CRLF/CR to LF in CopyPayload. Chunk
        // descriptors must partition the exact displayed payload so private
        // placement preflight can prove contiguous source offsets and text.
        string displayedCodeText = box.CodeText;
        foreach ((int offset, int length) in SplitAtLineBoundaries(displayedCodeText, chunkCount))
        {
            var chunk = new InlineContainerBox(context, MarkdownElementKeys.CodeBlock)
            {
                BlockIndex = context.NextBlockIndex(),
                CodeLanguage = metadata.Language,
                CodeBlockTextOffset = offset,
                CodeBlockTextLength = length,
                WordWrapping = wrapping,
            };
            chunk.Add(new TextRun(displayedCodeText.Substring(offset, length)));
            box.AddChunk(chunk);
        }

        box.Measure(availableWidth);
        box.Arrange(0, 0, availableWidth);
        return new CodeBlockVisualLineFixture(box, context, theme, codeStyle, ownsContext: true);
    }

    internal static MarkdownLayoutContext CreateCompatibleContext(CodeBlockVisualLineFixture source, CanvasDevice device)
        => CreateContext(
            device,
            source.Theme,
            source.Context.SourceMap,
            source.Context.Registry,
            source.Context.FlowDirection,
            (float)source.Context.RasterizationScale,
            disclosureStates: source.Context.DisclosureStates);

    private static MarkdownLayoutContext CreateContext(
        CanvasDevice device,
        ThemeSnapshot theme,
        MarkdownSourceMap sourceMap,
        MarkdownExtensionRegistry registry,
        FlowDirection flowDirection,
        float rasterizationScale,
        CancellationToken cancellationToken = default,
        IReadOnlyDictionary<string, bool>? disclosureStates = null)
        => new(device, theme, sourceMap, registry, flowDirection, language: "en-US")
        {
            CancellationToken = cancellationToken,
            RasterizationScale = rasterizationScale,
            IsCodeBlockCopyEnabled = true,
            // Private-placement preflight requires the exact immutable state
            // snapshots to be shared. A fresh context otherwise creates a new
            // empty DisclosureStates dictionary and is correctly rejected.
            DisclosureStates = disclosureStates ?? new Dictionary<string, bool>(StringComparer.Ordinal),
        };

    private static IEnumerable<(int Offset, int Length)> SplitAtLineBoundaries(string text, int chunkCount)
    {
        int start = 0;
        for (int part = 1; part < chunkCount; part++)
        {
            int target = (int)((long)text.Length * part / chunkCount);
            int split = text.IndexOf('\n', target);
            if (split < 0 || split + 1 >= text.Length)
                break;
            int end = split + 1;
            if (end <= start)
                continue;
            yield return (start, end - start);
            start = end;
        }

        if (start < text.Length)
            yield return (start, text.Length - start);
        else if (text.Length == 0)
            yield return (0, 0);
    }
}
