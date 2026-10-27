using System.Collections.Generic;
using Markdig.Syntax;
using MarkdownRenderer.CodeBlocks;
using MarkdownRenderer.Document;
using MarkdownRenderer.Layout;
using MarkdownRenderer.Layout.Boxes;
using MarkdownRenderer.Parsing;
using MarkdownRenderer.Theming;
using Microsoft.Graphics.Canvas;
using Microsoft.UI.Xaml;
using Windows.UI;
using Xunit;

namespace MarkdownRenderer.GitHub.Tests;

public sealed class CodeBlockChromeModeTests
{
    [Fact]
    public void PlainMarkdownOmitsHeaderSpaceAndCopyActionButKeepsLineNumberPolicy()
    {
        CanvasDevice device = CanvasDevice.GetSharedDevice();
        MarkdownLayoutContext defaultContext = CreateContext(device);
        var enhanced = CreateBox(defaultContext, showLineNumbers: true, copyEnabled: true);
        MarkdownLayoutContext plainContext = CreateContext(device, CodeBlockChromeMode.PlainMarkdown);
        var plain = CreateBox(plainContext, showLineNumbers: true, copyEnabled: true);
        MarkdownLayoutContext noNumbersContext = CreateContext(device, CodeBlockChromeMode.PlainMarkdown);
        var plainWithoutLineNumbers = CreateBox(noNumbersContext, showLineNumbers: false, copyEnabled: true);

        try
        {
            Assert.Equal(CodeBlockChromeMode.Enhanced, defaultContext.CodeBlockChromeMode);
            enhanced.Measure(400);
            plain.Measure(400);
            plainWithoutLineNumbers.Measure(400);

            Assert.Equal(38, enhanced.Bounds.Height - plain.Bounds.Height, precision: 2);
            Assert.Equal(38, enhanced.HorizontalViewportBounds.Y - plain.HorizontalViewportBounds.Y, precision: 2);
            Assert.True(enhanced.CopyButtonBounds.Width > 0);
            Assert.True(plain.CopyButtonBounds.IsEmpty);
            Assert.True(enhanced.HasCopyButtonAction);
            Assert.False(plain.HasCopyButtonAction);
            Assert.True(plain.ShowLineNumbers);
            Assert.Equal(enhanced.HorizontalViewport, plain.HorizontalViewport, precision: 2);
            Assert.True(
                plainWithoutLineNumbers.HorizontalViewport > plain.HorizontalViewport,
                "Plain Markdown chrome must leave the explicit line-number gutter policy independent.");
        }
        finally
        {
            enhanced.Dispose();
            plain.Dispose();
            plainWithoutLineNumbers.Dispose();
        }
    }

    private static CodeBlockBox CreateBox(
        MarkdownLayoutContext context,
        bool showLineNumbers,
        bool copyEnabled)
    {
        CodeBlockMetadata metadata = CodeBlockMetadata.FromDeclarative(
            new SourceSpan(0, 4),
            "code",
            "text",
            new Dictionary<string, string>());
        return new CodeBlockBox(context, metadata, "first\nsecond", copyEnabled, showLineNumbers);
    }

    private static MarkdownLayoutContext CreateContext(
        CanvasDevice device,
        CodeBlockChromeMode? mode = null)
    {
        var style = new ElementStyle();
        var styles = new Dictionary<string, ElementStyle>
        {
            [MarkdownElementKeys.Body] = style,
            [MarkdownElementKeys.CodeBlock] = style,
            [MarkdownElementKeys.CodeBlockHeader] = style,
            [MarkdownElementKeys.CodeBlockLanguage] = style,
            [MarkdownElementKeys.CodeBlockGutter] = style,
            [MarkdownElementKeys.CodeBlockLineNumber] = style,
        };
        Color transparent = Color.FromArgb(0, 0, 0, 0);
        var theme = new ThemeSnapshot(
            styles,
            new Dictionary<string, ElementStyleOverride>(),
            transparent, transparent, transparent, transparent,
            isDark: false,
            isHighContrast: false,
            textScaleFactor: 1);
        if (mode is CodeBlockChromeMode chromeMode)
        {
            return new MarkdownLayoutContext(
                device,
                theme,
                new MarkdownSourceMap("first\nsecond"),
                new MarkdownExtensionRegistry(),
                FlowDirection.LeftToRight)
            {
                CodeBlockChromeMode = chromeMode,
            };
        }

        return new MarkdownLayoutContext(
            device,
            theme,
            new MarkdownSourceMap("first\nsecond"),
            new MarkdownExtensionRegistry(),
            FlowDirection.LeftToRight);
    }
}
